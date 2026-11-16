using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static GlassDock.Interop.Native;

namespace GlassDock.UI;

/// <summary>
/// A thin strip along the screen edge of an auto-hidden dock (design §9.5). Nearly transparent but not
/// click-through (alpha 1), so the pointer or a file drag arriving at the edge reaches it.
/// </summary>
public sealed class EdgeSensorWindow : Window
{
    private readonly nint _hwnd;

    /// <summary>The pointer arrived on the strip (true) or left it (false).</summary>
    public event Action<bool>? PointerChanged;
    public event Action? DragArrived;

    public EdgeSensorWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        Left = -32000; // the first Show must not flash at a default position
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        int ex = (int)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        MouseEnter += (_, _) => PointerChanged?.Invoke(true);
        MouseLeave += (_, _) => PointerChanged?.Invoke(false);
        DragEnter += (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; DragArrived?.Invoke(); };
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_MOUSEACTIVATE) return 0;
        // A click on the edge must not take focus from the window the user is working in.
        handled = true;
        return MA_NOACTIVATE;
    }

    /// <summary>Shows the strip over <paramref name="rect"/> (physical px).</summary>
    public void ShowAt(RECT rect)
    {
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}
