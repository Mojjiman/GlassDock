using GlassDock.Interop;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Taskbar;

/// <summary>Finds Explorer's real taskbar windows (never ManagedShell's fake <c>Shell_TrayWnd</c>).</summary>
public static class ExplorerTaskbars
{
    public const string PrimaryClass = "Shell_TrayWnd";
    public const string SecondaryClass = "Shell_SecondaryTrayWnd";

    public static nint FindPrimary() => Find(PrimaryClass).FirstOrDefault();

    public static List<nint> Find(string className)
    {
        var result = new List<nint>();
        uint ownPid = (uint)Environment.ProcessId;
        nint prev = 0;
        while (true)
        {
            nint h = FindWindowEx(0, prev, className, null);
            if (h == 0) break;
            prev = h;
            uint pid = GetWindowProcessId(h);
            if (pid == ownPid) continue;
            var path = GetProcessPath(pid);
            if (path != null && Path.GetFileName(path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                result.Add(h);
        }
        return result;
    }

    /// <summary>Explorer's task band under its <c>Shell_TrayWnd</c> (nested a few levels down), or 0.</summary>
    public static nint FindTaskband(nint tray)
    {
        nint band = 0;
        if (tray == 0) return 0;
        EnumChildWindows(tray, (h, _) =>
        {
            if (GetClassName(h) != TaskbandClass) return true;
            band = h;
            return false;
        }, 0);
        return band;
    }

    /// <summary>
    /// After a crash, Explorer's tray can be left without <see cref="TaskbandProp"/>: ManagedShell moves it to
    /// its own fake tray and only puts it back on a clean dispose. Without it, apps' ITaskbarList calls go
    /// nowhere until Explorer restarts.
    /// </summary>
    public static void RestoreTaskbandProp()
    {
        nint tray = FindPrimary();
        if (tray == 0) return;
        nint current = GetProp(tray, TaskbandProp);
        if (current != 0 && IsWindow(current)) return;
        nint band = FindTaskband(tray);
        if (band == 0) { Log.Warn("Explorer's task band not found; ITaskbarList target left unset"); return; }
        if (SetProp(tray, TaskbandProp, band)) Log.Info($"Pointed ITaskbarList back at Explorer (0x{band:X}, was 0x{current:X})");
        else Log.Win32Failure("SetProp(TaskbandHWND)");
    }

    public static uint ExplorerPid()
    {
        nint tray = FindPrimary();
        return tray == 0 ? 0 : GetWindowProcessId(tray);
    }

    public static int GetAppBarState()
    {
        var abd = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = FindPrimary() };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref abd);
    }

    public static void SetAppBarState(int state)
    {
        var abd = new APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
            hWnd = FindPrimary(),
            lParam = state,
        };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }
}
