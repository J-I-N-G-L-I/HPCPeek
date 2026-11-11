using System.Diagnostics;
using AirePeek.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace AirePeek.Desktop.Infrastructure;

/// <summary>A small, nonactivating result card. Its content is also used by the desktop smoke captures.</summary>
internal sealed class NotificationWindow : Window
{
    private readonly DispatcherTimer closeTimer = new();
    private readonly Stopwatch countdown = new();
    private readonly bool autoDismiss;
    private TimeSpan remaining = TimeSpan.FromSeconds(8);
    private bool closed;
    internal JobNotification Notification { get; }
    internal Control CaptureRoot { get; }
    internal Button CopyJobIdButton { get; }
    internal Button OpenJobDetailsButton { get; }
    internal Button DismissButton { get; }
    internal string StateLabel { get; }

    internal NotificationWindow(JobNotification notification, Action<JobNotification>? openJobDetails = null, bool autoDismiss = true)
    {
        Notification = notification;
        this.autoDismiss = autoDismiss;
        StateLabel = ResultLabel(notification);
        Title = $"HPCPeek · {StateLabel}"; Icon = AppBrand.CreateWindowIcon();
        Width = 360; SizeToContent = SizeToContent.Height; MaxHeight = 230;
        CanResize = false; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        FontFamily = OperatingSystem.IsWindows() ? new FontFamily("Segoe UI") : FontFamily.Default;

        var surface = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(15, 11, 15, 10) };
        surface.Classes.Add("surface"); CaptureRoot = surface; Content = surface;
        var content = new StackPanel { Spacing = 7 }; surface.Child = content;
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24") };
        string cluster = ClusterCatalog.All.FirstOrDefault(c => c.Id.Equals(notification.Cluster, StringComparison.OrdinalIgnoreCase))?.Name
            ?? notification.Cluster ?? "Slurm";
        var identity = Ui.Text($"HPCPeek  /  {cluster}", 11, "muted"); identity.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(identity);
        DismissButton = Ui.Button("Dismiss notification", Close, true);
        DismissButton.Content = Icons.Create("x", 14); DismissButton.Classes.Add("icon");
        DismissButton.Width = DismissButton.Height = DismissButton.MinHeight = 24; DismissButton.Padding = new Thickness(5);
        ToolTip.SetTip(DismissButton, "Dismiss"); Grid.SetColumn(DismissButton, 1); heading.Children.Add(DismissButton);
        content.Children.Add(heading);

        var result = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        string stateBrush = notification.State is "COMPLETED" or "RUNNING" ? "Accent"
            : notification.State is "FAILED" or "TIMEOUT" or "OUT_OF_MEMORY" or "NODE_FAIL" or "BOOT_FAIL" or "DEADLINE" ? "Failure" : "Attention";
        result.Children.Add(ResultIcon(notification.State, stateBrush));
        var state = Ui.Text(StateLabel, 17); state.FontWeight = FontWeight.SemiBold;
        state[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(stateBrush);
        result.Children.Add(state); content.Children.Add(result);

        var name = Ui.Text(notification.JobName ?? notification.Message, 13, wrap: true);
        name.MaxLines = 2; name.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(name, notification.JobName ?? notification.Message);
        content.Children.Add(name);

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,28,*,Auto") };
        var id = Ui.Text($"Job {notification.JobId}", 11, "muted"); id.VerticalAlignment = VerticalAlignment.Center;
        id.MaxWidth = 185; id.TextTrimming = TextTrimming.CharacterEllipsis;
        id.IsVisible = !string.IsNullOrEmpty(notification.JobId); ToolTip.SetTip(id, notification.JobId); actions.Children.Add(id);
        CopyJobIdButton = Ui.AsyncButton("Copy job ID", CopyJobIdAsync, true);
        CopyJobIdButton.Content = Icons.Create("copy", 13); CopyJobIdButton.Classes.Add("icon");
        CopyJobIdButton.Width = CopyJobIdButton.Height = CopyJobIdButton.MinHeight = 24; CopyJobIdButton.Padding = new Thickness(5);
        CopyJobIdButton.IsVisible = id.IsVisible;
        AutomationProperties.SetName(CopyJobIdButton, $"Copy job ID {notification.JobId}");
        ToolTip.SetTip(CopyJobIdButton, "Copy job ID"); Grid.SetColumn(CopyJobIdButton, 1); actions.Children.Add(CopyJobIdButton);
        OpenJobDetailsButton = Ui.Button("View job", () => { openJobDetails?.Invoke(notification); Close(); });
        OpenJobDetailsButton.Classes.Add("quiet"); OpenJobDetailsButton.MinHeight = 26; OpenJobDetailsButton.Padding = new Thickness(6, 3);
        OpenJobDetailsButton.FontSize = 11; OpenJobDetailsButton.IsVisible = openJobDetails != null && id.IsVisible;
        Grid.SetColumn(OpenJobDetailsButton, 3); actions.Children.Add(OpenJobDetailsButton); content.Children.Add(actions);

        closeTimer.Tick += (_, _) => Close();
        Opened += (_, _) => ResumeCountdown();
        PointerEntered += (_, _) => PauseCountdown();
        PointerExited += (_, _) => { if (!IsPointerOver) ResumeCountdown(); };
        Closed += (_, _) => { closed = true; closeTimer.Stop(); countdown.Stop(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    internal async Task CopyJobIdAsync()
    {
        if (string.IsNullOrEmpty(Notification.JobId) || Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(Notification.JobId);
        CopyJobIdButton.Content = Icons.Create("check", 13);
        ToolTip.SetTip(CopyJobIdButton, "Job ID copied");
        AutomationProperties.SetName(CopyJobIdButton, $"Job ID {Notification.JobId} copied");
    }
    private void PauseCountdown()
    {
        if (!autoDismiss || closed || !countdown.IsRunning) return;
        countdown.Stop(); closeTimer.Stop(); remaining -= countdown.Elapsed; countdown.Reset();
    }
    private void ResumeCountdown()
    {
        if (!autoDismiss || closed || IsPointerOver || countdown.IsRunning) return;
        if (remaining <= TimeSpan.Zero) { Close(); return; }
        closeTimer.Interval = remaining; countdown.Restart(); closeTimer.Start();
    }
    private static string ResultLabel(JobNotification notification) => notification.State switch
    {
        "COMPLETED" => "Completed", "FAILED" => "Failed", "TIMEOUT" => "Timed out", "RUNNING" => "Started",
        "CANCELLED" => "Cancelled", "OUT_OF_MEMORY" => "Out of memory", "NODE_FAIL" => "Node failure",
        "BOOT_FAIL" => "Node failed to start", "PREEMPTED" => "Preempted", "DEADLINE" => "Deadline reached",
        "REVOKED" => "Revoked", _ => notification.Title
    };
    private static Control ResultIcon(string? state, string brush)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var ring = new Ellipse { Width = 20, Height = 20, StrokeThickness = 1.4 };
        ring[!Shape.StrokeProperty] = new DynamicResourceExtension(brush); Canvas.SetLeft(ring, 2); Canvas.SetTop(ring, 2); canvas.Children.Add(ring);
        string geometry = state switch
        {
            "COMPLETED" => "M7 12 L10.5 15.5 L17 8.5",
            "FAILED" or "OUT_OF_MEMORY" or "NODE_FAIL" or "BOOT_FAIL" => "M8.5 8.5 L15.5 15.5 M15.5 8.5 L8.5 15.5",
            "TIMEOUT" or "DEADLINE" => "M12 6.5 L12 12 L16 14",
            "RUNNING" => "M9 8 L16 12 L9 16 Z",
            _ => "M12 7 L12 13 M12 16 L12 16.2"
        };
        var glyph = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(geometry), StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
        glyph[!Shape.StrokeProperty] = new DynamicResourceExtension(brush); canvas.Children.Add(glyph);
        return new Viewbox { Child = canvas, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    }
}
