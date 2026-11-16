using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Input;

/// <summary>
/// Brings a window to the foreground despite the foreground lock. Tries the cheap documented path
/// first and escalates; logs which step worked so field issues can be diagnosed.
/// </summary>
public static class ForegroundActivator
{
    public static bool Activate(nint hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);

        if (TrySet(hwnd)) return true;

        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint me = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true))
        {
            try
            {
                BringWindowToTop(hwnd);
                if (TrySet(hwnd)) { Log.Debug("Activated via AttachThreadInput"); return true; }
            }
            finally { AttachThreadInput(me, fgThread, false); }
        }

        // An Alt press counts as user input and lifts the foreground lock (AutoHotkey's last resort).
        InputInjector.Tap(VK_MENU);
        if (TrySet(hwnd)) { Log.Debug("Activated via Alt injection"); return true; }

        SwitchToThisWindow(hwnd, true);
        bool ok = WaitForeground(hwnd);
        if (!ok) Log.Warn($"Could not activate window 0x{hwnd:X}");
        return ok;
    }

    private static bool TrySet(nint hwnd) => SetForegroundWindow(hwnd) && WaitForeground(hwnd);

    private static bool WaitForeground(nint hwnd)
    {
        var deadline = Environment.TickCount64 + AppConstants.Input.ActivateConfirmMs;
        do
        {
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(5);
        } while (Environment.TickCount64 < deadline);
        return GetForegroundWindow() == hwnd;
    }
}
