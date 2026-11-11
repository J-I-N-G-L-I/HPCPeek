using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;

namespace AirePeek.Desktop.Infrastructure;

/// <summary>Renders the checked-in Lucide SVG geometry without an SVG engine or font dependency.</summary>
public static class Icons
{
    public static Control Create(string name, double size = 16)
    {
        if (name == "power") name = "log-out";
        using var stream = AssetLoader.Open(new Uri($"avares://HPCPeek/Assets/Icons/{name}.svg"));
        var document = XDocument.Load(stream);
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        foreach (var element in document.Root!.Elements())
        {
            Shape? shape = element.Name.LocalName switch
            {
                "path" => new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(element.Attribute("d")!.Value) },
                "circle" => Circle(element),
                "line" => new Line { StartPoint = new Point(Number(element, "x1"), Number(element, "y1")), EndPoint = new Point(Number(element, "x2"), Number(element, "y2")) },
                "rect" => Rectangle(element),
                "polyline" => new Polyline { Points = ParsePoints(element.Attribute("points")!.Value) },
                _ => null
            };
            if (shape is null) continue;
            shape.StrokeThickness = 1.65;
            shape.StrokeLineCap = PenLineCap.Round;
            shape.StrokeJoin = PenLineJoin.Round;
            shape[!Shape.StrokeProperty] = new DynamicResourceExtension("Ink");
            canvas.Children.Add(shape);
        }
        return new Viewbox { Child = canvas, Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    }
    private static double Number(XElement element, string name) => double.TryParse(element.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static Shape Circle(XElement element)
    {
        var radius = Number(element, "r");
        var shape = new Ellipse { Width = 2 * radius, Height = 2 * radius };
        Canvas.SetLeft(shape, Number(element, "cx") - radius); Canvas.SetTop(shape, Number(element, "cy") - radius); return shape;
    }
    private static Shape Rectangle(XElement element)
    {
        var shape = new Rectangle { Width = Number(element, "width"), Height = Number(element, "height"), RadiusX = Number(element, "rx"), RadiusY = Number(element, "rx") };
        Canvas.SetLeft(shape, Number(element, "x")); Canvas.SetTop(shape, Number(element, "y")); return shape;
    }
    private static Points ParsePoints(string points)
    {
        var values = points.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        var result = new Points();
        for (int i = 0; i + 1 < values.Length; i += 2) result.Add(new Point(values[i], values[i + 1]));
        return result;
    }
}
