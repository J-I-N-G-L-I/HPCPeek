using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AirePeek.Core;
using Renci.SshNet.Common;

namespace AirePeek.Ssh.Tests;

public sealed class PasswordPersistenceTests
{
    private static readonly SshEndpoint Gateway = new("rash.example.test", 22, "student");
    private static readonly SshEndpoint Target = new("aire.example.test", 22, "student");
    private static AuthenticationChallenge Password(SshEndpoint endpoint) => new(endpoint, "keyboard-interactive", "", [new("Password:", true)]);

    [Fact]
    public async Task EachEndpointIsSavedOnlyAfterItsOwnSuccessfulAuthenticationAndDuoIsNeverSaved()
    {
        var vault = new MemoryVault(); var ui = new RecordingUi();
        using var authentication = new ConnectionPasswordSharing(new(Target, Gateway, true, true), ui, vault);
        await authentication.PromptAsync(Password(Gateway), default);
        Assert.Empty(vault.Saved);
        await authentication.PromptAsync(new(Gateway, "keyboard-interactive", "Duo", [new("Passcode or option", false)]), default);
        authentication.GatewayAuthenticated();
        Assert.Equal(new[] { Gateway }, vault.Saved);
        Assert.Equal("ordinary-password", new string(vault.Values[OsPasswordStore.ScopeKey(Gateway)]));
        await authentication.PromptAsync(Password(Target), default);
        Assert.Single(vault.Saved);
        await authentication.PromptAsync(new(Target, "keyboard-interactive", "Duo", [new("Code", true)]), default);
        authentication.TargetAuthenticated();
        Assert.Equal(new[] { Gateway, Target }, vault.Saved);
        Assert.All(vault.Values.Values, value => Assert.Equal("ordinary-password", new string(value)));
        Assert.Equal(3, ui.Prompts.Count);
    }

    [Fact]
    public async Task FutureConnectionReadsSavedPasswordsButStillPromptsBothDuoChallenges()
    {
        var vault = new MemoryVault(); vault.Seed(Gateway); vault.Seed(Target);
        var ui = new RecordingUi();
        using var authentication = new ConnectionPasswordSharing(new(Target, Gateway, false, true), ui, vault);
        Assert.Equal("ordinary-password", Assert.Single((await authentication.PromptAsync(Password(Gateway), default))!));
        await authentication.PromptAsync(new(Gateway, "keyboard-interactive", "Choose Duo", [new("Choose 7 or 9", false)]), default);
        authentication.GatewayAuthenticated();
        Assert.Equal("ordinary-password", Assert.Single((await authentication.PromptAsync(Password(Target), default))!));
        await authentication.PromptAsync(new(Target, "keyboard-interactive", "Approve Duo", []), default);
        authentication.TargetAuthenticated();
        Assert.Equal(2, ui.Prompts.Count);
        Assert.All(ui.Prompts, prompt => Assert.Contains("Duo", prompt.Instruction));
        Assert.Equal(new[] { Gateway, Target }, vault.Reads);
    }

    [Fact]
    public async Task RememberedTargetPasswordWinsOverSharedGatewayPassword()
    {
        var vault = new MemoryVault(); vault.Seed(Gateway); vault.Seed(Target);
        vault.Values[OsPasswordStore.ScopeKey(Target)] = "different-target-password".ToCharArray();
        using var authentication = new ConnectionPasswordSharing(new(Target, Gateway, true, true), new RecordingUi(), vault);
        await authentication.PromptAsync(Password(Gateway), default); authentication.GatewayAuthenticated();
        Assert.Equal("different-target-password", Assert.Single((await authentication.PromptAsync(Password(Target), default))!));
        Assert.False(authentication.TargetPasswordReused);
        Assert.False(authentication.HasCachedPassword);
        Assert.Equal(new[] { Gateway, Target }, vault.Reads);
    }

    [Fact]
    public async Task RejectedGatewayCopyDoesNotDeleteAnUnreadTargetCredentialOrTheValidGateway()
    {
        var vault = new MemoryVault(); vault.Seed(Gateway);
        using var authentication = new ConnectionPasswordSharing(new(Target, Gateway, true, true), new RecordingUi(), vault);
        await authentication.PromptAsync(Password(Gateway), default); authentication.GatewayAuthenticated();
        await authentication.PromptAsync(Password(Target), default);
        authentication.AuthenticationFailed();
        Assert.True(authentication.TargetPasswordReused);
        Assert.False(authentication.FailedSavedPassword);
        Assert.Empty(vault.Deleted);
        Assert.True(vault.Values.ContainsKey(OsPasswordStore.ScopeKey(Gateway)));
    }

    [Theory]
    [InlineData("Password:", "Duo verification", true)]
    [InlineData("Password:", "Enter one-time code", true)]
    [InlineData("Password:", "", false)]
    [InlineData("Passcode or option:", "", true)]
    [InlineData("Verification code:", "", true)]
    [InlineData("Secret:", "", true)]
    public async Task AmbiguousOrNonPasswordFactorsDoNotReadOrWriteVault(string label, string instruction, bool secret)
    {
        var vault = new MemoryVault(); vault.Seed(Target);
        var ui = new RecordingUi();
        using var authentication = new ConnectionPasswordSharing(new(Target, RememberPassword: true), ui, vault);
        await authentication.PromptAsync(new(Target, "keyboard-interactive", instruction, [new(label, secret)]), default);
        authentication.TargetAuthenticated();
        Assert.Empty(vault.Reads); Assert.Empty(vault.Saved); Assert.Single(ui.Prompts);
    }

    [Fact]
    public async Task MultiplePromptsAreNeverPartiallyAutofilledOrPersisted()
    {
        var vault = new MemoryVault(); vault.Seed(Target);
        var ui = new RecordingUi();
        using var authentication = new ConnectionPasswordSharing(new(Target, RememberPassword: true), ui, vault);
        await authentication.PromptAsync(new(Target, "keyboard-interactive", "", [new("Password:", true), new("OTP", true)]), default);
        authentication.TargetAuthenticated();
        Assert.Empty(vault.Reads); Assert.Empty(vault.Saved); Assert.Single(ui.Prompts);
    }

    [Fact]
    public async Task RepeatedStoredPasswordRequestDeletesEntryAndStopsWithoutAutomaticRetry()
    {
        var vault = new MemoryVault(); vault.Seed(Target); var ui = new RecordingUi();
        using var authentication = new ConnectionPasswordSharing(new(Target, RememberPassword: true), ui, vault);
        await authentication.PromptAsync(Password(Target), default);
        await Assert.ThrowsAsync<SavedPasswordAuthenticationException>(() => authentication.PromptAsync(Password(Target), default));
        Assert.Equal(new[] { Target }, vault.Deleted); Assert.Empty(ui.Prompts);
        Assert.Empty(vault.Values); Assert.Single(vault.Reads);
    }

    [Fact]
    public async Task CurrentConnectionChoiceNeverTouchesExistingVaultEntries()
    {
        var vault = new MemoryVault(); vault.Seed(Gateway); vault.Seed(Target); var ui = new RecordingUi();
        using var authentication = new ConnectionPasswordSharing(new(Target, Gateway, true, false), ui, vault);
        await authentication.PromptAsync(Password(Gateway), default); authentication.GatewayAuthenticated();
        await authentication.PromptAsync(Password(Target), default); authentication.TargetAuthenticated();
        Assert.Empty(vault.Reads); Assert.Empty(vault.Saved); Assert.Empty(vault.Deleted);
        Assert.Single(ui.Prompts);
    }

    [Fact]
    public async Task CancellationOrFailedAuthenticationNeverStoresUnconfirmedAnswers()
    {
        var vault = new MemoryVault();
        using (var authentication = new ConnectionPasswordSharing(new(Target, RememberPassword: true), new RecordingUi(), vault))
        {
            await authentication.PromptAsync(Password(Target), default);
            authentication.AuthenticationFailed();
        }
        Assert.Empty(vault.Saved); Assert.Empty(vault.Values);
    }

    [Fact]
    public async Task VaultFailuresAllowManualLoginButReportNoPortableFallback()
    {
        var vault = new MemoryVault { FailReads = true, FailWrites = true }; var ui = new RecordingUi();
        using var authentication = new ConnectionPasswordSharing(new(Target, RememberPassword: true), ui, vault);
        await authentication.PromptAsync(Password(Target), default);
        authentication.TargetAuthenticated();
        Assert.Single(ui.Prompts); Assert.Empty(vault.Values);
        Assert.Contains("could not remember", authentication.PasswordStorageWarning);
        Assert.Contains("No password was saved in portable files", authentication.PasswordStorageWarning);
    }

    [Fact]
    public async Task BuffersReturnedByStoreAreClearedAfterUseAndPendingBuffersOnDisposal()
    {
        var vault = new MemoryVault(); vault.Seed(Target);
        var authentication = new ConnectionPasswordSharing(new(Target, RememberPassword: true), new RecordingUi(), vault);
        await authentication.PromptAsync(Password(Target), default);
        Assert.All(vault.LastReadBuffer!, c => Assert.Equal('\0', c));
        var pending = (Dictionary<SshEndpoint, char[]>)typeof(ConnectionPasswordSharing).GetField("pendingPasswords", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(authentication)!;
        var owned = pending[Target]; authentication.Dispose();
        Assert.All(owned, c => Assert.Equal('\0', c)); Assert.Empty(vault.Saved);
    }

    [Fact]
    public void EndpointScopeNormalizesHostnameButKeepsPortAndCaseSensitiveUsernameSeparate()
    {
        Assert.Equal(OsPasswordStore.ScopeKey(Target), OsPasswordStore.ScopeKey(Target with { Host = " AIRE.EXAMPLE.TEST " }));
        Assert.NotEqual(OsPasswordStore.ScopeKey(Target), OsPasswordStore.ScopeKey(Target with { Port = 2222 }));
        Assert.NotEqual(OsPasswordStore.ScopeKey(Target), OsPasswordStore.ScopeKey(Target with { Username = "Student" }));
        Assert.NotEqual(OsPasswordStore.ScopeKey(Target), OsPasswordStore.ScopeKey(Gateway));
    }

    [Fact]
    public async Task ChangedFingerprintStopsBeforeVaultReadAndQueriesDoNotReadItAgain()
    {
        var vault = new MemoryVault(); vault.Seed(Target);
        var keys = new Keys(); keys.Save(Target, "SHA256:wrong-key");
        var factory = new Factory();
        await using var session = new SshCommandSession(factory, vault);
        await Assert.ThrowsAsync<HostKeyMismatchException>(() => session.ConnectAsync(new(Target, RememberPassword: true), new RecordingUi(), keys));
        Assert.Empty(vault.Reads);
        await session.ConnectAsync(new(Target, RememberPassword: true), new RecordingUi(), new Keys());
        Assert.Single(vault.Reads); Assert.True(session.IsConnected);
        await session.ExecuteAsync(SlurmCommands.Queue); await session.ExecuteAsync(SlurmCommands.Nodes);
        Assert.Single(vault.Reads);
    }

    [Fact]
    public async Task RejectedSavedAuthenticationStopsAndDeletesOnlyFailedEndpoint()
    {
        var vault = new MemoryVault(); vault.Seed(Gateway); vault.Seed(Target);
        var factory = new Factory { FailTarget = true };
        await using var session = new SshCommandSession(factory, vault);
        await Assert.ThrowsAsync<SavedPasswordAuthenticationException>(() => session.ConnectAsync(new(Target, Gateway, false, true), new RecordingUi(), new Keys()));
        Assert.False(session.IsConnected); Assert.Equal(2, factory.ConnectionCount);
        Assert.Equal(new[] { Target }, vault.Deleted);
        Assert.True(vault.Values.ContainsKey(OsPasswordStore.ScopeKey(Gateway)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuoFailureRetainsKnownPasswordsAndIsNotMisreportedAsPasswordSharing(bool share)
    {
        var vault = new MemoryVault(); vault.Seed(Gateway); vault.Seed(Target);
        var factory = new Factory { FailDuo = true };
        await using var session = new SshCommandSession(factory, vault);
        var error = await Assert.ThrowsAsync<AdditionalAuthenticationException>(() => session.ConnectAsync(new(Target, Gateway, share, true), new RecordingUi(), new Keys()));
        Assert.Equal(Target, error.Endpoint);
        Assert.Contains("Additional verification", error.Message);
        Assert.Empty(vault.Deleted);
        Assert.True(vault.Values.ContainsKey(OsPasswordStore.ScopeKey(Target)));
        Assert.False(session.IsConnected); Assert.Equal(2, factory.ConnectionCount);
    }

    [Fact]
    public async Task PasswordPartialSuccessThenPasswordLabelledSecondFactorNeverReusesOrStoresTheFactor()
    {
        var vault = new MemoryVault(); vault.Seed(Target);
        var ui = new RecordingUi { PasswordAnswer = "synthetic-one-time-factor" };
        using var authentication = new ConnectionPasswordSharing(new(Target, RememberPassword: true), ui, vault);
        var bridge = new AuthenticationBridge(Target, authentication, new Keys(), default);
        Assert.Equal("ordinary-password", Assert.Single(await bridge.PromptAsync("password", "", [new("Password", true)])));
        bridge.PasswordAccepted();
        Assert.Equal("synthetic-one-time-factor", Assert.Single(await bridge.PromptAsync("keyboard-interactive", "", [new("Password:", true)])));
        authentication.TargetAuthenticated();
        Assert.Single(ui.Prompts); Assert.Single(vault.Reads); Assert.Empty(vault.Deleted);
        Assert.Equal("ordinary-password", new string(vault.Values[OsPasswordStore.ScopeKey(Target)]));
    }

    [Theory]
    [InlineData("student")]
    [InlineData("A\\B\"C<>&+")]
    [InlineData("学员-é-🔒")]
    [InlineData("line\n\t\u0001")]
    public void TrimSafeCredentialKeyPreservesExistingV050VaultIdentities(string username)
    {
        var endpoint = Target with { Username = username };
        var originalJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] { endpoint.Host.Trim().ToLowerInvariant(), endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), endpoint.Username });
        Assert.Equal("HPCPeek/SSH/v1/" + Convert.ToHexString(SHA256.HashData(originalJson)), OsPasswordStore.ScopeKey(endpoint));
    }

    [Fact]
    public async Task CancellationRacingSuccessfulTransportDoesNotCommitPendingPassword()
    {
        using var cancellation = new CancellationTokenSource();
        var vault = new MemoryVault();
        var factory = new Factory { Connected = cancellation.Cancel };
        await using var session = new SshCommandSession(factory, vault);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(new(Target, RememberPassword: true), new RecordingUi(), new Keys(), cancellation.Token));
        Assert.False(session.IsConnected); Assert.Empty(vault.Saved); Assert.Empty(vault.Values);
    }

    [Fact]
    public async Task UnencodablePasswordKeepsAuthenticatedSessionAndReportsStorageFailure()
    {
        // This synthetic malformed string must fail before any native credential store write.
        var ui = new RecordingUi { PasswordAnswer = new string(['x', '\uD800']) };
        await using var session = new SshCommandSession(new Factory(), new EncodingCheckingVault());
        await session.ConnectAsync(new(Target, RememberPassword: true), ui, new Keys());
        Assert.True(session.IsConnected);
        Assert.Contains("could not remember", session.PasswordStorageWarning);
        Assert.Contains("No password was saved in portable files", session.PasswordStorageWarning);
    }

    [Fact]
    public void NativeWindowsVaultRoundtripUsesAndCleansOnlyAnIsolatedSyntheticRecord()
    {
        if (!OperatingSystem.IsWindows()) return; // macOS native execution is separately reported as not available on Windows.
        var endpoint = new SshEndpoint("hpcpeek-vault-test-" + Guid.NewGuid().ToString("N") + ".invalid", 2222, "synthetic-user");
        var store = new OsPasswordStore(); var password = "synthetic-only-\u6D4B\u8BD5-\uD83D\uDD12".ToCharArray();
        char[]? read = null;
        try
        {
            Assert.Null(store.Read(endpoint));
            store.Save(endpoint, password); read = store.Read(endpoint);
            Assert.Equal(password, read); store.Delete(endpoint); Assert.Null(store.Read(endpoint));
        }
        finally
        {
            store.Delete(endpoint);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
            if (read is not null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(read.AsSpan()));
        }
    }

    private sealed class MemoryVault : IPasswordStore
    {
        public Dictionary<string, char[]> Values { get; } = new();
        public List<SshEndpoint> Reads { get; } = new();
        public List<SshEndpoint> Saved { get; } = new();
        public List<SshEndpoint> Deleted { get; } = new();
        public char[]? LastReadBuffer { get; private set; }
        public bool FailReads { get; init; }
        public bool FailWrites { get; init; }
        public void Seed(SshEndpoint endpoint) => Values[OsPasswordStore.ScopeKey(endpoint)] = "ordinary-password".ToCharArray();
        public char[]? Read(SshEndpoint endpoint)
        {
            Reads.Add(endpoint); if (FailReads) throw new PasswordStoreException();
            return LastReadBuffer = Values.TryGetValue(OsPasswordStore.ScopeKey(endpoint), out var value) ? (char[])value.Clone() : null;
        }
        public void Save(SshEndpoint endpoint, ReadOnlySpan<char> password)
        {
            if (FailWrites) throw new PasswordStoreException();
            Saved.Add(endpoint); Values[OsPasswordStore.ScopeKey(endpoint)] = password.ToArray();
        }
        public void Delete(SshEndpoint endpoint) { Deleted.Add(endpoint); Values.Remove(OsPasswordStore.ScopeKey(endpoint)); }
    }
    private sealed class RecordingUi : IAuthenticationUI
    {
        public string PasswordAnswer { get; init; } = "ordinary-password";
        public List<AuthenticationChallenge> Prompts { get; } = new();
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken)
        {
            Prompts.Add(challenge);
            return Task.FromResult<IReadOnlyList<string>?>(challenge.Prompts.Select(_ => ConnectionPasswordSharing.IsOrdinaryPassword(challenge) ? PasswordAnswer : "synthetic-second-factor").ToArray());
        }
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class EncodingCheckingVault : IPasswordStore
    {
        public char[]? Read(SshEndpoint endpoint) => null;
        public void Save(SshEndpoint endpoint, ReadOnlySpan<char> password) => new OsPasswordStore().Save(endpoint, password);
        public void Delete(SshEndpoint endpoint) { }
    }
    private sealed class Keys : IHostKeyStore
    {
        private readonly Dictionary<SshEndpoint, string> keys = new();
        public string? Get(SshEndpoint endpoint) => keys.GetValueOrDefault(endpoint);
        public void Save(SshEndpoint endpoint, string sha256) => keys[endpoint] = sha256;
    }
    private sealed class Factory : ISshWireFactory
    {
        public bool FailTarget { get; init; }
        public bool FailDuo { get; init; }
        public Action? Connected { get; init; }
        public int ConnectionCount { get; private set; }
        public ISshWire Create(SshEndpoint identity, string connectHost, int connectPort, IAuthenticationUI ui, IHostKeyStore keys) => new Wire(this, identity, ui, keys);
        private sealed class Wire(Factory factory, SshEndpoint endpoint, IAuthenticationUI ui, IHostKeyStore keys) : ISshWire
        {
            public bool IsConnected { get; private set; }
            public async Task ConnectAsync(CancellationToken cancellationToken)
            {
                factory.ConnectionCount++;
                var bridge = new AuthenticationBridge(endpoint, ui, keys, cancellationToken);
                await bridge.VerifyHostKeyAsync("ssh-ed25519", [1, 2, 3, 4]);
                await bridge.PromptAsync("keyboard-interactive", "", [new("Password:", true)]);
                if (factory.FailTarget && endpoint == Target) throw new SshAuthenticationException("Synthetic password rejection");
                await bridge.PromptAsync("keyboard-interactive", "Duo verification", [new("Choose option", false)]);
                if (factory.FailDuo && endpoint == Target) throw new SshAuthenticationException("Synthetic Duo rejection");
                bridge.AuthenticationCompleted(); IsConnected = true; factory.Connected?.Invoke();
            }
            public ILocalForward Forward(SshEndpoint target) => new Forward();
            public Task<CommandResult> ExecuteAsync(string command, CancellationToken cancellationToken) => Task.FromResult(new CommandResult("sample", 0));
            public void Dispose() => IsConnected = false;
        }
        private sealed class Forward : ILocalForward { public int Port => 43123; public void Dispose() { } }
    }
}
