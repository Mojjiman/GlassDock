using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDock.Config;
using GlassDock.Glass;
using static GlassDock.Interop.Native;

namespace GlassDock.UI;

/// <summary>
/// The outline shown at another screen edge while the dock is dragged there: where it lands on release.
/// Click-through and never activated, so the drag keeps its capture.
/// </summary>
public sealed class DockGhostWindow : Window
{
    private readonly Border _shape = new();
    private readonly nint _hwnd;

    public DockGhostWindow(GlassLook look)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Left = -32000; // the first Show must not flash at a default position
        var a = look.Accent;
        _shape.BorderThickness = new Thickness(AppConstants.DockMove.GhostOutlineDip);
        _shape.BorderBrush = new SolidColorBrush(Color.FromArgb(AppConstants.DockMove.GhostOutlineAlpha, a.R, a.G, a.B));
        _shape.Background = new SolidColorBrush(Color.FromArgb(AppConstants.DockMove.GhostFillAlpha, a.R, a.G, a.B));
        Content = _shape;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        int ex = (int)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
    }

    /// <summary>Shows the outline over <paramref name="rect"/> (physical px).</summary>
    public void ShowAt(RECT rect, double cornerRadiusDip)
    {
        _shape.CornerRadius = new CornerRadius(cornerRadiusDip);
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}
