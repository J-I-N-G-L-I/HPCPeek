using Avalonia;

namespace AirePeek.Desktop;

internal static class WindowSizing
{
    public const int CompactHeight = 148;
    // Work area uses physical pixels. Leave space for both edges on high-DPI/laptop displays.
    public static double ExpandedHeight(double workAreaPixels, double scale) =>
        Math.Clamp(Math.Floor(workAreaPixels / Math.Max(.5, scale) - 24), CompactHeight, 720);

    public static PixelPoint KeepWithinWorkArea(PixelPoint desired, PixelRect workArea, double widthDip, double heightDip, double scale)
    {
        var width = (int)Math.Ceiling(widthDip * scale);
        var height = (int)Math.Ceiling(heightDip * scale);
        return new(Math.Clamp(desired.X, workArea.X, Math.Max(workArea.X, workArea.Right - width)),
            Math.Clamp(desired.Y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - height)));
    }
}
