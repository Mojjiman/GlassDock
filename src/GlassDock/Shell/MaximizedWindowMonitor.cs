using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Shell;

/// <summary>
/// Tells whether a maximized window is showing on the dock's monitor (it stops at the space the dock
/// reserves, leaving the wallpaper visible around the pill). Event driven like
/// <see cref="Glass.BackdropOcclusionMonitor"/>: maximize/restore (location change), minimize, show/hide,
/// close and cloak (virtual desktop switch) arm a throttled walk of the top-level windows.
/// </summary>
public sealed class MaximizedWindowMonitor : IDisposable
{
    private readonly WinEventProc _proc;
    private readonly List<nint> _hooks = [];
    private readonly DispatcherTimer _throttle;
    private readonly Func<nint> _monitor;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private bool _enabled, _any;

    public event Action? Changed;
    public bool AnyMaximized => _enabled && _any;

    /// <param name="monitor">The dock's monitor handle.</param>
    public MaximizedWindowMonitor(Func<nint> monitor)
    {
        _monitor = monitor;
        _proc = OnEvent;
        _throttle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000.0 / AppConstants.Glass.OcclusionCheckHz) };
        _throttle.Tick += (_, _) => { _throttle.Stop(); Evaluate(); };
    }

    /// <summary>Hooks are installed only while the band is wanted.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value)
            {
                Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
                Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
                Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE);
                Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
                Evaluate();
            }
            else
            {
                foreach (var h in _hooks)
                    if (!UnhookWinEvent(h)) Log.Warn("UnhookWinEvent failed");
                _hooks.Clear();
                _throttle.Stop();
                _any = false;
            }
        }
    }

    private void Hook(uint min, uint max)
    {
        nint h = SetWinEventHook(min, max, 0, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h == 0) Log.Warn($"SetWinEventHook(0x{min:X}-0x{max:X}) failed; the band may miss maximized windows");
        else _hooks.Add(h);
    }

    // Fires for every window event on the desktop: only filter and arm the throttle here.
    private void OnEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == 0) return;
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    private void Evaluate()
    {
        if (!_enabled) return;
        nint monitor = _monitor();
        nint found = 0;
        EnumWindows((h, _) =>
        {
            if (!IsZoomed(h) || !IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
            if (GetWindowProcessId(h) == _ownPid || MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != monitor) return true;
            found = h;
            return false;
        }, 0);
        bool any = found != 0;
        if (any == _any) return;
        _any = any;
        Log.Debug(any ? $"Maximized window 0x{found:X} ({GetClassName(found)}); showing the band" : "No maximized window; hiding the band");
        Changed?.Invoke();
    }

    public void Dispose() => Enabled = false;
}
