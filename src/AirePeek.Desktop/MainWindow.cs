using AirePeek.Core;
using AirePeek.Desktop.Dialogs;
using AirePeek.Desktop.Infrastructure;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AirePeek.Desktop;

internal sealed class MainWindow : Window
{
    private readonly SettingsStore settings;
    internal ClusterWorkspace Workspace { get; }
    internal AppController Controller => Workspace.Selected;
    private readonly Border connectionDot = new() { Width = 9, Height = 9, CornerRadius = new(5), Margin = new(6, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform(0, 1.5) };
    private readonly TextBlock demo = Ui.Text("DEMO", 8, "attention"), ages = Ui.Text("", 9, "muted");
    private readonly TextBlock running = Ui.Text("—", 28), queued = Ui.Text("—", 28);
    private readonly TextBlock gpuSummary = Ui.Text("Connect to view GPUs", 12, "muted");
    private readonly TextBlock message = Ui.Text("", 12, "attention", true);
    private readonly TextBlock jobsAge = Ui.Text("", 10, "muted"), gpuAge = Ui.Text("", 10, "muted");
    private readonly TextBlock jobsError = Ui.Text("", 11, "attention", true), gpuError = Ui.Text("", 11, "attention", true);
    private readonly TextBlock emptyJobs = Ui.Text("No jobs", 12, "muted", true);
    private readonly TextBlock candidates = Ui.Text("", 12), selectedNode = Ui.Text("", 12, wrap: true);
    private readonly TextBlock capacity = Ui.Text("", 10, "muted"), partial = Ui.Text("", 11, "attention", true);
    private readonly TextBlock unavailable = Ui.Text("", 10, "muted"), sourceHint = Ui.Text("", 11, "attention", true);
    private readonly StackPanel jobs = new() { Spacing = 3 }, departed = new() { Spacing = 5 };
    private readonly Dictionary<string, GpuNodeGrid> compactMaps = new();
    private readonly Dictionary<string, TextBlock> compactStates = new();
    private readonly GpuNodeGrid nodeMap = new(false);
    private readonly Border nodeInspector;
    private readonly ComboBox model = new() { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button connectCluster;
    private readonly Dictionary<string, JobRow> jobRows = new(StringComparer.Ordinal);
    private readonly Button pin, more, expandChevron;
    private readonly MenuItem refreshMenu;
    private readonly ScrollViewer scroll;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly AuthenticationUI authentication;
    private bool expanded, updating, modal, quitting, positionRestored;
    private string? selectedNodeName;
    private string? displayedClusterId;
    private bool? displayedSignedIn;
    private GpuSnapshot? summarySnapshot;
    private string? summaryModel;
    private GpuSummary? cachedSummary;
    internal const int CompactHeight = WindowSizing.CompactHeight;
    private sealed record ClusterModel(string ClusterId, string ClusterName, string Model)
    {
        public override string ToString() => $"{ClusterName} · {(Model.Equals("H200NVL", StringComparison.OrdinalIgnoreCase) ? "H200 NVL" : Model)}";
    }
    internal Control CaptureRoot { get; }
    internal Button ExpandButton { get; }
    internal ContextMenu MoreMenu { get; }
    internal bool IsExpanded => expanded;
    internal IReadOnlyDictionary<string, JobRow> JobRows => jobRows;
    internal Vector ScrollOffset { get => scroll.Offset; set => scroll.Offset = value; }
    internal IReadOnlyList<GpuNodeTile> VisibleNodeTiles => nodeMap.Tiles;
    internal GpuNodeGrid ExpandedNodeGrid => nodeMap;
    internal GpuNodeGrid CompactNodeGrid => compactMaps[Controller.ClusterId];
    internal GpuNodeGrid CompactNodes(string clusterId) => compactMaps[clusterId];
    internal ComboBox ClusterModelSelector => model;
    internal Border LoginIndicator => connectionDot;
    internal bool IsUserSignedIn => Controller.IsConnected && !Controller.IsDemo;

    public MainWindow(SettingsStore settings, ClusterWorkspace workspace, Action quit, bool inspect = false)
    {
        this.settings = settings; Workspace = workspace; authentication = new(this, text => Controller.ReportAuthentication(text));
        Title = "HPCPeek"; Icon = AppBrand.CreateWindowIcon(); Width = 340; Height = CompactHeight; CanResize = false;
        WindowDecorations = WindowDecorations.None; ShowInTaskbar = inspect; Topmost = settings.Value.IsPinned;
        Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        FontFamily = OperatingSystem.IsWindows() ? new("Segoe UI") : FontFamily.Default;
        var surface = new Border { CornerRadius = new(10), BorderThickness = new(1), ClipToBounds = true };
        surface.Classes.Add("surface"); CaptureRoot = surface; Content = surface;
        var layout = new Grid { RowDefinitions = new($"{CompactHeight - 2},*") }; surface.Child = layout;
        var compact = new Grid { Margin = new(14, 7, 14, 7), RowDefinitions = new("26,74,*") }; layout.Children.Add(compact);
        var header = new Grid { ColumnDefinitions = new("Auto,Auto,Auto,*,24,24"), Background = Brushes.Transparent };
        var brand = Ui.Text("HPCPeek", 14); brand.FontWeight = FontWeight.SemiBold; brand.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(brand);
        Grid.SetColumn(connectionDot, 1); header.Children.Add(connectionDot);
        connectionDot.Child = new Border { Width = 3, Height = 2, CornerRadius = new(2), Background = Brushes.White, Opacity = .35, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(2, 1, 0, 0) };
        demo.Margin = new(0, 0, 3, 0); demo.VerticalAlignment = ages.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(demo, 2); header.Children.Add(demo);
        ages.HorizontalAlignment = HorizontalAlignment.Right; ages.Margin = new(0, 0, 4, 0); Grid.SetColumn(ages, 3); header.Children.Add(ages);
        pin = IconButton("pin", "Pin window", TogglePin); Grid.SetColumn(pin, 4); header.Children.Add(pin);
        more = IconButton("ellipsis", "More", () => MoreMenu!.Open(more)); Grid.SetColumn(more, 5); header.Children.Add(more);
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is Visual source && source is not Button && !source.GetVisualAncestors().OfType<Button>().Any()) BeginMoveDrag(e);
        };
        compact.Children.Add(header);
        var overview = new Grid { ColumnDefinitions = new("*,156"), Margin = new(0, 2, 0, 0) }; Grid.SetRow(overview, 1); compact.Children.Add(overview);
        var metrics = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 25, VerticalAlignment = VerticalAlignment.Center };
        metrics.Children.Add(Metric(running, "Running")); metrics.Children.Add(Metric(queued, "Queued"));
        ExpandButton = Ui.Button("Expand details", () => SetExpanded(!expanded), true); ExpandButton.Content = metrics;
        ExpandButton.HorizontalContentAlignment = HorizontalAlignment.Left; overview.Children.Add(ExpandButton);
        var maps = new StackPanel { Name = "CompactClusters", Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(maps, 1); overview.Children.Add(maps);
        foreach (var cluster in ClusterCatalog.All)
        {
            var map = new GpuNodeGrid(true) { VerticalAlignment = VerticalAlignment.Center }; compactMaps.Add(cluster.Id, map);
            var row = new Grid { ColumnDefinitions = new("48,88"), MinHeight = cluster.Id == ClusterCatalog.AireId ? 45 : 9 };
            var label = Ui.Button(cluster.Name, () => _ = SelectClusterAsync(cluster.Id), true);
            label.Content = Ui.Text(cluster.Name, 10, "muted"); label.HorizontalContentAlignment = HorizontalAlignment.Left; label.MinHeight = 0;
            row.Children.Add(label); Grid.SetColumn(map, 1); row.Children.Add(map);
            var state = Ui.Text("No data", 9, "muted"); state.VerticalAlignment = VerticalAlignment.Center;
            compactStates.Add(cluster.Id, state); Grid.SetColumn(state, 1); row.Children.Add(state); maps.Children.Add(row);
            map.Selected += async tile =>
            {
                var refresh = SelectClusterAsync(cluster.Id);
                SetExpanded(true); SelectNode(tile);
                await refresh; // A slow read must not navigate back after the user switches again.
            };
        }
        var footer = new Grid { ColumnDefinitions = new("*,24"), Margin = new(0, 3, 0, 0) }; Grid.SetRow(footer, 2); compact.Children.Add(footer);
        var gpuButton = Ui.Button("Expand cluster details", () => SetExpanded(!expanded), true); gpuButton.Content = gpuSummary; footer.Children.Add(gpuButton);
        gpuSummary.VerticalAlignment = VerticalAlignment.Center; gpuSummary.TextTrimming = TextTrimming.CharacterEllipsis;
        expandChevron = IconButton("chevron-down", "Expand details", () => SetExpanded(!expanded)); Grid.SetColumn(expandChevron, 1); footer.Children.Add(expandChevron);
        var details = new StackPanel { Margin = new(14, 0, 14, 14), Spacing = 0 };
        scroll = new() { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVisible = false };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        details.Children.Add(Ui.Rule(5)); message.Margin = new(0, 6, 0, 6); details.Children.Add(message);
        details.Children.Add(Heading("Cluster GPUs", gpuAge)); details.Children.Add(gpuError);
        model.Margin = new(0, 3, 0, 12); details.Children.Add(model);
        connectCluster = Ui.Button("Connect", () => _ = RefreshOrReconnectAsync()); connectCluster.Margin = new(0, 0, 0, 10); details.Children.Add(connectCluster);
        model.SelectionChanged += async (_, _) => { if (!updating && model.SelectedItem is ClusterModel chosen) await SelectClusterAsync(chosen.ClusterId, chosen.Model); };
        AutomationProperties.SetName(model, "Cluster and GPU model");
        ToolTip.SetTip(model, "Switch cluster and refresh its authenticated connection.");
        details.Children.Add(nodeMap); nodeMap.Selected += SelectNode;
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, Margin = new(0, 8, 0, 8) };
        foreach (var item in new[] { ("Free", "TileFree"), ("Full", "TileFull"), ("Unavailable", "TileUnavailable"), ("Unknown", "TileUnknown") }) legend.Children.Add(Legend(item.Item1, item.Item2));
        details.Children.Add(legend);
        candidates.FontWeight = FontWeight.SemiBold; candidates.Margin = new(0, 0, 0, 4); details.Children.Add(candidates);
        capacity.Margin = new(0, 0, 0, 4); details.Children.Add(capacity);
        unavailable.Margin = new(0, 0, 0, 4); details.Children.Add(unavailable);
        sourceHint.Margin = new(0, 0, 0, 4); details.Children.Add(sourceHint);
        partial.Text = "Partial visibility. Unknown capacity is excluded."; details.Children.Add(partial);
        nodeInspector = new Border { Child = selectedNode, Padding = new(10), CornerRadius = new(8), Margin = new(0, 8, 0, 0), IsVisible = false };
        nodeInspector.Classes.Add("panel"); details.Children.Add(nodeInspector);
        details.Children.Add(Ui.Rule(8)); details.Children.Add(Heading("My jobs", jobsAge)); details.Children.Add(jobsError); details.Children.Add(emptyJobs); details.Children.Add(jobs); details.Children.Add(departed);
        refreshMenu = new() { Header = "Refresh", Icon = Icons.Create("refresh-cw"), InputGesture = Shortcut(Key.R) };
        refreshMenu.Click += async (_, _) => await RefreshOrReconnectAsync();
        var preferences = new MenuItem { Header = "Settings…", Icon = Icons.Create("settings"), InputGesture = Shortcut(Key.OemComma) };
        preferences.Click += async (_, _) => await OpenSettingsAsync();
        var exit = new MenuItem { Header = "Quit HPCPeek", Icon = Icons.Create("power"), InputGesture = Shortcut(Key.Q) }; exit.Click += (_, _) => quit();
        MoreMenu = new() { Placement = PlacementMode.BottomEdgeAlignedRight, ItemsSource = new object[] { refreshMenu, new Separator(), preferences, new Separator(), exit } };
        more.ContextMenu = MoreMenu;
        MoreMenu.Closed += (_, _) => DismissTemporaryWhenInactive(); model.DropDownClosed += (_, _) => DismissTemporaryWhenInactive();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { if (MoreMenu.IsOpen) MoreMenu.Close(); else if (expanded) SetExpanded(false); else HidePanel(); e.Handled = true; }
            else if (Shortcut(Key.R).Matches(e)) { await RefreshOrReconnectAsync(); e.Handled = true; }
            else if (Shortcut(Key.OemComma).Matches(e)) { await OpenSettingsAsync(); e.Handled = true; }
            else if (Shortcut(Key.Q).Matches(e)) { quit(); e.Handled = true; }
        };
        Deactivated += (_, _) => DismissTemporaryWhenInactive();
        Closing += (_, e) => { SavePosition(); if (!quitting) { e.Cancel = true; HidePanel(); } };
        PositionChanged += (_, _) => { if (positionRestored && IsVisible) { settings.Value.Left = Position.X; settings.Value.Top = Position.Y; } };
        Opened += (_, _) => { RestorePosition(); positionRestored = true; settings.Value.Left = Position.X; settings.Value.Top = Position.Y; };
        ScalingChanged += (_, _) => { if (expanded) SetExpanded(true); };
        workspace.Changed += Update;
        // Polling checks due times locally; SSH commands only run at the configured interval.
        timer.Tick += async (_, _) => { UpdateHeader(dataChanged: false); TickJobTimers(DateTimeOffset.UtcNow); await Workspace.PollAsync(); }; timer.Start();
        UpdatePin(); Update();
    }
    private static Control Metric(TextBlock value, string label)
    {
        value.FontWeight = FontWeight.SemiBold; value.LineHeight = 32;
        var block = new StackPanel { Spacing = 0 }; block.Children.Add(value); block.Children.Add(Ui.Text(label, 11, "muted")); return block;
    }
    private static Control Legend(string text, string brush)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var square = new Border { Width = 6, Height = 6, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center };
        square[!Border.BackgroundProperty] = new DynamicResourceExtension(brush); row.Children.Add(square); row.Children.Add(Ui.Text(text, 9, "muted")); return row;
    }
    private static Button IconButton(string icon, string label, Action action)
    {
        var button = Ui.Button(label, action, true); button.Content = Icons.Create(icon, 15); button.Classes.Add("icon"); ToolTip.SetTip(button, label); return button;
    }
    private static Control Heading(string title, TextBlock age)
    {
        var row = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 5, 0, 6) };
        row.Children.Add(Ui.Heading(title)); age.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(age, 1); row.Children.Add(age); return row;
    }
    private void DismissTemporaryWhenInactive() => Dispatcher.UIThread.Post(() =>
    {
        if (!quitting && IsVisible && !IsActive && !settings.Value.IsPinned && !modal && !MoreMenu.IsOpen && !model.IsDropDownOpen && !jobRows.Values.Any(r => r.JobMenu.IsOpen)) HidePanel();
    }, DispatcherPriority.Background);
    private static KeyGesture Shortcut(Key key) => new(key, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
    internal void SetExpanded(bool value)
    {
        expanded = value; scroll.IsVisible = value;
        Height = value ? WindowSizing.ExpandedHeight(Screens.ScreenFromWindow(this)?.WorkingArea.Height ?? 1000, RenderScaling) : CompactHeight;
        AutomationProperties.SetName(ExpandButton, value ? "Collapse details" : "Expand details");
        AutomationProperties.SetName(expandChevron, value ? "Collapse details" : "Expand details"); expandChevron.Content = Icons.Create(value ? "chevron-up" : "chevron-down", 15);
        if (value && Screens.ScreenFromWindow(this) is { } screen) Position = WindowSizing.KeepWithinWorkArea(Position, screen.WorkingArea, Width, Height, RenderScaling);
    }
    internal void TogglePin() { settings.Value.IsPinned = !settings.Value.IsPinned; UpdatePin(); SavePreferences(); }
    internal void OpenMoreMenu() => MoreMenu.Open(more);
    private void UpdatePin() { Topmost = settings.Value.IsPinned; pin.Opacity = settings.Value.IsPinned ? 1 : .45; ToolTip.SetTip(pin, settings.Value.IsPinned ? "Pinned" : "Temporary panel"); }
    internal void HidePanel() { SavePosition(); Hide(); }
    internal void ShowPanel() { Show(); Activate(); }
    internal void PermitClose() { quitting = true; timer.Stop(); Workspace.Changed -= Update; }
    internal async Task ChooseConnectionAsync()
    {
        if (modal || Controller.IsConnecting) return; modal = true;
        try
        {
            bool editAgain;
            do
            {
                editAgain = false;
                var controller = Controller;
                var choice = await new ConnectionWindow(controller.Preferences.Connection, controller.Cluster.Name,
                    id => Workspace[id].Preferences.Connection).ShowDialog<ConnectionChoice?>(this);
                if (choice?.Profile is not { } profile) break;
                await ConnectProfileAsync(choice.ClusterId, profile);
                var connected = Workspace[choice.ClusterId];
                if (!string.IsNullOrEmpty(connected.Message))
                    editAgain = await ShowConnectionNoticeAsync(connected.IsConnected ? "Signed in" : "Sign-in not completed", connected.Message, !connected.IsConnected);
            } while (editAgain);
        }
        finally { modal = false; }
    }
    internal async Task ConnectProfileAsync(string clusterId, ConnectionProfile profile, IAuthenticationUI? ui = null)
    {
        var controller = Workspace[clusterId];
        await Workspace.SelectAsync(clusterId, refresh: false);
        SetExpanded(false); Update();
        await Workspace.ConnectAsync(clusterId, profile, ui ?? new AuthenticationUI(this, controller.ReportAuthentication));
        if (controller.IsConnected) SetExpanded(false);
    }
    private Task<bool> ShowConnectionNoticeAsync(string title, string text, bool offerEditConnection)
    {
        var notice = new MessageWindow(title, text, offerEditConnection);
        return notice.ShowDialog<bool>(this);
    }
    internal async Task OpenSettingsAsync()
    {
        if (modal) return;
        if (Controller.IsConnecting) { Controller.CancelConnection(); return; }
        modal = true; SettingsResult? result;
        try { result = await new SettingsWindow(settings).ShowDialog<SettingsResult?>(this); } finally { modal = false; }
        App.ApplyTheme(settings.Value.Theme); Workspace.ApplyPreferences(); UpdatePin(); Update();
        if (result?.Action == SettingsAction.Connect) { await SelectClusterAsync(result.ClusterId ?? Workspace.SelectedClusterId); await ChooseConnectionAsync(); }
    }
    internal Task StartDemoAsync() => Workspace.StartDemoAsync(authentication);
    internal Task SelectClusterAsync(string id, string? gpuModel = null)
    {
        if (gpuModel != null) Workspace[id].Preferences.GpuModel = gpuModel;
        var refresh = Workspace.SelectAsync(id);
        Update();
        return refresh;
    }
    internal async Task ShowJobAsync(JobNotification notification)
    {
        var clusterId = ClusterCatalog.All.FirstOrDefault(c => c.Id == notification.Cluster)?.Id ?? Controller.ClusterId;
        var refresh = SelectClusterAsync(clusterId);
        ShowPanel(); SetExpanded(true);
        Dispatcher.UIThread.Post(() =>
        {
            if (Controller.ClusterId != clusterId) return;
            if (jobRows.TryGetValue(notification.JobId, out var row)) { if (!row.IsExpanded) row.Toggle(); row.BringIntoView(); }
            else departed.BringIntoView();
        }, DispatcherPriority.Loaded);
        await refresh;
    }
    internal async Task RefreshOrReconnectAsync()
    {
        if (Controller.IsConnecting) { Controller.CancelConnection(); return; }
        if (Controller.IsConnected) await Workspace.RefreshAsync();
        else await ChooseConnectionAsync();
    }
    private void Update()
    {
        if (quitting) return;
        if (displayedClusterId != Controller.ClusterId)
        {
            displayedClusterId = Controller.ClusterId; selectedNodeName = null; jobRows.Clear(); jobs.Children.Clear(); scroll.Offset = default;
        }
        connectCluster.IsVisible = !Controller.IsConnected;
        connectCluster.Content = Controller.IsConnecting ? "Cancel connection" : $"Connect to {Controller.Cluster.Name}…";
        message.Text = Controller.Message ?? (Controller.Monitor?.IsDisconnected == true ? "Offline · last successful snapshot" : ""); message.IsVisible = !string.IsNullOrEmpty(message.Text);
        jobsError.Text = Controller.Monitor?.JobsError ?? Controller.Monitor?.HistoryError ?? ""; jobsError.IsVisible = !string.IsNullOrEmpty(jobsError.Text);
        gpuError.Text = Controller.Monitor?.GpuError ?? ""; gpuError.IsVisible = !string.IsNullOrEmpty(gpuError.Text);
        var snapshot = Controller.Monitor?.Jobs;
        emptyJobs.IsVisible = snapshot == null || snapshot.Jobs.Count == 0;
        emptyJobs.Text = snapshot == null ? "Connect to view jobs" : Controller.IsConnected ? "No active jobs" : "Last sample: no active jobs";
        var current = snapshot?.Jobs.OrderByDescending(j => j.IsPending).ThenByDescending(j => j.Id, StringComparer.Ordinal).ToArray() ?? [];
        var valid = current.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in jobRows.Keys.Where(id => !valid.Contains(id)).ToArray()) { jobs.Children.Remove(jobRows[id]); jobRows.Remove(id); }
        for (int i = 0; i < current.Length; i++)
        {
            var job = current[i];
            if (!jobRows.TryGetValue(job.Id, out var row)) { row = new(job, snapshot!.SampledAt, QueueIsLive(DateTimeOffset.UtcNow)); row.JobMenu.Closed += (_, _) => DismissTemporaryWhenInactive(); jobRows[job.Id] = row; }
            else row.Update(job, snapshot!.SampledAt, QueueIsLive(DateTimeOffset.UtcNow));
            Ui.MoveInto(jobs, row, i);
        }
        departed.Children.Clear();
        foreach (var past in snapshot?.Departed ?? []) departed.Children.Add(Ui.Text($"{past.Job.Name}\n{past.Job.Id} · {past.StateLabel}", 11, "muted", true));
        UpdateGpu();
        UpdateHeader();
    }
    private bool QueueIsLive(DateTimeOffset now) => Controller.IsConnected && Controller.Monitor is { JobsError: null, Jobs: { } snapshot } monitor
        && SampleFreshness.Evaluate(snapshot.SampledAt, monitor.RefreshInterval, now, monitor.IsDisconnected).State == FreshnessState.Fresh;
    internal void TickJobTimers(DateTimeOffset now)
    {
        bool live = QueueIsLive(now);
        foreach (var row in jobRows.Values) row.Tick(now, live);
    }
    private GpuSummary? SelectedGpuSummary()
    {
        var snapshot = Controller.Monitor?.Gpus;
        var selectedModel = Controller.Preferences.GpuModel;
        // Samples are replaced after a successful query. Clock ticks do not change GPU capacity.
        if (!ReferenceEquals(snapshot, summarySnapshot) || !string.Equals(selectedModel, summaryModel, StringComparison.Ordinal))
        {
            summarySnapshot = snapshot;
            summaryModel = selectedModel;
            cachedSummary = snapshot?.Summarize(selectedModel, 1);
        }
        return cachedSummary;
    }
    private void UpdateHeader(bool dataChanged = true)
    {
        if (dataChanged)
        {
            demo.IsVisible = Workspace.IsDemo;
            if (displayedSignedIn != IsUserSignedIn)
            {
                displayedSignedIn = IsUserSignedIn;
                connectionDot[!Border.BackgroundProperty] = new DynamicResourceExtension(IsUserSignedIn ? "Accent" : "Failure");
            }
            string LoginState(AppController c) => c.IsDemo ? $"{c.Cluster.Name}: Demo · no account signed in"
                : c.IsConnecting ? $"{c.Cluster.Name}: signing in"
                : c.IsConnected ? $"{c.Cluster.Name}: signed in as {c.Preferences.Connection?.Target.Username ?? "current user"}"
                : $"{c.Cluster.Name}: not signed in";
            var states = "Selected cluster — " + LoginState(Controller) + "\n" + string.Join("\n", Workspace.Entries.Where(c => c != Controller).Select(LoginState));
            ToolTip.SetTip(connectionDot, states);
            AutomationProperties.SetName(connectionDot, states);
            bool hasJobs = Workspace.Entries.Any(c => c.Monitor?.Jobs != null);
            running.Text = hasJobs ? Workspace.RunningCount.ToString() : "—";
            queued.Text = hasJobs ? Workspace.QueuedCount.ToString() : "—";
            running.Opacity = queued.Opacity = Workspace.Entries.Any(c => c.Monitor?.IsDisconnected == true) ? .65 : 1;
        }
        var now = DateTimeOffset.UtcNow;
        SampleFreshness Fresh(AppController c, bool gpu) => SampleFreshness.Evaluate(gpu ? c.Monitor?.Gpus?.SampledAt : c.Monitor?.Jobs?.SampledAt,
            c.Monitor?.RefreshInterval ?? TimeSpan.FromSeconds(settings.Value.RefreshIntervalSeconds), now, c.Monitor?.IsDisconnected == true);
        var q = Fresh(Controller, false); var g = Fresh(Controller, true);
        var oldestJobs = Workspace.Entries.Select(c => Fresh(c, false)).Where(f => f.State != FreshnessState.Never).OrderByDescending(f => f.Age).FirstOrDefault();
        var oldestGpu = Workspace.Entries.Select(c => Fresh(c, true)).Where(f => f.State != FreshnessState.Never).OrderByDescending(f => f.Age).FirstOrDefault();
        ages.Text = $"Jobs {ShortAge(oldestJobs)} · GPUs {ShortAge(oldestGpu)}";
        ToolTip.SetTip(ages, "Oldest available sample · ! means stale or offline\n" + string.Join("\n", Workspace.Entries.Select(c => $"{c.Cluster.Name} — Jobs: {Fresh(c, false).Label}; GPUs: {Fresh(c, true).Label}")));
        jobsAge.Text = q.Label; gpuAge.Text = g.Label;
        jobsAge.Classes.Set("attention", q.NeedsAttention); gpuAge.Classes.Set("attention", g.NeedsAttention);
        ToolTip.SetTip(ExpandButton, "Jobs across sampled clusters\n" + string.Join("\n", Workspace.Entries.Select(c => $"{c.Cluster.Name}: {c.Monitor?.Jobs?.Jobs.Count(j => j.IsRunning).ToString() ?? "—"} running · {c.Monitor?.Jobs?.Jobs.Count(j => j.IsPending).ToString() ?? "—"} queued · {Fresh(c, false).Label}")));
        var stats = SelectedGpuSummary();
        gpuSummary.Text = stats == null ? $"{Controller.Cluster.Name} · not connected" : $"{Controller.Cluster.Name} · {Ui.Count(stats.Unallocated)} free · up to {Ui.Count(stats.MaxPerNode)}/node";
        if (stats?.IsPartial == true) gpuSummary.Text = $"{Controller.Cluster.Name} · partial · {Ui.Count(stats.Unallocated)} free";
        if (g.NeedsAttention && stats != null) gpuSummary.Text += Controller.IsConnected ? " · stale" : " · offline";
        ToolTip.SetTip(gpuSummary, $"{Controller.Preferences.GpuModel} · {g.Label}\nFree: total unallocated GPUs on eligible nodes. Per node: the most free on any one eligible node, not its installed capacity.\nReservations, priority, CPU and memory still affect scheduling.");
        foreach (var entry in Workspace.Entries)
        {
            compactMaps[entry.ClusterId].Opacity = entry.Monitor?.IsDisconnected == true ? .45 : 1;
            ToolTip.SetTip(compactMaps[entry.ClusterId], $"{entry.Cluster.Name} · {Fresh(entry, true).Label}");
        }
        int seconds = (int)Math.Ceiling(Controller.Monitor?.RefreshCooldownRemaining.TotalSeconds ?? 0);
        if (refreshMenu != null)
        {
            refreshMenu.Header = Controller.IsConnecting ? "Cancel connection" : !Controller.IsConnected ? $"Connect to {Controller.Cluster.Name}…" : seconds > 0 ? $"Refresh in {seconds}s" : $"Refresh {Controller.Cluster.Name}";
            refreshMenu.IsEnabled = Controller.IsConnecting || !Controller.IsConnected || seconds == 0;
        }
    }
    private static string ShortAge(SampleFreshness? f)
    {
        if (f?.Age is not { } age) return "—";
        var label = age.TotalDays >= 1 ? $"{(int)age.TotalDays}d" : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h" : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m" : $"{Math.Max(0, (int)age.TotalSeconds)}s";
        return (f.NeedsAttention ? "!" : "") + label;
    }
    private void UpdateGpu()
    {
        updating = true;
        try
        {
            var choices = new List<ClusterModel>();
            bool changed = false;
            foreach (var entry in Workspace.Entries)
            {
                var selection = GpuModelSelection.Resolve(entry.Cluster, entry.Monitor?.Gpus, entry.Preferences.GpuModel);
                if (!entry.Preferences.GpuModel.Equals(selection.SelectedModel, StringComparison.Ordinal))
                {
                    entry.Preferences.GpuModel = selection.SelectedModel; changed = true;
                }
                choices.AddRange(selection.Models.Select(m => new ClusterModel(entry.ClusterId, entry.Cluster.Name, m)));
            }
            if (changed) SavePreferences();
            var available = choices.ToArray();
            if (model.ItemsSource is not ClusterModel[] old || !old.SequenceEqual(available)) model.ItemsSource = available;
            model.SelectedItem = available.First(m => m.ClusterId == Controller.ClusterId && m.Model.Equals(Controller.Preferences.GpuModel, StringComparison.OrdinalIgnoreCase));
        }
        finally { updating = false; }
        foreach (var entry in Workspace.Entries)
        {
            var tiles = entry.Monitor?.Gpus is { } sample ? GpuNodeProjection.Create(sample, entry.Preferences.GpuModel, 1) : [];
            compactMaps[entry.ClusterId].Update(tiles);
            compactStates[entry.ClusterId].IsVisible = tiles.Count == 0;
            compactStates[entry.ClusterId].Text = entry.IsConnecting ? "Connecting…" : entry.Monitor?.Gpus != null ? "No GPUs" : "No data";
        }
        if (Controller.Monitor?.Gpus is not { } snapshot)
        {
            nodeMap.Update([]); candidates.Text = "No GPU sample"; capacity.Text = ""; partial.IsVisible = nodeInspector.IsVisible = unavailable.IsVisible = sourceHint.IsVisible = false; return;
        }
        var s = SelectedGpuSummary()!;
        var selectedTiles = GpuNodeProjection.Create(snapshot, Controller.Preferences.GpuModel, 1);
        nodeMap.Update(selectedTiles, selectedNodeName);
        candidates.Text = $"{Ui.Count(s.Unallocated)} GPUs free · up to {Ui.Count(s.MaxPerNode)} free/node";
        candidates.Classes.Set("accent", s.Candidates.Count > 0);
        var configurations = selectedTiles.Select(t => t.Configured).Distinct().ToArray();
        string installed = configurations.Length == 1 && configurations[0] is { } count ? $" · {count} GPUs/node" : "";
        capacity.Text = $"{selectedTiles.Count} nodes{installed} · {Ui.Count(s.Configured)} configured";
        int excluded = selectedTiles.Count(t => t.Availability == GpuNodeAvailability.Unavailable);
        unavailable.Text = $"{excluded} unavailable node{(excluded == 1 ? "" : "s")}"; unavailable.IsVisible = excluded > 0;
        sourceHint.IsVisible = GpuModelSelection.Resolve(Controller.Cluster, snapshot, Controller.Preferences.GpuModel).HasUnexpectedModel;
        sourceHint.Text = "L40 GPUs reported. Check your Calder host.";
        ToolTip.SetTip(capacity, $"Visible total: {Ui.Count(s.Configured)} configured, {Ui.Count(s.Allocated)} allocated. Unavailable nodes are excluded from free/max. Reservations, priority, CPU and memory still affect scheduling.");
        partial.IsVisible = s.IsPartial;
        var chosen = selectedTiles.FirstOrDefault(t => t.Name == selectedNodeName); nodeInspector.IsVisible = chosen != null;
        if (chosen != null) selectedNode.Text = chosen.DetailText;
    }
    private void SelectNode(GpuNodeTile tile) { selectedNodeName = selectedNodeName == tile.Name ? null : tile.Name; UpdateGpu(); }
    private void RestorePosition()
    {
        PixelPoint? saved = settings.Value.Left.HasValue && settings.Value.Top.HasValue ? new((int)settings.Value.Left.Value, (int)settings.Value.Top.Value) : null;
        var savedScreen = saved is { } point ? Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(point)) : null;
        var screen = savedScreen ?? Screens.Primary; var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1280, 800); var scale = screen?.Scaling ?? RenderScaling;
        var desired = savedScreen != null ? saved!.Value : new PixelPoint(area.Right - (int)Math.Ceiling(Width * scale) - 24, area.Y + 32);
        Position = WindowSizing.KeepWithinWorkArea(desired, area, Width, Height, scale);
    }
    private void SavePosition() { settings.Value.Left = Position.X; settings.Value.Top = Position.Y; SavePreferences(); }
    private void SavePreferences() { try { settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { message.Text = "Preferences could not be saved"; message.IsVisible = true; } }
}
