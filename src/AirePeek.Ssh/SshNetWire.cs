using System.Security.Cryptography;
using System.Text;
using AirePeek.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using Prompt = AirePeek.Core.AuthenticationPrompt;

namespace AirePeek.Ssh;

internal sealed class SshNetWireFactory : ISshWireFactory
{
    public ISshWire Create(SshEndpoint identity, string connectHost, int connectPort, IAuthenticationUI ui, IHostKeyStore keys)
        => new SshNetWire(identity, connectHost, connectPort, ui, keys);
}

internal sealed class SshNetWire(SshEndpoint identity, string connectHost, int connectPort, IAuthenticationUI ui, IHostKeyStore keys) : ISshWire
{
    private SshClient? _client;
    private AuthenticationMethod[] _methods = [];
    private Exception? _hostKeyFailure;
    private CancellationTokenSource? _lifetime;
    public bool IsConnected => _client?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        var token = _lifetime.Token;
        var bridge = new AuthenticationBridge(identity, ui, keys, token);
        var keyboard = new KeyboardInteractiveAuthenticationMethod(identity.Username);
        keyboard.AuthenticationPrompt += (_, args) =>
        {
            var ordered = args.Prompts.OrderBy(p => p.Id).ToArray();
            var prompts = ordered.Select(p => new Prompt(p.Request, !p.IsEchoed)).ToArray();
            var responses = bridge.PromptAsync("keyboard-interactive", args.Instruction, prompts).GetAwaiter().GetResult();
            for (var i = 0; i < ordered.Length; i++) ordered[i].Response = responses[i];
            // Response objects are owned by SSH.NET until it transmits the response. Nothing is logged or persisted.
        };
        // Some MFA servers advertise both methods but only accept keyboard-interactive
        // after password partial success. SSH.NET will not revisit a failed method.
        _methods = [new PromptedPasswordAuthentication(identity.Username, bridge), keyboard];
        var connection = new ConnectionInfo(connectHost, connectPort, identity.Username, _methods)
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        _client = new SshClient(connection) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        _client.HostKeyReceived += (_, args) =>
        {
            args.CanTrust = false;
            try
            {
                bridge.VerifyHostKeyAsync(args.HostKeyName, args.HostKey).GetAwaiter().GetResult();
                args.CanTrust = true;
            }
            catch (Exception error) { _hostKeyFailure = error; }
        };
        try
        {
            // Authentication callbacks can wait for the desktop dispatcher. Never start the SSH handshake on that dispatcher.
            await Task.Run(() => _client.ConnectAsync(token), token).ConfigureAwait(false);
            if (_hostKeyFailure is not null) throw _hostKeyFailure;
            bridge.AuthenticationCompleted();
            _lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch
        {
            if (_hostKeyFailure is not null) throw _hostKeyFailure;
            token.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            foreach (var method in _methods) method.Dispose();
            _methods = [];
        }
    }

    public ILocalForward Forward(SshEndpoint target)
    {
        if (_client?.IsConnected != true) throw new SessionDisconnectedException();
        var port = new ForwardedPortLocal("127.0.0.1", 0, target.Host, (uint)target.Port);
        _client.AddForwardedPort(port);
        try
        {
            port.Start();
            return new LocalForward(port);
        }
        catch { port.Dispose(); throw; }
    }

    public async Task<CommandResult> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        if (_client?.IsConnected != true) throw new SessionDisconnectedException();
        using var sshCommand = _client.CreateCommand(command);
        sshCommand.CommandTimeout = TimeSpan.FromSeconds(30);
        await sshCommand.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new(sshCommand.Result, sshCommand.ExitStatus);
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _client?.Dispose();
        _client = null;
        foreach (var method in _methods) method.Dispose();
        _methods = [];
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private sealed class LocalForward(ForwardedPortLocal port) : ILocalForward
    {
        public int Port => checked((int)port.BoundPort);
        public void Dispose() => port.Dispose();
    }
}

/// <summary>SSH.NET selects this only when the server advertises password authentication.</summary>
internal sealed class PromptedPasswordAuthentication(string username, AuthenticationBridge bridge) : AuthenticationMethod(username)
{
    public override string Name => "password";

    public override AuthenticationResult Authenticate(Session session)
    {
        var answers = bridge.PromptAsync("password", "The server requests password authentication.", [new Prompt("Password", true)]).GetAwaiter().GetResult();
        var bytes = Encoding.UTF8.GetBytes(answers[0]);
        answers = [];
        try
        {
            using var password = new PasswordAuthenticationMethod(Username, bytes);
            var result = password.Authenticate(session);
            AllowedAuthentications = password.AllowedAuthentications;
            if (result == AuthenticationResult.PartialSuccess) bridge.PasswordAccepted();
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
