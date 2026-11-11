using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AirePeek.Core;

namespace AirePeek.Ssh;

/// <summary>
/// An opt-in, single connection-attempt adapter. It does not cache generic hidden inputs or Duo responses.
/// SSH.NET still verifies each real endpoint's host key before requesting authentication.
/// </summary>
internal sealed class ConnectionPasswordSharing(ConnectionProfile profile, IAuthenticationUI inner, IPasswordStore? passwordStore = null) : IAuthenticationUI, IAuthenticationProgress, IDisposable
{
    private readonly bool enabled = profile.ReuseGatewayPassword && profile.Gateway is { } gateway &&
        string.Equals(gateway.Username, profile.Target.Username, StringComparison.Ordinal);
    private readonly object sync = new();
    private readonly bool remember = profile.RememberPassword && passwordStore is not null;
    private readonly Dictionary<SshEndpoint, char[]> pendingPasswords = new();
    private readonly HashSet<SshEndpoint> checkedVault = new(), usedVault = new();
    private readonly HashSet<SshEndpoint> passwordSubmitted = new(), additionalVerification = new();
    private char[]? password;
    private bool targetStage, disposed;
    public bool TargetPasswordReused { get; private set; }
    public string? PasswordStorageWarning { get; private set; }
    public bool FailedSavedPassword { get; private set; }
    public SshEndpoint? AdditionalVerificationFailure { get; private set; }
    internal bool HasCachedPassword { get { lock (sync) return password is not null; } }

    public void GatewayAuthenticated()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (profile.Gateway is { } gateway) SaveAuthenticated(gateway);
            targetStage = true;
        }
    }
    public void TargetAuthenticated() { lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); SaveAuthenticated(profile.Target); } }
    public void PasswordAccepted(SshEndpoint endpoint)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (endpoint == profile.Target || endpoint == profile.Gateway) additionalVerification.Add(endpoint);
        }
    }
    public void AuthenticationFailed()
    {
        lock (sync)
        {
            var endpoint = targetStage || profile.Gateway is null ? profile.Target : profile.Gateway;
            if (additionalVerification.Contains(endpoint)) { AdditionalVerificationFailure = endpoint; return; }
            FailedSavedPassword = usedVault.Contains(endpoint);
            if (FailedSavedPassword) DeleteSaved(endpoint);
        }
    }

    public async Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken)
    {
        bool ordinaryPassword = IsOrdinaryPassword(challenge) && (challenge.Endpoint == profile.Target || challenge.Endpoint == profile.Gateway);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            // Later factors can also be labelled "Password". Once password is accepted,
            // never fill, capture or invalidate it on the basis of a Duo/OTP response.
            if (additionalVerification.Contains(challenge.Endpoint)) ordinaryPassword = false;
            else if (!ordinaryPassword && passwordSubmitted.Contains(challenge.Endpoint)) additionalVerification.Add(challenge.Endpoint);
            if (ordinaryPassword && usedVault.Contains(challenge.Endpoint))
            {
                // A repeated password prompt may be a rejected keyboard-interactive/password fallback.
                // Stop instead of replaying a stored value or silently creating another authentication attempt.
                DeleteSaved(challenge.Endpoint); FailedSavedPassword = true;
                throw new SavedPasswordAuthenticationException();
            }
            if (ordinaryPassword && remember && checkedVault.Add(challenge.Endpoint))
            {
                char[]? stored = null;
                try
                {
                    stored = passwordStore!.Read(challenge.Endpoint);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stored is { Length: > 0 })
                    {
                        usedVault.Add(challenge.Endpoint);
                        if (targetStage) Clear();
                        CapturePassword(challenge.Endpoint, stored);
                        return new[] { new string(stored) };
                    }
                }
                catch (PasswordStoreException) { PasswordStorageWarning = "Device password storage was unavailable. This connection used manual authentication."; }
                finally { Zero(stored); }
            }
            // A confirmed password for this exact target wins over a shared gateway copy.
            // Sharing is only a fallback; rejecting it must not delete a different target's saved item.
            if (ordinaryPassword && enabled && targetStage && !TargetPasswordReused && password is not null && challenge.Endpoint == profile.Target)
            {
                TargetPasswordReused = true;
                passwordSubmitted.Add(challenge.Endpoint);
                if (remember) RememberPending(challenge.Endpoint, password);
                try { return new[] { new string(password) }; }
                finally { Clear(); }
            }
        }

        var answers = await inner.PromptAsync(challenge, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            // A late UI completion cannot repopulate an already-disposed connection attempt.
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (ordinaryPassword && answers is { Count: 1 } && !string.IsNullOrEmpty(answers[0]))
            {
                var captured = answers[0].ToCharArray();
                try { CapturePassword(challenge.Endpoint, captured); }
                finally { Zero(captured); }
            }
        }
        return answers;
    }

    public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken)
        => inner.ConfirmHostKeyAsync(challenge, cancellationToken);

    internal static bool IsOrdinaryPassword(AuthenticationChallenge challenge)
    {
        if (challenge.Prompts.Count != 1 || !challenge.Prompts[0].IsSecret) return false;
        var label = challenge.Prompts[0].Text.Trim();
        if (!label.Equals("Password", StringComparison.OrdinalIgnoreCase) && !label.Equals("Password:", StringComparison.OrdinalIgnoreCase)) return false;
        if (challenge.Name == "password") return true; // The explicit SSH password method, not server-provided text.
        if (challenge.Name != "keyboard-interactive") return false;
        // Ambiguous factors always stay interactive, even if a server labels the field "Password".
        string[] factorWords = ["duo", "code", "verification", "one-time", "one time", "otp", "token", "pin", "challenge", "response", "authenticator", "second factor", "2fa", "mfa", "验证码", "动态", "令牌"];
        return !factorWords.Any(word => challenge.Instruction.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private void CapturePassword(SshEndpoint endpoint, char[] captured)
    {
        passwordSubmitted.Add(endpoint);
        if (remember) RememberPending(endpoint, captured);
        if (enabled && !targetStage && endpoint == profile.Gateway) { Clear(); password = (char[])captured.Clone(); }
    }
    private void RememberPending(SshEndpoint endpoint, char[] captured)
    {
        if (pendingPasswords.Remove(endpoint, out var previous)) Zero(previous);
        pendingPasswords[endpoint] = (char[])captured.Clone();
    }
    private void SaveAuthenticated(SshEndpoint endpoint)
    {
        if (!remember || !pendingPasswords.Remove(endpoint, out var captured)) return;
        try { passwordStore!.Save(endpoint, captured); }
        catch (PasswordStoreException) { PasswordStorageWarning = "Signed in, but this device could not remember the password. No password was saved in portable files."; }
        finally { Zero(captured); }
    }
    private void DeleteSaved(SshEndpoint endpoint)
    {
        try { passwordStore?.Delete(endpoint); }
        catch (PasswordStoreException) { PasswordStorageWarning = "The saved password could not be removed from the device password store. Use Forget saved passwords before retrying."; }
    }
    private static void Zero(char[]? value) { if (value is not null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan())); }

    private void Clear()
    {
        if (password is null) return;
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
        password = null;
    }
    public void Dispose()
    {
        lock (sync)
        {
            Clear(); foreach (var captured in pendingPasswords.Values) Zero(captured);
            pendingPasswords.Clear(); disposed = true;
        }
    }
}
