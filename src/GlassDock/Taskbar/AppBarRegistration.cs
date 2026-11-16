using System.Runtime.InteropServices;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Taskbar;

/// <summary>
/// Registers the dock as an appbar so maximized windows stop at the dock. The reserved strip always
/// spans the whole monitor edge (a work area is a rectangle) and independent of the dock window's real
/// bounds, which include transparent headroom.
/// </summary>
public sealed class AppBarRegistration : IDisposable
{
    private readonly nint _hwnd;
    private readonly int _callbackMsg;
    private bool _registered;
    private int _edge = -1, _thickness;
    private RECT _monitor;

    public RECT Reserved { get; private set; }
    public event Action? PositionChanged;
    public event Action<bool>? FullscreenChanged;

    public AppBarRegistration(nint hwnd)
    {
        _hwnd = hwnd;
        _callbackMsg = RegisterWindowMessage("GlassDock.AppBarCallback");
    }

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = _callbackMsg };

    /// <summary>Reserve <paramref name="thicknessPx"/> physical pixels at <paramref name="edge"/> of the monitor.</summary>
    public void Apply(RECT monitor, int edge, int thicknessPx)
    {
        // Re-applying an identical position makes the shell send ABN_POSCHANGED again; avoid the loop.
        if (_registered && edge == _edge && thicknessPx == _thickness && monitor.Equals(_monitor)) return;
        _edge = edge;
        _thickness = thicknessPx;
        _monitor = monitor;
        var abd = NewData();
        if (!_registered)
        {
            if (SHAppBarMessage(ABM_NEW, ref abd) == 0) { Log.Warn("ABM_NEW failed"); return; }
            _registered = true;
        }
        abd.uEdge = edge;
        abd.rc = Strip(monitor, edge, thicknessPx);
        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        // QUERYPOS may move the strip past other appbars but only adjusts the far side.
        switch (edge)
        {
            case ABE_BOTTOM: abd.rc.Top = abd.rc.Bottom - thicknessPx; break;
            case ABE_TOP: abd.rc.Bottom = abd.rc.Top + thicknessPx; break;
            case ABE_LEFT: abd.rc.Right = abd.rc.Left + thicknessPx; break;
            default: abd.rc.Left = abd.rc.Right - thicknessPx; break;
        }
        SHAppBarMessage(ABM_SETPOS, ref abd);
        Reserved = abd.rc;
        Log.Info($"Appbar reserved {abd.rc} on edge {edge}");
    }

    private static RECT Strip(RECT m, int edge, int t) => edge switch
    {
        ABE_BOTTOM => new RECT(m.Left, m.Bottom - t, m.Right, m.Bottom),
        ABE_TOP => new RECT(m.Left, m.Top, m.Right, m.Top + t),
        ABE_LEFT => new RECT(m.Left, m.Top, m.Left + t, m.Bottom),
        _ => new RECT(m.Right - t, m.Top, m.Right, m.Bottom),
    };

    public void Remove()
    {
        if (!_registered) return;
        var abd = NewData();
        SHAppBarMessage(ABM_REMOVE, ref abd);
        _registered = false;
        _edge = -1;
        Log.Info("Appbar removed");
    }

    /// <summary>After an Explorer restart the new shell has an empty appbar list.</summary>
    public void Reset() { _registered = false; _edge = -1; }

    /// <summary>Call from the dock window's WndProc. Returns true if handled.</summary>
    public bool HandleMessage(int msg, nint wParam, nint lParam)
    {
        if (_registered && msg == _callbackMsg)
        {
            switch ((int)wParam)
            {
                case ABN_POSCHANGED: PositionChanged?.Invoke(); break;
                case ABN_FULLSCREENAPP: FullscreenChanged?.Invoke(lParam != 0); break;
            }
            return true;
        }
        if (!_registered) return false;
        if (msg == WM_ACTIVATE) { var abd = NewData(); SHAppBarMessage(ABM_ACTIVATE, ref abd); }
        else if (msg == WM_WINDOWPOSCHANGED) { var abd = NewData(); SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref abd); }
        return false;
    }

    public void Dispose() => Remove();
}
