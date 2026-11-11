namespace AirePeek.Core;

/// <summary>Read-only sampling. It deliberately has no authority to authenticate or reconnect.</summary>
public sealed class MonitorService(ICommandSession session, TimeProvider? timeProvider = null, TimeSpan? refreshInterval = null)
{
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaximumRefreshInterval = TimeSpan.FromHours(24);
    // Defaults retained for callers that display a sample before a monitor exists.
    public static readonly TimeSpan QueueInterval = DefaultRefreshInterval;
    public static readonly TimeSpan GpuInterval = DefaultRefreshInterval;
    public static readonly TimeSpan ManualCooldown = TimeSpan.FromSeconds(15);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _jobsGate = new(1, 1), _gpuGate = new(1, 1), _historyGate = new(1, 1);
    private readonly Dictionary<string, DepartedJob> _departed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private Dictionary<string, SlurmJob> _previous = new(StringComparer.Ordinal);
    private DateTimeOffset _lastJobsAttempt = DateTimeOffset.MinValue, _lastGpuAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastManual = DateTimeOffset.MinValue, _lastHistoryAttempt = DateTimeOffset.MinValue;
    private bool _hasBaseline;
    private int _generation;
    private int _historyOffset;
    private readonly object _sync = new();
    private long _refreshIntervalTicks = ValidateInterval(refreshInterval ?? DefaultRefreshInterval).Ticks;

    public JobSnapshot? Jobs { get; private set; }
    public GpuSnapshot? Gpus { get; private set; }
    public string? JobsError { get; private set; }
    public string? GpuError { get; private set; }
    public string? HistoryError { get; private set; }
    public bool IsDisconnected { get; private set; } = !session.IsConnected;
    public TimeSpan RefreshInterval => TimeSpan.FromTicks(Interlocked.Read(ref _refreshIntervalTicks));
    public TimeSpan RefreshCooldownRemaining => TimeSpan.FromTicks(Math.Max(0, (ManualCooldown - (_clock.GetUtcNow() - _lastManual)).Ticks));
    public event Action? Updated;
    public event Action<JobNotification>? Notification;

    /// <summary>Changes both sampling deadlines relative to their last attempts. Never authenticates or discards a snapshot.</summary>
    public void ConfigureRefreshInterval(TimeSpan interval)
    {
        var ticks = ValidateInterval(interval).Ticks;
        if (Interlocked.Exchange(ref _refreshIntervalTicks, ticks) != ticks) Updated?.Invoke();
    }

    private static TimeSpan ValidateInterval(TimeSpan interval) =>
        interval >= MinimumRefreshInterval && interval <= MaximumRefreshInterval
            ? interval : throw new ArgumentOutOfRangeException(nameof(interval), "Refresh interval must be between 15 seconds and 24 hours.");

    /// <summary>Call after a user-initiated successful connection. The first new snapshot establishes a silent baseline.</summary>
    public void ResetAfterConnect()
    {
        lock (_sync)
        {
            _generation++;
            _hasBaseline = false;
            _previous = new(StringComparer.Ordinal);
            IsDisconnected = !session.IsConnected;
            JobsError = GpuError = HistoryError = null;
            _lastJobsAttempt = _lastGpuAttempt = _lastManual = _lastHistoryAttempt = DateTimeOffset.MinValue;
            _historyOffset = 0;
        }
        Updated?.Invoke();
    }

    public void MarkDisconnected()
    {
        lock (_sync)
        {
            _generation++;
            IsDisconnected = true;
            _hasBaseline = false;
        }
        Updated?.Invoke();
    }

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisconnected) return;
        if (!session.IsConnected) { MarkDisconnected(); return; }
        await Task.WhenAll(SampleJobsAsync(false, cancellationToken), SampleGpusAsync(false, cancellationToken)).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (IsDisconnected || _clock.GetUtcNow() - _lastManual < ManualCooldown) return;
            _lastManual = _clock.GetUtcNow();
        }
        if (!session.IsConnected) { MarkDisconnected(); return; }
        await Task.WhenAll(SampleJobsAsync(true, cancellationToken), SampleGpusAsync(true, cancellationToken)).ConfigureAwait(false);
    }

    private async Task SampleJobsAsync(bool force, CancellationToken ct)
    {
        if (!await _jobsGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        int generation = _generation;
        try
        {
            var now = _clock.GetUtcNow();
            if (!force && now - _lastJobsAttempt < RefreshInterval) return;
            _lastJobsAttempt = now;
            var jobs = SlurmParser.ParseQueue(await session.ExecuteAsync(SlurmCommands.Queue, ct).ConfigureAwait(false));
            var notifications = new List<JobNotification>();
            lock (_sync)
            {
                if (generation != _generation) return;
                var current = jobs.ToDictionary(j => j.Id, StringComparer.Ordinal);
                foreach (var job in jobs) _departed.Remove(job.Id);
                if (_hasBaseline)
                {
                    foreach (var job in jobs)
                    {
                        if (job.IsRunning && _previous.TryGetValue(job.Id, out var old) && old.IsPending && _notified.Add(job.Id + ":RUNNING"))
                            notifications.Add(new(job.Id, "Experiment started", $"{job.Name} · {job.Id} · {job.Nodes}", "RUNNING", job.Name));
                    }
                    foreach (var old in _previous.Values.Where(old => !current.ContainsKey(old.Id)))
                        _departed.TryAdd(old.Id, new(old));
                }
                _hasBaseline = true;
                _previous = current;
                Jobs = new(now, jobs, _departed.Values.Reverse().Take(50).ToArray());
                JobsError = null;
            }
            Updated?.Invoke();
            foreach (var notification in notifications) Notification?.Invoke(notification);
            await ConfirmDepartedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (generation != _generation) return;
                JobsError = DescribeError(ex, "Queue query failed. Showing the last successful snapshot.");
            }
            HandleDisconnect(ex, generation); Updated?.Invoke();
        }
        finally { _jobsGate.Release(); }
    }

    private async Task SampleGpusAsync(bool force, CancellationToken ct)
    {
        if (!await _gpuGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        int generation = _generation;
        try
        {
            var now = _clock.GetUtcNow();
            if (!force && now - _lastGpuAttempt < RefreshInterval) return;
            _lastGpuAttempt = now;
            var gpu = SlurmParser.ParseNodes(await session.ExecuteAsync(SlurmCommands.Nodes, ct).ConfigureAwait(false), now);
            lock (_sync)
            {
                if (generation != _generation) return;
                Gpus = gpu;
                GpuError = null;
            }
            Updated?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (generation != _generation) return;
                GpuError = DescribeError(ex, "GPU query failed. Queue updates remain available; showing the last successful GPU snapshot.");
            }
            HandleDisconnect(ex, generation); Updated?.Invoke();
        }
        finally { _gpuGate.Release(); }
    }

    public async Task ConfirmDepartedAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisconnected || !await _historyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        int generation = _generation;
        try
        {
            if (_clock.GetUtcNow() - _lastHistoryAttempt < ManualCooldown) return;
            string[] ids;
            lock (_sync)
            {
                var pending = _departed.Values.Where(j => j.State == "UNKNOWN").Select(j => j.Job.Id).ToArray();
                if (pending.Length == 0) return;
                int offset = _historyOffset % pending.Length;
                ids = pending.Skip(offset).Concat(pending.Take(offset)).Take(128).ToArray();
                _historyOffset = (offset + ids.Length) % pending.Length;
            }
            if (ids.Length == 0) return;
            _lastHistoryAttempt = _clock.GetUtcNow();
            var results = SlurmParser.ParseAccounting(await session.ExecuteAsync(SlurmCommands.Accounting(ids), cancellationToken).ConfigureAwait(false));
            var notifications = new List<JobNotification>();
            lock (_sync)
            {
                if (generation != _generation) return;
                foreach (var result in results.Where(r => r.IsTerminal))
                {
                    if (!_departed.TryGetValue(result.Id, out var departed) || departed.State != "UNKNOWN") continue;
                    var confirmed = departed with { State = result.State, ExitCode = result.ExitCode };
                    _departed[result.Id] = confirmed;
                    if (_notified.Add(result.Id + ":TERMINAL"))
                        notifications.Add(new(result.Id, $"Experiment {confirmed.StateLabel.ToLowerInvariant()}", $"{departed.Job.Name} · {result.Id} · {result.ExitCode}", result.State, departed.Job.Name));
                }
                if (Jobs is not null) Jobs = Jobs with { Departed = _departed.Values.Reverse().Take(50).ToArray() };
                HistoryError = null;
            }
            Updated?.Invoke();
            foreach (var notification in notifications) Notification?.Invoke(notification);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (generation != _generation) return;
                HistoryError = "History is temporarily unavailable. Departed jobs are waiting for confirmation.";
            }
            HandleDisconnect(ex, generation); Updated?.Invoke();
        }
        finally { _historyGate.Release(); }
    }

    private void HandleDisconnect(Exception ex, int generation)
    {
        bool changed = false;
        lock (_sync)
        {
            if (generation != _generation) return;
            if (ex is SessionDisconnectedException || !session.IsConnected)
            {
                _generation++;
                IsDisconnected = true;
                _hasBaseline = false;
                changed = true;
            }
        }
        if (changed) Updated?.Invoke();
    }

    // Server output is intentionally not propagated into errors, logs, or notifications.
    private static string DescribeError(Exception ex, string fallback) => ex is SessionDisconnectedException ? "Disconnected. Showing the last successful snapshot. Reconnect to update." : fallback;
}
