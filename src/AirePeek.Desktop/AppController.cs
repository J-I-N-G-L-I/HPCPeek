using AirePeek.Core;
using AirePeek.Ssh;
using AirePeek.Desktop.Infrastructure;
using Avalonia.Threading;

namespace AirePeek.Desktop;

internal sealed class AppController : IAsyncDisposable
{
    private readonly SettingsStore settings;
    private readonly Func<ICommandSession> createSshSession;
    private readonly Action<Action> post;
    private readonly TimeProvider? timeProvider;
    private ICommandSession? session;
    private ConnectionProfile? profile;
    private readonly SemaphoreSlim operations = new(1);
    private readonly object lifecycle = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private CancellationTokenSource? connection;
    private Action<JobNotification>? monitorNotification;
    private Task? disposal;
    private volatile bool disposing;
    private int polling;
    private int refreshRequested;
    private long generation;
    public MonitorService? Monitor { get; private set; }
    public string ClusterId { get; }
    public ClusterDefinition Cluster => ClusterCatalog.Get(ClusterId);
    public ClusterPreferences Preferences => settings.GetCluster(ClusterId);
    public bool IsDemo => session is DemoSession;
    public bool IsConnecting { get; private set; }
    public bool IsConnected => !disposing && session?.IsConnected == true && Monitor?.IsDisconnected != true;
    public int? DemoConnectionCount => (session as DemoSession)?.ConnectionCount;
    public string? Message { get; private set; }
    public event Action? Changed;
    public event Action<JobNotification>? Notification;

    // Optional adapters let the actual lifecycle be tested without a display or SSH server.
    public AppController(SettingsStore settings, Func<ICommandSession>? createSshSession = null, Action<Action>? post = null,
        string clusterId = ClusterCatalog.AireId, TimeProvider? timeProvider = null)
    {
        this.settings = settings;
        ClusterId = ClusterCatalog.Get(clusterId).Id;
        this.timeProvider = timeProvider;
        this.createSshSession = createSshSession ?? (() => new SshCommandSession());
        this.post = post ?? (action => Dispatcher.UIThread.Post(action));
        lifetimeToken = lifetime.Token;
    }
    public void ReportAuthentication(string text) { if (!disposing) { Message = text; Raise(); } }
    private void Raise() { if (!disposing) post(() => { if (!disposing) Changed?.Invoke(); }); }
    private void OnNotification(MonitorService source, long notificationGeneration, JobNotification notification)
    {
        if (disposing || notificationGeneration != Interlocked.Read(ref generation) || !ReferenceEquals(Monitor, source)) return;
        post(() =>
        {
            if (!disposing && notificationGeneration == Interlocked.Read(ref generation) && ReferenceEquals(Monitor, source))
                Notification?.Invoke(notification with { Cluster = ClusterId });
        });
    }
    private void BindNotifications(MonitorService monitor, long notificationGeneration)
    {
        if (monitorNotification is not null) monitor.Notification -= monitorNotification;
        monitorNotification = notification => OnNotification(monitor, notificationGeneration, notification);
        monitor.Notification += monitorNotification;
    }

    public void ApplyPreferences()
    {
        if (disposing) return;
        Monitor?.ConfigureRefreshInterval(TimeSpan.FromSeconds(settings.Value.RefreshIntervalSeconds));
        Raise();
    }

    private async Task<bool> TryEnterAsync()
    {
        if (disposing || !await operations.WaitAsync(0)) return false;
        if (!disposing) return true;
        operations.Release();
        return false;
    }
    public async Task StartDemoAsync(IAuthenticationUI ui)
    {
        if (!await TryEnterAsync()) return;
        try
        {
            lock (lifecycle) generation++;
            await ReplaceAsync(new DemoSession(ClusterId) { Scenario = ClusterId == ClusterCatalog.CalderId ? DemoScenario.Calder7 : DemoScenario.Aire28 });
            await session!.ConnectAsync(new(new($"demo-{ClusterId}", 22, "demo")), ui, settings, lifetimeToken);
            lock (lifecycle)
            {
                lifetimeToken.ThrowIfCancellationRequested();
                if (disposing) return;
                profile = null; Monitor!.ResetAfterConnect(); Message = null;
            }
            await Monitor.RefreshAsync(lifetimeToken);
        }
        catch (OperationCanceledException) when (disposing) { }
        finally { operations.Release(); Raise(); }
    }
    public async Task ClearDemoAsync()
    {
        if (disposing) return;
        try { await operations.WaitAsync(lifetimeToken); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (disposing || session is not DemoSession demo) return;
            lock (lifecycle)
            {
                generation++;
                if (Monitor is { } monitor)
                {
                    monitor.MarkDisconnected(); monitor.Updated -= Raise;
                    if (monitorNotification is not null) monitor.Notification -= monitorNotification;
                }
                Monitor = null; session = null; profile = null; monitorNotification = null; Message = null;
                Interlocked.Exchange(ref refreshRequested, 0);
            }
            await demo.DisposeAsync();
        }
        finally { operations.Release(); Raise(); }
    }
    public async Task ConnectAsync(ConnectionProfile nextProfile, IAuthenticationUI ui)
    {
        if (!await TryEnterAsync()) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        long connectingGeneration;
        lock (lifecycle)
        {
            connectingGeneration = ++generation;
            connection = cancel;
            IsConnecting = true;
        }
        Message = "Connecting. Follow the server prompts and approve Duo when requested."; Raise();
        try
        {
            Preferences.Connection = nextProfile; settings.Save();
            if (session is null or DemoSession || profile != nextProfile) await ReplaceAsync(createSshSession());
            profile = nextProfile;
            await session!.ConnectAsync(nextProfile, ui, settings, cancel.Token);
            // Cancellation can race a successful server response. Do not revive that connection's baseline.
            lock (lifecycle)
            {
                if (disposing || generation != connectingGeneration) throw new OperationCanceledException(cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                Monitor!.ResetAfterConnect(); BindNotifications(Monitor, connectingGeneration); Message = (session as IPasswordStorageStatus)?.PasswordStorageWarning;
            }
            await Monitor.RefreshAsync(cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            Monitor?.MarkDisconnected();
            if (session != null) await session.DisconnectAsync();
            if (!disposing) Message = "Connection cancelled. Reconnect when you are ready.";
        }
        catch (SharedPasswordAuthenticationException ex)
        {
            Monitor?.MarkDisconnected();
            profile = nextProfile with { ReuseGatewayPassword = false };
            Preferences.Connection = profile;
            try { settings.Save(); } catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
            Message = PasswordFailureMessage(ex.Message);
        }
        catch (SavedPasswordAuthenticationException ex)
        {
            Monitor?.MarkDisconnected();
            profile = nextProfile with { RememberPassword = false, ReuseGatewayPassword = false }; Preferences.Connection = profile;
            try { settings.Save(); } catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
            Message = PasswordFailureMessage(ex.Message);
        }
        catch (AdditionalAuthenticationException ex)
        {
            Monitor?.MarkDisconnected();
            Message = PasswordFailureMessage(ex.Message);
        }
        catch (HostKeyMismatchException) { Monitor?.MarkDisconnected(); Message = "Host fingerprint changed. Connection stopped. Verify it with your administrator; the saved key was not replaced."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Monitor?.MarkDisconnected(); Message = "Cannot save preferences. Use a folder you can write to."; }
        catch { Monitor?.MarkDisconnected(); Message = "Could not connect. Check your network, VPN, host details and authentication responses, then reconnect."; }
        finally
        {
            lock (lifecycle) { connection = null; IsConnecting = false; }
            operations.Release(); Raise();
        }
    }
    private string PasswordFailureMessage(string message) => (session as IPasswordStorageStatus)?.PasswordStorageWarning is { Length: > 0 } warning
        ? message + "\n\n" + warning : message;

    private async Task ReplaceAsync(ICommandSession replacement)
    {
        if (Monitor != null) { Monitor.MarkDisconnected(); Monitor.Updated -= Raise; if (monitorNotification is not null) Monitor.Notification -= monitorNotification; }
        if (session != null) await session.DisposeAsync();
        lock (lifecycle)
        {
            session = replacement; Monitor = new(session, timeProvider, refreshInterval: TimeSpan.FromSeconds(settings.Value.RefreshIntervalSeconds));
            Interlocked.Exchange(ref refreshRequested, 0);
            Monitor.Updated += Raise; BindNotifications(Monitor, generation);
        }
    }
    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { /* The captured connection already finished. */ }
    }
    public void CancelConnection()
    {
        CancellationTokenSource? pending;
        lock (lifecycle) pending = connection;
        Cancel(pending);
    }
    public async Task PollAsync()
    {
        if (disposing || IsConnecting || Interlocked.CompareExchange(ref polling, 1, 0) != 0) return;
        try
        {
            if (!disposing && Monitor is { } monitor)
            {
                if (Volatile.Read(ref refreshRequested) != 0 && IsConnected && monitor.RefreshCooldownRemaining == TimeSpan.Zero)
                {
                    Interlocked.Exchange(ref refreshRequested, 0);
                    await monitor.RefreshAsync(lifetimeToken);
                }
                else await monitor.PollAsync(lifetimeToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { Interlocked.Exchange(ref polling, 0); }
    }
    public async Task RefreshAsync()
    {
        if (disposing || IsConnecting || Monitor is not { } monitor) return;
        try { await monitor.RefreshAsync(lifetimeToken); }
        catch (OperationCanceledException) { }
    }
    /// <summary>A selection can defer a read until cooldown expires, but can never establish a connection.</summary>
    public async Task RequestRefreshAsync()
    {
        if (disposing || IsConnecting || !IsConnected || Monitor is null) return;
        Interlocked.Exchange(ref refreshRequested, 1);
        // Poll ownership keeps a selection made during an in-flight read queued for the next tick.
        await PollAsync();
    }
    public async Task InvalidateAsync()
    {
        ICommandSession? invalidatedSession;
        CancellationTokenSource? pending;
        long invalidatedGeneration;
        lock (lifecycle)
        {
            if (disposing || session is DemoSession) return;
            invalidatedSession = session;
            invalidatedGeneration = ++generation;
            pending = connection;
            Monitor?.MarkDisconnected();
        }
        Cancel(pending);
        await operations.WaitAsync();
        try
        {
            // Reconnect may reuse the same session object: both identity and generation must match.
            lock (lifecycle)
                if (disposing || generation != invalidatedGeneration || !ReferenceEquals(session, invalidatedSession)) return;
            if (invalidatedSession != null) await invalidatedSession.DisconnectAsync();
            if (!disposing) { Message = "Disconnected. Last successful snapshots are retained. Reconnect to authenticate again."; Raise(); }
        }
        finally { operations.Release(); }
    }
    public async Task ChangeDemoAsync(DemoScenario scenario, IAuthenticationUI ui)
    {
        if (!await TryEnterAsync()) return;
        try
        {
            if (session is not DemoSession demo) return;
            demo.Scenario = scenario;
            if (scenario != DemoScenario.Disconnected && !demo.IsConnected)
                await demo.ConnectAsync(new(new("demo", 22, "demo")), ui, settings, lifetimeToken);
            lock (lifecycle)
            {
                lifetimeToken.ThrowIfCancellationRequested();
                if (disposing) return;
                generation++;
                Monitor!.ResetAfterConnect(); BindNotifications(Monitor, generation);
            }
            await Monitor.RefreshAsync(lifetimeToken); Raise();
        }
        catch (OperationCanceledException) when (disposing) { }
        finally { operations.Release(); }
    }
    public async Task AdvanceDemoAsync()
    {
        if (!await TryEnterAsync()) return;
        try
        {
            if (session is DemoSession demo && Monitor?.RefreshCooldownRemaining == TimeSpan.Zero)
            { demo.Advance(); await Monitor.RefreshAsync(lifetimeToken); }
        }
        catch (OperationCanceledException) when (disposing) { }
        finally { operations.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (lifecycle)
        {
            if (disposal != null) return new(disposal);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
            disposing = true; generation++;
            Monitor?.MarkDisconnected();
        }
        _ = CompleteDisposalAsync(completion);
        return new(disposal);
    }
    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            lifetime.Cancel(); CancelConnection();
            await operations.WaitAsync();
            try
            {
                if (Monitor != null) { Monitor.Updated -= Raise; if (monitorNotification is not null) Monitor.Notification -= monitorNotification; }
                if (session != null) await session.DisposeAsync();
            }
            finally { operations.Release(); lifetime.Dispose(); }
            completion.SetResult();
        }
        catch (Exception error) { completion.SetException(error); }
    }
}

