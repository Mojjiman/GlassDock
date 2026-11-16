using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Input;
using static GlassDock.Interop.Native;

namespace GlassDock.UI;

/// <summary>
/// The apps inside a dock group, as a few large icons under the group's name. Click starts an app or
/// brings it forward, right-click has the app's menu, holding an app picks it up to reorder, and a click
/// on the name edits it in place.
/// </summary>
public sealed class GroupFlyout : FlyoutWindow
{
    private const string MoreGlyph = "";

    private readonly AppItem _item;
    private readonly AppsModel _apps;
    private readonly IconProvider _icons;
    private readonly Color _activeColor;
    private readonly Canvas _grid = new();
    private readonly TextBlock _title;
    private readonly TextBox _name;
    private readonly TextBlock _count;
    // Cells in their current visual order; reordering moves entries here and slides the cells after them.
    private readonly List<(PinnedItem Child, IconCell Cell)> _cells = [];
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(AppConstants.Groups.HoldToReorderMs) };
    private int _columns;
    private bool _renameOnLoad;

    private IconCell? _pressed;
    private Point _pressAt;
    private (PinnedItem Child, IconCell Cell)? _lifted;
    private int _liftedFrom;
    private Vector _grabOffset;

    private static double CellW => AppConstants.Groups.FlyoutCellWidthDip;
    private static double CellH => AppConstants.Groups.FlyoutCellHeightDip;

    public AppItem Item => _item;

    public GroupFlyout(DockWindow dock, AppItem item, AppsModel apps, IconProvider icons, string activeIndicatorColor, bool rename) : base(dock)
    {
        _item = item;
        _apps = apps;
        _icons = icons;
        _activeColor = AppButton.ActiveIndicatorColor(Look, activeIndicatorColor);
        _renameOnLoad = rename;

        _title = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = AppConstants.Groups.FlyoutTitleFontSize,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.IBeam,
            ToolTip = "눌러서 이름 바꾸기",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _title.MouseLeftButtonUp += (_, e) => { e.Handled = true; BeginRename(); };
        _name = new TextBox
        {
            FontSize = AppConstants.Groups.FlyoutTitleFontSize,
            FontWeight = FontWeights.SemiBold,
            Background = Brushes.Transparent,
            Foreground = Foreground,
            CaretBrush = Foreground,
            BorderThickness = new Thickness(0, 0, 0, 1.5),
            BorderBrush = new SolidColorBrush(Look.Accent),
            Padding = new Thickness(0),
            MinWidth = CellW,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            SelectionBrush = new SolidColorBrush(Look.Accent),
            Template = UnderlineTextBoxTemplate(),
        };
        _name.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; EndRename(commit: true); }
            // Handled here so Esc only cancels the edit instead of also closing the flyout.
            else if (e.Key == Key.Escape) { e.Handled = true; EndRename(commit: false); }
        };
        _name.LostKeyboardFocus += (_, _) => EndRename(commit: true);
        _count = new TextBlock
        {
            FontSize = AppConstants.Groups.FlyoutCountFontSize,
            Opacity = 0.55,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var titleHost = new Grid();
        titleHost.Children.Add(_title);
        titleHost.Children.Add(_name);
        var names = new StackPanel { Orientation = Orientation.Horizontal };
        names.Children.Add(titleHost);
        names.Children.Add(_count);
        Button? more = null;
        more = IconButton(MoreGlyph, "묶음 메뉴", () => ShowGroupMenu(more!));
        DockPanel.SetDock(more, System.Windows.Controls.Dock.Right);
        var header = new DockPanel { Margin = new Thickness(10, 0, 0, 10) };
        header.Children.Add(more);
        header.Children.Add(names);

        _grid.MouseMove += OnGridMouseMove;
        _grid.MouseLeftButtonUp += (_, _) => PutDown();
        _grid.LostMouseCapture += (_, _) => PutDown();
        _hold.Tick += (_, _) => Lift();

        var root = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer
        {
            Content = _grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = AppConstants.Stacks.VisibleRows * CellH,
        });
        Content = Tinted(root);

        Build();
        item.Windows.CollectionChanged += OnWindowsChanged;
        item.PropertyChanged += OnItemChanged;
        Loaded += (_, _) => { if (_renameOnLoad) BeginRename(); };
        Closing += (_, _) => { _hold.Stop(); EndRename(commit: true); };
        Closed += (_, _) =>
        {
            item.Windows.CollectionChanged -= OnWindowsChanged;
            item.PropertyChanged -= OnItemChanged;
        };
    }

    /// <summary>
    /// Just the text over an accent underline. The Fluent theme's TextBox turns white with a clear button
    /// once focused, which hides light text on this dark panel.
    /// </summary>
    private static ControlTemplate UnderlineTextBoxTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(FrameworkElement.FocusableProperty, false);
        border.AppendChild(host);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = border };
    }

    public void BeginRename()
    {
        _renameOnLoad = false;
        _name.Text = _item.DisplayName;
        _title.Visibility = Visibility.Hidden; // keeps the header from jumping while the box is up
        _name.Visibility = Visibility.Visible;
        _name.Focus();
        _name.SelectAll();
    }

    private void EndRename(bool commit)
    {
        if (_name.Visibility != Visibility.Visible) return;
        _name.Visibility = Visibility.Collapsed;
        _title.Visibility = Visibility.Visible;
        if (commit && _name.Text.Trim() is { Length: > 0 } n) _apps.RenameGroup(_item, n);
        _title.Text = _item.DisplayName;
        Keyboard.ClearFocus();
    }

    private void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshIndicators);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppItem.IsActive) or nameof(AppItem.IsRunning)) Dispatcher.BeginInvoke(RefreshIndicators);
    }

    private void Build()
    {
        var children = _item.GroupChildren;
        _columns = Math.Clamp(children.Count, 1, AppConstants.Groups.FlyoutColumns);
        int rows = (children.Count + _columns - 1) / _columns;
        _grid.Width = _columns * CellW;
        _grid.Height = rows * CellH;
        double scale = VisualTreeHelper.GetDpi(Dock).DpiScaleX;
        int px = IconProvider.SnapSize(AppConstants.Groups.FlyoutIconDip * scale);
        _grid.Children.Clear();
        _cells.Clear();
        foreach (var child in children)
        {
            var cell = CreateCell(child, px);
            _cells.Add((child, cell));
            _grid.Children.Add(cell);
            Place(cell, _cells.Count - 1, animate: false);
        }
        _count.Text = $"앱 {children.Count}개";
        RefreshIndicators();
    }

    private void RefreshIndicators()
    {
        foreach (var (child, cell) in _cells)
        {
            var ws = _item.WindowsOf(child);
            // ManagedShell's state, not the foreground window: the flyout itself is in front while open.
            bool active = ws.Any(w => w.State == ManagedShell.WindowsTasks.ApplicationWindow.WindowState.Active);
            cell.SetIndicator(ws.Count > 0, active, _activeColor);
        }
    }

    private Point SlotOf(int index) => new(index % _columns * CellW, index / _columns * CellH);

    private void Place(IconCell cell, int index, bool animate)
    {
        var at = SlotOf(index);
        if (!animate)
        {
            cell.BeginAnimation(Canvas.LeftProperty, null);
            cell.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(cell, at.X);
            Canvas.SetTop(cell, at.Y);
            return;
        }
        var d = TimeSpan.FromMilliseconds(AppConstants.Groups.ReorderShiftMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        cell.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(at.X, d) { EasingFunction = ease });
        cell.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(at.Y, d) { EasingFunction = ease });
    }

    private IconCell CreateCell(PinnedItem child, int px)
    {
        string name = child.DisplayName ?? "?";
        var cell = new IconCell(name, name, Look, withIndicator: true,
            (AppConstants.Groups.FlyoutIconDip, CellW, CellH, AppConstants.Groups.FlyoutNameFontSize))
        {
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        if (AppsModel.IconSource(child) is { } src) _ = SetIconAsync(cell.Icon, src, px);
        cell.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = cell;
            _pressAt = e.GetPosition(_grid);
            _hold.Stop();
            _hold.Start();
        };
        cell.MouseLeave += (_, _) => { if (_lifted == null) CancelPress(); };
        cell.MouseLeftButtonUp += (_, _) =>
        {
            if (_lifted != null || _pressed != cell) return;
            CancelPress();
            SafeClose();
            _apps.ActivateChild(_item, child);
        };
        cell.MouseRightButtonUp += (_, ev) => { ev.Handled = true; ShowChildMenu(cell, child); };
        return cell;
    }

    private void CancelPress()
    {
        _hold.Stop();
        _pressed = null;
    }

    /// <summary>The hold elapsed without the pointer wandering off: pick the app up.</summary>
    private void Lift()
    {
        _hold.Stop();
        if (_pressed is not { } cell || Mouse.LeftButton != MouseButtonState.Pressed) { _pressed = null; return; }
        int index = _cells.FindIndex(c => c.Cell == cell);
        if (index < 0) return;
        _lifted = _cells[index];
        _liftedFrom = index;
        _grabOffset = _pressAt - SlotOf(index);
        Panel.SetZIndex(cell, 1);
        AnimateLift(cell, true);
        Mouse.Capture(_grid);
    }

    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_grid);
        if (_lifted is not { } lifted)
        {
            // A press that moves off before the hold elapses is neither a click nor a pick-up.
            if (_pressed != null && (pos - _pressAt).Length > SystemParameters.MinimumHorizontalDragDistance * 2) CancelPress();
            return;
        }
        var topLeft = pos - _grabOffset;
        lifted.Cell.BeginAnimation(Canvas.LeftProperty, null);
        lifted.Cell.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetLeft(lifted.Cell, topLeft.X);
        Canvas.SetTop(lifted.Cell, topLeft.Y);

        // The slot under the icon's centre, kept inside the grid and the list.
        int col = Math.Clamp((int)((topLeft.X + CellW / 2) / CellW), 0, _columns - 1);
        int row = Math.Clamp((int)((topLeft.Y + CellH / 2) / CellH), 0, (_cells.Count - 1) / _columns);
        int target = Math.Min(row * _columns + col, _cells.Count - 1);
        int current = _cells.IndexOf(lifted);
        if (target == current) return;
        _cells.RemoveAt(current);
        _cells.Insert(target, lifted);
        for (int i = 0; i < _cells.Count; i++)
            if (_cells[i].Cell != lifted.Cell) Place(_cells[i].Cell, i, animate: true);
    }

    private void PutDown()
    {
        if (_lifted is not { } lifted) return;
        _lifted = null;
        _pressed = null;
        if (Mouse.Captured == _grid) _grid.ReleaseMouseCapture();
        int final = _cells.IndexOf(lifted);
        Place(lifted.Cell, final, animate: true);
        AnimateLift(lifted.Cell, false);
        Panel.SetZIndex(lifted.Cell, 0);
        if (final != _liftedFrom) _apps.MoveInGroup(_item, lifted.Child, final);
    }

    private static void AnimateLift(IconCell cell, bool up)
    {
        var d = TimeSpan.FromMilliseconds(AppConstants.Animation.HoverMs);
        double scale = up ? AppConstants.Groups.LiftScale : 1;
        var t = (ScaleTransform)cell.RenderTransform;
        t.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, d));
        t.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, d));
        cell.BeginAnimation(OpacityProperty, new DoubleAnimation(up ? AppConstants.Groups.LiftOpacity : 1, d));
    }

    private async Task SetIconAsync(Image img, string source, int px)
    {
        var src = await _icons.GetAsync(source, px);
        if (src != null) img.Source = src;
    }

    private void ShowGroupMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        Add(menu, "이름 바꾸기", BeginRename);
        Add(menu, "묶음 풀기", () => { SafeClose(); _apps.Ungroup(_item); });
        menu.IsOpen = true;
    }

    private void ShowChildMenu(IconCell cell, PinnedItem child)
    {
        var menu = new ContextMenu { PlacementTarget = cell };
        var ws = _item.WindowsOf(child);
        foreach (var w in ws)
        {
            var title = string.IsNullOrWhiteSpace(w.Title) ? child.DisplayName : w.Title;
            Add(menu, title ?? "", () => { SafeClose(); ForegroundActivator.Activate(w.Handle); });
        }
        if (ws.Count > 0) menu.Items.Add(new Separator());
        Add(menu, ws.Count > 0 ? "새 창" : "열기", () => { SafeClose(); _apps.LaunchChild(_item, child); });
        Add(menu, "관리자 권한으로 실행", () => { SafeClose(); _apps.LaunchChild(_item, child, elevated: true); });
        Add(menu, "묶음에서 빼기", () => TakeOut(child));
        if (ws.Count > 0)
        {
            menu.Items.Add(new Separator());
            Add(menu, ws.Count > 1 ? "모든 창 닫기" : "창 닫기", () =>
            {
                foreach (var w in ws) PostMessage(w.Handle, WM_SYSCOMMAND, SC_CLOSE, 0);
            });
        }
        menu.IsOpen = true;
    }

    private void TakeOut(PinnedItem child)
    {
        _apps.RemoveFromGroup(_item, child);
        // Down to one app the group dissolves into that app; nothing is left to show.
        if (_apps.Items.Contains(_item) && _item.IsGroup && _item.GroupChildren.Count > 1) Build();
        else SafeClose();
    }

    private static void Add(ContextMenu menu, string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }
}
