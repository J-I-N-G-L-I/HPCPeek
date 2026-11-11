using AirePeek.Core;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AirePeek.Desktop.Infrastructure;

/// <summary>Exercises the actual indicator with isolated simulated sessions, without contacting a server.</summary>
internal static class LoginIndicatorSmoke
{
    internal static async Task RunAsync(MainWindow owner, string output, Action<bool, string> check)
    {
        var store = new SettingsStore(Path.Combine(output, "login-indicator-preferences"));
        store.Value.IsPinned = true;
        var sessions = ClusterCatalog.All.ToDictionary(cluster => cluster.Id,
            cluster => new SimulatedAuthenticatedSession(cluster.Id));
        await using var workspace = new ClusterWorkspace(store, id => sessions[id]);
        var window = new MainWindow(store, workspace, () => { }) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            await FrameAsync(window);
            var signedOutColor = ColorOf(window);
            check(!window.IsUserSignedIn && !workspace.Selected.IsConnected,
                "Login indicator begins signed out before any authenticated session exists");

            var ui = new NoAuthentication();
            window.SetExpanded(true); await FrameAsync(window);
            var panelPosition = window.Position;
            await window.ConnectProfileAsync(ClusterCatalog.AireId,
                new(new("aire.example.test", 22, "indicator-test-user")), ui);
            await FrameAsync(window);
            check(!window.IsExpanded && window.Height == MainWindow.CompactHeight && window.Position == panelPosition,
                "Successful sign-in returns to the compact panel without moving it");
            var signedInColor = ColorOf(window);
            check(window.IsUserSignedIn && workspace.Selected.IsConnected && !workspace.Selected.IsDemo
                && signedInColor != signedOutColor,
                "An authenticated Aire session changes the actual login indicator to its signed-in color");
            var aireTip = ToolTip.GetTip(window.LoginIndicator)?.ToString() ?? "";
            check(aireTip.StartsWith("Selected cluster — Aire: signed in as indicator-test-user", StringComparison.Ordinal)
                && AutomationProperties.GetName(window.LoginIndicator) == aireTip,
                "Signed-in indicator tooltip and accessibility text identify the selected cluster and account");

            await window.SelectClusterAsync(ClusterCatalog.CalderId);
            await FrameAsync(window);
            check(!window.IsUserSignedIn && workspace[ClusterCatalog.AireId].IsConnected
                && ColorOf(window) == signedOutColor,
                "Selecting signed-out Calder returns the indicator to signed-out color even while Aire remains signed in");
            var calderTip = ToolTip.GetTip(window.LoginIndicator)?.ToString() ?? "";
            check(calderTip.StartsWith("Selected cluster — Calder: not signed in", StringComparison.Ordinal)
                && calderTip.Contains("Aire: signed in as indicator-test-user", StringComparison.Ordinal),
                "Signed-out Calder tooltip distinguishes its own login state from the other signed-in cluster");
            check(sessions[ClusterCatalog.AireId].ConnectionCount == 1 && sessions[ClusterCatalog.CalderId].ConnectionCount == 0,
                "Changing the indicator's selected cluster never starts another authentication");

            await window.SelectClusterAsync(ClusterCatalog.AireId);
            await FrameAsync(window);
            check(window.IsUserSignedIn && ColorOf(window) == signedInColor,
                "Returning to authenticated Aire restores the signed-in indicator without reconnecting");

            await window.ConnectProfileAsync(ClusterCatalog.CalderId,
                new(new("calder.example.test", 22, "indicator-test-user")), ui);
            await FrameAsync(window);
            check(window.Controller.ClusterId == ClusterCatalog.CalderId && workspace.Entries.All(c => c.IsConnected)
                && !window.IsExpanded && window.Position == panelPosition,
                "Calder can sign in directly while Aire stays connected and the panel remains compact");
            await window.SelectClusterAsync(ClusterCatalog.AireId);

            await workspace.StartDemoAsync(ui);
            await FrameAsync(window);
            check(workspace.IsDemo && workspace.Selected.IsConnected && !window.IsUserSignedIn
                && ColorOf(window) == signedOutColor,
                "Connected Demo data never represents a signed-in user and keeps the signed-out indicator");
            check((ToolTip.GetTip(window.LoginIndicator)?.ToString() ?? "").StartsWith(
                "Selected cluster — Aire: Demo · no account signed in", StringComparison.Ordinal),
                "Demo indicator explicitly says that no account is signed in");
        }
        finally
        {
            window.PermitClose();
            window.Close();
        }
    }

    private static Color ColorOf(MainWindow window) => window.LoginIndicator.Background is ISolidColorBrush brush
        ? brush.Color : throw new InvalidOperationException("Login indicator did not resolve its status color.");

    private static async Task FrameAsync(Window window)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => completion.TrySetResult()));
        window.InvalidateVisual();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    // The wrapper is intentionally not a DemoSession: it exercises the production authenticated-session branch.
    // Its transport still uses only raw offline Slurm fixtures and cannot make any network connection.
    private sealed class SimulatedAuthenticatedSession(string clusterId) : ICommandSession
    {
        private readonly DemoSession fixtures = new(clusterId)
        {
            Scenario = clusterId == ClusterCatalog.AireId ? DemoScenario.Aire28 : DemoScenario.Calder7
        };
        public bool IsConnected => fixtures.IsConnected;
        public int ConnectionCount => fixtures.ConnectionCount;
        public Task ConnectAsync(ConnectionProfile profile, IAuthenticationUI ui, IHostKeyStore keys, CancellationToken cancellationToken = default) =>
            fixtures.ConnectAsync(profile, ui, keys, cancellationToken);
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default) => fixtures.ExecuteAsync(command, cancellationToken);
        public Task DisconnectAsync() => fixtures.DisconnectAsync();
        public ValueTask DisposeAsync() => fixtures.DisposeAsync();
    }

    private sealed class NoAuthentication : IAuthenticationUI
    {
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Offline login-indicator verification requested authentication.");
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Offline login-indicator verification requested host trust.");
    }
}
