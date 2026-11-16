using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Taskbar;

/// <summary>
/// Keeps Explorer and its taskbar alive (so every Windows shortcut keeps working) but invisible:
/// auto-hide releases its work area, SWP_HIDEWINDOW makes it invisible, and an event hook re-hides
/// it the moment Explorer shows it again. Must be used from the UI thread (out-of-context WinEvent
/// callbacks are delivered through that thread's message loop).
/// </summary>
public sealed class TaskbarHider : IDisposable
{
    private nint _showHook;
    private WinEventProc? _showProc; // kept alive: the hook holds only a native pointer to it
    private System.Windows.Threading.DispatcherTimer? _poll;
    private uint _hookedExplorerPid;
    private bool _active;

    public int RehideCount { get; private set; }

    public void Hide()
    {
        if (_active) { Rehide("hide requested again"); return; }

        var existing = TaskbarJournal.TryLoad();
        var journal = new TaskbarJournal
        {
            OwnerPid = (uint)Environment.ProcessId,
            OwnerStartFileTime = GetProcessStartFileTime((uint)Environment.ProcessId) ?? 0,
            // An existing journal holds the real originals; the taskbar is already modified by now.
            OriginalAppBarState = existing?.OriginalAppBarState ?? ExplorerTaskbars.GetAppBarState(),
            HiddenClasses = [ExplorerTaskbars.PrimaryClass],
        };
        journal.Save();
        Log.Info($"Journal written; original appbar state {journal.OriginalAppBarState}");

        _active = true;
        ExplorerTaskbars.SetAppBarState(ABS_AUTOHIDE | (journal.OriginalAppBarState & ABS_ALWAYSONTOP));
        Rehide("initial");
        InstallShowHook();

        _poll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(AppConstants.Taskbar.RehidePollMs),
        };
        _poll.Tick += (_, _) => SafetyPoll();
        _poll.Start();

        var verify = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppConstants.Taskbar.VerifyDelayMs) };
        verify.Tick += (_, _) => { verify.Stop(); Verify(); };
        verify.Start();
    }

    private void Verify()
    {
        int state = ExplorerTaskbars.GetAppBarState();
        if ((state & ABS_AUTOHIDE) == 0)
        {
            Log.Warn($"Auto-hide not applied (state {state}); retrying once");
            ExplorerTaskbars.SetAppBarState(ABS_AUTOHIDE | (state & ABS_ALWAYSONTOP));
            state = ExplorerTaskbars.GetAppBarState();
            if ((state & ABS_AUTOHIDE) == 0)
                Log.Error("Explorer ignored ABM_SETSTATE; work area may stay reserved. Enable taskbar auto-hide in Settings as a workaround");
        }
        else
        {
            Log.Info("Native taskbar hidden and set to auto-hide");
        }
    }

    private void InstallShowHook()
    {
        uint pid = ExplorerTaskbars.ExplorerPid();
        if (pid == 0) { Log.Warn("Explorer taskbar not found; relying on the safety poll"); return; }
        if (_showHook != 0 && pid == _hookedExplorerPid) return;
        RemoveShowHook();
        _showProc = OnShowEvent;
        _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, 0, _showProc, pid, 0, WINEVENT_OUTOFCONTEXT);
        if (_showHook == 0) Log.Warn("SetWinEventHook(EVENT_OBJECT_SHOW) failed; relying on the safety poll");
        _hookedExplorerPid = pid;
    }

    private void RemoveShowHook()
    {
        if (_showHook != 0 && !UnhookWinEvent(_showHook)) Log.Warn("UnhookWinEvent failed");
        _showHook = 0;
    }

    private void OnShowEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_active || idObject != OBJID_WINDOW || hwnd == 0) return;
        var cls = GetClassName(hwnd);
        if (cls == ExplorerTaskbars.PrimaryClass) Rehide("show event");
    }

    private void SafetyPoll()
    {
        if (!_active) return;
        nint tray = ExplorerTaskbars.FindPrimary();
        if (tray != 0 && IsWindowVisible(tray)) Rehide("poll");
    }

    private void Rehide(string why)
    {
        foreach (var h in ExplorerTaskbars.Find(ExplorerTaskbars.PrimaryClass))
        {
            if (!IsWindowVisible(h)) continue;
            if (!SetWindowPos(h, 0, 0, 0, 0, 0, SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
                Log.Win32Failure("SetWindowPos(HIDE)");
            RehideCount++;
            if (why != "initial") Log.Debug($"Re-hid native taskbar ({why}), count {RehideCount}");
        }
    }

    /// <summary>Explorer restarted: its new taskbar is visible and auto-hide may need re-applying.</summary>
    public void OnExplorerRestarted()
    {
        if (!_active) return;
        Log.Info("Explorer restarted; re-hiding native taskbar");
        ExplorerTaskbars.SetAppBarState(ABS_AUTOHIDE | (ExplorerTaskbars.GetAppBarState() & ABS_ALWAYSONTOP));
        Rehide("explorer restart");
        InstallShowHook();
    }

    /// <summary>Re-applies hiding after display/settings changes that make Explorer re-layout its bar.</summary>
    public void Reassert(string why)
    {
        if (!_active) return;
        Rehide(why);
    }

    public void Restore(string reason)
    {
        if (!_active) return;
        _active = false;
        _poll?.Stop();
        RemoveShowHook();
        var journal = TaskbarJournal.TryLoad() ?? new TaskbarJournal { OriginalAppBarState = ABS_ALWAYSONTOP };
        TaskbarRestorer.Restore(journal, reason);
    }

    public void Dispose() => Restore("dispose");
}
