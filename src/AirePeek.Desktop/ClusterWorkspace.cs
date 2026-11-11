using AirePeek.Core;
using AirePeek.Desktop.Infrastructure;

namespace AirePeek.Desktop;

/// <summary>Independent, explicitly authenticated cluster sessions. Switching views only requests reads.</summary>
internal sealed class ClusterWorkspace : IAsyncDisposable
{
    private readonly SettingsStore settings;
    private readonly IReadOnlyDictionary<string, AppController> controllers;
    private readonly object lifecycle = new();
    private readonly SemaphoreSlim modeChanges = new(1, 1);
    private Task? disposal;
    private bool disposing;

    public IReadOnlyList<AppController> Entries { get; }
    public AppController this[string id] => controllers[ClusterCatalog.Get(id).Id];
    public string SelectedClusterId => settings.Value.SelectedClusterId;
    public AppController Selected => this[SelectedClusterId];
    public int RunningCount => Entries.Sum(c => c.Monitor?.Jobs?.Jobs.Count(j => j.IsRunning) ?? 0);
    public int QueuedCount => Entries.Sum(c => c.Monitor?.Jobs?.Jobs.Count(j => j.IsPending) ?? 0);
    public bool IsDemo => Entries.Any(c => c.IsDemo);
    public bool IsConnecting => Entries.Any(c => c.IsConnecting);
    public event Action? Changed;
    public event Action<JobNotification>? Notification;

    public ClusterWorkspace(SettingsStore settings, Func<string, ICommandSession>? createSshSession = null,
        Action<Action>? post = null, TimeProvider? timeProvider = null)
    {
        this.settings = settings;
        Entries = Array.AsReadOnly(ClusterCatalog.All.Select(cluster => new AppController(settings,
            createSshSession is null ? null : () => createSshSession(cluster.Id), post, cluster.Id, timeProvider)).ToArray());
        controllers = Entries.ToDictionary(c => c.ClusterId, StringComparer.Ordinal);
        foreach (var controller in Entries) { controller.Changed += Raise; controller.Notification += ForwardNotification; }
    }

    private void Raise() { if (!disposing) Changed?.Invoke(); }
    private void ForwardNotification(JobNotification notification) { if (!disposing) Notification?.Invoke(notification); }
    public async Task SelectAsync(string id, bool refresh = true)
    {
        if (disposing) return;
        var selected = this[id];
        settings.Value.SelectedClusterId = selected.ClusterId;
        try { settings.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { selected.ReportAuthentication("Cluster selected, but preferences could not be saved. Use a folder you can write to."); }
        Raise();
        if (refresh) await selected.RequestRefreshAsync();
    }
    public Task PollAsync() => disposing ? Task.CompletedTask : Task.WhenAll(Entries.Select(c => c.PollAsync()));
    public Task RefreshAsync() => disposing ? Task.CompletedTask : Selected.RequestRefreshAsync();
    public async Task ConnectAsync(string id, ConnectionProfile profile, IAuthenticationUI ui)
    {
        if (disposing) return;
        var target = this[id];
        await modeChanges.WaitAsync();
        try
        {
            if (disposing) return;
            // Synthetic snapshots must never contribute to the aggregate after a real connection attempt.
            await Task.WhenAll(Entries.Select(c => c.ClearDemoAsync()));
            if (!disposing) await target.ConnectAsync(profile, ui);
        }
        finally { modeChanges.Release(); }
    }
    public async Task StartDemoAsync(IAuthenticationUI ui)
    {
        if (disposing) return;
        await modeChanges.WaitAsync();
        try { if (!disposing) await Task.WhenAll(Entries.Select(c => c.StartDemoAsync(ui))); }
        finally { modeChanges.Release(); }
    }
    public Task InvalidateAsync() => disposing ? Task.CompletedTask : Task.WhenAll(Entries.Select(c => c.InvalidateAsync()));
    public void ApplyPreferences() { if (!disposing) foreach (var controller in Entries) controller.ApplyPreferences(); }
    public void CancelConnections() { foreach (var controller in Entries) controller.CancelConnection(); }
    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposal is not null) return new(disposal);
            disposing = true;
            foreach (var controller in Entries) { controller.Changed -= Raise; controller.Notification -= ForwardNotification; }
            disposal = Task.WhenAll(Entries.Select(c => c.DisposeAsync().AsTask()));
            return new(disposal);
        }
    }
}
