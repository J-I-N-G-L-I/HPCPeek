using AirePeek.Core;

namespace AirePeek.Core.Tests;

public class MonitorServiceTests
{
    private const string Pending = "123_2|train|PENDING|0:00|12:00:00|(null)|Resources";
    private const string Running = "123_2|train|RUNNING|0:12|12:00:00|gpu01|None";
    private static async Task<(FakeSession Session, TestClock Clock, MonitorService Monitor)> Create(string queue = Pending)
    {
        var session = new FakeSession { Queue = queue };
        await session.ConnectAsync(new(new("test", 22, "user")), new AuthUI(), new HostKeys());
        var clock = new TestClock();
        var monitor = new MonitorService(session, clock);
        monitor.ResetAfterConnect();
        return (session, clock, monitor);
    }

    [Fact]
    public async Task EmptyQueueHasTimestampAndNoJobs()
    {
        var (session, clock, monitor) = await Create("");
        await monitor.RefreshAsync();
        Assert.NotNull(monitor.Jobs);
        Assert.Empty(monitor.Jobs.Jobs);
        Assert.Equal(clock.GetUtcNow(), monitor.Jobs.SampledAt);
        Assert.Null(monitor.JobsError);
        Assert.Equal(1, session.ConnectionCount);
    }

    [Fact]
    public async Task QueueAndGpuDefaultToTheSameFiveMinuteCadence()
    {
        var (session, clock, monitor) = await Create();
        Assert.Equal(TimeSpan.FromMinutes(5), monitor.RefreshInterval);
        await monitor.PollAsync();
        var gpuTime = monitor.Gpus!.SampledAt;
        clock.Advance(TimeSpan.FromSeconds(120));
        await monitor.PollAsync();
        Assert.Equal(1, session.QueueCount);
        Assert.Equal(1, session.NodeCount);
        Assert.Equal(gpuTime, monitor.Gpus.SampledAt);
        clock.Advance(TimeSpan.FromSeconds(180));
        await monitor.PollAsync();
        Assert.Equal(2, session.QueueCount);
        Assert.Equal(2, session.NodeCount);
        Assert.Equal(clock.GetUtcNow(), monitor.Jobs!.SampledAt);
        Assert.Equal(clock.GetUtcNow(), monitor.Gpus.SampledAt);
    }

    [Fact]
    public async Task ShortenedIntervalUsesLastAttemptAndBecomesDueWithoutRelogin()
    {
        var (session, clock, monitor) = await Create();
        monitor.ConfigureRefreshInterval(TimeSpan.FromMinutes(15));
        await monitor.PollAsync();
        var jobs = monitor.Jobs; var gpus = monitor.Gpus;
        clock.Advance(TimeSpan.FromMinutes(6));
        await monitor.PollAsync();
        Assert.Equal(1, session.QueueCount);
        monitor.ConfigureRefreshInterval(TimeSpan.FromMinutes(5));
        Assert.Same(jobs, monitor.Jobs); Assert.Same(gpus, monitor.Gpus);
        Assert.Equal(1, session.QueueCount); // Saving preferences itself never queries or authenticates.
        await monitor.PollAsync();
        Assert.Equal(2, session.QueueCount); Assert.Equal(2, session.NodeCount);
        Assert.Equal(1, session.ConnectionCount);
        clock.Advance(TimeSpan.FromMinutes(4)); await monitor.PollAsync();
        Assert.Equal(2, session.QueueCount);
        clock.Advance(TimeSpan.FromMinutes(1)); await monitor.PollAsync();
        Assert.Equal(3, session.QueueCount); Assert.Equal(3, session.NodeCount);
    }

    [Fact]
    public async Task LengthenedIntervalDefersBothQueriesAndPreservesIndependentFailureTimestamps()
    {
        var (session, clock, monitor) = await Create();
        await monitor.PollAsync();
        var gpuTime = monitor.Gpus!.SampledAt;
        clock.Advance(TimeSpan.FromMinutes(4));
        monitor.ConfigureRefreshInterval(TimeSpan.FromMinutes(30));
        clock.Advance(TimeSpan.FromMinutes(1)); await monitor.PollAsync();
        Assert.Equal(1, session.QueueCount); Assert.Equal(1, session.NodeCount);
        clock.Advance(TimeSpan.FromMinutes(25));
        session.GpuFailure = new RemoteCommandException("permission denied");
        await monitor.PollAsync();
        Assert.Equal(clock.GetUtcNow(), monitor.Jobs!.SampledAt);
        Assert.Equal(gpuTime, monitor.Gpus.SampledAt);
        Assert.NotNull(monitor.GpuError); Assert.Null(monitor.JobsError);
        Assert.Equal(2, session.QueueCount); Assert.Equal(2, session.NodeCount);
    }

    [Fact]
    public async Task ConfiguredIntervalControlsFreshnessAndDoesNotReconnectWhileDisconnected()
    {
        var (session, clock, monitor) = await Create();
        await monitor.PollAsync();
        clock.Advance(TimeSpan.FromMinutes(12));
        Assert.Equal(FreshnessState.Stale, SampleFreshness.Evaluate(monitor.Jobs!.SampledAt, monitor.RefreshInterval, clock.GetUtcNow()).State);
        monitor.ConfigureRefreshInterval(TimeSpan.FromMinutes(15));
        Assert.Equal(FreshnessState.Fresh, SampleFreshness.Evaluate(monitor.Jobs.SampledAt, monitor.RefreshInterval, clock.GetUtcNow()).State);
        monitor.MarkDisconnected();
        monitor.ConfigureRefreshInterval(TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromMinutes(60)); await monitor.PollAsync();
        Assert.True(monitor.IsDisconnected); Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(1, session.QueueCount); Assert.Equal(1, session.NodeCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(86401)]
    public async Task InvalidRefreshIntervalsCannotReplaceTheCurrentCadence(int seconds)
    {
        var (session, _, monitor) = await Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.ConfigureRefreshInterval(TimeSpan.FromSeconds(seconds)));
        Assert.Equal(TimeSpan.FromMinutes(5), monitor.RefreshInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonitorService(session, refreshInterval: TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(900)]
    [InlineData(1800)]
    [InlineData(3600)]
    [InlineData(86400)]
    public async Task ConfiguredIntervalsTriggerBothRealQueriesAtTheirDeadline(int seconds)
    {
        var (session, clock, monitor) = await Create();
        monitor.ConfigureRefreshInterval(TimeSpan.FromSeconds(seconds)); await monitor.PollAsync();
        clock.Advance(TimeSpan.FromSeconds(seconds - 1)); await monitor.PollAsync();
        Assert.Equal(1, session.QueueCount); Assert.Equal(1, session.NodeCount);
        clock.Advance(TimeSpan.FromSeconds(1)); await monitor.PollAsync();
        Assert.Equal(2, session.QueueCount); Assert.Equal(2, session.NodeCount); Assert.Equal(1, session.ConnectionCount);
    }

    [Fact]
    public async Task ManualRefreshHasCooldownAndNeverAuthenticatesAgain()
    {
        var (session, clock, monitor) = await Create();
        await monitor.RefreshAsync();
        await monitor.RefreshAsync();
        Assert.Equal(1, session.QueueCount);
        Assert.Equal(1, session.NodeCount);
        Assert.True(monitor.RefreshCooldownRemaining > TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.RefreshAsync();
        Assert.Equal(2, session.QueueCount);
        Assert.Equal(1, session.ConnectionCount);
    }

    [Fact]
    public async Task SameCategoryDoesNotOverlap()
    {
        var (session, clock, monitor) = await Create();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeforeExecute = async _ => await blocker.Task;
        var first = monitor.PollAsync();
        clock.Advance(TimeSpan.FromMinutes(6));
        await monitor.PollAsync();
        Assert.Equal(1, session.QueueCount);
        Assert.Equal(1, session.NodeCount);
        blocker.SetResult();
        await first;
    }

    [Fact]
    public async Task GpuFailureLeavesPersonalQueueUsable()
    {
        var (session, clock, monitor) = await Create();
        await monitor.RefreshAsync();
        var oldGpu = monitor.Gpus;
        clock.Advance(TimeSpan.FromSeconds(15));
        session.Queue = Running;
        session.GpuFailure = new RemoteCommandException("permission denied");
        await monitor.RefreshAsync();
        Assert.True(Assert.Single(monitor.Jobs!.Jobs).IsRunning);
        Assert.Same(oldGpu, monitor.Gpus);
        Assert.NotNull(monitor.GpuError);
        Assert.Null(monitor.JobsError);
        Assert.False(monitor.IsDisconnected);
    }

    [Fact]
    public async Task DisconnectRetainsLastGoodSnapshotAndDoesNotReconnect()
    {
        var (session, clock, monitor) = await Create();
        await monitor.RefreshAsync();
        var jobs = monitor.Jobs; var gpu = monitor.Gpus;
        await session.DisconnectAsync();
        clock.Advance(TimeSpan.FromMinutes(6));
        await monitor.PollAsync();
        await monitor.RefreshAsync();
        Assert.True(monitor.IsDisconnected);
        Assert.Same(jobs, monitor.Jobs);
        Assert.Same(gpu, monitor.Gpus);
        Assert.Equal(1, session.ConnectionCount);
        Assert.Equal(1, session.QueueCount);
    }

    [Fact]
    public async Task PendingToRunningNotifiesOnceAndFirstConnectionIsSilent()
    {
        var (session, clock, monitor) = await Create();
        var notifications = new List<JobNotification>(); monitor.Notification += notifications.Add;
        await monitor.RefreshAsync();
        Assert.Empty(notifications);
        session.Queue = Running; clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.RefreshAsync();
        Assert.Single(notifications);
        clock.Advance(TimeSpan.FromSeconds(15)); await monitor.RefreshAsync();
        Assert.Single(notifications);
    }

    [Fact]
    public async Task ReconnectionEstablishesSilentBaseline()
    {
        var (session, clock, monitor) = await Create();
        var notifications = new List<JobNotification>(); monitor.Notification += notifications.Add;
        await monitor.RefreshAsync();
        monitor.MarkDisconnected(); session.Queue = Running;
        await session.ConnectAsync(new(new("test", 22, "user")), new AuthUI(), new HostKeys());
        monitor.ResetAfterConnect();
        await monitor.RefreshAsync();
        Assert.Empty(notifications);
        Assert.True(Assert.Single(monitor.Jobs!.Jobs).IsRunning);
        Assert.Empty(monitor.Jobs.Departed);
    }

    [Fact]
    public async Task ReappearingJobIsRemovedFromDepartedEvenOnRecoveryBaseline()
    {
        var (session, clock, monitor) = await Create(Running);
        await monitor.RefreshAsync();
        session.Queue = ""; clock.Advance(TimeSpan.FromSeconds(15)); await monitor.RefreshAsync();
        Assert.Single(monitor.Jobs!.Departed);
        monitor.MarkDisconnected(); session.Queue = Pending;
        await session.ConnectAsync(new(new("test", 22, "user")), new AuthUI(), new HostKeys());
        monitor.ResetAfterConnect(); await monitor.RefreshAsync();
        Assert.Single(monitor.Jobs.Jobs);
        Assert.Empty(monitor.Jobs.Departed);
    }

    [Fact]
    public async Task AccountingRetriesRotateSoLargeArraysCannotStarveLaterTasks()
    {
        var queue = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"123_{i}|train|RUNNING|0:12|12:00:00|gpu01|None"));
        var (session, clock, monitor) = await Create(queue);
        await monitor.RefreshAsync();
        session.Queue = ""; clock.Advance(TimeSpan.FromSeconds(15)); await monitor.RefreshAsync();
        Assert.DoesNotContain("123_200", session.LastAccountingCommand);
        clock.Advance(TimeSpan.FromSeconds(15)); await monitor.ConfirmDepartedAsync();
        Assert.Contains("123_200", session.LastAccountingCommand);
    }

    [Fact]
    public async Task DepartedJobStaysUnconfirmedThenAccountingNotifiesOnce()
    {
        var (session, clock, monitor) = await Create(Running);
        var notifications = new List<JobNotification>(); monitor.Notification += notifications.Add;
        await monitor.RefreshAsync();
        session.Queue = ""; clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.RefreshAsync();
        Assert.Equal("Left queue; result pending", Assert.Single(monitor.Jobs!.Departed).StateLabel);
        Assert.Empty(notifications);
        session.Accounting = "123_2|TIMEOUT|0:15";
        clock.Advance(TimeSpan.FromSeconds(15)); await monitor.ConfirmDepartedAsync();
        Assert.Equal("Timed out", Assert.Single(monitor.Jobs.Departed).StateLabel);
        Assert.Equal("Experiment timed out", Assert.Single(notifications).Title);
        clock.Advance(TimeSpan.FromSeconds(15)); await monitor.ConfirmDepartedAsync();
        Assert.Single(notifications);
        Assert.Equal(1, session.ConnectionCount);
    }

    [Fact]
    public async Task AccountingFailureDoesNotEraseQueueOrClaimCompletion()
    {
        var (session, clock, monitor) = await Create(Running);
        await monitor.RefreshAsync();
        session.Queue = ""; session.AccountingFailure = new RemoteCommandException("disabled");
        clock.Advance(TimeSpan.FromSeconds(15)); await monitor.RefreshAsync();
        Assert.Empty(monitor.Jobs!.Jobs);
        Assert.Equal("UNKNOWN", Assert.Single(monitor.Jobs.Departed).State);
        Assert.NotNull(monitor.HistoryError);
        Assert.Null(monitor.JobsError);
    }

    [Fact]
    public async Task ResultsFromConnectionBeforeDisconnectAreDiscarded()
    {
        var (session, clock, monitor) = await Create();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeforeExecute = async _ => await blocker.Task;
        var pending = monitor.RefreshAsync();
        monitor.MarkDisconnected();
        blocker.SetResult(); await pending;
        Assert.Null(monitor.Jobs);
        Assert.Null(monitor.Gpus);
    }

    [Fact]
    public async Task OldConnectionFailureCannotMarkNewConnectionDisconnected()
    {
        var (session, clock, monitor) = await Create();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeforeExecute = async _ => { await blocker.Task; throw new SessionDisconnectedException(); };
        var pending = monitor.RefreshAsync();
        monitor.MarkDisconnected();
        monitor.ResetAfterConnect();
        blocker.SetResult(); await pending;
        Assert.False(monitor.IsDisconnected);
        Assert.Null(monitor.JobsError);
        Assert.Null(monitor.GpuError);
    }

    [Fact]
    public async Task DemoRunsSameMonitorParserAndOfflineScenarios()
    {
        var session = new DemoSession();
        await session.ConnectAsync(new(new("demo", 22, "demo")), new AuthUI(), new HostKeys());
        var clock = new TestClock(); var monitor = new MonitorService(session, clock);
        monitor.ResetAfterConnect(); await monitor.RefreshAsync();
        Assert.Equal(2, monitor.Jobs!.Jobs.Count(j => j.IsRunning));
        Assert.Single(monitor.Jobs.Jobs, j => j.IsPending);
        Assert.Equal(5, monitor.Gpus!.Summarize("L40S").Unallocated);
        session.Scenario = DemoScenario.Empty; clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.RefreshAsync(); Assert.Empty(monitor.Jobs.Jobs);
        session.Scenario = DemoScenario.Partial; clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.RefreshAsync(); Assert.True(monitor.Gpus.IsPartial);
        var snapshot = monitor.Gpus;
        session.Scenario = DemoScenario.Disconnected; clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.RefreshAsync(); Assert.True(monitor.IsDisconnected); Assert.Same(snapshot, monitor.Gpus);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class FakeSession : ICommandSession
    {
        public bool IsConnected { get; private set; }
        public int ConnectionCount, QueueCount, NodeCount;
        public string Queue = "", Accounting = "", LastAccountingCommand = "";
        public Exception? GpuFailure, AccountingFailure;
        public Func<string, Task>? BeforeExecute;
        public Task ConnectAsync(ConnectionProfile profile, IAuthenticationUI authenticationUI, IHostKeyStore hostKeys, CancellationToken cancellationToken = default)
        { IsConnected = true; ConnectionCount++; return Task.CompletedTask; }
        public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        {
            if (!IsConnected) throw new SessionDisconnectedException();
            if (command == SlurmCommands.Queue) QueueCount++;
            if (command == SlurmCommands.Nodes) NodeCount++;
            if (BeforeExecute is not null) await BeforeExecute(command);
            if (command == SlurmCommands.Queue) return Queue;
            if (command == SlurmCommands.Nodes) return GpuFailure is not null ? throw GpuFailure : DemoSession.NormalNodes;
            LastAccountingCommand = command;
            return AccountingFailure is not null ? throw AccountingFailure : Accounting;
        }
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }

    private sealed class AuthUI : IAuthenticationUI
    {
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>([]);
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class HostKeys : IHostKeyStore
    {
        public string? Get(SshEndpoint endpoint) => null;
        public void Save(SshEndpoint endpoint, string sha256) { }
    }
}
