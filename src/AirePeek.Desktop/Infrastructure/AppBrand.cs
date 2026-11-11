using Avalonia.Controls;
using Avalonia.Platform;

namespace AirePeek.Desktop.Infrastructure;

/// <summary>The original four-node H mark shared by desktop windows, the launcher and the tray.</summary>
internal static class AppBrand
{
    public static WindowIcon CreateWindowIcon() => Load("hpcpeek-app.ico");
    public static WindowIcon CreateTrayIcon(bool light) => Load(light ? "hpcpeek-tray-light.ico" : "hpcpeek-tray-dark.ico");

    private static WindowIcon Load(string asset)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://HPCPeek/Assets/{asset}"));
        return new WindowIcon(stream);
    }
}
