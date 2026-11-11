using System.Net.Sockets;
using AirePeek.Core;
using Renci.SshNet.Common;

namespace AirePeek.Ssh;

/// <summary>One explicitly authenticated target connection, optionally carried by one gateway connection.</summary>
public sealed class SshCommandSession : ICommandSession, IPasswordStorageStatus
{
    private readonly ISshWireFactory _factory;
    private readonly IPasswordStore? _passwordStore;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private ISshWire? _gateway;
    private ISshWire? _target;
    private ILocalForward? _forward;
    private bool _disposed;
    private volatile bool _broken = true;

    public SshCommandSession() : this(new SshNetWireFactory(), new OsPasswordStore()) { }
    internal SshCommandSession(ISshWireFactory factory, IPasswordStore? passwordStore = null) { _factory = factory; _passwordStore = passwordStore; }
    public string? PasswordStorageWarning { get; private set; }

    public bool IsConnected => !_disposed && !_broken && _target?.IsConnected == true && (_gateway is null || _gateway.IsConnected);

    public async Task ConnectAsync(ConnectionProfile profile, IAuthenticationUI authenticationUI, IHostKeyStore hostKeys,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(profile.Target);
        if (profile.Gateway is not null) Validate(profile.Gateway);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var authentication = new ConnectionPasswordSharing(profile, authenticationUI, _passwordStore);
        PasswordStorageWarning = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DisposeConnections();
            if (profile.Gateway is { } gateway)
            {
                _gateway = _factory.Create(gateway, gateway.Host, gateway.Port, authentication, hostKeys);
                await _gateway.ConnectAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                authentication.GatewayAuthenticated();
                _forward = _gateway.Forward(profile.Target);
                _target = _factory.Create(profile.Target, "127.0.0.1", _forward.Port, authentication, hostKeys);
            }
            else
            {
                _target = _factory.Create(profile.Target, profile.Target.Host, profile.Target.Port, authentication, hostKeys);
            }
            await _target.ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            authentication.TargetAuthenticated();
            _broken = false;
        }
        catch (Exception error)
        {
            DisposeConnections();
            if (error is HostKeyMismatchException or OperationCanceledException or ArgumentException or SavedPasswordAuthenticationException) throw;
            // Never expose library exception messages, which may contain a server challenge or response.
            if (error is SshAuthenticationException)
            {
                authentication.AuthenticationFailed();
                if (authentication.AdditionalVerificationFailure is { } endpoint) throw new AdditionalAuthenticationException(endpoint);
                if (authentication.TargetPasswordReused) throw new SharedPasswordAuthenticationException();
                if (authentication.FailedSavedPassword) throw new SavedPasswordAuthenticationException();
                throw new InvalidOperationException("Authentication failed. Check the username, password or server options, then reconnect.");
            }
            throw new SessionDisconnectedException("Could not establish SSH. Check the campus wired network / university VPN, host, port and gateway settings.");
        }
        finally { PasswordStorageWarning = authentication.PasswordStorageWarning; _operations.Release(); }
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsConnected) throw new SessionDisconnectedException();
            var result = await _target!.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            if (result.ExitStatus != 0)
                throw new RemoteCommandException($"Slurm query failed (exit code {result.ExitStatus?.ToString() ?? "unknown"}). Check command support and account permissions.");
            return result.Output;
        }
        catch (OperationCanceledException) { throw; }
        catch (RemoteCommandException) { throw; }
        catch (SshOperationTimeoutException) when (IsConnected)
        {
            throw new RemoteCommandException("Slurm query timed out. Retaining the last successful result.");
        }
        catch (Exception error) when (error is SshException or SocketException or IOException or ObjectDisposedException or SessionDisconnectedException)
        {
            DisposeConnections();
            throw new SessionDisconnectedException();
        }
        finally { _operations.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try { DisposeConnections(); }
        finally { _operations.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            DisposeConnections();
        }
        finally { _operations.Release(); }
        // Keep the semaphore usable for a racing already-started operation; no unmanaged state is held.
    }

    private void DisposeConnections()
    {
        _broken = true;
        try { _target?.Dispose(); } catch { /* Best effort local connection cleanup only. */ }
        try { _forward?.Dispose(); } catch { }
        try { _gateway?.Dispose(); } catch { }
        _target = null;
        _forward = null;
        _gateway = null;
    }

    private static void Validate(SshEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Host.Any(char.IsWhiteSpace) || endpoint.Host.Contains('/') ||
            endpoint.Host.Contains('\\') || endpoint.Host.Contains('\0'))
            throw new ArgumentException("Enter a valid host name or IP address.");
        if (endpoint.Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(endpoint.Username) || endpoint.Username.Any(char.IsControl))
            throw new ArgumentException("Enter a valid username.");
    }
}

internal sealed record CommandResult(string Output, int? ExitStatus);
internal interface ILocalForward : IDisposable { int Port { get; } }
internal interface ISshWire : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    ILocalForward Forward(SshEndpoint target);
    Task<CommandResult> ExecuteAsync(string command, CancellationToken cancellationToken);
}
internal interface ISshWireFactory
{
    ISshWire Create(SshEndpoint identity, string connectHost, int connectPort, IAuthenticationUI ui, IHostKeyStore keys);
}
