using GlassDock.Interop;
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
