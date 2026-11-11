using AirePeek.Core;
using AirePeek.Desktop.Infrastructure;

namespace AirePeek.Desktop.Tests;

public sealed class ClusterWorkspaceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "HPCPeekWorkspaceTests", Guid.NewGuid().ToString("N"));
    private static readonly SilentAuthentication Authentication = new();
    private static ConnectionProfile Profile(string id) => new(new($"{id}.example.test", 22, "same-hpc-account"));
    private static ClusterWorkspace Create(SettingsStore store, Dictionary<string, FakeSession> sessions, TestClock clock) =>
        new(store, id => sessions[id], action => action(), clock);
    private static Dictionary<string, FakeSession> Sessions() => new()
    {
        [ClusterCatalog.AireId] = new("L40S", 3),
        [ClusterCatalog.CalderId] = new("H200NVL", 8)
    };

    [Fact]
    public async Task SelectingAClusterNeverAuthenticatesAndConnectedSelectionRefreshesAfterCooldown()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        await using var workspace = Create(store, sessions, clock);
        await workspace.SelectAsync("calder"); await workspace.PollAsync();
        Assert.Equal("calder", workspace.SelectedClusterId);
        Assert.All(sessions.Values, session => Assert.Equal(0, session.ConnectCount));

        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        Assert.Equal(2, sessions["aire"].CommandCount);
        await workspace.SelectAsync("aire");
        Assert.Equal(2, sessions["aire"].CommandCount);
        clock.Advance(TimeSpan.FromSeconds(10)); await workspace.PollAsync();
        Assert.Equal(2, sessions["aire"].CommandCount);
        clock.Advance(TimeSpan.FromSeconds(5)); await workspace.PollAsync();
        Assert.Equal(4, sessions["aire"].CommandCount);
        Assert.Equal(1, sessions["aire"].ConnectCount);
        Assert.Equal(0, sessions["calder"].ConnectCount);
        Assert.Equal("aire", new SettingsStore(directory).Value.SelectedClusterId);
    }

    [Fact]
    public async Task OverlappingJobAndNodeIdsRemainIndependentAndCountsAggregate()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        await using var workspace = Create(store, sessions, clock);
        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        await workspace.ConnectAsync("calder", Profile("calder"), Authentication);
        Assert.Equal(2, workspace.RunningCount);
        Assert.Equal(2, workspace.QueuedCount);
        Assert.Equal("42", workspace["aire"].Monitor!.Jobs!.Jobs[0].Id);
        Assert.Equal("42", workspace["calder"].Monitor!.Jobs!.Jobs[0].Id);
        Assert.Equal("gpu001", Assert.Single(workspace["aire"].Monitor!.Gpus!.Nodes).Name);
        Assert.Equal("gpu001", Assert.Single(workspace["calder"].Monitor!.Gpus!.Nodes).Name);
        Assert.Equal(3, workspace["aire"].Monitor!.Gpus!.Summarize("L40S").Configured);
        Assert.Equal(8, workspace["calder"].Monitor!.Gpus!.Summarize("H200NVL").Configured);
        Assert.NotSame(workspace["aire"].Monitor, workspace["calder"].Monitor);
        Assert.Equal(Profile("aire"), store.GetCluster("aire").Connection);
        Assert.Equal(Profile("calder"), store.GetCluster("calder").Connection);
    }

    [Fact]
    public async Task SelectionDuringAnInFlightPollRetainsAFollowUpReadWithoutRelogin()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        await using var workspace = Create(store, sessions, clock);
        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        var session = sessions["aire"];
        session.HoldQueries = new(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Advance(TimeSpan.FromMinutes(5));
        var pending = workspace.PollAsync();
        await session.QueryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await workspace.SelectAsync("aire");
        Assert.Equal(4, session.CommandCount);
        session.HoldQueries.SetResult(); await pending;
        session.HoldQueries = null;
        await workspace.PollAsync();
        Assert.Equal(6, session.CommandCount);
        Assert.Equal(1, session.ConnectCount);
    }

    [Fact]
    public async Task OneClusterFailureCannotEraseOtherClusterSnapshotOrReconnectEitherSession()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        await using var workspace = Create(store, sessions, clock);
        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        await workspace.ConnectAsync("calder", Profile("calder"), Authentication);
        var aireSnapshot = workspace["aire"].Monitor!.Gpus;
        var calderTime = workspace["calder"].Monitor!.Gpus!.SampledAt;
        await sessions["aire"].DisconnectAsync();
        clock.Advance(TimeSpan.FromMinutes(5)); await workspace.PollAsync();
        Assert.Same(aireSnapshot, workspace["aire"].Monitor!.Gpus);
        Assert.True(workspace["aire"].Monitor!.IsDisconnected);
        Assert.True(workspace["calder"].IsConnected);
        Assert.True(workspace["calder"].Monitor!.Gpus!.SampledAt > calderTime);
        await workspace.SelectAsync("aire"); await workspace.RefreshAsync();
        Assert.Same(aireSnapshot, workspace.Selected.Monitor!.Gpus);
        Assert.All(sessions.Values, session => Assert.Equal(1, session.ConnectCount));
        Assert.Equal(2, sessions["aire"].CommandCount);
    }

    [Fact]
    public async Task GpuFailureIsIsolatedFromBothQueuesAndTheOtherCluster()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        await using var workspace = Create(store, sessions, clock);
        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        await workspace.ConnectAsync("calder", Profile("calder"), Authentication);
        var calderGpu = workspace["calder"].Monitor!.Gpus;
        sessions["calder"].FailGpu = true;
        clock.Advance(TimeSpan.FromMinutes(5)); await workspace.PollAsync();
        Assert.NotNull(workspace["calder"].Monitor!.GpuError);
        Assert.Same(calderGpu, workspace["calder"].Monitor!.Gpus);
        Assert.Null(workspace["calder"].Monitor!.JobsError);
        Assert.Null(workspace["aire"].Monitor!.GpuError);
        Assert.All(workspace.Entries, controller => Assert.True(controller.IsConnected));
        Assert.Equal(2, workspace.RunningCount);
    }

    [Fact]
    public async Task SameJobIdNotifiesOncePerClusterWithStructuredIdentity()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        await using var workspace = Create(store, sessions, clock);
        var notifications = new List<JobNotification>(); workspace.Notification += notifications.Add;
        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        await workspace.ConnectAsync("calder", Profile("calder"), Authentication);
        Assert.Empty(notifications);
        foreach (var session in sessions.Values) session.StartPending = true;
        clock.Advance(TimeSpan.FromMinutes(5)); await workspace.PollAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n => { Assert.Equal("43", n.JobId); Assert.Equal("RUNNING", n.State); Assert.Equal("next experiment", n.JobName); });
        Assert.Equal(new[] { "aire", "calder" }, notifications.Select(n => n.Cluster).Order().ToArray());
        clock.Advance(TimeSpan.FromMinutes(5)); await workspace.PollAsync();
        Assert.Equal(2, notifications.Count);
    }

    [Fact]
    public async Task TwoDemosUsePublishedHardwareShapeAndDoNotCreateSshSessions()
    {
        var store = new SettingsStore(directory); int created = 0;
        await using var workspace = new ClusterWorkspace(store, _ => { created++; throw new InvalidOperationException(); }, action => action());
        await workspace.StartDemoAsync(Authentication);
        Assert.True(workspace.IsDemo);
        Assert.Equal(0, created);
        Assert.Equal(4, workspace.RunningCount); Assert.Equal(2, workspace.QueuedCount);
        Assert.Equal(28, workspace["aire"].Monitor!.Gpus!.Nodes.Count);
        Assert.Equal(84, workspace["aire"].Monitor!.Gpus!.Summarize("L40S").Configured);
        Assert.Equal(7, workspace["calder"].Monitor!.Gpus!.Nodes.Count);
        Assert.Equal(56, workspace["calder"].Monitor!.Gpus!.Summarize("H200NVL").Configured);
        await workspace.SelectAsync("calder");
        Assert.Equal("H200NVL", workspace.Selected.Preferences.GpuModel);
        Assert.All(workspace.Entries, c => Assert.Equal(1, c.DemoConnectionCount));
    }

    [Fact]
    public async Task PreferenceChangesAndInvalidationApplyToBothConnections()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        await using var workspace = Create(store, sessions, clock);
        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        await workspace.ConnectAsync("calder", Profile("calder"), Authentication);
        store.Value.RefreshIntervalSeconds = 45; workspace.ApplyPreferences();
        Assert.All(workspace.Entries, c => Assert.Equal(TimeSpan.FromSeconds(45), c.Monitor!.RefreshInterval));
        await workspace.InvalidateAsync();
        Assert.All(workspace.Entries, c => { Assert.False(c.IsConnected); Assert.NotNull(c.Monitor!.Jobs); Assert.NotNull(c.Monitor.Gpus); });
        await workspace.SelectAsync("calder"); await workspace.PollAsync();
        Assert.All(sessions.Values, s => Assert.Equal(1, s.ConnectCount));
    }

    [Fact]
    public async Task FirstRealConnectClearsEveryDemoSnapshotWithoutLoggingIntoOtherCluster()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        store.GetCluster("calder").Connection = Profile("calder");
        await using var workspace = Create(store, sessions, clock);
        await workspace.StartDemoAsync(Authentication);
        Assert.Equal(4, workspace.RunningCount); Assert.True(workspace.IsDemo);

        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        Assert.False(workspace.IsDemo);
        Assert.Equal(1, workspace.RunningCount); Assert.Equal(1, workspace.QueuedCount);
        Assert.Null(workspace["calder"].Monitor); Assert.False(workspace["calder"].IsConnected);
        Assert.Equal(0, sessions["calder"].ConnectCount);
        Assert.Equal(1, sessions["aire"].ConnectCount);
        Assert.Equal(Profile("calder"), store.GetCluster("calder").Connection);
    }

    [Fact]
    public async Task QueuedNotificationsFromBothOldConnectionsAreDroppedWhenDemoStarts()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        var posted = new List<Action>(); var received = new List<JobNotification>();
        await using var workspace = new ClusterWorkspace(store, id => sessions[id], posted.Add, clock);
        workspace.Notification += received.Add;
        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        await workspace.ConnectAsync("calder", Profile("calder"), Authentication);
        foreach (var session in sessions.Values) session.StartPending = true;
        clock.Advance(TimeSpan.FromMinutes(5)); await workspace.PollAsync();
        Assert.NotEmpty(posted);
        await workspace.StartDemoAsync(Authentication);
        foreach (var action in posted.ToArray()) action();
        Assert.True(workspace.IsDemo);
        Assert.Empty(received);
        Assert.Equal(4, workspace.RunningCount);
    }

    [Fact]
    public async Task ReconnectedMonitorBindsNotificationsToItsNewConnectionGeneration()
    {
        var store = new SettingsStore(directory); var sessions = Sessions(); var clock = new TestClock();
        var received = new List<JobNotification>();
        await using var workspace = Create(store, sessions, clock); workspace.Notification += received.Add;
        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        await workspace.ConnectAsync("aire", Profile("aire"), Authentication);
        sessions["aire"].StartPending = true;
        clock.Advance(TimeSpan.FromMinutes(5)); await workspace.PollAsync();
        Assert.Equal("aire", Assert.Single(received).Cluster);
        Assert.Equal(2, sessions["aire"].ConnectCount);
    }

    [Fact]
    public async Task PreferenceWriteFailureDoesNotThrowFromSelectionOrStartAuthentication()
    {
        Directory.CreateDirectory(directory);
        var blockedDirectory = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blockedDirectory, "test fixture blocks Directory.CreateDirectory");
        var store = new SettingsStore(blockedDirectory); var sessions = Sessions(); var clock = new TestClock();
        await using var workspace = Create(store, sessions, clock);
        await workspace.SelectAsync("calder");
        Assert.Equal("calder", workspace.SelectedClusterId);
        Assert.Contains("could not be saved", workspace.Selected.Message);
        Assert.All(sessions.Values, s => Assert.Equal(0, s.ConnectCount));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }
    private sealed class FakeSession(string model, int cards) : ICommandSession
    {
        public bool IsConnected { get; private set; }
        public int ConnectCount, CommandCount;
        public bool StartPending, FailGpu;
        public TaskCompletionSource? HoldQueries;
        public TaskCompletionSource QueryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConnectAsync(ConnectionProfile profile, IAuthenticationUI ui, IHostKeyStore keys, CancellationToken ct = default)
        { IsConnected = true; ConnectCount++; return Task.CompletedTask; }
        public async Task<string> ExecuteAsync(string command, CancellationToken ct = default)
        {
            if (!IsConnected) throw new SessionDisconnectedException();
            CommandCount++;
            if (HoldQueries is { } pending) { QueryEntered.TrySetResult(); await pending.Task.WaitAsync(ct); }
            if (command == SlurmCommands.Queue)
                return $"42|{model} training|RUNNING|1:00|12:00:00|gpu001|None\n43|next experiment|{(StartPending ? "RUNNING" : "PENDING")}|0:00|12:00:00|gpu001|Resources";
            if (FailGpu) throw new RemoteCommandException("GPU query unavailable.");
            return $"NodeName=gpu001 State=MIXED CfgTRES=gres/gpu={cards},gres/gpu:{model}={cards} AllocTRES=gres/gpu=1,gres/gpu:{model}=1";
        }
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }
    private sealed class SilentAuthentication : IAuthenticationUI
    {
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken ct) => throw new InvalidOperationException("Simulation must not prompt.");
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken ct) => throw new InvalidOperationException("Simulation must not prompt.");
    }
    public void Dispose()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HPCPeekWorkspaceTests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target)) Directory.Delete(target, true);
    }
}
