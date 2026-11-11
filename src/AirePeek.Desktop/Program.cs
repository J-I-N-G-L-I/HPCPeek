using System.Diagnostics;
using Avalonia;

namespace AirePeek.Desktop;

internal static class Program
{
    public static readonly Stopwatch Startup = Stopwatch.StartNew();
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
