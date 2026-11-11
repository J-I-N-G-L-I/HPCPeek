using AirePeek.Core;
using AirePeek.Desktop.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Layout;
using Avalonia.Media;

namespace AirePeek.Desktop;

/// <summary>A job keeps its control identity and expanded state across samples.</summary>
internal sealed class JobRow : StackPanel
{
    private SlurmJob job;
    private readonly TextBlock reason = Ui.Text("", 13, "attention", true);
    private readonly TextBlock name = Ui.Text("", 13);
    private readonly TextBlock identity = Ui.Text("", 12, "muted");
    private readonly TextBlock timing = Ui.Text("", 12, "muted");
    private readonly TextBlock fullName = Ui.Text("", 12, wrap: true);
    private readonly TextBlock fullDetails = Ui.Text("", 11, "muted", true);
    private readonly TextBlock elapsedLabel = Ui.Text("Elapsed", 11, "muted"), elapsedValue = Ui.Text("", 15);
    private readonly JobElapsedClock elapsedClock = new();
    private bool liveElapsed;
    private readonly StackPanel inspector = new() { Spacing = 5, Margin = new Thickness(0, 6, 0, 3), IsVisible = false };
    private readonly Button header;
    private readonly Border marker;
    public bool IsExpanded => inspector.IsVisible;
    public string JobId => job.Id;
    public ContextMenu JobMenu { get; }
    internal string DisplayedElapsed => elapsedValue.Text ?? "";
    internal string ElapsedLabel => elapsedLabel.Text ?? "";
    public JobRow(SlurmJob job, DateTimeOffset? sampledAt = null, bool live = false)
    {
        this.job = job; Spacing = 0;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var summary = new StackPanel { Spacing = 2, Margin = new Thickness(1, 4, 1, 4) };
        summary.Children.Add(reason); summary.Children.Add(name);
        summary.Children.Add(identity);
        summary.Children.Add(timing);
        header = Ui.Button("View job details", Toggle, true); header.Content = summary;
        header.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        marker = new Border { Child = header };
        marker[!Border.BorderBrushProperty] = new DynamicResourceExtension("Attention");
        Children.Add(marker);
        inspector.Children.Add(fullName);
        var elapsed = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 2, 0, 2) };
        elapsedLabel.VerticalAlignment = elapsedValue.VerticalAlignment = VerticalAlignment.Center;
        elapsedValue.FontWeight = FontWeight.SemiBold;
        elapsed.Children.Add(elapsedLabel); Grid.SetColumn(elapsedValue, 1); elapsed.Children.Add(elapsedValue);
        inspector.Children.Add(elapsed); inspector.Children.Add(fullDetails);
        var copy = Ui.AsyncButton("Copy job ID", () => CopyAsync(job.Id)); copy.FontSize = 11;
        copy.HorizontalAlignment = HorizontalAlignment.Left; inspector.Children.Add(copy); Children.Add(inspector);
        Children.Add(Ui.Rule(3));
        var detail = new MenuItem { Header = "View details", Icon = Icons.Create("file-text") }; detail.Click += (_, _) => { inspector.IsVisible = true; Tick(DateTimeOffset.UtcNow, liveElapsed); };
        var id = new MenuItem { Header = "Copy job ID", Icon = Icons.Create("copy") }; id.Click += async (_, _) => await CopyAsync(this.job.Id);
        var title = new MenuItem { Header = "Copy full name", Icon = Icons.Create("copy") }; title.Click += async (_, _) => await CopyAsync(this.job.Name);
        JobMenu = new ContextMenu { ItemsSource = new object[] { detail, id, title } };
        header.ContextMenu = JobMenu;
        Update(job, sampledAt, live);
    }
    public void Toggle() { inspector.IsVisible = !inspector.IsVisible; Tick(DateTimeOffset.UtcNow, liveElapsed); }
    public void Update(SlurmJob value, DateTimeOffset? sampledAt = null, bool live = false)
    {
        job = value;
        elapsedClock.Update(value, sampledAt);
        marker.BorderThickness = new Thickness(value.IsPending ? 3 : 0, 0, 0, 0);
        marker.Padding = new Thickness(value.IsPending ? 8 : 0, 0, 0, 0);
        reason.Text = "Queued · " + value.Reason; reason.IsVisible = value.IsPending;
        reason.FontWeight = FontWeight.SemiBold;
        name.Text = value.Name; ToolTip.SetTip(name, value.Name);
        identity.Text = value.IsPending ? value.Id : $"{value.Id} · {value.StateLabel}";
        timing.Text = value.IsRunning ? $"{value.Elapsed} / {value.TimeLimit}" : "";
        timing.IsVisible = value.IsRunning;
        if (value.IsRunning) timing.Text += $" · {value.Nodes}";
        // Row keeps its original controls; only content changes, so keyboard/scroll/expansion remain stable.
        fullName.Text = value.Name;
        fullDetails.Text = $"Job ID  {value.Id}\nStatus  {value.StateLabel}\nTime limit  {value.TimeLimit}\nNodes  {value.Nodes}" +
            (value.IsPending ? $"\nWaiting reason  {value.Reason}" : "");
        Tick(DateTimeOffset.UtcNow, live);
        Avalonia.Automation.AutomationProperties.SetName(header, $"{value.Name}, {value.StateLabel}, job {value.Id}. View details.");
    }
    public void Tick(DateTimeOffset now, bool live)
    {
        liveElapsed = live;
        if (!IsExpanded) return;
        var reading = elapsedClock.Read(now, live);
        elapsedLabel.Text = !reading.IsEstimated && (job.State is "RUNNING" or "R") ? "Last sampled elapsed" : "Elapsed";
        elapsedValue.Text = (reading.IsEstimated ? "~" : "") + reading.Text;
        ToolTip.SetTip(elapsedValue, reading.IsEstimated
            ? "Local estimate, updated every second from the last queue sample. The next sample confirms the job state and elapsed time."
            : "Elapsed time reported by the last successful queue sample. The clock pauses when offline or the sample is stale.");
        Avalonia.Automation.AutomationProperties.SetName(elapsedValue,
            $"{elapsedLabel.Text}: {reading.Text}{(reading.IsEstimated ? ", estimated" : "")}");
    }
    private async Task CopyAsync(string value)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(value);
    }
}
