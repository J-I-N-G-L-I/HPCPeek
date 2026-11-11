using System.Security.Cryptography;
using AirePeek.Core;

namespace AirePeek.Ssh;

/// <summary>Maps protocol callbacks to the UI without interpreting Duo options or caching responses.</summary>
internal sealed class AuthenticationBridge(
    SshEndpoint identity,
    IAuthenticationUI ui,
    IHostKeyStore hostKeys,
    CancellationToken cancellationToken)
{
    private bool _authenticated;
    public SshEndpoint Identity { get; } = identity;

    public void AuthenticationCompleted() => _authenticated = true;
    public void PasswordAccepted() => (ui as IAuthenticationProgress)?.PasswordAccepted(Identity);

    public async Task<IReadOnlyList<string>> PromptAsync(string name, string instruction, IReadOnlyList<AuthenticationPrompt> prompts)
    {
        if (_authenticated)
            throw new InvalidOperationException("Authentication cannot restart on an established connection. Reconnect explicitly.");
        cancellationToken.ThrowIfCancellationRequested();
        var responses = await ui.PromptAsync(new(Identity, name, instruction, prompts), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (responses is null)
            throw new OperationCanceledException("Authentication cancelled.", cancellationToken);
        if (responses.Count != prompts.Count)
            throw new InvalidOperationException("The number of authentication responses does not match the server prompts.");
        return responses;
    }

    public async Task VerifyHostKeyAsync(string algorithm, byte[] hostKey)
    {
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
        var known = hostKeys.Get(Identity);
        if (known is not null)
        {
            if (!string.Equals(known, fingerprint, StringComparison.Ordinal))
                throw new HostKeyMismatchException($"The host key for {Identity.Host}:{Identity.Port} has changed. Connection stopped. Verify it with the university administrator; saved fingerprints are never replaced automatically.");
            return;
        }
        // A server key is checked before credentials are requested, including the target behind a tunnel.
        if (_authenticated || !await ui.ConfirmHostKeyAsync(new(Identity, algorithm, fingerprint), cancellationToken).ConfigureAwait(false))
            throw new OperationCanceledException("The host fingerprint was not confirmed. Connection stopped.", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        hostKeys.Save(Identity, fingerprint);
    }
}

internal interface IAuthenticationProgress
{
    void PasswordAccepted(SshEndpoint endpoint);
}
