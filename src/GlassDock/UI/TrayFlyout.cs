using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Shell;
using ManagedShell.WindowsTray;
using static GlassDock.Interop.Native;
using NotifyIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace GlassDock.UI;

/// <summary>Grid of notification-area icons captured by ManagedShell's tray service.</summary>
public sealed class TrayFlyout : FlyoutWindow
{
    private const int Columns = 6;
    private const double CellDip = 40;
    private const double IconDip = 20;
    private readonly NotificationArea _area;
    private readonly WrapPanel _grid = new() { Width = Columns * CellDip };

    public TrayFlyout(DockWindow dock, NotificationArea area) : base(dock)
    {
        _area = area;
        var root = new StackPanel { Margin = new Thickness(10) };
        root.Children.Add(_grid);
        var footer = new TextBlock { Text = "시스템 아이콘(네트워크·소리·배터리)은 빠른 설정 타일에 있습니다.", FontSize = 11, Opacity = 0.65, Margin = new Thickness(4, 8, 4, 0), TextWrapping = TextWrapping.Wrap, Width = Columns * CellDip - 8 };
        root.Children.Add(footer);
        Content = root;
        _area.TrayIcons.CollectionChanged += OnIconsChanged;
        Closed += (_, _) => _area.TrayIcons.CollectionChanged -= OnIconsChanged;
        Build();
    }

    private void OnIconsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(Build);

    private void Build()
    {
        _grid.Children.Clear();
        foreach (var icon in _area.TrayIcons.Where(i => !i.IsHidden).ToList())
            _grid.Children.Add(CreateCell(icon));
        if (_grid.Children.Count == 0)
            _grid.Children.Add(new TextBlock { Text = "표시할 아이콘이 없습니다.", Margin = new Thickness(6), Opacity = 0.7 });
    }

    private Border CreateCell(NotifyIcon icon)
    {
        var img = new Image { Source = icon.Icon, Width = IconDip, Height = IconDip };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var cell = new Border
        {
            Width = CellDip,
            Height = CellDip,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Child = img,
            ToolTip = string.IsNullOrWhiteSpace(icon.Title) ? null : icon.Title,
        };
        var hover = new SolidColorBrush(Look.IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0, 0, 0));
        cell.MouseEnter += (_, _) => { cell.Background = hover; SetPlacement(icon, cell); Safe(() => icon.IconMouseEnter(CursorParam()), "enter"); };
        cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; Safe(() => icon.IconMouseLeave(CursorParam()), "leave"); };
        cell.MouseMove += (_, _) => Safe(() => icon.IconMouseMove(CursorParam()), "move");
        cell.MouseDown += (_, e) =>
        {
            e.Handled = true;
            // A right-click opens the app's menu at the pointer (some apps already on the press). Closing the
            // grid would leave that menu floating where the grid was, so it stays until the menu is over.
            if (e.ChangedButton == MouseButton.Right) WatchMenu();
            Safe(() => icon.IconMouseDown(e.ChangedButton, CursorParam(), System.Windows.Forms.SystemInformation.DoubleClickTime), "down");
        };
        cell.MouseUp += (_, e) =>
        {
            e.Handled = true;
            var button = e.ChangedButton;
            // A left click usually opens the app's window, which the grid must not cover.
            if (button != MouseButton.Right) SafeClose();
            Safe(() => icon.IconMouseUp(button, CursorParam(), System.Windows.Forms.SystemInformation.DoubleClickTime), "up");
        };
        return cell;
    }

    /// <summary>Window class of a classic Win32 popup menu (TrackPopupMenu).</summary>
    private const string PopupMenuClass = "#32768";
    private static readonly int[] MouseButtons = [VK_LBUTTON, VK_RBUTTON, VK_MBUTTON];
    private readonly bool[] _buttonDown = new bool[MouseButtons.Length];
    private DispatcherTimer? _menuWatch;
    private bool _sawPopupMenu;
    private DateTime _menuAsked;

    private void WatchMenu()
    {
        KeepOpenWhileDeactivated = true;
        _sawPopupMenu = false;
        _menuAsked = DateTime.UtcNow;
        for (int i = 0; i < MouseButtons.Length; i++) _buttonDown[i] = ButtonDown(MouseButtons[i]);
        if (_menuWatch == null)
        {
            _menuWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppConstants.Tray.MenuWatchMs) };
            _menuWatch.Tick += (_, _) => CheckMenu();
            Closed += (_, _) => _menuWatch.Stop();
        }
        _menuWatch.Start();
    }

    /// <summary>
    /// Which window is the menu cannot be told reliably: Steam's menu lives in another process than its icon,
    /// and the foreground hops between hidden windows while it opens. So the grid stays until the menu must
    /// be over: a Win32 popup menu that was seen is gone, or the user clicks outside the grid (choosing an
    /// item or dismissing the menu both end it).
    /// </summary>
    private void CheckMenu()
    {
        if (PopupMenuOpen()) { _sawPopupMenu = true; return; }
        if (_sawPopupMenu) { CloseAfterMenu("the menu closed"); return; }
        if (ClickedOutside()) { CloseAfterMenu("a click outside"); return; }
        // Still (or again) the active window and no menu came up: back to the normal light dismiss.
        if (GetForegroundWindow() == Hwnd && DateTime.UtcNow - _menuAsked > TimeSpan.FromMilliseconds(AppConstants.Tray.MenuWaitMs))
        {
            _menuWatch?.Stop();
            KeepOpenWhileDeactivated = false;
        }
    }

    private void CloseAfterMenu(string why)
    {
        Log.Debug($"Tray grid closed after an icon menu: {why}");
        _menuWatch?.Stop();
        SafeClose();
    }

    private bool ClickedOutside()
    {
        bool pressed = false;
        for (int i = 0; i < MouseButtons.Length; i++)
        {
            bool down = ButtonDown(MouseButtons[i]);
            if (down && !_buttonDown[i]) pressed = true;
            _buttonDown[i] = down;
        }
        if (!pressed || !GetCursorPos(out var p)) return false;
        // By window, not by rectangle: the app's menu often opens over the grid, and a click on it counts.
        return GetAncestor(WindowFromPoint(p), GA_ROOT) != Hwnd;
    }

    private static bool ButtonDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool PopupMenuOpen()
    {
        // Hidden menu windows can linger in the z-order, so look past them.
        for (nint w = FindWindowEx(0, 0, PopupMenuClass, null); w != 0; w = FindWindowEx(0, w, PopupMenuClass, null))
            if (IsWindowVisible(w)) return true;
        return false;
    }

    /// <summary>Apps ask where their icon is (Shell_NotifyIconGetRect) to place their own popups.</summary>
    private void SetPlacement(NotifyIcon icon, FrameworkElement cell)
    {
        try
        {
            var r = Dock.ScreenRectOf(cell);
            icon.Placement = new ManagedShell.Interop.NativeMethods.Rect(r.Left, r.Top, r.Right, r.Bottom);
        }
        catch (InvalidOperationException ex) { Log.Debug($"Tray icon placement unavailable: {ex.Message}"); }
    }

    private static uint CursorParam()
    {
        GetCursorPos(out var p);
        return (uint)MakeLParam(p.X, p.Y);
    }

    private static void Safe(Action a, string what)
    {
        try { a(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Forwarding tray mouse {what} failed", ex);
        }
    }
}
