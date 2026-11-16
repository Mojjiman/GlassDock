using System.Numerics;
using System.Runtime.InteropServices;
using GlassDock.Logging;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using static GlassDock.Interop.Native;

namespace GlassDock.Glass;

/// <summary>
/// Win32 window that draws only the live frosted backdrop (tier G1) behind the WPF dock window.
/// WPF content cannot sit above a composition visual in the same HWND (airspace), hence the
/// two-window sandwich: this window owns the dock window, which keeps the pair glued in z-order.
/// Preferred path: Windows.UI.Composition host backdrop brush with an antialiased rounded clip.
/// Fallback: accent-policy acrylic with a GDI rounded region.
/// </summary>
public sealed unsafe class GlassBackdropWindow : IDisposable
{
    private const string ClassName = "GlassDock.GlassBackdrop";
    private static WndProc? _wndProc; // one shared delegate for the class, kept alive for the process
    private static bool _classRegistered;
    private static object? _dispatcherQueueController;
    private static Compositor? _compositor;

    private readonly nint _hwnd;
    private readonly bool _useComposition;
    private DesktopWindowTarget? _target;
    private SpriteVisual? _sprite;
    private CompositionRoundedRectangleGeometry? _geometry;
    private SpriteVisual? _tintSprite;
    private (System.Windows.Media.Color Tint, bool Blur)? _tint;
    private RECT _bounds;
    private float _radius;
    private bool _visible;

    public nint Handle => _hwnd;
    public string Kind => _useComposition ? "hostBackdrop" : "accentAcrylic";

    public GlassBackdropWindow()
    {
        EnsureClass();
        _useComposition = TryCreate(WS_EX_NOREDIRECTIONBITMAP, out _hwnd) && TryAttachComposition();
        if (!_useComposition)
        {
            if (_hwnd != 0) DestroyWindow(_hwnd);
            if (!TryCreate(0, out _hwnd)) throw new InvalidOperationException("Could not create glass backdrop window");
            ApplyAccent(0x00FFFFFF);
        }
        int on = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_EXCLUDED_FROM_PEEK, ref on, sizeof(int));
        Log.Info($"Glass backdrop window created ({Kind})");
    }

    private static void EnsureClass()
    {
        if (_classRegistered) return;
        _wndProc = (h, m, w, l) => m == WM_NCHITTEST ? HTTRANSPARENT : DefWindowProc(h, m, w, l);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0) Log.Win32Failure("RegisterClassEx(glass)");
        _classRegistered = true;
    }

    private static bool TryCreate(int extraEx, out nint hwnd)
    {
        hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | extraEx, ClassName, "GlassDock Glass",
            WS_POPUP, 0, 0, 1, 1, 0, 0, GetModuleHandle(null), 0);
        if (hwnd == 0) Log.Win32Failure("CreateWindowEx(glass)");
        return hwnd != 0;
    }

    private bool TryAttachComposition()
    {
        try
        {
            EnsureCompositor();
            int on = 1;
            if (DwmSetWindowAttribute(_hwnd, DWMWA_USE_HOSTBACKDROPBRUSH, ref on, sizeof(int)) != 0)
                Log.Warn("DWMWA_USE_HOSTBACKDROPBRUSH rejected");
            _target = CreateDesktopWindowTarget(_compositor!, _hwnd);
            _sprite = _compositor!.CreateSpriteVisual();
            _sprite.Brush = _compositor.CreateHostBackdropBrush();
            _geometry = _compositor.CreateRoundedRectangleGeometry();
            _sprite.Clip = _compositor.CreateGeometricClip(_geometry);
            _target.Root = _sprite;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Warn("Host backdrop composition unavailable; using accent-policy acrylic", ex);
            return false;
        }
    }

    private static void EnsureCompositor()
    {
        if (_compositor != null) return;
        // Windows.UI.Composition needs a DispatcherQueue on this thread; it coexists with WPF's Dispatcher.
        var opts = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* current */, apartmentType = 2 /* STA */ };
        int hr = CreateDispatcherQueueController(opts, out nint controller);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        _dispatcherQueueController = WinRT.MarshalInspectable<object>.FromAbi(controller);
        Marshal.Release(controller);
        _compositor = new Compositor();
    }

    private static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, nint hwnd)
    {
        nint unk = WinRT.MarshalInspectable<object>.FromManaged(compositor);
        try
        {
            var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"); // ICompositorDesktopInterop
            int hr = Marshal.QueryInterface(unk, in iid, out nint interop);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                nint raw;
                var vtbl = *(nint**)interop;
                var create = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)vtbl[3];
                hr = create(interop, hwnd, 0, &raw);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                var target = DesktopWindowTarget.FromAbi(raw);
                Marshal.Release(raw);
                return target;
            }
            finally { Marshal.Release(interop); }
        }
        finally { Marshal.Release(unk); }
    }

    /// <summary>Fallback blur. <paramref name="tintAbgr"/> is AABBGGRR.</summary>
    private void ApplyAccent(uint tintAbgr, AccentState state = AccentState.AcrylicBlurBehind)
    {
        var accent = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = tintAbgr };
        int size = Marshal.SizeOf<AccentPolicy>();
        nint p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, p, false);
            var data = new WindowCompositionAttribData { Attribute = 19, Data = p, SizeOfData = size };
            if (SetWindowCompositionAttribute(_hwnd, ref data) == 0) Log.Warn("SetWindowCompositionAttribute(acrylic) failed");
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Places the backdrop exactly under the pill. All values are physical pixels.</summary>
    public void SetShape(RECT bounds, float cornerRadiusPx)
    {
        if (bounds.Equals(_bounds) && Math.Abs(cornerRadiusPx - _radius) < 0.01f) return;
        _bounds = bounds;
        _radius = cornerRadiusPx;
        SetWindowPos(_hwnd, HWND_TOPMOST, bounds.Left, bounds.Top, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height),
            SWP_NOACTIVATE | (_visible ? SWP_SHOWWINDOW : 0));
        if (_useComposition)
        {
            var size = new Vector2(bounds.Width, bounds.Height);
            _sprite!.Size = size;
            _geometry!.Size = size;
            _geometry.CornerRadius = new Vector2(cornerRadiusPx, cornerRadiusPx);
        }
        else
        {
            int d = (int)(cornerRadiusPx * 2);
            nint rgn = CreateRoundRectRgn(0, 0, bounds.Width + 1, bounds.Height + 1, d, d);
            // The window owns the region after SetWindowRgn succeeds.
            if (SetWindowRgn(_hwnd, rgn, true) == 0) { Log.Win32Failure("SetWindowRgn"); DeleteObject(rgn); }
        }
    }

    /// <summary>
    /// A colour over the blur, or with <paramref name="blur"/> off a plain colour, for styles without a
    /// backdrop. The pill draws its tint in WPF; this is for windows with no WPF layer above (the band).
    /// </summary>
    public void SetTint(System.Windows.Media.Color tint, bool blur)
    {
        if (_tint == (tint, blur)) return;
        _tint = (tint, blur);
        if (_useComposition)
        {
            if (_tintSprite == null)
            {
                _tintSprite = _compositor!.CreateSpriteVisual();
                _tintSprite.RelativeSizeAdjustment = Vector2.One;
                _sprite!.Children.InsertAtTop(_tintSprite);
            }
            var oldTint = _tintSprite.Brush;
            _tintSprite.Brush = _compositor!.CreateColorBrush(new Windows.UI.Color { A = tint.A, R = tint.R, G = tint.G, B = tint.B });
            oldTint?.Dispose();
            var oldBackdrop = _sprite!.Brush;
            _sprite.Brush = blur ? _compositor.CreateHostBackdropBrush() : null;
            oldBackdrop?.Dispose();
        }
        else
        {
            uint abgr = (uint)(tint.A << 24 | tint.B << 16 | tint.G << 8 | tint.R);
            ApplyAccent(abgr, blur ? AccentState.AcrylicBlurBehind : AccentState.TransparentGradient);
        }
    }

    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        ShowWindow(_hwnd, visible ? SW_SHOWNA : SW_HIDE);
        if (visible) SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Dispose()
    {
        _target?.Dispose();
        _tintSprite?.Dispose();
        _sprite?.Dispose();
        if (_hwnd != 0) DestroyWindow(_hwnd);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);
}
