using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Glass;

/// <summary>
/// Tells whether any window lies behind the pill. The lens only knows the wallpaper, so while a
/// window is behind the dock the live frosted backdrop must take over. Event driven: window
/// move/show/hide/minimize/cloak events mark the state dirty and a throttled check walks the
/// top-level windows; nothing runs while the desktop is still.
/// </summary>
public sealed class BackdropOcclusionMonitor : IDisposable
{
    private static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    private readonly WinEventProc _proc;
    private readonly List<nint> _hooks = [];
    private readonly DispatcherTimer _throttle;
    private readonly DispatcherTimer _release;
    private readonly Func<RECT?> _region;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private bool _occluded;
    private bool _enabled;

    public event Action? Changed;
    public bool IsOccluded => _enabled && _occluded;

    /// <param name="region">Pill rectangle in physical pixels, or null while the dock is not shown.</param>
    public BackdropOcclusionMonitor(Func<RECT?> region)
    {
        _region = region;
        _proc = OnEvent;
        _throttle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000.0 / AppConstants.Glass.OcclusionCheckHz) };
        _throttle.Tick += (_, _) => { _throttle.Stop(); Evaluate(); };
        _release = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppConstants.Glass.OcclusionReleaseMs) };
        _release.Tick += (_, _) => { _release.Stop(); Evaluate(releaseNow: true); };
    }

    /// <summary>Hooks are installed only while a lens style is active.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value)
            {
                Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
                Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE);
                Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
                Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
                Evaluate(releaseNow: true);
            }
            else
            {
                Unhook();
                _throttle.Stop();
                _release.Stop();
                _occluded = false;
            }
        }
    }

    /// <summary>Re-check now, e.g. after the pill moved or resized.</summary>
    public void Kick()
    {
        if (_enabled && !_throttle.IsEnabled) _throttle.Start();
    }

    private void Hook(uint min, uint max)
    {
        nint h = SetWinEventHook(min, max, 0, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h == 0) Log.Warn($"SetWinEventHook(0x{min:X}-0x{max:X}) failed; glass may not notice windows behind the dock");
        else _hooks.Add(h);
    }

    private void Unhook()
    {
        foreach (var h in _hooks)
            if (!UnhookWinEvent(h)) Log.Warn("UnhookWinEvent failed");
        _hooks.Clear();
    }

    // Fires for every window event on the desktop: only filter and arm the throttle here.
    private void OnEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == 0) return;
        Kick();
    }

    private void Evaluate(bool releaseNow = false)
    {
        if (!_enabled) return;
        var region = _region();
        nint hit = region is { } r ? FindWindowBehind(r) : 0;
        bool now = hit != 0;
        if (now == _occluded) { _release.Stop(); return; }
        if (!now && !releaseNow)
        {
            // Leaving is debounced so a window dragged along the edge does not flicker the glass.
            if (!_release.IsEnabled) _release.Start();
            return;
        }
        _release.Stop();
        _occluded = now;
        Log.Debug(now ? $"Window behind the dock: {Describe(hit)}; switching to frosted glass" : "Nothing behind the dock; liquid glass");
        Changed?.Invoke();
    }

    private nint FindWindowBehind(RECT region)
    {
        nint found = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
            if (GetWindowProcessId(h) == _ownPid) return true;
            // Click-through overlays (game/recording HUDs) are drawn but hide nothing meaningful.
            if (((long)GetWindowLongPtr(h, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT b, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0 || b.Width <= 0 || b.Height <= 0) return true;
            if (!b.Intersects(region)) return true;
            if (ShellClasses.Contains(GetClassName(h))) return true;
            found = h;
            return false;
        }, 0);
        return found;
    }

    private static string Describe(nint hwnd) =>
        $"0x{hwnd:X} class={GetClassName(hwnd)} exe={Path.GetFileName(GetProcessPath(GetWindowProcessId(hwnd)))}";

    public void Dispose()
    {
        Enabled = false;
    }
}
