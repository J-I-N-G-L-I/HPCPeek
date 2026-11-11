using Avalonia;

namespace AirePeek.Desktop.Tests;

public sealed class WindowSizingTests
{
    [Theory]
    [InlineData(1900, 100, 0, 1, 1580, 100)] // Top-left is visible; right-hand controls would be offscreen.
    [InlineData(50, 1020, 0, 1, 50, 892)] // A remembered position below the new taskbar must move up.
    [InlineData(1900, 1020, 0, 2, 1240, 744)] // Restore full physical bounds at 200% scaling.
    [InlineData(-10, 1010, -1920, 1, -340, 892)] // Left-hand displays use negative virtual coordinates.
    [InlineData(300, 100, 0, 1, 300, 100)] // Preserve an already valid remembered position exactly.
    public void RestoreKeepsTheWholeCompactWindowInsideItsDisplay(int x, int y, int screenLeft, double scale, int expectedX, int expectedY)
    {
        var workArea = new PixelRect(screenLeft, 0, 1920, 1040);
        var restored = WindowSizing.KeepWithinWorkArea(new PixelPoint(x, y), workArea, 340, WindowSizing.CompactHeight, scale);

        Assert.Equal(new PixelPoint(expectedX, expectedY), restored);
        Assert.True(workArea.Contains(new PixelRect(restored, new PixelSize((int)(340 * scale), (int)(WindowSizing.CompactHeight * scale)))));
    }

    [Theory]
    [InlineData(1040, 2, 496)] // 1920 x 1080 display, 200%, taskbar excluded.
    [InlineData(728, 1.5, 461)] // 1366 x 768 laptop, 150%, taskbar excluded.
    [InlineData(1040, 1.25, 720)] // 125% can retain the complete design height.
    [InlineData(1000, 1, 720)] // Native test desktop retains the 720 DIP design cap.
    public void ExpandedViewportFitsTheWorkingAreaAndKeepsBothEdgesReachable(double workAreaPixels, double scale, double expectedDip)
    {
        double height = WindowSizing.ExpandedHeight(workAreaPixels, scale);

        Assert.Equal(expectedDip, height);
        Assert.InRange(height, WindowSizing.CompactHeight, 720);
        // The viewport and its 12 DIP top/bottom clearance must all remain on screen.
        Assert.True((height + 24) * scale <= workAreaPixels,
            $"Expanded {height} DIP at {scale:P0} exceeds the {workAreaPixels}-pixel work area.");
    }
}
