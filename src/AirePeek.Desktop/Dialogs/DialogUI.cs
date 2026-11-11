using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Input;
using AirePeek.Desktop.Infrastructure;
using Avalonia.Threading;

namespace AirePeek.Desktop.Dialogs;

internal static class DialogUI
{
    public static TextBlock Text(string text, double size = 13, bool muted = false)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (muted) label.Classes.Add("muted");
        return label;
    }
    public static TextBlock Title(string text) => new() { Text = text, FontSize = 23, FontWeight = FontWeight.Medium, Margin = new Thickness(0, 0, 0, 20) };
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, MinHeight = 34, Padding = new Thickness(14, 7) };
        AutomationProperties.SetName(button, text);
        if (primary) button.Classes.Add("accent");
        button.Click += (_, _) => action();
        return button;
    }
    public static void Field(Panel parent, string label, TextBox field)
    {
        parent.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        field.MinHeight = 34; field.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(field, label);
        parent.Children.Add(field);
    }
    public static Border Divider()
    {
        var border = new Border { Height = 1, Margin = new Thickness(0, 16) };
        border[!Border.BackgroundProperty] = new DynamicResourceExtension("Line"); return border;
    }
    public static Border Group(Control child, Thickness? padding = null)
    {
        var border = new Border { Child = child, Padding = padding ?? new Thickness(14), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        border.Classes.Add("panel"); return border;
    }
    public static Grid Actions(params Control[] buttons)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', buttons.Select(_ => "Auto"))), ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++) { Grid.SetColumn(buttons[i], i); row.Children.Add(buttons[i]); }
        return row;
    }
    public static void Configure(Window window, string title, double width = 440, double height = 600)
    {
        window.Title = title; window.Width = width; window.Height = height; window.Icon = AppBrand.CreateWindowIcon();
        window.MinWidth = 360; window.MinHeight = 280; window.CanResize = true;
        window.FontFamily = OperatingSystem.IsWindows() ? new FontFamily("Segoe UI") : FontFamily.Default;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowInTaskbar = false;
        window.KeyDown += (_, args) => { if (args.Key == Key.Escape) { window.Close(); args.Handled = true; } };
        window.Opened += (_, _) => CenterOnScreen(window);
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == TopLevel.ClientSizeProperty && window.IsVisible)
                Dispatcher.UIThread.Post(() => CenterOnScreen(window), DispatcherPriority.Loaded);
        };
    }

    /// <summary>Short messages and server prompts fit their content; long prompts retain a scrollable body.</summary>
    public static void FitContentHeight(Window window, double maximumHeight = 700)
    {
        window.MinHeight = 0; window.MaxHeight = maximumHeight;
        window.Height = double.NaN; window.SizeToContent = SizeToContent.Height;
    }

    /// <summary>Dialogs use the owner's display, not the position of its small corner panel.</summary>
    public static void CenterOnScreen(Window window)
    {
        if (!window.IsVisible) return;
        Center();
        // A route change can resize the native frame after the property assignment.
        Dispatcher.UIThread.Post(() => { if (window.IsVisible) Center(); }, DispatcherPriority.Loaded);
        void Center()
        {
            var screen = (window.Owner is { } owner ? window.Screens.ScreenFromWindow(owner) : null)
                ?? window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null) return;
            var area = screen.WorkingArea; double scale = screen.Scaling;
            var frame = window.FrameSize ?? window.ClientSize;
            var frameHeight = Math.Max(0, frame.Height - window.ClientSize.Height);
            var allowedHeight = Math.Max(window.MinHeight, area.Height / scale - 32 - frameHeight);
            window.Width = Math.Min(window.Width, Math.Max(window.MinWidth, area.Width / scale - 32));
            if (window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight)
                window.MaxHeight = Math.Min(window.MaxHeight, allowedHeight);
            else window.Height = Math.Min(window.Height, allowedHeight);
            // Width/Height are requested client size; include the native title bar and borders in centering.
            double width = window.Width + Math.Max(0, frame.Width - window.ClientSize.Width);
            double height = (double.IsNaN(window.Height) ? window.ClientSize.Height : window.Height) + frameHeight;
            var desired = new PixelPoint(area.X + (int)Math.Round((area.Width - width * scale) / 2),
                area.Y + (int)Math.Round((area.Height - height * scale) / 2));
            window.Position = WindowSizing.KeepWithinWorkArea(desired, area, width, height, scale);
        }
    }
}
