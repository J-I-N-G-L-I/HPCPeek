using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using AirePeek.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AirePeek.Desktop.Infrastructure;

/// <summary>Small platform adapters without Windows Forms, shell commands or extra runtimes.</summary>
public sealed class NativePlatform : IDisposable
{
    private readonly Window owner;
    private readonly System.Threading.Timer heartbeat;
    private readonly Queue<(JobNotification Notification, Action<JobNotification>? OpenJobDetails)> notifications = new();
    private NotificationWindow? notification;
    private long lastTick = Environment.TickCount64;
    private volatile bool disposed;
    private IntPtr powerRegistration;
    private PowerCallback? powerCallback;
    public event Action? ConnectionInvalidated;

    public NativePlatform(Window owner)
    {
        this.owner = owner;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        NetworkChange.NetworkAddressChanged += AddressChanged;
        // Cross-platform resume fallback: timers stop while the machine sleeps. No authentication is started here.
        heartbeat = new System.Threading.Timer(_ =>
        {
            var now = Environment.TickCount64;
            var previous = Interlocked.Exchange(ref lastTick, now);
            if (!disposed && now - previous > 20_000) Invalidate();
        }, null, 5000, 5000);
        if (OperatingSystem.IsWindows())
        {
            powerCallback = (_, type, _) => { if (type == 4) Invalidate(); return 0; };
            var parameters = new PowerSubscribe { Callback = Marshal.GetFunctionPointerForDelegate(powerCallback), Context = IntPtr.Zero };
            try { PowerRegisterSuspendResumeNotification(2, ref parameters, out powerRegistration); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) { if (!args.IsAvailable) Invalidate(); }
    private void AddressChanged(object? sender, EventArgs args) => Invalidate();
    private void Invalidate() { if (!disposed) Dispatcher.UIThread.Post(() => { if (!disposed) ConnectionInvalidated?.Invoke(); }); }

    /// <summary>App-owned, dismissible desktop notice. It does not persist in the OS notification center.</summary>
    public void ShowNotification(JobNotification result, Action<JobNotification>? openJobDetails = null)
    {
        if (disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed) return;
            notifications.Enqueue((result, openJobDetails));
            if (notification is null) ShowNextNotification();
        });
    }
    // Kept for legacy callers; structured notifications preserve job names and cluster identity without parsing text.
    public void ShowNotification(string title, string message) => ShowNotification(new JobNotification("", title, message));
    private void ShowNextNotification()
    {
        if (disposed || notifications.Count == 0) return;
        var item = notifications.Dequeue();
        var window = new NotificationWindow(item.Notification, item.OpenJobDetails);
        notification = window;
        window.Opened += (_, _) =>
        {
            var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
            if (screen is { } display)
            {
                var area = display.WorkingArea; var scale = display.Scaling;
                var desired = new PixelPoint(area.Right - (int)Math.Ceiling(window.Width * scale) - 20, area.Bottom - (int)Math.Ceiling(window.Bounds.Height * scale) - 20);
                window.Position = WindowSizing.KeepWithinWorkArea(desired, area, window.Width, window.Bounds.Height, scale);
            }
        };
        window.Closed += (_, _) =>
        {
            notification = null;
            if (!disposed) ShowNextNotification();
        };
        window.Show();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= AddressChanged;
        heartbeat.Dispose();
        if (powerRegistration != IntPtr.Zero) { PowerUnregisterSuspendResumeNotification(powerRegistration); powerRegistration = IntPtr.Zero; }
        Dispatcher.UIThread.Post(() => { notifications.Clear(); notification?.Close(); });
        GC.KeepAlive(powerCallback);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint PowerCallback(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)] private struct PowerSubscribe { public IntPtr Callback; public IntPtr Context; }
    [DllImport("powrprof.dll", SetLastError = true)] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref PowerSubscribe recipient, out IntPtr registration);
    [DllImport("powrprof.dll", SetLastError = true)] private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
}
