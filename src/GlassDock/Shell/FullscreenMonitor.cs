using System.Collections.Specialized;
using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Logging;
using ManagedShell.AppBar;
using static GlassDock.Interop.Native;

namespace GlassDock.Shell;

/// <summary>
/// Decides whether a fullscreen app (game, video, presentation) covers the dock's monitor. Sources are
/// OR-ed and debounced: ManagedShell's FullScreenHelper, our appbar's ABN_FULLSCREENAPP, and
/// SHQueryUserNotificationState. Start or search in front overrides them all: the game is still
/// fullscreen behind it, but the user asked for the shell.
/// </summary>
public sealed class FullscreenMonitor : IDisposable
{
    private readonly FullScreenHelper _helper;
    private readonly Dispatcher _dispatcher;
    // A thread-pool timer that posts back, not a DispatcherTimer: WM_TIMER is only generated when the
    // queue is empty, and the WinEvent flood of the Alt+Tab switch animation held it back ~200 ms.
    private readonly Timer _enterTimer;
    private bool _appBarSaysFullscreen;
    private bool _state;
    private bool _exitCheckQueued;
    private readonly WinEventProc _foregroundProc;
    private readonly nint _foregroundHook;

    public Func<nint> MonitorProvider { get; set; } = () => DisplayInfo.Primary().Handle;
    public event Action<bool>? Changed;
    public bool IsFullscreen => _state;
    /// <summary>Start or search is the foreground window.</summary>
    public bool LauncherOpen { get; private set; }
    /// <summary>Our own UI in use (a press on the dock, an open flyout): keep the dock over the game.</summary>
    public Func<bool>? HoldShown { get; set; }

    /// <summary>Re-evaluates now, e.g. once what <see cref="HoldShown"/> reports has ended.</summary>
    public void Recheck() => Kick();

    public FullscreenMonitor(FullScreenHelper helper)
    {
        _helper = helper;
        _helper.FullScreenApps.CollectionChanged += OnAppsChanged;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _enterTimer = new Timer(_ => _dispatcher.BeginInvoke(DispatcherPriority.Normal, Evaluate));
        _foregroundProc = OnForeground; // kept in a field: the hook calls it after this constructor returns
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_foregroundHook == 0) Log.Warn("SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed; Start will not bring the dock over fullscreen games");
    }

    /// <summary>Whether the window belongs to Start or search.</summary>
    public static bool IsLauncher(nint hwnd) =>
        hwnd != 0 && GetProcessPath(GetWindowProcessId(hwnd)) is { } path
        && AppConstants.Fullscreen.LauncherProcesses.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    private void OnForeground(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        bool launcher = IsLauncher(hwnd);
        if (launcher != LauncherOpen)
        {
            LauncherOpen = launcher;
            Log.Debug($"Launcher {(launcher ? "opened" : "closed")}");
        }
        // Every change: the game coming back from one of our popups changes no fullscreen list
        // (ManagedShell keeps a fullscreen app active while a topmost window is in front).
        Kick();
    }

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Log.Debug($"Fullscreen app list changed: {e.Action}");
        Kick();
    }

    public void ReportAppBarFullscreen(bool full)
    {
        Log.Debug($"ABN_FULLSCREENAPP {full}");
        _appBarSaysFullscreen = full;
        Kick();
    }

    private void Kick()
    {
        if (!_state)
        {
            // It may be starting: wait briefly so a game's startup mode switches do not flash the dock.
            _enterTimer.Change(AppConstants.Fullscreen.EnterDebounceMs, Timeout.Infinite);
            return;
        }
        // It most likely ended: check as soon as the current events are handled.
        _enterTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_exitCheckQueued) return;
        _exitCheckQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => { _exitCheckQueued = false; Evaluate(); });
    }

    private void Evaluate()
    {
        nint mon = MonitorProvider();
        var hit = _helper.FullScreenApps.FirstOrDefault(a => MonitorFromWindow(a.hWnd, MONITOR_DEFAULTTONEAREST) == mon && !IsIgnored(a.hWnd));
        bool any = hit != null;
        string reason = hit != null ? Describe(hit.hWnd) : "";
        if (!any && _appBarSaysFullscreen)
        {
            // ABN_FULLSCREENAPP is flaky on Windows 11; confirm with the foreground window.
            nint fg = GetForegroundWindow();
            any = fg != 0 && MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) == mon && CoversMonitor(fg, mon) && !IsIgnored(fg);
            if (any) reason = "appbar+" + Describe(fg);
        }
        if (!any && SHQueryUserNotificationState(out int qs) == 0 && qs is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
        {
            any = true;
            reason = $"notification state {qs}";
        }
        if (any && LauncherOpen)
        {
            any = false;
            reason = "(Start or search open)";
        }
        else if (any && (GetWindowProcessId(GetForegroundWindow()) == (uint)Environment.ProcessId || HoldShown?.Invoke() == true))
        {
            // Opened from Start: Start closes and the game comes back for a moment before our popup
            // takes the foreground; keep the dock with it.
            any = false;
            reason = "(dock in use)";
        }

        if (any == _state) return;
        _state = any;
        Log.Info($"Fullscreen state on dock monitor: {any} {reason}");
        Changed?.Invoke(any);
    }

    private static bool IsIgnored(nint hwnd) =>
        !IsWindowVisible(hwnd) || IsCloaked(hwnd)
        || GetWindowProcessId(hwnd) == (uint)Environment.ProcessId
        || AppConstants.Fullscreen.IgnoredClasses.Contains(GetClassName(hwnd));

    private static string Describe(nint hwnd)
    {
        uint pid = GetWindowProcessId(hwnd);
        var path = GetProcessPath(pid);
        // An elevated game gets no Win+N from our medium-integrity hook (UIPI); log it to tell that apart.
        string elevated = IsProcessElevated(pid) switch { true => "yes", false => "no", null => "unknown" };
        return $"(0x{hwnd:X} class={GetClassName(hwnd)} exe={Path.GetFileName(path)} elevated={elevated})";
    }

    private static bool CoversMonitor(nint hwnd, nint mon)
    {
        // A maximized window's frame overhangs the monitor, so it would pass the rect test and be taken
        // for a fullscreen app while ABN_FULLSCREENAPP is still set (Explorer clears it a few ms after
        // the Alt+Tab out of a game, so the exit check can run before that).
        if (IsZoomed(hwnd) || !GetWindowRect(hwnd, out var r)) return false;
        var d = DisplayInfo.FromHandle(mon);
        return r.Contains(d.Bounds);
    }

    public void Dispose()
    {
        _helper.FullScreenApps.CollectionChanged -= OnAppsChanged;
        if (_foregroundHook != 0 && !UnhookWinEvent(_foregroundHook)) Log.Warn("UnhookWinEvent(foreground) failed");
        _enterTimer.Dispose();
    }
}
