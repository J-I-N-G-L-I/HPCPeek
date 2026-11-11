using System.Security.Cryptography;
using AirePeek.Core;
using SshAuthenticationException = Renci.SshNet.Common.SshAuthenticationException;

namespace AirePeek.Ssh.Tests;

public sealed class SessionTests
{
    private static readonly SshEndpoint Target = new("aire.example.edu", 22, "student");
    private static readonly SshEndpoint Gateway = new("gateway.example.edu", 2222, "student");

    [Fact]
    public async Task TwoHopMultiRoundAuthenticationThenAllQueriesReuseTargetSession()
    {
        var ui = new FakeUi();
        var keys = new MemoryKeys();
        var factory = new FakeFactory();
        await using var session = new SshCommandSession(factory);
        await session.ConnectAsync(new(Target, Gateway), ui, keys);

        Assert.True(session.IsConnected);
        Assert.Equal(2, factory.Wires.Count);
        Assert.Equal(6, ui.Challenges.Count); // password, choice, zero-prompt Duo waiting round per host
        Assert.Equal(new[] { Gateway, Target }, ui.Confirmed.Select(c => c.Endpoint));
        Assert.Equal("127.0.0.1", factory.Wires[1].ConnectHost);
        Assert.Equal(43123, factory.Wires[1].ConnectPort);
        Assert.Equal(Target, factory.Wires[1].Identity);
        Assert.Contains("SHA256:", keys.Get(Target));

        await session.ExecuteAsync("squeue --me");
        await session.ExecuteAsync("scontrol -o -d show nodes");
        await session.ExecuteAsync("sacct -j 123");
        await session.ExecuteAsync("squeue --me");
        Assert.Equal(6, ui.Challenges.Count);
        Assert.Equal(2, factory.Wires.Sum(w => w.ConnectCount));
        Assert.Empty(factory.Wires[0].Commands);
        Assert.Equal(4, factory.Wires[1].Commands.Count);
    }

    [Fact]
    public async Task SharedGatewayPasswordIsScopedToOneConnectAttemptAndNeverReplacesDuo()
    {
        var ui = new FakeUi();
        var factory = new FakeFactory();
        await using var session = new SshCommandSession(factory);
        await session.ConnectAsync(new(Target, Gateway, true), ui, new MemoryKeys());
        Assert.True(session.IsConnected);
        Assert.Equal(5, ui.Challenges.Count);
        Assert.Equal(2, ui.Challenges.Count(c => c.Instruction == "Choose a Duo option"));
        Assert.Equal(2, ui.Challenges.Count(c => c.Prompts.Count == 0));
        Assert.All(factory.Wires, w => Assert.False(w.Authentication.HasCachedPassword));
        await session.ExecuteAsync("squeue --me");
        Assert.Equal(5, ui.Challenges.Count);
        await session.ConnectAsync(new(Target, Gateway, true), ui, new MemoryKeys());
        Assert.Equal(10, ui.Challenges.Count);
        Assert.Equal(Gateway, ui.Challenges[5].Endpoint);
        Assert.All(factory.Wires, w => Assert.False(w.Authentication.HasCachedPassword));
    }

    [Fact]
    public async Task RejectedSharedAuthenticationClearsBufferAndRequiresExplicitSeparateRetry()
    {
        var ui = new FakeUi();
        var factory = new FakeFactory { FailTargetAuthentication = true };
        await using var session = new SshCommandSession(factory);
        await Assert.ThrowsAsync<SharedPasswordAuthenticationException>(() => session.ConnectAsync(new(Target, Gateway, true), ui, new MemoryKeys()));
        Assert.Equal(2, factory.Wires.Count); // No automatic new login or Duo attempt.
        Assert.All(factory.Wires, w => Assert.True(w.Disposed));
        Assert.All(factory.Wires, w => Assert.False(w.Authentication.HasCachedPassword));
        factory.FailTargetAuthentication = false;
        await session.ConnectAsync(new(Target, Gateway), ui, new MemoryKeys());
        Assert.True(session.IsConnected);
        Assert.Equal(3, ui.Challenges.Count(c => c.Prompts.Any(p => p.Text == "Password:")));
    }

    [Fact]
    public async Task DisconnectNeverTriggersImplicitAuthenticationAndExplicitReconnectUsesPins()
    {
        var factory = new FakeFactory();
        var ui = new FakeUi();
        var keys = new MemoryKeys();
        await using var session = new SshCommandSession(factory);
        await session.ConnectAsync(new(Target), ui, keys);
        factory.Wires[0].Connected = false;
        await Assert.ThrowsAsync<SessionDisconnectedException>(() => session.ExecuteAsync("squeue --me"));
        Assert.Equal(3, ui.Challenges.Count);
        Assert.Single(factory.Wires);
        Assert.False(session.IsConnected);

        await session.ConnectAsync(new(Target), ui, keys);
        Assert.True(session.IsConnected);
        Assert.Equal(6, ui.Challenges.Count);
        Assert.Single(ui.Confirmed); // reconnect verifies the pin without asking again
    }

    [Fact]
    public async Task ChangedHostKeyStopsBeforeAuthenticationAndDoesNotOfferReplacement()
    {
        var factory = new FakeFactory();
        var ui = new FakeUi();
        var keys = new MemoryKeys();
        keys.Save(Target, "SHA256:previously-confirmed-key");
        await using var session = new SshCommandSession(factory);
        await Assert.ThrowsAsync<HostKeyMismatchException>(() => session.ConnectAsync(new(Target), ui, keys));
        Assert.Empty(ui.Challenges);
        Assert.Empty(ui.Confirmed);
        Assert.Equal("SHA256:previously-confirmed-key", keys.Get(Target));
        Assert.False(session.IsConnected);
        Assert.True(factory.Wires[0].Disposed);
    }

    [Fact]
    public async Task TargetPinFailureAlsoClosesGatewayAndLoopbackForward()
    {
        var factory = new FakeFactory();
        var keys = new MemoryKeys();
        keys.Save(Target, "SHA256:wrong-target-key");
        await using var session = new SshCommandSession(factory);
        await Assert.ThrowsAsync<HostKeyMismatchException>(() => session.ConnectAsync(new(Target, Gateway), new FakeUi(), keys));
        Assert.All(factory.Wires, wire => Assert.True(wire.Disposed));
        Assert.True(factory.Wires[0].Tunnel!.Disposed);
    }

    [Fact]
    public async Task OptInNeverSharesPasswordBeforeTargetFingerprintValidation()
    {
        var factory = new FakeFactory();
        var ui = new FakeUi();
        var keys = new MemoryKeys();
        keys.Save(Target, "SHA256:wrong-target-key");
        await using var session = new SshCommandSession(factory);
        await Assert.ThrowsAsync<HostKeyMismatchException>(() => session.ConnectAsync(new(Target, Gateway, true), ui, keys));
        Assert.Equal(3, ui.Challenges.Count);
        Assert.All(ui.Challenges, c => Assert.Equal(Gateway, c.Endpoint));
        Assert.All(factory.Wires, w => Assert.False(w.Authentication.HasCachedPassword));
        Assert.All(factory.Wires, w => Assert.False(w.Authentication.TargetPasswordReused));
    }

    [Fact]
    public async Task NonzeroGpuCommandLeavesConnectionAndQueueUsable()
    {
        var factory = new FakeFactory();
        var ui = new FakeUi();
        await using var session = new SshCommandSession(factory);
        await session.ConnectAsync(new(Target), ui, new MemoryKeys());
        factory.Wires[0].NextExitStatus = 1;
        await Assert.ThrowsAsync<RemoteCommandException>(() => session.ExecuteAsync("scontrol -o -d show nodes"));
        Assert.True(session.IsConnected);
        Assert.Equal("sample", await session.ExecuteAsync("squeue --me"));
        Assert.Equal(3, ui.Challenges.Count);
    }

    [Fact]
    public async Task DeclinedFirstFingerprintSendsNoCredentialsAndStoresNothing()
    {
        var ui = new FakeUi { Trust = false };
        var keys = new MemoryKeys();
        await using var session = new SshCommandSession(new FakeFactory());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(new(Target), ui, keys));
        Assert.Empty(ui.Challenges);
        Assert.Null(keys.Get(Target));
    }

    [Fact]
    public async Task ChallengeCancellationEndsConnectionAndDoesNotStoreAnswers()
    {
        var ui = new FakeUi { CancelPrompts = true };
        var factory = new FakeFactory();
        await using var session = new SshCommandSession(factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(new(Target), ui, new MemoryKeys()));
        Assert.False(session.IsConnected);
        Assert.True(factory.Wires[0].Disposed);
        Assert.Single(ui.Challenges);
    }

    [Fact]
    public async Task PromptsAreForwardedInServerOrderWithoutAssumingDuoOptionNumbers()
    {
        var ui = new FakeUi();
        var bridge = new AuthenticationBridge(Target, ui, new MemoryKeys(), default);
        var prompts = new[] { new AuthenticationPrompt("Enter 7 for Duo Mobile, 9 for SMS", false), new AuthenticationPrompt("Code:", true) };
        var answers = await bridge.PromptAsync("keyboard-interactive", "Use Duo Mobile to approve", prompts);
        Assert.Equal(prompts, ui.Challenges.Single().Prompts);
        Assert.Equal("Use Duo Mobile to approve", ui.Challenges.Single().Instruction);
        Assert.Equal(new[] { "answer-0", "answer-1" }, answers);
        bridge.AuthenticationCompleted();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.PromptAsync("password", "", prompts));
    }

    [Fact]
    public async Task Sha256FingerprintMatchesOpenSshFormatAndRealHostPortScopesTrust()
    {
        byte[] rawKey = [1, 2, 3, 4];
        var ui = new FakeUi();
        var keys = new MemoryKeys();
        var otherPort = Target with { Port = 2222 };
        var first = new AuthenticationBridge(Target, ui, keys, default);
        var second = new AuthenticationBridge(otherPort, ui, keys, default);
        await first.VerifyHostKeyAsync("ssh-ed25519", rawKey);
        await second.VerifyHostKeyAsync("ssh-ed25519", rawKey);
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(rawKey)).TrimEnd('=');
        Assert.All(ui.Confirmed, challenge => Assert.Equal(expected, challenge.Sha256));
        Assert.Equal(2, ui.Confirmed.Count);
    }

    [Theory]
    [InlineData("", 22, "user")]
    [InlineData("aire.example.edu", 0, "user")]
    [InlineData("aire.example.edu", 65536, "user")]
    [InlineData("aire.example.edu", 22, "")]
    public async Task InvalidConnectionParametersNeverReachTransport(string host, int port, string username)
    {
        var factory = new FakeFactory();
        await using var session = new SshCommandSession(factory);
        await Assert.ThrowsAsync<ArgumentException>(() => session.ConnectAsync(new(new(host, port, username)), new FakeUi(), new MemoryKeys()));
        Assert.Empty(factory.Wires);
    }

    private sealed class FakeUi : IAuthenticationUI
    {
        public bool Trust { get; init; } = true;
        public bool CancelPrompts { get; init; }
        public List<AuthenticationChallenge> Challenges { get; } = [];
        public List<HostKeyChallenge> Confirmed { get; } = [];
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken)
        {
            Challenges.Add(challenge);
            return Task.FromResult<IReadOnlyList<string>?>(CancelPrompts ? null : challenge.Prompts.Select((_, i) => $"answer-{i}").ToArray());
        }
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken)
        {
            Confirmed.Add(challenge);
            return Task.FromResult(Trust);
        }
    }

    private sealed class MemoryKeys : IHostKeyStore
    {
        private readonly Dictionary<string, string> _keys = [];
        private static string Key(SshEndpoint endpoint) => $"{endpoint.Host.ToLowerInvariant()}:{endpoint.Port}";
        public string? Get(SshEndpoint endpoint) => _keys.GetValueOrDefault(Key(endpoint));
        public void Save(SshEndpoint endpoint, string sha256) => _keys[Key(endpoint)] = sha256;
    }

    private sealed class FakeFactory : ISshWireFactory
    {
        public bool FailTargetAuthentication { get; set; }
        public List<FakeWire> Wires { get; } = [];
        public ISshWire Create(SshEndpoint identity, string connectHost, int connectPort, IAuthenticationUI ui, IHostKeyStore keys)
        {
            var wire = new FakeWire(identity, connectHost, connectPort, ui, keys) { FailAuthentication = FailTargetAuthentication && identity == Target };
            Wires.Add(wire);
            return wire;
        }
    }

    private sealed class FakeForward : ILocalForward
    {
        public int Port => 43123;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeWire(SshEndpoint identity, string connectHost, int connectPort, IAuthenticationUI ui, IHostKeyStore keys) : ISshWire
    {
        public SshEndpoint Identity => identity;
        public ConnectionPasswordSharing Authentication => (ConnectionPasswordSharing)ui;
        public bool FailAuthentication { get; init; }
        public string ConnectHost => connectHost;
        public int ConnectPort => connectPort;
        public int ConnectCount { get; private set; }
        public bool Connected { get; set; }
        public bool Disposed { get; private set; }
        public bool IsConnected => Connected && !Disposed;
        public FakeForward? Tunnel { get; private set; }
        public int NextExitStatus { get; set; }
        public List<string> Commands { get; } = [];
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCount++;
            var bridge = new AuthenticationBridge(identity, ui, keys, cancellationToken);
            await bridge.VerifyHostKeyAsync("ssh-ed25519", [1, 2, 3, 4]);
            await bridge.PromptAsync("keyboard-interactive", "", [new("Password:", true)]);
            if (FailAuthentication) throw new SshAuthenticationException("Synthetic password rejection.");
            await bridge.PromptAsync("keyboard-interactive", "Choose a Duo option", [new("7. Duo push\n9. SMS", false)]);
            await bridge.PromptAsync("keyboard-interactive", "Approve on Duo Mobile", []);
            bridge.AuthenticationCompleted();
            Connected = true;
        }
        public ILocalForward Forward(SshEndpoint target) => Tunnel = new FakeForward();
        public Task<CommandResult> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            var exit = NextExitStatus;
            NextExitStatus = 0;
            return Task.FromResult(new CommandResult("sample", exit));
        }
        public void Dispose() { Disposed = true; Connected = false; }
    }
}
