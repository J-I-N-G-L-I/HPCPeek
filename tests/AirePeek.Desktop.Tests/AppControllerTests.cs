using AirePeek.Core;
using AirePeek.Desktop.Infrastructure;
using AirePeek.Ssh;

namespace AirePeek.Desktop.Tests;

public sealed class AppControllerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AirePeekControllerTests", Guid.NewGuid().ToString("N"));
    private static readonly ConnectionProfile Profile = new(new("aire.example.test", 22, "test-user"));
    private static readonly SilentAuthentication Authentication = new();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PasswordStorageWarningRetainsTheAuthenticatedSessionAndItsSamples()
    {
        const string warning = "Signed in, but this device could not remember the password. No password was saved in portable files.";
        var store = new SettingsStore(directory);
        var wire = new ControlledSession { PasswordStorageWarning = warning };
        await using var controller = new AppController(store, () => wire, action => action());
        await controller.ConnectAsync(Profile with { RememberPassword = true }, Authentication);

        Assert.True(controller.IsConnected);
        Assert.Equal(warning, controller.Message);
        Assert.NotNull(controller.Monitor!.Jobs);
        Assert.NotNull(controller.Monitor.Gpus);
        Assert.True(new SettingsStore(directory).Value.Connection!.RememberPassword);

        controller.Monitor.ResetAfterConnect();
        await controller.RefreshAsync();
        Assert.True(controller.IsConnected);
        Assert.Equal(warning, controller.Message);
        Assert.Equal(1, wire.ConnectCount);
        Assert.Equal(4, wire.CommandCount);
    }

    [Fact]
    public async Task RejectedSavedPasswordPersistsManualChoiceAndRequiresExplicitReconnect()
    {
        var store = new SettingsStore(directory);
        var wire = new ControlledSession { RejectSavedPassword = true };
        await using var controller = new AppController(store, () => wire, action => action());
        await controller.ConnectAsync(Profile with { RememberPassword = true, ReuseGatewayPassword = true }, Authentication);

        Assert.False(controller.IsConnected);
        Assert.True(controller.Monitor!.IsDisconnected);
        Assert.False(controller.Preferences.Connection!.RememberPassword);
        Assert.False(controller.Preferences.Connection.ReuseGatewayPassword);
        Assert.False(new SettingsStore(directory).Value.Connection!.RememberPassword);
        Assert.Contains("This connection", controller.Message);

        await controller.PollAsync();
        await controller.RefreshAsync();
        await controller.RequestRefreshAsync();
        Assert.Equal(1, wire.ConnectCount);
        Assert.Equal(0, wire.CommandCount);

        wire.RejectSavedPassword = false;
        await controller.ConnectAsync(controller.Preferences.Connection, Authentication);
        Assert.True(controller.IsConnected);
        Assert.False(wire.LastConnectionProfile!.RememberPassword);
        Assert.Equal(2, wire.ConnectCount);
        Assert.Equal(2, wire.CommandCount);
    }

    [Fact]
    public async Task AdditionalVerificationFailurePreservesPasswordPreferencesAndAllowsManualRetry()
    {
        var store = new SettingsStore(directory);
        var wire = new ControlledSession { FailAdditionalVerification = true };
        await using var controller = new AppController(store, () => wire, action => action());
        var profile = Profile with { RememberPassword = true, ReuseGatewayPassword = true };
        await controller.ConnectAsync(profile, Authentication);
        Assert.False(controller.IsConnected);
        Assert.Equal(profile, new SettingsStore(directory).Value.Connection);
        Assert.Contains(profile.Target.Host, controller.Message);
        Assert.Contains("Additional verification", controller.Message);
        await controller.RefreshAsync(); await controller.PollAsync();
        Assert.Equal(1, wire.ConnectCount);
        wire.FailAdditionalVerification = false;
        await controller.ConnectAsync(controller.Preferences.Connection!, Authentication);
        Assert.True(controller.IsConnected);
        Assert.Equal(profile, wire.LastConnectionProfile);
        Assert.Equal(2, wire.ConnectCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticationFailureSurfacesAnUnremovedSavedPassword(bool shared)
    {
        const string warning = "The saved password could not be removed from the device password store. Use Forget saved passwords before retrying.";
        var wire = new ControlledSession { RejectSavedPassword = !shared, RejectSharedPassword = shared, PasswordStorageWarning = warning };
        await using var controller = Create(wire);
        await controller.ConnectAsync(Profile with { RememberPassword = true, ReuseGatewayPassword = shared }, Authentication);
        Assert.False(controller.IsConnected);
        Assert.Contains(warning, controller.Message);
        Assert.Contains(shared ? "separate passwords" : "This connection", controller.Message);
        await controller.RefreshAsync();
        Assert.Equal(1, wire.ConnectCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PasswordStorageChoiceRoundtripsIndependentlyForEachCluster(bool rememberAire)
    {
        var store = new SettingsStore(directory);
        var gateway = new SshEndpoint("rash.example.test", 22, "test-user");
        var aire = Profile with { Gateway = gateway, ReuseGatewayPassword = true, RememberPassword = rememberAire };
        var calder = new ConnectionProfile(new("calder.example.test", 2222, "other-user"), null, false, !rememberAire);
        store.GetCluster(ClusterCatalog.AireId).Connection = aire;
        store.GetCluster(ClusterCatalog.CalderId).Connection = calder;
        store.Save();

        var restored = new SettingsStore(directory);
        Assert.Equal(aire, restored.GetCluster(ClusterCatalog.AireId).Connection);
        Assert.Equal(calder, restored.GetCluster(ClusterCatalog.CalderId).Connection);
    }

    [Fact]
    public void LegacySharedPasswordPreferenceDoesNotOptIntoPersistentStorage()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), """
            {"Connection":{"Target":{"Host":"aire.example.test","Port":22,"Username":"test-user"},"ReuseGatewayPassword":true}}
            """);
        var restored = new SettingsStore(directory);
        Assert.True(restored.Value.Connection!.ReuseGatewayPassword);
        Assert.False(restored.Value.Connection.RememberPassword);
    }

    [Fact]
    public async Task RefreshPreferencesApplyToExistingSessionAndFutureConnectionsWithoutRelogin()
    {
        var store = new SettingsStore(directory); store.Value.RefreshIntervalSeconds = 900;
        var wire = new ControlledSession();
        await using var controller = new AppController(store, () => wire, action => action());
        await controller.ConnectAsync(Profile, Authentication);
        var monitor = controller.Monitor!; var jobs = monitor.Jobs; var gpus = monitor.Gpus;
        Assert.Equal(TimeSpan.FromMinutes(15), monitor.RefreshInterval);

        store.Value.RefreshIntervalSeconds = 30; store.Save(); controller.ApplyPreferences();
        Assert.Same(monitor, controller.Monitor); Assert.Same(jobs, monitor.Jobs); Assert.Same(gpus, monitor.Gpus);
        Assert.Equal(TimeSpan.FromSeconds(30), monitor.RefreshInterval);
        Assert.Equal(1, wire.ConnectCount); Assert.Equal(2, wire.CommandCount);
        Assert.True(controller.IsConnected);

        await controller.InvalidateAsync();
        store.Value.RefreshIntervalSeconds = 3600; controller.ApplyPreferences(); await controller.PollAsync();
        Assert.True(monitor.IsDisconnected); Assert.Equal(TimeSpan.FromMinutes(60), monitor.RefreshInterval);
        Assert.Equal(1, wire.ConnectCount); Assert.Equal(2, wire.CommandCount);
    }

    [Fact]
    public async Task DesktopDemoStartsWithTheSuppliedTwentyEightNodeCluster()
    {
        await using var controller = Create(new ControlledSession());
        await controller.StartDemoAsync(Authentication);

        Assert.True(controller.IsDemo);
        Assert.Equal(28, controller.Monitor!.Gpus!.Nodes.Count);
        var summary = controller.Monitor.Gpus.Summarize("L40S", 2);
        Assert.Equal(2, summary.Unallocated);
        Assert.Equal(2, summary.MaxPerNode);
        Assert.Equal("gpu020", Assert.Single(summary.Candidates).NodeName);
        Assert.Equal(1, controller.DemoConnectionCount);
    }

    [Fact]
    public async Task InvalidationCannotOutliveItsSessionAndOverwriteANewDemo()
    {
        var wire = new ControlledSession();
        await using var controller = Create(wire);
        await controller.ConnectAsync(Profile, Authentication);
        var snapshot = controller.Monitor!.Jobs;
        wire.HoldDisconnect = new();

        var invalidation = controller.InvalidateAsync();
        await wire.DisconnectEntered.Task.WaitAsync(Timeout);
        await controller.StartDemoAsync(Authentication);
        Assert.False(controller.IsDemo); // Session replacement waits until invalidation has released ownership.
        Assert.Same(snapshot, controller.Monitor.Jobs);
        Assert.True(controller.Monitor.IsDisconnected);

        wire.HoldDisconnect.SetResult();
        await invalidation.WaitAsync(Timeout);
        await controller.StartDemoAsync(Authentication);
        Assert.True(controller.IsDemo);
        Assert.True(controller.IsConnected);
        Assert.Null(controller.Message);
        Assert.Equal(1, wire.DisconnectCount);
    }

    [Fact]
    public async Task LateAuthenticationSuccessCannotUndoAnInvalidation()
    {
        var wire = new ControlledSession { HoldConnect = new() };
        await using var controller = Create(wire);
        var connect = controller.ConnectAsync(Profile, Authentication);
        await wire.ConnectEntered.Task.WaitAsync(Timeout);

        var invalidation = controller.InvalidateAsync();
        wire.HoldConnect.SetResult(); // Deliberately simulates a transport that completes despite cancellation.
        await Task.WhenAll(connect, invalidation).WaitAsync(Timeout);

        Assert.False(controller.IsConnected);
        Assert.True(controller.Monitor!.IsDisconnected);
        Assert.Null(controller.Monitor.Jobs);
        Assert.Null(controller.Monitor.Gpus);
        Assert.Equal(0, wire.CommandCount);

        wire.HoldConnect = null;
        await controller.ConnectAsync(Profile, Authentication);
        Assert.True(controller.IsConnected);
        Assert.Equal(2, wire.ConnectCount);
        Assert.Null(controller.Message);
    }

    [Fact]
    public async Task DisposeIsSharedAndPreventsEveryNewActionWhileCleanupIsPending()
    {
        var wire = new ControlledSession();
        var controller = Create(wire);
        await controller.ConnectAsync(Profile, Authentication);
        int commandsBeforeDispose = wire.CommandCount;
        wire.HoldDispose = new();

        var first = controller.DisposeAsync().AsTask();
        await wire.DisposeEntered.Task.WaitAsync(Timeout);
        var second = controller.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await controller.PollAsync();
        await controller.RefreshAsync();
        await controller.ConnectAsync(Profile, Authentication);
        await controller.StartDemoAsync(Authentication);
        await controller.InvalidateAsync();
        await controller.AdvanceDemoAsync();
        Assert.False(controller.IsConnected);
        Assert.False(controller.IsDemo);
        Assert.Equal(1, wire.ConnectCount);
        Assert.Equal(commandsBeforeDispose, wire.CommandCount);
        Assert.Equal(1, wire.DisposeCount);

        wire.HoldDispose.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        await controller.DisposeAsync();
        await controller.PollAsync();
        await controller.RefreshAsync();
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightInitialSampleAndKeepsItFromPublishing()
    {
        var wire = new ControlledSession { HoldQueries = true };
        var controller = Create(wire);
        var connect = controller.ConnectAsync(Profile, Authentication);
        await wire.QueryEntered.Task.WaitAsync(Timeout);
        await controller.DisposeAsync().AsTask().WaitAsync(Timeout);
        await connect.WaitAsync(Timeout);

        Assert.False(controller.IsConnected);
        Assert.True(controller.Monitor!.IsDisconnected);
        Assert.Null(controller.Monitor.Jobs);
        Assert.Null(controller.Monitor.Gpus);
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task CancellingDuringInitialSamplingClosesTheConnectionAndRequiresExplicitReconnect()
    {
        var wire = new ControlledSession { HoldQueries = true };
        await using var controller = Create(wire);
        var connect = controller.ConnectAsync(Profile, Authentication);
        await wire.QueryEntered.Task.WaitAsync(Timeout);
        controller.CancelConnection();
        await connect.WaitAsync(Timeout);

        int commandsAfterCancel = wire.CommandCount;
        await controller.PollAsync();
        await controller.RefreshAsync();
        Assert.False(controller.IsConnected);
        Assert.True(controller.Monitor!.IsDisconnected);
        Assert.Equal(1, wire.ConnectCount);
        Assert.Equal(1, wire.DisconnectCount);
        Assert.Equal(commandsAfterCancel, wire.CommandCount);
    }

    [Fact]
    public async Task QueuedUiCallbacksAreSuppressedAfterDisposal()
    {
        var callbacks = new List<Action>();
        var wire = new ControlledSession();
        var controller = new AppController(new SettingsStore(directory), () => wire, callbacks.Add);
        int updates = 0;
        controller.Changed += () => updates++;
        await controller.ConnectAsync(Profile, Authentication);
        Assert.NotEmpty(callbacks);
        await controller.DisposeAsync();

        foreach (var callback in callbacks) callback();
        controller.ReportAuthentication("Late server status");
        Assert.Equal(0, updates);
        Assert.Null(controller.Message);
    }

    [Fact]
    public async Task FailedReconnectRetainsTheSameMonitorAndLastSuccessfulSnapshots()
    {
        var wire = new ControlledSession();
        await using var controller = Create(wire);
        await controller.ConnectAsync(Profile, Authentication);
        var monitor = controller.Monitor!;
        var jobs = monitor.Jobs;
        var gpus = monitor.Gpus;
        wire.FailConnect = true;

        await controller.ConnectAsync(Profile, Authentication);
        await controller.PollAsync();
        await controller.RefreshAsync();

        Assert.Same(monitor, controller.Monitor);
        Assert.Same(jobs, monitor.Jobs);
        Assert.Same(gpus, monitor.Gpus);
        Assert.True(monitor.IsDisconnected);
        Assert.False(controller.IsConnected);
        Assert.Equal(2, wire.ConnectCount);
        Assert.Equal(2, wire.CommandCount);
    }

    private AppController Create(ControlledSession wire) => new(new SettingsStore(directory), () => wire, action => action());

    private sealed class ControlledSession : ICommandSession, IPasswordStorageStatus
    {
        public bool IsConnected { get; private set; }
        public string? PasswordStorageWarning { get; set; }
        public ConnectionProfile? LastConnectionProfile { get; private set; }
        public int ConnectCount, CommandCount, DisconnectCount, DisposeCount;
        public TaskCompletionSource? HoldConnect, HoldDisconnect, HoldDispose;
        public bool HoldQueries, FailConnect, RejectSavedPassword, RejectSharedPassword, FailAdditionalVerification;
        public TaskCompletionSource ConnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisconnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource QueryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ConnectAsync(ConnectionProfile profile, IAuthenticationUI ui, IHostKeyStore keys, CancellationToken ct = default)
        {
            ConnectCount++; ConnectEntered.TrySetResult();
            LastConnectionProfile = profile;
            if (HoldConnect is { } hold) await hold.Task;
            if (RejectSavedPassword) { IsConnected = false; throw new SavedPasswordAuthenticationException(); }
            if (RejectSharedPassword) { IsConnected = false; throw new SharedPasswordAuthenticationException(); }
            if (FailAdditionalVerification) { IsConnected = false; throw new AdditionalAuthenticationException(profile.Target); }
            if (FailConnect) { IsConnected = false; throw new SessionDisconnectedException(); }
            IsConnected = true;
        }
        public async Task<string> ExecuteAsync(string command, CancellationToken ct = default)
        {
            CommandCount++; QueryEntered.TrySetResult();
            if (HoldQueries) await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
            return command == SlurmCommands.Queue ? "42|test experiment|RUNNING|1:00|1:00:00|node1|None"
                : "NodeName=node1 State=MIXED CfgTRES=cpu=8,gres/gpu=4,gres/gpu:l40s=4 AllocTRES=cpu=2,gres/gpu=2,gres/gpu:l40s=2";
        }
        public async Task DisconnectAsync()
        {
            DisconnectCount++; DisconnectEntered.TrySetResult();
            if (HoldDisconnect is { } hold) await hold.Task;
            IsConnected = false;
        }
        public async ValueTask DisposeAsync()
        {
            DisposeCount++; DisposeEntered.TrySetResult();
            if (HoldDispose is { } hold) await hold.Task;
            IsConnected = false;
        }
    }
    private sealed class SilentAuthentication : IAuthenticationUI
    {
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken ct) => throw new InvalidOperationException("The controller test must not prompt.");
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken ct) => throw new InvalidOperationException("The controller test must not prompt.");
    }
    public void Dispose()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AirePeekControllerTests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
