using System.Text.Json;
using AirePeek.Core;
using AirePeek.Desktop.Dialogs;
using AirePeek.Desktop.Infrastructure;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AirePeek.Desktop;

/// <summary>Runs inside the extracted, self-contained desktop executable on its native OS.</summary>
internal static class SmokeTest
{
    private static async Task FrameAsync(Window window)
    {
        // Two compositor callbacks allow the preceding layout/data update to reach a frame.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => completion.TrySetResult()));
        window.InvalidateVisual();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    public static async Task ReadyAsync(MainWindow window, string output)
    {
        Directory.CreateDirectory(output);
        await FrameAsync(window);
        if (!window.Workspace.IsDemo || window.Workspace.RunningCount != 4 || window.Workspace.QueuedCount != 2)
            throw new InvalidOperationException("Both cluster demos were not rendered.");
        // Rasterizing the same live visual verifies a nonempty layout before signalling the external stopwatch.
        using var bitmap = Render(window.CaptureRoot, 1);
        var temporary = Path.Combine(output, "benchmark-ready.tmp");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new BenchmarkReady(
            true, Program.Startup.Elapsed.TotalMilliseconds, window.Width, window.Height, "demo", "en",
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription), EvidenceJsonContext.Default.BenchmarkReady));
        File.Move(temporary, Path.Combine(output, "benchmark-ready.json"), true);
    }

    public static async Task RunAsync(MainWindow window, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        try
        {
            await FrameAsync(window);
            await window.SelectClusterAsync(ClusterCatalog.AireId); await FrameAsync(window);
            var controller = window.Controller;
            var monitor = controller.Monitor!;
            Check(controller.IsDemo && controller.IsConnected, "Offline Demo uses the connected monitor pipeline");
            Check(window.Workspace.RunningCount == 4 && window.Workspace.QueuedCount == 2 && window.Workspace.Entries.All(c => c.Monitor!.Jobs!.Jobs.Count == 3), "Both cluster demos aggregate four running and two queued jobs without deduplicating overlapping job IDs");
            var capacity = monitor.Gpus!.Summarize("L40S", 2);
            Check(capacity.Unallocated == 2 && capacity.MaxPerNode == 2 && capacity.Candidates.Count == 1 && capacity.Candidates[0].NodeName == "gpu020", "28-node sample has two free GPUs, both on gpu020; down and drained nodes are excluded");
            Check(window.VisibleNodeTiles.Count == 28 && window.VisibleNodeTiles.Count(n => n.Availability == GpuNodeAvailability.Full) == 24 && window.VisibleNodeTiles.Count(n => n.Availability == GpuNodeAvailability.Unavailable) == 3, "Node matrix represents all 28 real node names, including three unavailable nodes");
            var calderCapacity = window.Workspace[ClusterCatalog.CalderId].Monitor!.Gpus!.Summarize("H200NVL", 1);
            Check(calderCapacity.Configured == 56 && calderCapacity.Unallocated == 20 && calderCapacity.MaxPerNode == 8 && calderCapacity.ExcludedNodes.Count == 1, "Calder sample deduplicates seven H200 NVL nodes: 56 configured, 20 eligible free, eight max per node");
            Check(window.Width == 340 && window.Height == MainWindow.CompactHeight && MainWindow.CompactHeight == 148, "Compact window is 340 by 148 DIP");
            Check(window.Title == "HPCPeek", "Official product name is HPCPeek");
            CheckRoundedWindow(window, Check);
            var compactGroups = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "CompactClusters");
            var groupOrigin = compactGroups.TranslatePoint(default, window)!.Value;
            var metricEnd = window.ExpandButton.GetVisualDescendants().OfType<TextBlock>()
                .Max(t => t.TranslatePoint(default, window)!.Value.X + t.Bounds.Width);
            Check(groupOrigin.X <= 170 && groupOrigin.X - metricEnd >= 20,
                "Compact cluster overviews sit further left with at least 20 DIP clear of job numbers");
            foreach (var cluster in ClusterCatalog.All)
            {
                var map = window.CompactNodes(cluster.Id);
                Check(map.Tiles.Count == cluster.NodeCount && map.GetVisualDescendants().OfType<Button>().Count() == cluster.NodeCount,
                    $"{cluster.Name}: compact overview includes every sample node");
                Check(map.GetVisualDescendants().OfType<Button>().All(b => !b.GetVisualDescendants().OfType<TextBlock>().Any()
                    && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))),
                    $"{cluster.Name}: compact tiles are color-only with explicit accessible capacity/status");
            }
            Check(window.ClusterModelSelector.Items.Count == 2 && window.ClusterModelSelector.Items.Cast<object>().Any(item => item.ToString() == "Calder · H200 NVL"), "Cluster selector offers Aire L40S and Calder H200 NVL without a Need filter");
            Check(new[] { "Running", "Queued" }.All(label => window.CaptureRoot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label)), "Default interface is English");
            Check(window.CaptureRoot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("Jobs ") == true && t.Text.Contains("GPUs ")), "Freshness explicitly labels Jobs and GPUs");
            Check(!window.IsUserSignedIn && AutomationProperties.GetName(window.LoginIndicator)?.Contains("no account signed in") == true, "Demo does not claim the user is signed in");
            await CheckClusterSwitchingAsync(window, Check);
            await CheckElapsedTimerAsync(window, Check);
            var initialPosition = window.Position;
            int? connections = controller.DemoConnectionCount;
            window.SetExpanded(true); await FrameAsync(window);
            Check(window.Width == 340 && window.Height <= 720 && window.Height > MainWindow.CompactHeight && window.IsExpanded, "Details expand at the same width within the native work area");
            await CheckNodeSelectionAsync(window, output, Check);
            var selected = window.JobRows["482087_12"];
            selected.Toggle(); window.ScrollOffset = new Vector(0, 45); await FrameAsync(window);
            window.ExpandButton.Focus();
            var focused = window.FocusManager?.GetFocusedElement();
            var offset = window.ScrollOffset;
            var position = window.Position;
            // Reset only the sampling timers to exercise an immediate real monitor refresh, without authenticating.
            monitor.ResetAfterConnect(); await controller.RefreshAsync(); await FrameAsync(window);
            Check(ReferenceEquals(selected, window.JobRows["482087_12"]) && selected.IsExpanded, "Refresh retains array-job row identity and expansion");
            Check(window.ScrollOffset == offset, "Refresh retains scroll offset");
            Check(ReferenceEquals(focused, window.FocusManager?.GetFocusedElement()), "Refresh retains keyboard focus");
            Check(window.Position == position, "Refresh retains window position");
            Check(controller.DemoConnectionCount == connections, "Expand and refresh reuse the authenticated connection");
            selected.Toggle(); window.ScrollOffset = default;
            await controller.ChangeDemoAsync(DemoScenario.Disconnected, new NoAuthentication()); await FrameAsync(window);
            Check(monitor.IsDisconnected && monitor.Jobs?.Jobs.Count == 3 && monitor.Gpus?.Summarize("L40S", 2).Unallocated == 2, "Disconnect retains both successful snapshots");
            Check(window.Workspace[ClusterCatalog.CalderId].IsConnected && window.CompactNodes(ClusterCatalog.CalderId).Tiles.Count == 7, "An Aire disconnect preserves the independent connected Calder session and overview");
            Capture(window.CaptureRoot, Path.Combine(output, "disconnected-150.png"), 1.5);
            await controller.ChangeDemoAsync(DemoScenario.Empty, new NoAuthentication()); await FrameAsync(window);
            Check(monitor.Jobs?.Jobs.Count == 0 && controller.IsConnected, "Fresh empty queue is distinct from disconnected data");
            Capture(window.CaptureRoot, Path.Combine(output, "empty-150.png"), 1.5);
            var rememberedModel = controller.Preferences.GpuModel;
            await controller.ChangeDemoAsync(DemoScenario.Partial, new NoAuthentication()); await FrameAsync(window);
            Check(monitor.Gpus?.Summarize("L40S", 2).IsPartial == true, "Missing fields and shared/MIG data are partial");
            Check(controller.Preferences.GpuModel == rememberedModel, "Partial GPU visibility never replaces the remembered model");
            Capture(window.CaptureRoot, Path.Combine(output, "partial-150.png"), 1.5);
            await window.StartDemoAsync(); await FrameAsync(window);
            connections = controller.DemoConnectionCount;
            window.HidePanel(); Check(!window.IsVisible, "Hiding leaves application and monitor alive");
            window.ShowPanel(); await FrameAsync(window);
            Check(controller.DemoConnectionCount == connections, "Showing a hidden panel does not authenticate");
            Check(window.MoreMenu.Items.Count == 5 && window.MoreMenu.Items.OfType<MenuItem>().Count() == 3, "More menu has three scoped action groups");
            Check(window.JobRows.Values.All(r => r.JobMenu.Items.OfType<MenuItem>().Count() == 3), "Job menus contain only three job actions");

            App.ApplyTheme("light");
            foreach (double scale in new[] { 1.25, 1.5, 2.0 })
            {
                window.SetExpanded(false); await FrameAsync(window);
                Capture(window.CaptureRoot, Path.Combine(output, $"compact-{(int)(scale * 100)}.png"), scale);
                window.SetExpanded(true); await FrameAsync(window);
                Capture(window.CaptureRoot, Path.Combine(output, $"expanded-{(int)(scale * 100)}.png"), scale);
            }
            window.SetExpanded(false); await FrameAsync(window);
            Capture(window.CaptureRoot, Path.Combine(output, "compact-100.png"), 1);
            window.SetExpanded(true); await FrameAsync(window);
            Capture(window.CaptureRoot, Path.Combine(output, "expanded-100.png"), 1);
            window.SetExpanded(false); await FrameAsync(window);
            window.OpenMoreMenu(); await FrameAsync(window);
            Check(window.MoreMenu.IsOpen, "Live native-window popup menu opens");
            CaptureMenu(window, Path.Combine(output, "menu-light.png"));
            window.MoreMenu.Close();
            await CaptureClusterAsync(window, output, "light");
            await CaptureElapsedAsync(window, output, "light");
            await CaptureDialogsAsync(window, output, "light", Check);
            await CaptureNotificationsAsync(window, output, "light", Check);
            App.ApplyTheme("dark"); await FrameAsync(window);
            Capture(window.CaptureRoot, Path.Combine(output, "compact-dark-150.png"), 1.5);
            window.SetExpanded(true); await FrameAsync(window);
            Capture(window.CaptureRoot, Path.Combine(output, "expanded-dark-150.png"), 1.5);
            window.SetExpanded(false); await FrameAsync(window);
            window.OpenMoreMenu(); await FrameAsync(window);
            CaptureMenu(window, Path.Combine(output, "menu-dark.png"));
            window.MoreMenu.Close();
            await CaptureClusterAsync(window, output, "dark");
            await CaptureElapsedAsync(window, output, "dark");
            await CaptureDialogsAsync(window, output, "dark", Check);
            await CaptureNotificationsAsync(window, output, "dark", Check);
            await LoginIndicatorSmoke.RunAsync(window, output, Check);
            checks.Add("Live Avalonia layouts rendered at 125%, 150%, 200%, in light and dark themes; this does not change OS display scaling");
            await File.WriteAllTextAsync(Path.Combine(output, "smoke-result.json"), JsonSerializer.Serialize(new SmokeSuccess(
                true, checks, System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), window.RenderScaling,
                "340x148", "340x720, capped to native work area", false, false, DateTimeOffset.UtcNow), EvidenceJsonContext.Default.SmokeSuccess));
            await App.QuitAsync();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "smoke-result.json"), JsonSerializer.Serialize(new SmokeFailure(false, checks, ex.ToString()), EvidenceJsonContext.Default.SmokeFailure));
            await App.QuitAsync(1);
        }
    }
    private static async Task CheckClusterSwitchingAsync(MainWindow window, Action<bool, string> check)
    {
        bool wasExpanded = window.IsExpanded;
        window.SetExpanded(true); await FrameAsync(window);
        var counts = window.Workspace.Entries.ToDictionary(c => c.ClusterId, c => c.DemoConnectionCount);
        var aireRow = window.JobRows["482103"]; aireRow.Toggle();
        window.Workspace[ClusterCatalog.CalderId].Preferences.GpuModel = "L40S";
        await window.SelectClusterAsync(ClusterCatalog.CalderId); await FrameAsync(window);
        var calderRow = window.JobRows["482103"];
        check(window.Controller.ClusterId == ClusterCatalog.CalderId && window.VisibleNodeTiles.Count == 7
            && window.ClusterModelSelector.SelectedItem?.ToString() == "Calder · H200 NVL", "Switching to Calder selects its H200 model and seven-node details");
        check(!window.ClusterModelSelector.Items.Cast<object>().Any(item => item.ToString()?.StartsWith("Calder · L40", StringComparison.OrdinalIgnoreCase) == true),
            "An obsolete Calder L40 preference does not create an unsupported model option");
        var nodeButtons = window.ExpandedNodeGrid.GetVisualDescendants().OfType<Button>().ToArray();
        var freeTiles = window.VisibleNodeTiles.Where(t => t.Availability == GpuNodeAvailability.Available).ToArray();
        var freeBrushes = freeTiles.Select(t => nodeButtons.Single(b => AutomationProperties.GetName(b)?.StartsWith(t.Name + " ·", StringComparison.Ordinal) == true)
            .GetVisualDescendants().OfType<Border>().First(b => b.Background is ISolidColorBrush).Background)
            .Cast<ISolidColorBrush>().Select(b => b.Color).Distinct().ToArray();
        check(freeTiles.Select(t => t.Available).Order().SequenceEqual(new int?[] { 2, 4, 6, 8 }) && freeBrushes.Length == 1,
            "Calder nodes with two, four, six and eight free GPUs all use one green");
        check(nodeButtons.Any(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "8"))
            && window.CaptureRoot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "7 nodes · 8 GPUs/node · 56 configured"),
            "Calder retains the exact eight-free cell and distinguishes installed eight-per-node capacity");
        check(!window.CaptureRoot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "3+")
            && new[] { "Free", "Full", "Unavailable", "Unknown" }.All(label => window.CaptureRoot.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label)),
            "Node legend uses availability states with no three-GPU color cap");
        check(!ReferenceEquals(aireRow, calderRow) && !calderRow.IsExpanded
            && calderRow.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "H200 large model pretraining"),
            "Overlapping job IDs do not reuse Aire controls, experiment text or expansion in Calder");
        await window.SelectClusterAsync(ClusterCatalog.AireId); await FrameAsync(window);
        check(window.VisibleNodeTiles.Count == 28 && window.JobRows["482103"].GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.Text == "Vision Transformer large-scale pretraining"), "Switching back restores Aire jobs and its 28-node sample");
        check(window.Workspace.Entries.All(c => c.DemoConnectionCount == counts[c.ClusterId]), "Cluster and model switching reuse each authenticated connection without new login");
        window.SetExpanded(wasExpanded); await FrameAsync(window);
    }
    private static async Task CheckElapsedTimerAsync(MainWindow window, Action<bool, string> check)
    {
        window.SetExpanded(true);
        var row = window.JobRows["482103"];
        row.Toggle(); await FrameAsync(window);
        var before = row.DisplayedElapsed;
        var sample = window.Controller.Monitor!.Jobs!.SampledAt;
        var connections = window.Controller.DemoConnectionCount;
        // Timer phase and integer-second rounding can leave the first tick unchanged.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (row.DisplayedElapsed == before && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
        await FrameAsync(window);
        check(row.DisplayedElapsed.StartsWith('~') && row.DisplayedElapsed != before, "Expanded running-job elapsed advances on the one-second UI timer");
        check(window.Controller.Monitor.Jobs.SampledAt == sample && window.Controller.DemoConnectionCount == connections,
            "Live elapsed changes without executing another queue query or authenticating");
        window.TickJobTimers(sample + window.Controller.Monitor.RefreshInterval * 3);
        check(row.ElapsedLabel == "Last sampled elapsed" && !row.DisplayedElapsed.StartsWith('~'), "Stale queue data stops local elapsed extrapolation");
        window.TickJobTimers(DateTimeOffset.UtcNow);
        row.Toggle(); window.ScrollOffset = default; window.SetExpanded(false); await FrameAsync(window);
    }
    private static async Task CaptureElapsedAsync(MainWindow window, string output, string theme)
    {
        await window.SelectClusterAsync(ClusterCatalog.AireId); window.SetExpanded(true); await FrameAsync(window);
        var row = window.JobRows["482103"];
        if (!row.IsExpanded) row.Toggle();
        await FrameAsync(window); row.BringIntoView(); await FrameAsync(window);
        Capture(window.CaptureRoot, Path.Combine(output, $"job-elapsed-{theme}-150.png"), 1.5);
        row.Toggle(); window.ScrollOffset = default; window.SetExpanded(false); await FrameAsync(window);
    }
    private static async Task CaptureClusterAsync(MainWindow window, string output, string theme)
    {
        await window.SelectClusterAsync(ClusterCatalog.CalderId); window.SetExpanded(true); await FrameAsync(window);
        Capture(window.CaptureRoot, Path.Combine(output, $"expanded-calder-{theme}-150.png"), 1.5);
        await window.SelectClusterAsync(ClusterCatalog.AireId); window.SetExpanded(false); await FrameAsync(window);
    }
    private static async Task CheckNodeSelectionAsync(MainWindow window, string output, Action<bool, string> check)
    {
        var map = window.ExpandedNodeGrid;
        var node = map.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b)?.StartsWith("gpu020 ·", StringComparison.Ordinal) == true);
        var labels = map.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).ToArray();
        window.ExpandButton.Focus(); await FrameAsync(window);
        var original = labels.ToDictionary(t => t, t => (t.Bounds, t.TranslatePoint(default, map)));
        bool Stable() => labels.All(t => original[t] == (t.Bounds, t.TranslatePoint(default, map)));
        foreach (double scale in new[] { 1.25, 1.5, 2.0 }) Capture(map, Path.Combine(output, $"nodes-before-{(int)(scale * 100)}.png"), scale);
        node.Focus(NavigationMethod.Tab); await FrameAsync(window);
        check(Stable(), "Keyboard focus preserves every node label's bounds and position");
        node.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FrameAsync(window);
        check(Stable(), "Selecting a node changes only its overlay, never label geometry");
        foreach (double scale in new[] { 1.25, 1.5, 2.0 }) Capture(map, Path.Combine(output, $"nodes-selected-{(int)(scale * 100)}.png"), scale);
        Capture(window.CaptureRoot, Path.Combine(output, "selected-node-150.png"), 1.5);
        node.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.ExpandButton.Focus(); await FrameAsync(window);
        check(Stable(), "Deselecting and blurring a node preserves label geometry");
    }
    private static async Task CaptureDialogsAsync(MainWindow owner, string output, string theme, Action<bool, string> check)
    {
        // Real application windows, with synthetic non-secret inputs. No SSH connection is made.
        var endpoint = new SshEndpoint("aire.example.test", 22, "demo-user");
        var gateway = new SshEndpoint("gateway.example.test", 22, "demo-user");
        var profile = new ConnectionProfile(endpoint, gateway, true);
        async Task CaptureDialog(Window dialog, string name, Action? inspect = null)
        {
            var panelPosition = owner.Position;
            var closed = dialog.ShowDialog<object?>(owner);
            try
            {
                await FrameAsync(dialog); inspect?.Invoke(); await FrameAsync(dialog);
                var area = owner.Screens.ScreenFromWindow(owner)!.WorkingArea;
                var size = dialog.FrameSize ?? dialog.ClientSize;
                check(Math.Abs(dialog.Position.X + size.Width * dialog.RenderScaling / 2 - (area.X + area.Width / 2d)) <= 3
                    && Math.Abs(dialog.Position.Y + size.Height * dialog.RenderScaling / 2 - (area.Y + area.Height / 2d)) <= 3,
                    $"{theme}: {name} opens in the center of the panel's display");
                check(owner.Position == panelPosition && dialog.Icon != null, $"{theme}: {name} has the HPCPeek icon and leaves panel position unchanged");
                Capture(dialog, Path.Combine(output, $"{name}-{theme}.png"), 1);
            }
            finally { dialog.Close(); await closed; }
        }
        var direct = new ConnectionWindow();
        var failure = new MessageWindow("Sign-in not completed", "Aire did not complete additional verification. Your saved password was not changed. Reconnect and follow the server's Duo prompts.", offerEditConnection: true);
        await CaptureDialog(failure, "message-signin", () =>
        {
            var actions = failure.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "MessageActions");
            var bottom = actions.TranslatePoint(default, failure)!.Value.Y + actions.Bounds.Height;
            check(failure.Bounds.Height < 250 && failure.Bounds.Height - bottom is >= 20 and <= 24,
                $"{theme}: short sign-in messages fit their content with a 22-DIP bottom inset");
        });
        var longMessage = new MessageWindow("Sign-in not completed", string.Join("\n\n", Enumerable.Repeat("The server requested additional verification. Check the instructions and reconnect when ready.", 20)));
        await CaptureDialog(longMessage, "message-long", () =>
        {
            var close = longMessage.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Close");
            var bottom = close.TranslatePoint(default, longMessage)!.Value.Y + close.Bounds.Height;
            check(longMessage.Bounds.Height <= 480 && bottom <= longMessage.Bounds.Height - 20,
                $"{theme}: long messages scroll while the Close action stays inside the window");
        });
        await CaptureDialog(direct, "connection-direct", () =>
        {
            check(!direct.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString()?.Contains("demo", StringComparison.OrdinalIgnoreCase) == true), $"{theme}: login has no Demo action");
            var fields = direct.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name?.StartsWith("Target") == true || t.Name?.StartsWith("Gateway") == true).ToDictionary(t => t.Name!);
            check(fields["TargetHost"].Text == "aire.leeds.ac.uk" && fields["GatewayHost"].Text == "rash.leeds.ac.uk" && fields["TargetPort"].Text == "22" && fields["GatewayPort"].Text == "22",
                $"{theme}: Aire and gateway host defaults use the user-provided addresses and port 22");
            fields["TargetUsername"].Text = "demo-user";
            check(fields["GatewayUsername"].Text == "demo-user", $"{theme}: first username automatically fills the gateway username");
            check(direct.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString()?.StartsWith("From MobaXterm") == true), $"{theme}: From MobaXterm keeps the existing help entry");
        });
        var calderLogin = new ConnectionWindow(clusterName: "Calder");
        await CaptureDialog(calderLogin, "connection-calder", () =>
        {
            var host = calderLogin.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TargetHost");
            check(calderLogin.Title == "Connect to Calder" && host.Text == "calder.leeds.ac.uk" && !host.IsReadOnly,
                $"{theme}: Calder uses the user-provided editable default host");
        });
        var rememberedLogin = new ConnectionWindow();
        await CaptureDialog(rememberedLogin, "connection-remember", () =>
        {
            var fields = rememberedLogin.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name?.StartsWith("Target") == true || t.Name?.StartsWith("Gateway") == true).ToDictionary(t => t.Name!);
            fields["TargetUsername"].Text = "demo-user";
            fields["TargetHost"].Text = "custom-aire.example.test";
            var storage = rememberedLogin.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "PasswordStorage");
            storage.SelectedIndex = 1;
            check(rememberedLogin.ReadProfile().RememberPassword && rememberedLogin.ReadProfile().Gateway == null,
                $"{theme}: direct login can explicitly remember its password on this device");
            rememberedLogin.SelectCluster(ClusterCatalog.CalderId);
            check(rememberedLogin.ReadProfile().Target.Host == "calder.leeds.ac.uk" && rememberedLogin.ReadProfile().Target.Username == "demo-user"
                && !rememberedLogin.ReadProfile().RememberPassword, $"{theme}: first login can switch to Calder without silently opting into password storage");
            rememberedLogin.SelectCluster(ClusterCatalog.AireId);
            check(rememberedLogin.ReadProfile().Target.Host == "custom-aire.example.test" && rememberedLogin.ReadProfile().RememberPassword,
                $"{theme}: switching back preserves the unsent custom host and password-storage choice");
        });
        var savedProfile = profile with { Target = endpoint with { Port = 2222 }, Gateway = gateway with { Port = 2200 } };
        var savedLogin = new ConnectionWindow(savedProfile);
        check(savedLogin.ReadProfile() == savedProfile, $"{theme}: saved login restores both hosts, non-default ports, usernames and sharing choice");
        await CaptureDialog(savedLogin, "connection-gateway", () =>
        {
            var username = savedLogin.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TargetUsername");
            username.Text = "another-user";
            check(savedLogin.ReadProfile().Target.Username == "another-user", $"{theme}: the prefilled account remains editable without restarting the computer");
            username.Text = endpoint.Username;
        });
        var differentUsers = new ConnectionWindow(profile with { Gateway = gateway with { Username = "other-gateway-user" }, ReuseGatewayPassword = false });
        check(differentUsers.ReadProfile().Gateway?.Username == "other-gateway-user" && !differentUsers.ReadProfile().ReuseGatewayPassword, $"{theme}: a distinct gateway account is preserved and not silently password-shared");
        var password = new AuthenticationPromptWindow(new(endpoint, "password", "", [new("Password:", true)]));
        await CaptureDialog(password, "authentication-password", () => check(password.GetVisualDescendants().OfType<TextBox>().Single().PasswordChar != '\0', $"{theme}: password input is masked"));
        var duo = new AuthenticationPromptWindow(new(gateway, "Duo two-factor login", "Choose an option:\n1. Duo Push to your registered device\n2. Phone call\n3. Enter a passcode", [new("Passcode or option (1–3):", false)]));
        await CaptureDialog(duo, "authentication-duo", () => check(duo.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("3. Enter a passcode") == true), $"{theme}: server Duo instructions and choices are retained"));
        await CaptureDialog(new HostTrustWindow(new(endpoint, "ssh-ed25519", "SHA256:demoFingerprintForLayoutOnlyNotARealHostKey123")), "host-fingerprint");
        var store = new SettingsStore(Path.Combine(output, "dialog-preferences"));
        store.GetCluster(ClusterCatalog.AireId).Connection = profile;
        store.GetCluster(ClusterCatalog.CalderId).Connection = new(new("calder.example.test", 2223, "calder-user"));
        for (int section = 0; section < 3; section++)
        {
            var settings = new SettingsWindow(store); settings.SelectSection(section);
            await CaptureDialog(settings, $"settings-{section}");
        }
        store.Value.SelectedClusterId = ClusterCatalog.CalderId;
        var calderSettings = new SettingsWindow(store);
        await CaptureDialog(calderSettings, "settings-calder", () =>
        {
            var selector = calderSettings.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ConnectionCluster");
            check(selector.Items.Count == 2 && selector.SelectedItem?.ToString() == "Calder"
                && calderSettings.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "calder.example.test:2223"),
                $"{theme}: Settings selects Calder and shows its independent saved connection");
        });
        store.Value.SelectedClusterId = ClusterCatalog.AireId;
        store.Value.RefreshIntervalSeconds = 45;
        var custom = new SettingsWindow(store);
        await CaptureDialog(custom, "settings-refresh-custom", () =>
        {
            var input = custom.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CustomRefreshSeconds");
            var options = custom.GetVisualDescendants().OfType<ComboBox>().Single(t => t.Name == "AutoRefresh");
            check(input.IsEffectivelyVisible && input.Text == "45" && options.SelectedIndex == 4, $"{theme}: custom refresh seconds are restored into the visible settings form");
            check(options.Items.Count == 5, $"{theme}: refresh offers 5, 15, 30, 60 minutes and Custom");
        });
        check(true, $"{theme}: direct connection, gateway, password, Duo, fingerprint and all three settings pages rendered as live windows");
    }
    private static async Task CaptureNotificationsAsync(MainWindow owner, string output, string theme, Action<bool, string> check)
    {
        foreach (var sample in new[] { ("COMPLETED", "completed", "Completed", ClusterCatalog.AireId),
            ("FAILED", "failed", "Failed", ClusterCatalog.CalderId), ("TIMEOUT", "timeout", "Timed out", ClusterCatalog.CalderId) })
        {
            var data = new JobNotification("482119", "Experiment result", "Synthetic notification for layout verification",
                sample.Item1, "Vision foundation model finetuning · high-resolution ablation", sample.Item4);
            JobNotification? opened = null;
            var notice = new NotificationWindow(data, item => opened = item, autoDismiss: false);
            bool ownerWasActive = owner.IsActive;
            notice.Show();
            try
            {
                await FrameAsync(notice);
                CheckRoundedWindow(notice, check);
                check(notice.Bounds.Height < 170 && notice.Bounds.Height -
                    (notice.OpenJobDetailsButton.TranslatePoint(default, notice)!.Value.Y + notice.OpenJobDetailsButton.Bounds.Height) <= 13,
                    $"{theme}: {sample.Item2} notice has a content-sized height without an empty footer");
                check(notice.StateLabel == sample.Item3 && !notice.ShowActivated && !notice.ShowInTaskbar && !notice.IsActive
                    && (!ownerWasActive || owner.IsActive), $"{theme}: {sample.Item2} notice shows a clear result without activating its window");
                check(notice.CopyJobIdButton.IsVisible && notice.OpenJobDetailsButton.IsVisible
                    && AutomationProperties.GetName(notice.CopyJobIdButton)?.Contains(data.JobId) == true,
                    $"{theme}: {sample.Item2} notice exposes copy ID, open details and dismiss controls");
                Capture(notice.CaptureRoot, Path.Combine(output, $"notification-{sample.Item2}-{theme}.png"), 1.5);
                if (sample.Item1 == "COMPLETED")
                {
                    notice.OpenJobDetailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(ReferenceEquals(opened, data) && !notice.IsVisible, $"{theme}: View job forwards the complete cluster-qualified notification and closes the card");
                }
                else
                {
                    notice.DismissButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    check(opened is null && !notice.IsVisible, $"{theme}: dismissing {sample.Item2} closes only that notice");
                }
            }
            finally { notice.Close(); }
        }
    }
    private static void CheckRoundedWindow(Window window, Action<bool, string> check)
    {
        check(window.ActualTransparencyLevel == WindowTransparencyLevel.Transparent,
            $"{window.Title}: native compositor supports transparent rounded corners");
        using var bitmap = Render(window, 1);
        int stride = bitmap.PixelSize.Width * 4, bytes = stride * bitmap.PixelSize.Height;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
        try
        {
            bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), buffer, bytes, stride);
            int right = (bitmap.PixelSize.Width - 1) * 4, bottom = (bitmap.PixelSize.Height - 1) * stride;
            check(new[] { 3, right + 3, bottom + 3, bottom + right + 3 }
                .All(index => System.Runtime.InteropServices.Marshal.ReadByte(buffer, index) == 0),
                $"{window.Title}: all four full-window corner pixels are transparent, not a rectangular background");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    private static void CaptureMenu(MainWindow window, string path)
    {
        if (TopLevel.GetTopLevel(window.MoreMenu) is not Control root || root.Bounds.Width <= 0) throw new InvalidOperationException("Popup root was not rendered.");
        Capture(root, path, 1.5);
    }
    private static RenderTargetBitmap Render(Control root, double scale)
    {
        if (root.Bounds.Width <= 0 || root.Bounds.Height <= 0) throw new InvalidOperationException("Empty visual layout.");
        var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(root.Bounds.Width * scale), (int)Math.Ceiling(root.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(root); return bitmap;
    }
    private static void Capture(Control root, string path, double scale) { using var bitmap = Render(root, scale); bitmap.Save(path, PngBitmapEncoderOptions.Default); }
    private sealed class NoAuthentication : IAuthenticationUI
    {
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken) => throw new InvalidOperationException("Demo unexpectedly requested authentication.");
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken) => throw new InvalidOperationException("Demo unexpectedly requested host trust.");
    }
}
