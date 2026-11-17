using System.Globalization;
using AirePeek.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace AirePeek.Desktop;

/// <summary>A personal quota meter; unavailable readings never appear as empty storage.</summary>
internal sealed class StorageBar : StackPanel
{
    internal const double MeterWidth = 76;
    private readonly string label;
    private readonly TextBlock total = Ui.Text("—", 9, "muted");
    private readonly Border fill = new() { Width = 0, Height = 4, CornerRadius = new(2), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border track = new() { Width = MeterWidth, Height = 4, CornerRadius = new(2), ClipToBounds = true };
    private string? fillBrush;

    public StorageBar(string label)
    {
        this.label = label;
        Name = label + "Storage";
        Width = MeterWidth; Spacing = 3;
        var heading = new Grid { ColumnDefinitions = new("*,Auto") };
        var name = Ui.Text(label, 9, "muted"); name.LineHeight = total.LineHeight = 12;
        heading.Children.Add(name);
        total.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(total, 1); heading.Children.Add(total); Children.Add(heading);
        track[!Border.BackgroundProperty] = new DynamicResourceExtension("TileFull");
        track.Child = fill; Children.Add(track);
    }

    public void Update(StorageUsage? usage, string cluster, string? error,
        TimeSpan interval, DateTimeOffset now, bool disconnected)
    {
        var freshness = SampleFreshness.Evaluate(usage?.SampledAt, interval, now, disconnected);
        double? fraction = usage?.Fraction;
        total.Text = usage?.LimitBytes is > 0 ? CompactBytes(usage.LimitBytes.Value) : "—";
        fill.Width = fraction is { } value ? MeterWidth * Math.Clamp(value, 0, 1) : 0;
        string brush = fraction >= .9 ? "Failure" : fraction >= .75 ? "Attention" : "Accent";
        if (brush != fillBrush) { fillBrush = brush; fill[!Border.BackgroundProperty] = new DynamicResourceExtension(brush); }
        // Keep missing data distinct from a measured zero; preserve dated readings on a failed refresh.
        track.Opacity = fraction is null ? .4 : 1;
        Opacity = freshness.NeedsAttention || error is not null ? .6 : 1;
        string quota = usage?.Scope.StartsWith("project", StringComparison.Ordinal) == true ? "Project quota" : "User quota";
        string reading = fraction is { } ratio
            ? $"{CompactBytes(usage!.UsedBytes!.Value)} / {CompactBytes(usage.LimitBytes!.Value)} used ({ratio.ToString("P0", CultureInfo.InvariantCulture)})"
            : "Quota unavailable";
        string tip = $"{cluster} · {label}\n{usage?.Path ?? "Path available after connection"}\n{quota}: {reading}\n{freshness.Label}";
        if (usage?.Scope.StartsWith("project ", StringComparison.Ordinal) == true) tip += "\nQuota scope: " + usage.Scope;
        if (!string.IsNullOrEmpty(usage?.Note)) tip += "\n" + usage.Note;
        if (error is not null) tip += "\n" + error;
        ToolTip.SetTip(this, tip);
        AutomationProperties.SetName(this, tip);
    }

    private static string CompactBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + units[unit];
    }
}
