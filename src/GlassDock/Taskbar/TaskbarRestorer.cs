using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Taskbar;

/// <summary>
/// Undoes everything <see cref="TaskbarHider"/> did. Static and free of WPF so the watchdog and the
/// <c>--restore-taskbar</c> command can use it.
/// </summary>
public static class TaskbarRestorer
{
    public static void Restore(TaskbarJournal journal, string reason)
    {
        Log.Info($"Restoring native taskbar ({reason}); original appbar state {journal.OriginalAppBarState}");
        foreach (var cls in new[] { ExplorerTaskbars.PrimaryClass, ExplorerTaskbars.SecondaryClass })
        {
            foreach (var h in ExplorerTaskbars.Find(cls))
            {
                if (!SetWindowPos(h, 0, 0, 0, 0, 0, SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
                    Log.Win32Failure($"SetWindowPos(SHOW, {cls})");
            }
        }
        ExplorerTaskbars.RestoreTaskbandProp();
        ExplorerTaskbars.SetAppBarState(journal.OriginalAppBarState);
        int now = ExplorerTaskbars.GetAppBarState();
        if (now != journal.OriginalAppBarState)
            Log.Warn($"Appbar state after restore is {now}, expected {journal.OriginalAppBarState}");
        TaskbarJournal.Delete();
    }

    /// <summary>Called first thing on start: a journal whose owner is dead means a crash we must undo.</summary>
    public static void RecoverIfStale()
    {
        var journal = TaskbarJournal.TryLoad();
        if (journal == null) return;
        if (journal.IsOwnerAlive())
        {
            Log.Info($"Taskbar journal owned by live process {journal.OwnerPid}; leaving it");
            return;
        }
        Restore(journal, $"stale journal from pid {journal.OwnerPid}");
    }

    /// <summary><c>GlassDock.exe --restore-taskbar</c>: also used by an uninstaller.</summary>
    public static int RunCli()
    {
        var journal = TaskbarJournal.TryLoad();
        if (journal == null)
        {
            // No journal: still make sure the taskbar is visible; leave auto-hide as the user has it.
            journal = new TaskbarJournal { OriginalAppBarState = ExplorerTaskbars.GetAppBarState() };
        }
        Restore(journal, "command line");
        return 0;
    }
}
