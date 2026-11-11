namespace AirePeek.Core;

public sealed record SshEndpoint(string Host, int Port, string Username);
public sealed record ConnectionProfile(SshEndpoint Target, SshEndpoint? Gateway = null, bool ReuseGatewayPassword = false, bool RememberPassword = false);
public sealed record AuthenticationPrompt(string Text, bool IsSecret);
public sealed record AuthenticationChallenge(SshEndpoint Endpoint, string Name, string Instruction, IReadOnlyList<AuthenticationPrompt> Prompts);
public sealed record HostKeyChallenge(SshEndpoint Endpoint, string Algorithm, string Sha256);

public interface IAuthenticationUI
{
    Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken);
    Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken);
}

public interface IHostKeyStore
{
    string? Get(SshEndpoint endpoint);
    void Save(SshEndpoint endpoint, string sha256);
}

public interface ICommandSession : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(ConnectionProfile profile, IAuthenticationUI authenticationUI, IHostKeyStore hostKeys, CancellationToken cancellationToken = default);
    Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}

public sealed class SessionDisconnectedException(string message = "Disconnected. Reconnect when you are ready.", Exception? inner = null) : Exception(message, inner);
public sealed class RemoteCommandException(string message) : Exception(message);
public sealed class HostKeyMismatchException(string message) : Exception(message);
public sealed class SharedPasswordAuthenticationException() : Exception("Authentication did not complete after sharing the gateway password. Reconnect with separate passwords; Duo verification may still be required for each host.");
public sealed class SavedPasswordAuthenticationException() : Exception("Authentication did not complete with a saved password. Reconnect with This connection to enter it again. Duo verification is still required when the server requests it.");
public sealed class AdditionalAuthenticationException(SshEndpoint endpoint) : Exception($"Additional verification did not complete on {endpoint.Host}:{endpoint.Port}. Reconnect and complete the server's Duo or verification prompts. Your saved password was not changed.")
{
    public SshEndpoint Endpoint { get; } = endpoint;
}
