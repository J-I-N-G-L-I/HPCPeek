using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace AirePeek.Desktop;

internal static class Ui
{
    public static TextBlock Text(string value, double size = 12, string? style = null, bool wrap = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (style != null) text.Classes.Add(style);
        return text;
    }
    public static Button Button(string text, Action action, bool flat = false)
    {
        var button = new Button { Content = text };
        if (flat) button.Classes.Add("flat");
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, text);
        return button;
    }
    public static Button AsyncButton(string text, Func<Task> action, bool flat = false)
    {
        var button = new Button { Content = text };
        if (flat) button.Classes.Add("flat");
        button.Click += async (_, _) => await action();
        AutomationProperties.SetName(button, text);
        return button;
    }
    public static Border Rule(double margin = 10)
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, margin, 0, margin) };
        line.Classes.Add("rule"); return line;
    }
    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold };
    public static void MoveInto(Panel panel, Control child, int index)
    {
        int existing = panel.Children.IndexOf(child);
        if (existing == index) return;
        if (existing >= 0) panel.Children.RemoveAt(existing);
        panel.Children.Insert(Math.Min(index, panel.Children.Count), child);
    }
    public static string Count(int? value) => value?.ToString() ?? "unknown";
}
