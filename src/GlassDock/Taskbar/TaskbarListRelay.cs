using System.Windows.Interop;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Taskbar;

/// <summary>
/// Keeps apps' ITaskbarList calls reaching Explorer while ManagedShell listens to them.
/// <para>
/// ITaskbarList sends to the <c>TaskbandHWND</c> property of the topmost <c>Shell_TrayWnd</c>. ManagedShell
/// points that at its own hook window (for overlay badges and progress) and takes it off Explorer's tray,
/// so AddTab/DeleteTab never reach Explorer. Discord hides to the tray with DeleteTab and comes back with
/// AddTab; Explorer then never re-adds the window, and it drops out of Alt+Tab until Discord restarts.
/// </para>
/// This window sits in between: it hands every message to ManagedShell's hook as before, and also posts the
/// SHELLHOOK notifications (window added, removed, activated) to Explorer's task band.
/// </summary>
public sealed class TaskbarListRelay : IDisposable
{
    private readonly HwndSource _source;
    private readonly int _shellHookMsg = RegisterWindowMessage("SHELLHOOK");
    private nint _fakeTray, _hook, _explorerBand;
    private readonly HashSet<nint> _loggedCodes = [];

    public TaskbarListRelay()
    {
        // Message-only: no broadcasts (TaskbarCreated and the like), only what is sent to it.
        _source = new HwndSource(new HwndSourceParameters("GlassDock.TaskbarListRelay") { ParentWindow = HWND_MESSAGE });
        _source.AddHook(WndProc);
        Attach();
    }

    /// <summary>
    /// Points the fake tray's property at this window. ManagedShell sets it back to its hook on every
    /// TaskbarCreated, so this runs again after each one; it is a no-op when the relay is already in place.
    /// </summary>
    public void Attach()
    {
        nint tray = FindOwnTray();
        if (tray == 0) { Log.Warn("ManagedShell's Shell_TrayWnd not found; ITaskbarList goes to it unrelayed"); return; }
        nint target = GetProp(tray, TaskbandProp);
        if (target == _source.Handle) return;
        if (target == 0 || GetWindowProcessId(target) != (uint)Environment.ProcessId)
        {
            Log.Warn($"Unexpected ITaskbarList target 0x{target:X} on our tray; not relaying");
            return;
        }
        _fakeTray = tray;
        _hook = target;
        if (!SetProp(tray, TaskbandProp, _source.Handle)) { Log.Win32Failure("SetProp(TaskbandHWND)"); return; }
        Log.Info($"Relaying ITaskbarList: tray 0x{tray:X}, ManagedShell hook 0x{target:X}");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // ITaskbarList uses WM_USER+n and the registered SHELLHOOK message; the rest is this window's own traffic.
        if (msg < WM_USER || _hook == 0) return 0;
        if (msg == _shellHookMsg) ToExplorer(wParam, lParam);
        handled = true;
        // Same thread as ManagedShell's hook, so this is a direct call; shared-memory handles in WM_USER+n
        // were made for this process and stay valid.
        return SendMessage(_hook, msg, wParam, lParam);
    }

    private void ToExplorer(nint code, nint window)
    {
        if (_explorerBand == 0 || !IsWindow(_explorerBand)) _explorerBand = ExplorerTaskbars.FindTaskband(ExplorerTaskbars.FindPrimary());
        if (_explorerBand == 0) return;
        if (!PostMessage(_explorerBand, _shellHookMsg, (nuint)code, window)) Log.Win32Failure("PostMessage(SHELLHOOK to Explorer)");
        // The first of each kind at Info, so the log shows the relay working without debug logging on.
        string line = $"ITaskbarList SHELLHOOK {code} for 0x{window:X} passed to Explorer 0x{_explorerBand:X}";
        if (_loggedCodes.Add(code)) Log.Info(line);
        else Log.Debug(line);
    }

    private static nint FindOwnTray()
    {
        uint self = (uint)Environment.ProcessId;
        nint h = 0;
        while ((h = FindWindowEx(0, h, ExplorerTaskbars.PrimaryClass, null)) != 0)
            if (GetWindowProcessId(h) == self) return h;
        return 0;
    }

    /// <summary>Hands the property back to ManagedShell's hook, so its own dispose restores Explorer's.</summary>
    public void Dispose()
    {
        if (_fakeTray != 0 && IsWindow(_fakeTray) && GetProp(_fakeTray, TaskbandProp) == _source.Handle
            && !SetProp(_fakeTray, TaskbandProp, _hook))
            Log.Win32Failure("SetProp(TaskbandHWND back to ManagedShell)");
        _source.Dispose();
    }
}
