using System.Globalization;
using System.Text.Json;
using AirePeek.Desktop.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AirePeek.Desktop;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? desktop;
    private SettingsStore? settings;
    private ClusterWorkspace? workspace;
    private MainWindow? panel;
    private NativePlatform? platform;
    private TrayIcon? tray;
    private bool quitting;
    private string theme = "system";
    private static readonly string[] PaletteKeys = ["Surface", "Panel", "Ink", "Muted", "Line", "Attention", "Failure", "Accent", "Selection", "TileInk", "TileFreeInk", "TileFull", "TileFree", "TileUnavailable", "TileUnknown"];

    public override void Initialize()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
        Name = "HPCPeek";
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, args) =>
            {
                if (quitting) return;
                args.Cancel = true;
                _ = QuitAsync();
            };
            var args = desktop.Args ?? [];
            var smokeOutput = Option(args, "--smoke-test");
            var benchmarkOutput = Option(args, "--benchmark");
            var output = smokeOutput ?? benchmarkOutput;
            try
            {
                settings = new SettingsStore(output is null ? null : Path.Combine(Path.GetFullPath(output), "portable-data"));
                ApplyTheme(settings.Value.Theme);
                if (PlatformSettings is { } platformSettings) platformSettings.ColorValuesChanged += PlatformColorsChanged;
                workspace = new ClusterWorkspace(settings);
                panel = new MainWindow(settings, workspace, () => _ = QuitAsync(), args.Contains("--inspect"));
                desktop.MainWindow = panel;
                platform = new NativePlatform(panel);
                platform.ConnectionInvalidated += async () => { if (!quitting) await workspace.InvalidateAsync(); };
                workspace.Notification += notification =>
                {
                    if (settings.Value.Notifications && !quitting) platform.ShowNotification(notification, item => _ = panel.ShowJobAsync(item));
                };
                CreateTray();
                var started = false;
                panel.Opened += async (_, _) =>
                {
                    if (started) return;
                    started = true;
                    try
                    {
                        if (args.Contains("--demo") || output is not null) await panel.StartDemoAsync();
                        else await panel.ChooseConnectionAsync();
                        if (smokeOutput is not null) { await SmokeTest.RunAsync(panel, smokeOutput); await QuitAsync(); }
                        else if (benchmarkOutput is not null) await SmokeTest.ReadyAsync(panel, benchmarkOutput);
                    }
                    catch (Exception ex)
                    {
                        if (output is not null)
                        {
                            Directory.CreateDirectory(output);
                            await File.WriteAllTextAsync(Path.Combine(output, "startup-error.txt"), $"Startup verification failed ({ex.GetType().Name}). No authentication data was logged.");
                            await QuitAsync(1);
                        }
                        else await ShowErrorAsync("HPCPeek could not finish starting. Close the app and try again. Your saved settings were preserved.");
                    }
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                if (output is not null)
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "startup-error.txt"), "Settings could not be loaded. The original settings file was preserved.");
                    Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
                }
                else ShowStartupError();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static string? Option(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void CreateTray()
    {
        if (panel is null || workspace is null || settings is null) return;
        var menu = new NativeMenu();
        var show = new NativeMenuItem("Show HPCPeek"); show.Click += (_, _) => panel.ShowPanel();
        var pin = new NativeMenuItem("Pin as floating window") { ToggleType = MenuItemToggleType.CheckBox };
        pin.Click += (_, _) => { panel.TogglePin(); panel.ShowPanel(); };
        var refresh = new NativeMenuItem("Refresh now");
        refresh.Click += async (_, _) =>
        {
            if (!workspace.Selected.IsConnected) panel.ShowPanel();
            await panel.RefreshOrReconnectAsync();
        };
        var preferences = new NativeMenuItem("Settings…") { Gesture = Shortcut(Key.OemComma) };
        preferences.Click += async (_, _) => { panel.ShowPanel(); await panel.OpenSettingsAsync(); };
        var quit = new NativeMenuItem("Quit HPCPeek") { Gesture = Shortcut(Key.Q) }; quit.Click += async (_, _) => await QuitAsync();
        menu.Items.Add(show); menu.Items.Add(pin); menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(refresh); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(preferences);
        menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quit);
        menu.NeedsUpdate += (_, _) =>
        {
            pin.IsChecked = settings.Value.IsPinned;
            refresh.Header = workspace.Selected.IsConnecting ? "Cancel connection" : workspace.Selected.IsConnected ? "Refresh now" : "Reconnect…";
            refresh.IsEnabled = !workspace.Selected.IsConnected || workspace.Selected.Monitor?.RefreshCooldownRemaining == TimeSpan.Zero;
        };
        tray = new TrayIcon { ToolTipText = "HPCPeek", Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => panel.ShowPanel();
        if (OperatingSystem.IsMacOS()) MacOSProperties.SetIsTemplateIcon(tray, true);
        RefreshTrayIcon();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private static KeyGesture Shortcut(Key key) => new(key, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    public static void ApplyTheme(string selected)
    {
        if (Current is not App app) return;
        app.theme = selected is "light" or "dark" ? selected : "system";
        app.RequestedThemeVariant = app.theme switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        app.ApplyContrast();
        app.RefreshTrayIcon();
    }

    private void PlatformColorsChanged(object? sender, PlatformColorValues colors) => Dispatcher.UIThread.Post(() => { ApplyContrast(); RefreshTrayIcon(); });
    private void ApplyContrast()
    {
        foreach (var key in PaletteKeys) Resources.Remove(key);
        if (PlatformSettings?.GetColorValues().ContrastPreference != ColorContrastPreference.High) return;
        // Opaque, motion-free surfaces are used in every theme. High contrast also removes muted color distinctions.
        bool dark = theme == "dark" || theme == "system" && PlatformSettings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
        var background = dark ? Brushes.Black : Brushes.White; var foreground = dark ? Brushes.White : Brushes.Black;
        foreach (var key in new[] { "Surface", "Panel" }) Resources[key] = background;
        foreach (var key in new[] { "Ink", "Muted", "Line", "Attention", "Failure", "Accent" }) Resources[key] = foreground;
        Resources["Selection"] = new SolidColorBrush(dark ? Color.Parse("#303030") : Color.Parse("#E5E5E5"));
        foreach (var key in new[] { "TileFull", "TileFree", "TileUnavailable", "TileUnknown" }) Resources[key] = background;
        Resources["TileInk"] = Resources["TileFreeInk"] = foreground;
    }

    private void RefreshTrayIcon()
    {
        if (tray is null) return;
        var lightIcon = false;
        if (OperatingSystem.IsWindows())
        {
            lightIcon = PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
            try { lightIcon = Convert.ToInt32(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", lightIcon ? 0 : 1)) == 0; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        tray.Icon = AppBrand.CreateTrayIcon(lightIcon);
    }

    public static async Task QuitAsync(int code = 0)
    {
        if (Current is not App app || app.quitting) return;
        app.quitting = true;
        app.panel?.PermitClose();
        if (app.PlatformSettings is { } platformSettings) platformSettings.ColorValuesChanged -= app.PlatformColorsChanged;
        app.platform?.Dispose();
        app.tray?.Dispose();
        try { if (app.workspace is not null) await app.workspace.DisposeAsync(); }
        finally { app.desktop?.Shutdown(code); }
    }

    private void ShowStartupError()
    {
        var error = ErrorWindow("HPCPeek could not read its settings. The original file has been preserved. Check that the settings folder is readable and the settings.json file is valid. Close HPCPeek before repairing or restoring that file.");
        error.Closed += (_, _) => desktop?.Shutdown(1);
        desktop!.MainWindow = error;
    }

    private async Task ShowErrorAsync(string message)
    {
        if (panel is null) return;
        await ErrorWindow(message).ShowDialog(panel);
        await QuitAsync(1);
    }
    private static Window ErrorWindow(string message)
    {
        var window = new Window { Title = "HPCPeek", Icon = AppBrand.CreateWindowIcon(), Width = 450, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        content.Children.Add(new TextBlock { Text = "HPCPeek could not start", FontSize = 21 });
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => window.Close(); content.Children.Add(close); window.Content = content; return window;
    }
}
