using AirePeek.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AirePeek.Desktop;

/// <summary>One live control per real node. Capacity and eligibility come from the shared Core projection.</summary>
internal sealed class GpuNodeGrid : StackPanel
{
    private readonly bool compact;
    private readonly Grid grid = new() { ColumnSpacing = 5, RowSpacing = 5 };
    private readonly TextBlock overflow = Ui.Text("", 10, "muted");
    private readonly Dictionary<string, NodeCell> cells = new(StringComparer.Ordinal);
    private string[] order = [];
    public IReadOnlyList<GpuNodeTile> Tiles { get; private set; } = [];
    public event Action<GpuNodeTile>? Selected;
    public GpuNodeGrid(bool compact)
    {
        this.compact = compact;
        Spacing = 3; Children.Add(grid); Children.Add(overflow); overflow.IsVisible = false;
        if (compact) { grid.ColumnSpacing = grid.RowSpacing = 3; Width = 88; }
        AutomationProperties.SetName(this, compact ? "Cluster node overview" : "GPU nodes; numbers are free whole GPUs");
    }
    public void Update(IReadOnlyList<GpuNodeTile> tiles, string? selected = null)
    {
        Tiles = tiles;
        var visible = compact ? tiles.Take(28).ToArray() : tiles.ToArray();
        var next = visible.Select(t => t.Name).ToArray();
        if (!order.SequenceEqual(next))
        {
            order = next; grid.Children.Clear(); cells.Clear();
            grid.ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat(compact ? "9" : "*", 7)));
            grid.RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat(compact ? "9" : "34", Math.Max(1, (visible.Length + 6) / 7))));
            for (int i = 0; i < visible.Length; i++)
            {
                var cell = new NodeCell(compact); cells[visible[i].Name] = cell;
                cell.Click += (_, _) => { if (cell.Tile is { } tile) Selected?.Invoke(tile); };
                Grid.SetRow(cell, i / 7); Grid.SetColumn(cell, i % 7); grid.Children.Add(cell);
            }
        }
        foreach (var tile in visible) cells[tile.Name].Update(tile, tile.Name == selected);
        overflow.Text = tiles.Count > visible.Length ? $"{visible.Length} / {tiles.Count} nodes" : "";
        overflow.IsVisible = tiles.Count > visible.Length;
    }
    private sealed class NodeCell : Button
    {
        private readonly bool compact;
        private readonly Border surface;
        private readonly TextBlock label = Ui.Text("", 9);
        private readonly TextBlock value = Ui.Text("", 15);
        private readonly Border outline;
        private bool selected;
        public GpuNodeTile? Tile { get; private set; }
        public NodeCell(bool compact)
        {
            this.compact = compact;
            Focusable = !compact;
            Padding = new Thickness(0); MinHeight = 0; MinWidth = 0;
            HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
            Background = Brushes.Transparent; BorderThickness = new Thickness(0); Classes.Add("node");
            surface = new Border { CornerRadius = new CornerRadius(compact ? 2 : 7), BorderThickness = new Thickness(1) };
            if (!compact)
            {
                var body = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
                label.HorizontalAlignment = value.HorizontalAlignment = HorizontalAlignment.Center;
                value.FontWeight = FontWeight.SemiBold; label.Opacity = .8;
                body.Children.Add(label); body.Children.Add(value); surface.Child = body;
            }
            // Selection/focus is an overlay: the content's measured inset never changes.
            // Compact tiles deliberately contain no text; details remain available on hover.
            outline = new Border { CornerRadius = surface.CornerRadius, BorderThickness = new Thickness(2), IsHitTestVisible = false, Opacity = 0 };
            var layers = new Grid(); layers.Children.Add(surface); layers.Children.Add(outline); Content = layers;
            GotFocus += (_, _) => UpdateOutline(); LostFocus += (_, _) => UpdateOutline();
        }
        public void Update(GpuNodeTile tile, bool selected)
        {
            Tile = tile; this.selected = selected;
            string brush = tile.Availability switch
            {
                GpuNodeAvailability.Full => "TileFull",
                GpuNodeAvailability.Unavailable => "TileUnavailable",
                GpuNodeAvailability.Unknown => "TileUnknown",
                _ => "TileFree"
            };
            surface[!Border.BackgroundProperty] = new DynamicResourceExtension(brush);
            surface[!Border.BorderBrushProperty] = new DynamicResourceExtension(tile.IsCandidate && !compact ? "Accent" : "Line");
            UpdateOutline();
            // Expanded tiles, tooltips and accessibility names retain explicit capacity/status.
            value.Text = tile.Availability switch
            {
                GpuNodeAvailability.Unavailable => "×", GpuNodeAvailability.Unknown => "?", _ => tile.Available?.ToString() ?? "?"
            };
            string digits = new(tile.Name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
            label.Text = digits.Length is > 0 and <= 4 ? digits : tile.Name;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            string ink = tile.Availability == GpuNodeAvailability.Available ? "TileFreeInk" : "TileInk";
            foreach (var text in new[] { label, value }) text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(ink);
            ToolTip.SetTip(this, tile.DetailText);
            AutomationProperties.SetName(this, tile.DetailText.Replace('\n', '.'));
        }
        private void UpdateOutline()
        {
            outline[!Border.BorderBrushProperty] = new DynamicResourceExtension(selected ? "Ink" : "Accent");
            outline.Opacity = !compact && (selected || IsFocused) ? 1 : 0;
        }
    }
}
