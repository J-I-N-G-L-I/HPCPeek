using Avalonia.Controls;
using Avalonia.Platform;

namespace AirePeek.Desktop.Infrastructure;

/// <summary>The original four-node H mark shared by desktop windows, the launcher and the tray.</summary>
internal static class AppBrand
{
    public static WindowIcon CreateWindowIcon() => Load("hpcpeek-app");
    public static WindowIcon CreateTrayIcon(bool light) => Load(light ? "hpcpeek-tray-light" : "hpcpeek-tray-dark");

    private static WindowIcon Load(string asset)
    {
        var extension = OperatingSystem.IsLinux() ? "png" : "ico";
        using var stream = AssetLoader.Open(new Uri($"avares://HPCPeek/Assets/{asset}.{extension}"));
        return new WindowIcon(stream);
    }
}
