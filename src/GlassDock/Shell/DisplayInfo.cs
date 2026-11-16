using System.Runtime.InteropServices;
using static GlassDock.Interop.Native;

namespace GlassDock.Shell;

public sealed record DisplayInfo(nint Handle, RECT Bounds, RECT WorkArea, string Device, bool IsPrimary, double Scale)
{
    public static DisplayInfo Primary() => FromHandle(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY));

    public static DisplayInfo FromHandle(nint hmon)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hmon, ref mi);
        double scale = GetDpiForMonitor(hmon, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        return new DisplayInfo(hmon, mi.rcMonitor, mi.rcWork, mi.szDevice, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, scale);
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmon, int type, out uint dpiX, out uint dpiY);
}
