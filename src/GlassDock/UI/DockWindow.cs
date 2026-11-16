using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Glass;
using GlassDock.Logging;
using GlassDock.Shell;
using GlassDock.Taskbar;
using GlassDock.Widgets;
using static GlassDock.Interop.Native;

namespace GlassDock.UI;

/// <summary>
/// The dock bar: a per-pixel transparent, never-activating, topmost WPF window spanning the monitor
/// width. Transparent pixels are click-through, so only the pill takes input. A separate
/// <see cref="GlassBackdropWindow"/> under the pill supplies the live blur.
/// </summary>
public sealed class DockWindow : Window
{
    private readonly AppHost _host;
    private readonly Grid _root = new();
    private readonly Grid _pill = new();
    // Lens, tint, specular, rim, outline: the first children of the pill, kept across rebuilds.
    private const int ChromeLayers = 5;
    private readonly LiquidLensLayer _lens = new();
    private BackdropOcclusionMonitor? _occlusion;
    private readonly Border _tint = new();
    private readonly Border _specular = new();
    private readonly Border _rim = new();
    private readonly Border _outline = new();
    private readonly StackPanel _apps = new() { Orientation = Orientation.Horizontal };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _label = new();
    private readonly TextBlock _labelText = new();
    private readonly Border _dropMarker = new() { Width = 3, Visibility = Visibility.Collapsed };
    private readonly Dictionary<AppItem, AppButton> _buttons = [];
    private readonly List<TileButton> _tiles = [];
    private readonly List<WidgetTile> _widgetTiles = [];
    // Tiles and dividers before and after the apps, which make room while the apps are magnified.
    private readonly List<FrameworkElement> _beforeApps = [], _afterApps = [];
    // Which TileSpec each element of the row was built from (tiles, dividers, the apps panel), for drag reordering.
    private readonly Dictionary<FrameworkElement, string> _specOf = [];

    private GlassBackdropWindow? _glass;
    private GlassBackdropWindow? _band;
    private MaximizedWindowMonitor? _maximized;
    private AppBarRegistration? _appBar;
    private nint _hwnd;
    private DockMetrics _m;
    private GlassLook _look;
    private DisplayInfo _display;
    private bool _hiddenForFullscreen;
    private nint _keyboardReturnTo;
    private bool _keyboardMode;

    public TileButton? TrayTile { get; private set; }
    public nint Handle => _hwnd;
    public DockMetrics Metrics => _m;
    public bool IsBottom => _m.Bottom;
    public GlassLook Look => _look;

    public DockWindow(AppHost host)
    {
        _host = host;
        _m = DockMetrics.From(host.Settings.Current);
        _look = GlassLook.Resolve(host.Settings.Current.Appearance);
        _display = DisplayInfo.Primary();

        Title = "GlassDock";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        AllowDrop = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SizeToContent = SizeToContent.Manual;

        _root.Children.Add(_pill);
        _root.Children.Add(_overlay);
        _overlay.Children.Add(_label);
        _overlay.Children.Add(_dropMarker);
        _label.Child = _labelText;
        _label.Visibility = Visibility.Collapsed;
        Content = _root;

        _pill.Children.Add(_lens.Element);
        _pill.Children.Add(_tint);
        _pill.Children.Add(_specular);
        _pill.Children.Add(_rim);
        _pill.Children.Add(_outline);
        _pill.SizeChanged += (_, _) => UpdateGlassBounds();
        _lens.FadeCompleted += UpdateGlassBounds;
        _pill.MouseRightButtonUp += OnPillRightClick;

        host.Apps.Items.CollectionChanged += OnAppsChanged;

        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseUp;
        // Alt+Tab, a flyout or a lock screen can steal the capture mid-drag; put everything back.
        _root.LostMouseCapture += (_, _) => { CancelDrag(); EndMove(); CancelTileDrag(); };
        DragOver += OnDragOver;
        DragLeave += (_, _) => EndExternalDrag();
        Drop += OnDrop;
        KeyDown += OnKeyDown;
        Deactivated += (_, _) => LeaveKeyboardMode(restoreFocus: false);
        _pill.MouseEnter += (_, _) => StartMagnify();
        _root.RenderTransform = _slide;
        _hideCheck.Tick += (_, _) => OnHideCheck();
        _areaCheck.Tick += (_, _) => OnAreaCheck();
    }

    /// <summary>Creates the HWNDs (glass first, so it can own the dock window) and shows the dock.</summary>
    public void Initialize()
    {
        _glass = new GlassBackdropWindow();
        _occlusion = new BackdropOcclusionMonitor(() =>
            _hwnd != 0 && !Hidden && _pill.ActualWidth > 0 && PresentationSource.FromVisual(_pill) != null ? PillScreenRect : null);
        _occlusion.Changed += UpdateGlassBounds;
        _maximized = new MaximizedWindowMonitor(() => _display.Handle);
        _maximized.Changed += UpdateGlassBounds;
        var helper = new WindowInteropHelper(this) { Owner = _glass.Handle };
        _hwnd = helper.EnsureHandle();
        var src = HwndSource.FromHwnd(_hwnd);
        src.AddHook(WndProc);
        int ex = (int)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        int on = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_EXCLUDED_FROM_PEEK, ref on, sizeof(int));

        _appBar = new AppBarRegistration(_hwnd);
        _appBar.PositionChanged += () => Dispatcher.BeginInvoke(PlaceWindow);
        _appBar.FullscreenChanged += full => _host.Fullscreen.ReportAppBarFullscreen(full);

        Rebuild();
        Show();
        PlaceWindow();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_appBar != null && _appBar.HandleMessage(msg, wParam, lParam)) { handled = true; return 0; }
        if (msg == WM_MOUSEACTIVATE && !_keyboardMode)
        {
            // Clicking the dock must not steal focus: "click an already-active app to minimize" relies on it.
            handled = true;
            return MA_NOACTIVATE;
        }
        return 0;
    }

    // ---- building ---------------------------------------------------------------------------------

    /// <summary>Rebuilds every visual from current settings (style, size, tiles).</summary>
    public void Rebuild()
    {
        var s = _host.Settings.Current;
        _m = DockMetrics.From(s);
        _look = GlassLook.Resolve(s.Appearance);
        _host.Apps.IconPixelSize = IconProvider.SnapSize(_m.IconSize * _display.Scale * (s.Appearance.Magnification.Enabled ? s.Appearance.Magnification.MaxScale : 1));

        ApplyChrome();

        // Remove old tile content (everything after the chrome layers).
        while (_pill.Children.Count > ChromeLayers) _pill.Children.RemoveAt(ChromeLayers);
        DisposeWidgets();
        _tiles.Clear();
        _buttons.Clear();
        _beforeApps.Clear();
        _afterApps.Clear();
        _specOf.Clear();
        // The apps panel outlives rebuilds; drop any offset a tile drag left on it.
        _apps.RenderTransform = Transform.Identity;
        _magScale = [];
        SetChromeExtension(0);
        _apps.Children.Clear();
        // _apps outlives rebuilds; it is still the child of the old row's panel, which WPF refuses to re-parent.
        (_apps.Parent as Panel)?.Children.Remove(_apps);
        TrayTile = null;

        var orientation = _m.Vertical ? Orientation.Vertical : Orientation.Horizontal;
        StackPanel Group() => new() { Orientation = orientation, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _apps.Orientation = orientation;
        var left = Group();
        var right = Group();
        var current = left;
        bool appsSeen = false;
        (StackPanel group, TileButton tile)? clock = null;
        foreach (var tile in s.Tiles.Where(t => t.Enabled))
        {
            if (tile.Type == "spacer")
            {
                if (_m.Bar) current = right;
                else
                {
                    var divider = new Divider(_m, _look);
                    current.Children.Add(divider);
                    _specOf[divider] = tile.Id;
                    (appsSeen ? _afterApps : _beforeApps).Add(divider);
                }
                continue;
            }
            if (tile.Type == "apps")
            {
                _apps.VerticalAlignment = VerticalAlignment.Center;
                _apps.HorizontalAlignment = HorizontalAlignment.Center;
                current.Children.Add(_apps);
                _specOf[_apps] = tile.Id;
                SyncAppButtons();
                appsSeen = true;
                continue;
            }
            var t = CreateTile(tile, s);
            if (t == null) { Log.Warn($"Unknown tile type '{tile.Type}'"); continue; }
            Hook(t);
            current.Children.Add(t);
            _specOf[t] = tile.Id;
            (appsSeen ? _afterApps : _beforeApps).Add(t);
            if (tile.Type == "clock") clock = (current, t);
        }
        if (_host.PendingUpdate is { } update)
        {
            // Not a tile spec (it comes and goes with the release), so it is left out of _specOf: tile drags skip it.
            var u = new UpdateTile(_m, _look, update.Version);
            Hook(u);
            if (clock is var (group, clockTile)) group.Children.Insert(group.Children.IndexOf(clockTile), u);
            else current.Children.Add(u);
            (appsSeen ? _afterApps : _beforeApps).Add(u);
        }

        UIElement row;
        if (_m.Bar)
        {
            var dp = new DockPanel { LastChildFill = false, Margin = _m.Along(_m.Padding, _m.Padding) };
            DockPanel.SetDock(left, _m.Vertical ? Dock.Top : Dock.Left);
            DockPanel.SetDock(right, _m.Vertical ? Dock.Bottom : Dock.Right);
            dp.Children.Add(right);
            dp.Children.Add(left);
            row = dp;
        }
        else
        {
            var sp = Group();
            sp.Margin = _m.Along(_m.Padding, _m.Padding);
            sp.Children.Add(left);
            if (right.Children.Count > 0) sp.Children.Add(right);
            row = sp;
        }
        _pill.Children.Add(row);

        _labelText.Foreground = new SolidColorBrush(_look.IsDark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
        _labelText.FontSize = 12;
        _labelText.Margin = new Thickness(10, 4, 10, 5);
        _labelText.TextTrimming = TextTrimming.CharacterEllipsis;
        // Beside a vertical dock the label must fit in the window's side headroom.
        _label.MaxWidth = _m.Vertical ? _m.Headroom - 2 * AppConstants.Dock.LabelGapDip : double.PositiveInfinity;
        _label.CornerRadius = new CornerRadius(8);
        _label.Background = new SolidColorBrush(_look.IsDark ? Color.FromArgb(0xE6, 0x24, 0x24, 0x28) : Color.FromArgb(0xEE, 0xFA, 0xFA, 0xFA));
        _label.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        _label.BorderThickness = new Thickness(1);
        _dropMarker.Background = new SolidColorBrush(_look.Accent);
        _dropMarker.Width = _m.Vertical ? _m.IconSize : 3;
        _dropMarker.Height = _m.Vertical ? 3 : _m.IconSize;

        if (_hwnd != 0) PlaceWindow();
    }

    private TileButton? CreateTile(TileSpec spec, DockSettings s) => spec.Type switch
    {
        "start" => new StartTile(_m, _look),
        "tray" => TrayTile = CreateTrayTile(),
        "quickSettings" => new GlyphTile(_m, _look, "빠른 설정", 0.4, Glyphs.Wifi, Glyphs.Volume) { Tag = "quickSettings" },
        "clock" => new ClockTile(_m, _look, s.Clock) { Tag = "clock" },
        "notifications" => new GlyphTile(_m, _look, "알림", 0.42, Glyphs.Bell) { Tag = "notifications" },
        "showDesktop" => new GlyphTile(_m, _look, "바탕 화면 보기", 0.42, Glyphs.Desktop) { Tag = "showDesktop" },
        _ => CreateWidgetTile(spec),
    };

    private GlyphTile CreateTrayTile()
    {
        // The chevron points into the screen, where the tray flyout opens.
        string chevron = _m.Edge switch { DockEdge.Top => Glyphs.ChevronDown, DockEdge.Left => Glyphs.ChevronRight, DockEdge.Right => Glyphs.ChevronLeft, _ => Glyphs.ChevronUp };
        var tile = new GlyphTile(_m, _look, "숨겨진 아이콘 표시", 0.36, chevron) { Tag = "tray" };
        tile.SetLength(_m.TileSize * 0.7);
        return tile;
    }

    private WidgetTile? CreateWidgetTile(TileSpec spec)
    {
        if (WidgetRegistry.Find(spec.Type) is not { } d) return null;
        var tile = new WidgetTile(_m, _look, d, d.Create(), new WidgetContext(_m, _look, _host.Widgets, _host.Icons, _display.Scale), spec.Id);
        _widgetTiles.Add(tile);
        return tile;
    }

    private void DisposeWidgets()
    {
        foreach (var w in _widgetTiles) w.Dispose();
        _widgetTiles.Clear();
    }

    private void Hook(TileButton t)
    {
        t.Margin = _m.Along(_m.ItemSpacing / 2, _m.ItemSpacing / 2);
        t.HoverChanged += OnTileHover;
        _tiles.Add(t);
        switch (t)
        {
            case StartTile:
                t.Clicked += _ => ShellCommands.OpenStart();
                t.RightClicked += _ => ShellCommands.OpenWinXMenu();
                break;
            case WidgetTile wt:
                t.RightClicked += _ => _host.ShowWidgetMenu(wt, this);
                if (AppHost.HasFlyout(wt.Widget)) t.Clicked += _ => _host.ToggleWidgetFlyout(this, wt);
                break;
            case UpdateTile:
                t.Clicked += _ => _host.ShowUpdateFlyout(this);
                t.RightClicked += _ => _host.ShowUpdateMenu(t, this);
                break;
            case AppButton ab:
                t.Clicked += _ => _host.OnAppClicked(ab.Item, this, ab);
                t.RightClicked += _ => _host.ShowAppMenu(ab.Item, ab, this);
                t.MiddleClicked += _ => _host.Apps.Launch(ab.Item);
                break;
            default:
                t.RightClicked += _ => { if (_specOf.TryGetValue(t, out var id)) _host.ShowTileMenu(id, t, this); };
                switch (t.Tag as string)
                {
                    case "tray": t.Clicked += _ => _host.ToggleTrayFlyout(this); break;
                    case "quickSettings": t.Clicked += _ => ShellCommands.OpenQuickSettings(); break;
                    case "clock": t.Clicked += _ => ShellCommands.OpenNotificationCenter(); break;
                    case "notifications": t.Clicked += _ => ShellCommands.OpenNotificationCenter(); break;
                    case "showDesktop": t.Clicked += _ => ShellCommands.ShowDesktop(); break;
                }
                break;
        }
    }

    private void ApplyChrome()
    {
        var r = new CornerRadius(_m.CornerRadius);
        // Fixed thickness across the dock; along it the pill sizes to its tiles (or stretches as a bar).
        if (_m.Vertical) { _pill.Width = _m.PillHeight; _pill.Height = double.NaN; }
        else { _pill.Height = _m.PillHeight; _pill.Width = double.NaN; }
        var along = _m.Bar ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        _pill.HorizontalAlignment = _m.Edge switch { DockEdge.Left => HorizontalAlignment.Left, DockEdge.Right => HorizontalAlignment.Right, _ => along };
        _pill.VerticalAlignment = _m.Edge switch
        {
            DockEdge.Bottom => VerticalAlignment.Bottom,
            DockEdge.Top => VerticalAlignment.Top,
            _ => _m.Bar ? VerticalAlignment.Stretch : VerticalAlignment.Center,
        };
        double g = _m.EdgeGap;
        _pill.Margin = _m.Edge switch
        {
            DockEdge.Top => new Thickness(0, g, 0, 0),
            DockEdge.Left => new Thickness(g, 0, 0, 0),
            DockEdge.Right => new Thickness(0, 0, g, 0),
            _ => new Thickness(0, 0, 0, g),
        };

        _tint.CornerRadius = r;
        _tint.Background = new SolidColorBrush(_look.Tint);

        // Specular sheen: light falls from the top edge and fades quickly, the core of the glass look.
        _specular.CornerRadius = r;
        _specular.IsHitTestVisible = false;
        // The sheen runs across the pill's short side, so a vertical pill reads as the same glass turned on its side.
        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = _m.Vertical ? new Point(1, 0) : new Point(0, 1) };
        sheen.GradientStops.Add(new GradientStop(_look.Specular, 0));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, _look.Specular.R, _look.Specular.G, _look.Specular.B), 0.45));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.85));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(_look.Specular.A / 3), 255, 255, 255), 1));
        _specular.Background = sheen;

        // Inner rim: bright on the top-left, fading to the bottom-right, like light on a glass bevel.
        _rim.CornerRadius = r;
        _rim.IsHitTestVisible = false;
        _rim.BorderThickness = new Thickness(1.2);
        _rim.Margin = new Thickness(RimInset);
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = _m.Vertical ? new Point(1, 0.35) : new Point(0.35, 1) };
        rim.GradientStops.Add(new GradientStop(_look.RimLight, 0));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(_look.RimLight.A / 4), 255, 255, 255), 0.55));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(_look.RimLight.A / 2), 255, 255, 255), 1));
        _rim.BorderBrush = rim;

        _outline.CornerRadius = r;
        _outline.IsHitTestVisible = false;
        _outline.BorderThickness = new Thickness(0.8);
        _outline.BorderBrush = new SolidColorBrush(_look.RimDark);

        if (_occlusion != null) _occlusion.Enabled = LensWanted;
    }

    private bool LensWanted => _look.UseLens && _lens.IsSupported;

    /// <summary>The wallpaper or the display layout changed: render the lens texture again.</summary>
    public void RefreshWallpaper()
    {
        _lens.Invalidate();
        PlaceWindow();
    }

    // ---- apps ---------------------------------------------------------------------------------------

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncAppButtons();

    private void SyncAppButtons()
    {
        var items = _host.Apps.Items;
        foreach (var gone in _buttons.Keys.Where(k => !items.Contains(k)).ToList())
        {
            var b = _buttons[gone];
            _apps.Children.Remove(b);
            _tiles.Remove(b);
            _buttons.Remove(gone);
        }
        for (int i = 0; i < items.Count; i++)
        {
            if (!_buttons.TryGetValue(items[i], out var b))
            {
                b = new AppButton(items[i], _m, _look, _host.Settings.Current.Apps);
                Hook(b);
                _buttons[items[i]] = b;
                AnimateIn(b);
            }
            int cur = _apps.Children.IndexOf(b);
            if (cur == i) continue;
            if (cur >= 0) _apps.Children.RemoveAt(cur);
            _apps.Children.Insert(i, b);
        }
        // Keep tile order (for Win+T navigation) equal to visual order.
        _tiles.Sort((a, b) => VisualIndex(a).CompareTo(VisualIndex(b)));
    }

    private int VisualIndex(TileButton t)
    {
        try { return (int)_m.Main(t.TransformToAncestor(_pill).Transform(new Point(0, 0))); }
        catch (InvalidOperationException) { return t is AppButton ab ? 1000 + _apps.Children.IndexOf(ab) : 0; }
    }

    private void AnimateIn(FrameworkElement e)
    {
        if (!_host.Settings.Current.Appearance.Animations) return;
        e.Opacity = 0;
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
    }

    /// <summary>The app buttons in dock order; Win+1..9 index into this list.</summary>
    public IReadOnlyList<AppButton> AppButtons => _apps.Children.OfType<AppButton>().ToList();

    public AppButton? ButtonFor(AppItem item) => _buttons.GetValueOrDefault(item);

    // ---- placement ----------------------------------------------------------------------------------

    public void PlaceWindow()
    {
        if (_hwnd == 0) return;
        _display = DisplayInfo.Primary();
        var mon = _display.Bounds;
        double scale = _display.Scale;
        var s = _host.Settings.Current;
        // The window spans the whole edge; only the pill inside it is opaque and takes clicks.
        int t = (int)Math.Ceiling(_m.WindowThickness * scale);
        var rect = _m.Edge switch
        {
            DockEdge.Top => new RECT(mon.Left, mon.Top, mon.Right, mon.Top + t),
            DockEdge.Left => new RECT(mon.Left, mon.Top, mon.Left + t, mon.Bottom),
            DockEdge.Right => new RECT(mon.Right - t, mon.Top, mon.Right, mon.Bottom),
            _ => new RECT(mon.Left, mon.Bottom - t, mon.Right, mon.Bottom),
        };
        double maxLength = (_m.Vertical ? mon.Height : mon.Width) / scale * s.Dock.MaxWidthPercent / 100.0;
        _pill.MaxWidth = _m.Bar || _m.Vertical ? double.PositiveInfinity : maxLength;
        _pill.MaxHeight = _m.Bar || !_m.Vertical ? double.PositiveInfinity : maxLength;

        // An auto-hidden dock overlays windows instead of reserving space (design §2.6).
        if (s.Dock.ReserveSpace && !s.AutoHide.Enabled) _appBar!.Apply(mon, _m.AppBarEdge, (int)Math.Ceiling(_m.ReserveThickness * scale));
        else _appBar!.Remove();

        SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE | (Hidden ? 0 : SWP_SHOWWINDOW));
        if (LensWanted) _lens.EnsureBand(mon, rect, scale);
        ApplyAutoHide(mon);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateGlassBounds);
    }

    public void UpdateGlassBounds()
    {
        if (_glass == null || _hwnd == 0 || PresentationSource.FromVisual(_pill) == null) return;
        bool hasPill = _pill.ActualWidth > 0;
        if (hasPill) UpdateGlassShape();
        // Liquid glass only knows the wallpaper; with a window behind the dock the live frosted glass takes over.
        // The lens is drawn in the dock window, so it may stay on while an auto-hiding dock slides away.
        // Over the band the lens would show sharp wallpaper through it like a hole, so the pill turns frosted too.
        bool lens = LensWanted && hasPill && _lens.HasWallpaper && !_hiddenForFullscreen && !(_occlusion?.IsOccluded ?? false)
            && !(_maximized?.AnyMaximized ?? false);
        _lens.Show(lens, _host.Settings.Current.Appearance.Animations);
        _occlusion?.Kick();
        // The frosted window stays under the lens until the lens is fully opaque, so the cross-fade never shows a hole.
        bool useGlass = _look.UseBackdrop && !Hidden && !(lens && _lens.IsOpaque);
        _glass.SetVisible(useGlass);
        // The glass owns the dock window, so re-asserting topmost on the dock keeps both above other windows.
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        UpdateBand();
    }

    /// <summary>
    /// A maximized window stops at the reserved strip, so the wallpaper shows around the pill. While one is
    /// up, the strip gets a full-width band in the dock's glass, like a taskbar under a floating dock.
    /// </summary>
    private void UpdateBand()
    {
        if (_maximized == null || _hwnd == 0) return;
        var s = _host.Settings.Current;
        _maximized.Enabled = s.Dock.FillMaximizedBand && s.Dock.ReserveSpace && !s.AutoHide.Enabled;
        bool show = _maximized.AnyMaximized && !Hidden;
        if (!show) { _band?.SetVisible(false); return; }
        _band ??= new GlassBackdropWindow();
        // The pill's light tint over a whole edge just reads as blurred wallpaper; a denser tint reads as a bar.
        var tint = _look.Tint;
        _band.SetTint(Color.FromArgb(Math.Max(tint.A, AppConstants.Glass.BandMinTintAlpha), tint.R, tint.G, tint.B), _look.UseBackdrop);
        var mon = _display.Bounds;
        int t = (int)Math.Ceiling(_m.ReserveThickness * _display.Scale);
        _band.SetShape(_m.Edge switch
        {
            DockEdge.Top => new RECT(mon.Left, mon.Top, mon.Right, mon.Top + t),
            DockEdge.Left => new RECT(mon.Left, mon.Top, mon.Left + t, mon.Bottom),
            DockEdge.Right => new RECT(mon.Right - t, mon.Top, mon.Right, mon.Bottom),
            _ => new RECT(mon.Left, mon.Bottom - t, mon.Right, mon.Bottom),
        }, 0);
        _band.SetVisible(true);
        // Every topmost re-assert puts its window on top: band first, then the pill's glass (which lifts the
        // dock window it owns), then the dock, so the band always ends up beneath both.
        SetWindowPos(_glass!.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Moves the backdrop and the lens to the pill's current screen rectangle (cheap; runs per frame while magnifying).</summary>
    private void UpdateGlassShape()
    {
        if (_glass == null || _pill.ActualWidth <= 0 || PresentationSource.FromVisual(_pill) == null) return;
        var pill = PillScreenRect;
        float radius = (float)(_m.CornerRadius * _display.Scale);
        if (_look.UseBackdrop) _glass.SetShape(pill, radius);
        if (LensWanted) _lens.SetPill(pill, radius, _display.Scale);
    }

    private bool Hidden => _hiddenForFullscreen || _autoHidden;

    public void SetFullscreenHidden(bool hidden)
    {
        if (_hiddenForFullscreen == hidden) return;
        _hiddenForFullscreen = hidden;
        Log.Info(hidden ? "Fullscreen app detected; hiding dock" : "Fullscreen ended; showing dock");
        ShowWindow(_hwnd, Hidden ? SW_HIDE : SW_SHOWNA);
        if (_sensor != null) UpdateSensor(_display.Bounds);
        UpdateGlassBounds();
    }

    /// <summary>Screen rect (physical px) of an element, for flyout placement and thumbnail anchors.</summary>
    public RECT ScreenRectOf(FrameworkElement e)
    {
        var tl = e.PointToScreen(new Point(0, 0));
        var br = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new RECT((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
    }

    /// <summary>The pill on screen (physical px), including the room it grows by while icons are magnified.</summary>
    public RECT PillScreenRect
    {
        get
        {
            var r = ScreenRectOf(_pill);
            int ext = (int)Math.Round(_chromeExtension * _display.Scale);
            return _m.Vertical ? new RECT(r.Left, r.Top - ext, r.Right, r.Bottom + ext) : new RECT(r.Left - ext, r.Top, r.Right + ext, r.Bottom);
        }
    }

    /// <summary>The pill in <see cref="_root"/> coordinates (DIP).</summary>
    private Rect PillRect => new(_pill.TranslatePoint(new Point(0, 0), _root), _pill.RenderSize);

    // ---- hover labels -------------------------------------------------------------------------------

    private void OnTileHover(TileButton t, bool on)
    {
        _hoverTile = on ? t : null;
        if (!on || !_host.Settings.Current.Dock.ShowLabelsOnHover || string.IsNullOrEmpty(t.Label) || _dragging != null || _tileDrag != null || _host.IsFlyoutOpen)
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }
        _labelText.Text = t.Label;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        PlaceLabel(t);
    }

    private TileButton? _hoverTile;

    /// <summary>Puts the label just outside the tile as drawn (a magnified tile pokes out of the pill).</summary>
    private void PlaceLabel(TileButton t)
    {
        if (PresentationSource.FromVisual(t) == null) return;
        var size = _label.DesiredSize;
        var pill = PillRect;
        var b = t.TransformToAncestor(_root).TransformBounds(new Rect(t.RenderSize));
        double gap = AppConstants.Dock.LabelGapDip;
        double cx = (b.Left + b.Right) / 2, cy = (b.Top + b.Bottom) / 2;
        double x, y;
        if (_m.Vertical)
        {
            x = _m.Edge == DockEdge.Left ? Math.Max(pill.Right, b.Right) + gap : Math.Min(pill.Left, b.Left) - size.Width - gap;
            y = Math.Clamp(cy - size.Height / 2, 4, Math.Max(4, _root.ActualHeight - size.Height - 4));
        }
        else
        {
            x = Math.Clamp(cx - size.Width / 2, 4, Math.Max(4, _root.ActualWidth - size.Width - 4));
            y = _m.Bottom ? Math.Min(pill.Top, b.Top) - size.Height - gap : Math.Max(pill.Bottom, b.Bottom) + gap;
        }
        Canvas.SetLeft(_label, x);
        Canvas.SetTop(_label, y);
        _label.Visibility = Visibility.Visible;
    }

    public void HideLabel() => _label.Visibility = Visibility.Collapsed;

    // ---- context menu on empty dock area ------------------------------------------------------------

    private void OnPillRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        _host.ShowDockMenu(this);
    }

    /// <summary>Lets popups (menus, flyouts) take focus; the user just clicked our window, so this is allowed.</summary>
    public void TakeForeground() => SetForegroundWindow(_hwnd);

    // ---- internal drag (reorder / unpin) -------------------------------------------------------------

    private AppButton? _pressedButton;
    private Point _pressPoint;
    private AppButton? _dragging;
    private int _dragOrigin, _dragTarget;
    private double _dragSlot, _dragDx;

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedButton = FindAncestor<AppButton>(e.OriginalSource as DependencyObject);
        _pressPoint = e.GetPosition(_root);
        _pressedTile = _pressedButton == null && FindAncestor<TileButton>(e.OriginalSource as DependencyObject) is { } tb && _specOf.ContainsKey(tb) ? tb : null;
        // Empty glass (no tile under the pointer) drags the whole dock to another edge.
        _movePressed = !_host.Settings.Current.Dock.Locked && e.OriginalSource is Visual v && v.IsDescendantOf(_pill)
                       && FindAncestor<TileButton>(v) == null;
        // Capture at once: the first step off the pill lands on transparent pixels, which pass the
        // pointer to the window below, and no further moves would reach us.
        if (_movePressed) Mouse.Capture(_root, CaptureMode.SubTree);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_movePressed) { OnMoveDrag(e); return; }
        if (_pressedTile != null) { OnTileDrag(e); return; }
        if (_pressedButton == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(_root);
        if (_dragging == null)
        {
            if (_host.Settings.Current.Dock.Locked) return;
            if (Math.Abs(pos.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
                Math.Abs(pos.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
            _dragging = _pressedButton;
            _dragging.CancelPress();
            HideLabel();
            _dragOrigin = _dragTarget = _apps.Children.IndexOf(_dragging);
            _dragSlot = Math.Max(1, _m.Vertical ? _dragging.ActualHeight : _dragging.ActualWidth);
            Panel.SetZIndex(_dragging, 1);
            _dragging.AnimateShiftAlong(0, _m.AwaySign * AppConstants.Drag.LiftDip, AppConstants.Drag.ShiftMs);
            Mouse.Capture(_root, CaptureMode.SubTree);
        }
        // The dragged icon follows the pointer 1:1; the panel order only changes on drop, while the
        // neighbours slide aside to show where it will land.
        _dragDx = _m.Main(pos) - _m.Main(_pressPoint);
        _dragging.SetShiftAlong(_dragDx);
        double slots = _dragDx / _dragSlot;
        int target = NextTarget(slots);
        if (target != _dragTarget)
        {
            _dragTarget = target;
            for (int i = 0; i < _apps.Children.Count; i++)
            {
                if (i == _dragOrigin || _apps.Children[i] is not TileButton t) continue;
                double x = _dragOrigin < i && i <= target ? -_dragSlot : target <= i && i < _dragOrigin ? _dragSlot : 0;
                t.AnimateShiftAlong(x, 0, AppConstants.Drag.ShiftMs);
            }
        }
        AimMerge(IsOutside(pos) ? null : MergeCandidate(slots), pos);
        _dragging.Opacity = IsOutside(pos) && _dragging.Item.IsPinned ? AppConstants.Drag.RemoveOpacity : AppConstants.Drag.Opacity;
        e.Handled = true;
    }

    // ---- drag onto an app: group them ------------------------------------------------------------

    private AppButton? _mergeAim, _mergeArmed;
    private DispatcherTimer? _mergeTimer;
    private Point _mergeHoldFrom;

    /// <summary>
    /// Panel index the dragged icon lands on for a pointer offset of <paramref name="slots"/>. Next to an
    /// app it can be grouped with, it only passes once it has cleared that app's merge zone.
    /// </summary>
    private int NextTarget(double slots)
    {
        int t = _dragTarget;
        while (true)
        {
            var (home, left, right) = Neighbours(t);
            if (right < _apps.Children.Count && slots > home + 1 + PassMargin(right)) { t++; continue; }
            if (left >= 0 && slots < home - 1 - PassMargin(left)) { t--; continue; }
            return t;
        }
    }

    /// <summary>
    /// With the dragged icon parked at panel index <paramref name="t"/>: its slot offset from where it
    /// started, and the panel indices of the apps now shown just left and right of it.
    /// </summary>
    private (int Home, int Left, int Right) Neighbours(int t) =>
        (t - _dragOrigin, t > _dragOrigin ? t : t - 1, t >= _dragOrigin ? t + 1 : t);

    /// <summary>Plain reordering swaps at the midpoint (-0.5); a groupable app is passed only beyond its zone.</summary>
    private double PassMargin(int index) => CanMergeWith(index) ? AppConstants.Groups.MergeZoneSlots : -0.5;

    private AppButton? MergeCandidate(double slots)
    {
        var (home, left, right) = Neighbours(_dragTarget);
        double zone = AppConstants.Groups.MergeZoneSlots;
        if (Math.Abs(slots - (home + 1)) < zone && CanMergeWith(right)) return (AppButton)_apps.Children[right];
        if (Math.Abs(slots - (home - 1)) < zone && CanMergeWith(left)) return (AppButton)_apps.Children[left];
        return null;
    }

    private bool CanMergeWith(int index) =>
        _dragging != null && index >= 0 && index < _apps.Children.Count && _apps.Children[index] is AppButton b && b != _dragging
        && AppsModel.CanJoinGroup(_dragging.Item) && AppsModel.CanHostGroup(b.Item);

    /// <summary>
    /// Starts (or cancels) the hold-to-group countdown over <paramref name="candidate"/>. The countdown
    /// runs only while the pointer is nearly still: an icon swept across an app on its way somewhere else
    /// never arms, so a quick drag always reorders and grouping needs a deliberate pause on the centre.
    /// </summary>
    private void AimMerge(AppButton? candidate, Point pos = default)
    {
        if (candidate != null && candidate == _mergeAim)
        {
            if (_mergeArmed != null) return;
            if ((pos - _mergeHoldFrom).Length > AppConstants.Groups.StillDip)
            {
                _mergeHoldFrom = pos;
                _mergeTimer!.Stop();
                _mergeTimer.Start();
            }
            return;
        }
        _mergeAim?.SetDropState(DropState.None);
        _dragging?.SetSinking(false);
        _mergeArmed = null;
        _mergeAim = candidate;
        _mergeTimer?.Stop();
        if (candidate == null) return;
        candidate.SetDropState(DropState.Aimed);
        _mergeHoldFrom = pos;
        if (_mergeTimer == null)
        {
            _mergeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppConstants.Groups.MergeHoverMs) };
            _mergeTimer.Tick += (_, _) =>
            {
                _mergeTimer.Stop();
                _mergeArmed = _mergeAim;
                _mergeArmed?.SetDropState(DropState.Armed);
                if (_mergeArmed != null) _dragging?.SetSinking(true);
            };
        }
        _mergeTimer.Start();
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_movePressed) { OnMoveDrop(e); return; }
        if (_pressedTile != null) { OnTileDrop(e); return; }
        var dragged = _dragging;
        _pressedButton = null;
        if (dragged == null) return;
        _dragging = null;
        Mouse.Capture(null);
        e.Handled = true;

        var pos = e.GetPosition(_root);
        var item = dragged.Item;
        var groupWith = _mergeArmed;
        AimMerge(null);
        if (groupWith != null)
        {
            SettleDrag(dragged, _dragOrigin);
            _host.Apps.MergeIntoGroup(item, groupWith.Item);
            return;
        }
        int pinned = _host.Apps.PinnedCount;
        bool unpin = IsOutside(pos) && item.IsPinned;
        // Where the icon really lands: unpinned apps cannot be reordered among themselves (snap back).
        int final = unpin ? _dragOrigin
            : item.IsPinned ? Math.Min(_dragTarget, pinned - 1)
            : _dragTarget < pinned + 1 ? Math.Min(_dragTarget, pinned)
            : _dragOrigin;
        SettleDrag(dragged, final);

        if (unpin) _host.Apps.Unpin(item);
        else if (final == _dragOrigin) return;
        else if (item.IsPinned) _host.Apps.MovePin(item, final);
        else _host.Apps.Pin(item, final);
    }

    /// <summary>
    /// Moves the dragged button to <paramref name="final"/> in the panel and lets it glide there from
    /// where the pointer left it. Neighbours already sit at their new places, so their offsets reset
    /// without a visible jump.
    /// </summary>
    private void SettleDrag(AppButton dragged, int final)
    {
        foreach (var t in _apps.Children.OfType<TileButton>())
            if (t != dragged) t.SetShiftAlong(0);
        if (final != _dragOrigin)
        {
            _apps.Children.RemoveAt(_dragOrigin);
            _apps.Children.Insert(final, dragged);
        }
        dragged.SetSinking(false);
        dragged.SetShiftAlong(_dragDx - (final - _dragOrigin) * _dragSlot);
        dragged.AnimateShift(0, 0, AppConstants.Drag.ShiftMs);
        dragged.Opacity = 1;
        Panel.SetZIndex(dragged, 0);
    }

    private void CancelDrag()
    {
        if (_dragging == null) return;
        var dragged = _dragging;
        AimMerge(null);
        _dragging = null;
        _pressedButton = null;
        foreach (var t in _apps.Children.OfType<TileButton>()) if (t != dragged) t.AnimateShift(0, 0, AppConstants.Drag.ShiftMs);
        dragged.AnimateShift(0, 0, AppConstants.Drag.ShiftMs);
        dragged.SetSinking(false);
        dragged.Opacity = 1;
        Panel.SetZIndex(dragged, 0);
    }

    private bool IsOutside(Point pos)
    {
        var r = PillRect;
        double dist = _m.Edge switch
        {
            DockEdge.Top => pos.Y - r.Bottom,
            DockEdge.Left => pos.X - r.Right,
            DockEdge.Right => r.Left - pos.X,
            _ => r.Top - pos.Y,
        };
        return dist > AppConstants.Drag.RemoveDistanceDip;
    }

    // ---- dragging the other tiles (start, widgets, tray, clock...) ------------------------------------

    private TileButton? _pressedTile, _tileDrag;
    private List<FrameworkElement> _tileRow = [];
    private double[] _tileCentres = [];
    private int _tileOrigin, _tileTarget;
    private double _tileLength;

    /// <summary>
    /// Like app reordering: the tile follows the pointer and the other elements of its row (tiles, the
    /// apps as one block, dividers) slide aside; the new order is written to the tile list on release.
    /// </summary>
    private void OnTileDrag(MouseEventArgs e)
    {
        var tile = _pressedTile!;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelTileDrag(); return; }
        var pos = e.GetPosition(_root);
        if (_tileDrag == null)
        {
            if (_host.Settings.Current.Dock.Locked) return;
            if (Math.Abs(pos.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
                Math.Abs(pos.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
            if (tile.Parent is not Panel row) return;
            _tileRow = row.Children.OfType<FrameworkElement>().Where(_specOf.ContainsKey).ToList();
            _tileOrigin = _tileTarget = _tileRow.IndexOf(tile);
            if (_tileOrigin < 0) return;
            // Unmoved layout slots (margins included), so the row's own sliding never shifts the swap points.
            _tileCentres = _tileRow.Select(el =>
            {
                var slot = LayoutInformation.GetLayoutSlot(el);
                return _m.Vertical ? slot.Top + slot.Height / 2 : slot.Left + slot.Width / 2;
            }).ToArray();
            var own = LayoutInformation.GetLayoutSlot(tile);
            _tileLength = _m.Vertical ? own.Height : own.Width;
            _tileDrag = tile;
            tile.CancelPress();
            HideLabel();
            Panel.SetZIndex(tile, 1);
            tile.AnimateShiftAlong(0, _m.AwaySign * AppConstants.Drag.LiftDip, AppConstants.Drag.ShiftMs);
            tile.Opacity = AppConstants.Drag.Opacity;
            Mouse.Capture(_root, CaptureMode.SubTree);
        }
        e.Handled = true;
        double dx = _m.Main(pos) - _m.Main(_pressPoint);
        tile.SetShiftAlong(dx);
        double centre = _tileCentres[_tileOrigin] + dx;
        int target = _tileOrigin;
        for (int j = 0; j < _tileRow.Count; j++)
        {
            if (j < _tileOrigin && centre < _tileCentres[j]) target--;
            else if (j > _tileOrigin && centre > _tileCentres[j]) target++;
        }
        if (target == _tileTarget) return;
        _tileTarget = target;
        for (int j = 0; j < _tileRow.Count; j++)
        {
            if (j == _tileOrigin) continue;
            double along = j < _tileOrigin && j >= target ? _tileLength : j > _tileOrigin && j <= target ? -_tileLength : 0;
            SlideAlong(_tileRow[j], along);
        }
    }

    private void OnTileDrop(MouseButtonEventArgs e)
    {
        var dragged = _tileDrag;
        _pressedTile = null;
        if (dragged == null) return; // a plain click: the tile handles it
        _tileDrag = null;
        e.Handled = true;
        Mouse.Capture(null);
        if (_tileTarget == _tileOrigin) { ResetTileRow(dragged); return; }
        var order = _tileRow.Select(el => _specOf[el]).ToList();
        var id = order[_tileOrigin];
        order.RemoveAt(_tileOrigin);
        order.Insert(_tileTarget, id);
        // The rebuild that follows recreates the row in its new order, so the offsets are left as they are.
        Dispatcher.BeginInvoke(() => _host.ReorderTiles(order));
    }

    private void CancelTileDrag()
    {
        _pressedTile = null;
        if (_tileDrag == null) return;
        var dragged = _tileDrag;
        _tileDrag = null;
        ResetTileRow(dragged);
    }

    private void ResetTileRow(TileButton dragged)
    {
        foreach (var el in _tileRow) SlideAlong(el, 0);
        dragged.Opacity = 1;
        Panel.SetZIndex(dragged, 0);
    }

    private void SlideAlong(FrameworkElement el, double along)
    {
        if (el is TileButton t) { t.AnimateShiftAlong(along, 0, AppConstants.Drag.ShiftMs); return; }
        if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen) el.RenderTransform = tt = new TranslateTransform();
        var anim = new DoubleAnimation(along, TimeSpan.FromMilliseconds(AppConstants.Drag.ShiftMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        tt.BeginAnimation(_m.Vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty, anim);
    }

    // ---- moving the dock to another edge (design §9.7) -------------------------------------------

    private bool _movePressed, _moving;
    private DockEdge? _moveTarget;
    private DockGhostWindow? _ghost;

    private void OnMoveDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { EndMove(); return; }
        if (!_moving)
        {
            if ((e.GetPosition(_root) - _pressPoint).Length < AppConstants.DockMove.StartDistanceDip) return;
            _moving = true;
            HideLabel();
            Mouse.OverrideCursor = Cursors.SizeAll;
        }
        e.Handled = true;
        GetCursorPos(out var pt);
        var edge = NearestEdge(pt);
        _moveTarget = edge == _m.Edge ? null : edge;
        if (_moveTarget is { } target)
        {
            _ghost ??= new DockGhostWindow(_look);
            _ghost.ShowAt(GhostRect(target), _m.CornerRadius);
        }
        else _ghost?.Hide();
    }

    private void OnMoveDrop(MouseButtonEventArgs e)
    {
        bool moved = _moving;
        var target = _moveTarget;
        EndMove();
        if (!moved) return; // a plain click on the glass
        e.Handled = true;
        // Rebuild after this input event has finished; the drop handler is still walking the old tiles.
        if (target is { } edge) Dispatcher.BeginInvoke(() => _host.MoveDock(edge));
    }

    private void EndMove()
    {
        if (!_movePressed) return;
        _movePressed = false;
        _moving = false;
        _moveTarget = null;
        _ghost?.Hide();
        Mouse.OverrideCursor = null;
        if (Mouse.Captured == _root) Mouse.Capture(null);
    }

    /// <summary>
    /// The edge the pointer is closest to, relative to the screen's size: the screen's diagonals split it
    /// into four triangles, one per edge.
    /// </summary>
    private DockEdge NearestEdge(POINT pt)
    {
        var mon = _display.Bounds;
        double fx = Math.Clamp((pt.X - mon.Left) / (double)Math.Max(1, mon.Width), 0, 1);
        double fy = Math.Clamp((pt.Y - mon.Top) / (double)Math.Max(1, mon.Height), 0, 1);
        return new[] { (DockEdge.Left, fx), (DockEdge.Right, 1 - fx), (DockEdge.Top, fy), (DockEdge.Bottom, 1 - fy) }.MinBy(d => d.Item2).Item1;
    }

    /// <summary>Where the pill would sit on <paramref name="edge"/> (physical px), at about its current length.</summary>
    private RECT GhostRect(DockEdge edge)
    {
        var mon = _display.Bounds;
        double scale = _display.Scale;
        var pill = PillScreenRect;
        int thick = (int)Math.Round(_m.PillHeight * scale);
        int gap = (int)Math.Round(_m.EdgeGap * scale);
        bool vertical = edge is DockEdge.Left or DockEdge.Right;
        int span = vertical ? mon.Height : mon.Width;
        int length = _m.Bar ? span : Math.Min(Math.Max(pill.Width, pill.Height), (int)(span * _host.Settings.Current.Dock.MaxWidthPercent / 100.0));
        int start = (vertical ? mon.Top : mon.Left) + (span - length) / 2;
        return edge switch
        {
            DockEdge.Top => new RECT(start, mon.Top + gap, start + length, mon.Top + gap + thick),
            DockEdge.Left => new RECT(mon.Left + gap, start, mon.Left + gap + thick, start + length),
            DockEdge.Right => new RECT(mon.Right - gap - thick, start, mon.Right - gap, start + length),
            _ => new RECT(start, mon.Bottom - gap - thick, start + length, mon.Bottom - gap),
        };
    }


    // ---- magnification (design §9.4) ----------------------------------------------------------------

    private bool _magnifying;
    private double[] _magScale = [];
    private double[] _magExtra = [];
    private double _chromeExtension;

    private void StartMagnify()
    {
        if (_magnifying || !_host.Settings.Current.Appearance.Magnification.Enabled) return;
        _magnifying = true;
        CompositionTarget.Rendering += OnMagnifyFrame;
    }

    /// <summary>
    /// Per frame while the pointer is over the dock or scales are still settling. Scales come from each
    /// icon's unmagnified slot, so moving icons never feed back into the pointer distance; the room they
    /// need is made with render offsets (neighbours, the tiles beside the apps and the glass all spread
    /// symmetrically), so the dock's layout is never redone.
    /// </summary>
    private void OnMagnifyFrame(object? sender, EventArgs e)
    {
        var mag = _host.Settings.Current.Appearance.Magnification;
        var kids = _apps.Children;
        int n = kids.Count;
        if (_magScale.Length != n)
        {
            _magScale = Enumerable.Repeat(1.0, n).ToArray();
            _magExtra = new double[n];
        }
        bool active = mag.Enabled && _pill.IsMouseOver && _dragging == null && _tileDrag == null && !_moving && !_host.IsFlyoutOpen && !Hidden;
        double pointer = _m.Main(Mouse.GetPosition(_apps));
        double range = Math.Max(1, mag.RangeItems * (_m.TileSize + _m.ItemSpacing));
        double total = 0;
        bool settled = true;
        for (int i = 0; i < n; i++)
        {
            var slot = LayoutInformation.GetLayoutSlot((FrameworkElement)kids[i]);
            double centre = _m.Vertical ? slot.Top + slot.Height / 2 : slot.Left + slot.Width / 2;
            double target = active ? DockMetrics.MagnifyScale(centre - pointer, range, mag.MaxScale) : 1;
            double cur = _magScale[i] + (target - _magScale[i]) * AppConstants.Magnify.EasePerFrame;
            if (Math.Abs(target - cur) < AppConstants.Magnify.SettleEpsilon) cur = target;
            else settled = false;
            _magScale[i] = cur;
            _magExtra[i] = (cur - 1) * _m.TileSize;
            total += _magExtra[i];
        }
        double before = 0;
        for (int i = 0; i < n; i++)
        {
            double after = total - before - _magExtra[i];
            if (kids[i] is TileButton t) t.SetMagnification(_magScale[i], (before - after) / 2);
            before += _magExtra[i];
        }
        foreach (var el in _beforeApps) ShiftAside(el, -total / 2);
        foreach (var el in _afterApps) ShiftAside(el, total / 2);
        if (!_m.Bar) SetChromeExtension(total / 2);
        UpdateGlassShape();
        if (_hoverTile != null && _label.Visibility == Visibility.Visible) PlaceLabel(_hoverTile);
        if (!active && settled)
        {
            CompositionTarget.Rendering -= OnMagnifyFrame;
            _magnifying = false;
        }
    }

    private void ShiftAside(FrameworkElement el, double along)
    {
        if (el is TileButton t) { t.SetMagnification(1, along); return; }
        if (el.RenderTransform is not TranslateTransform tt) el.RenderTransform = tt = new TranslateTransform();
        if (_m.Vertical) tt.Y = along; else tt.X = along;
    }

    /// <summary>Grows the glass layers past the pill's ends by <paramref name="along"/> DIP on each side.</summary>
    private void SetChromeExtension(double along)
    {
        _chromeExtension = along;
        foreach (var layer in new FrameworkElement[] { _lens.Element, _tint, _specular, _rim, _outline })
        {
            double b = layer == _rim ? RimInset : 0;
            layer.Margin = _m.Vertical ? new Thickness(b, b - along, b, b - along) : new Thickness(b - along, b, b - along, b);
        }
    }

    private const double RimInset = 0.6;

    // ---- auto-hide (design §9.5) -------------------------------------------------------------------

    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _hideCheck = new() { Interval = TimeSpan.FromMilliseconds(AppConstants.AutoHide.CheckMs) };
    // Polled rather than a sensor window over the dock's spot: a window there would take clicks meant for the app below.
    private readonly DispatcherTimer _areaCheck = new() { Interval = TimeSpan.FromMilliseconds(AppConstants.AutoHide.AreaCheckMs) };
    private DateTime? _inAreaSince;
    private DispatcherTimer? _revealTimer;
    private EdgeSensorWindow? _sensor;
    private bool _autoHidden, _winHeld;
    private DateTime _keepUntil;

    private AutoHideSettings AutoHideConfig => _host.Settings.Current.AutoHide;

    private void ApplyAutoHide(RECT mon)
    {
        if (!AutoHideConfig.Enabled)
        {
            _hideCheck.Stop();
            _areaCheck.Stop();
            _sensor?.Hide();
            if (_autoHidden)
            {
                _autoHidden = false;
                if (!_hiddenForFullscreen) ShowWindow(_hwnd, SW_SHOWNA);
            }
            SetSlide(false);
            return;
        }
        if (_sensor == null)
        {
            _sensor = new EdgeSensorWindow();
            _sensor.PointerChanged += OnSensorPointer;
            _sensor.DragArrived += () => Reveal(AutoHideConfig.HideDelayMs);
        }
        UpdateSensor(mon);
        // The edge may have changed while hidden: put the offset on the new axis.
        SetSlide(_autoHidden);
        if (!_autoHidden)
        {
            KeepShown(AutoHideConfig.HideDelayMs);
            _hideCheck.Start();
        }
        else StartAreaCheck();
    }

    private void StartAreaCheck()
    {
        _inAreaSince = null;
        if (AutoHideConfig.RevealOnDockArea) _areaCheck.Start();
        else _areaCheck.Stop();
    }

    private void OnAreaCheck()
    {
        if (!_autoHidden || !AutoHideConfig.Enabled || !AutoHideConfig.RevealOnDockArea) { _areaCheck.Stop(); return; }
        if (_hiddenForFullscreen || !PointerInDockArea()) { _inAreaSince = null; return; }
        _inAreaSince ??= DateTime.UtcNow;
        // A pointer only crossing the spot (on its way to a window's status bar, say) should not pull the dock out.
        if (DateTime.UtcNow - _inAreaSince.Value >= TimeSpan.FromMilliseconds(AutoHideConfig.DockAreaRevealDelayMs))
            Reveal(AutoHideConfig.HideDelayMs);
    }

    /// <summary>Whether the pointer is over where the shown pill sits, or between it and the screen edge.</summary>
    private bool PointerInDockArea()
    {
        if (!GetCursorPos(out var pt)) return false;
        RECT r;
        try { r = PillScreenRect; }
        catch (InvalidOperationException ex) { Log.Debug($"Dock area unavailable: {ex.Message}"); return false; }
        // The pill is measured where the slide has put it; undo the slide to get its shown place.
        double scale = _display.Scale;
        int dx = (int)Math.Round(_slide.X * scale), dy = (int)Math.Round(_slide.Y * scale);
        r = new RECT(r.Left - dx, r.Top - dy, r.Right - dx, r.Bottom - dy);
        var b = _display.Bounds;
        var area = _m.Edge switch
        {
            DockEdge.Top => new RECT(r.Left, b.Top, r.Right, r.Bottom),
            DockEdge.Left => new RECT(b.Left, r.Top, r.Right, r.Bottom),
            DockEdge.Right => new RECT(r.Left, r.Top, b.Right, r.Bottom),
            _ => new RECT(r.Left, r.Top, r.Right, b.Bottom),
        };
        return pt.X >= area.Left && pt.X < area.Right && pt.Y >= area.Top && pt.Y < area.Bottom;
    }

    private void UpdateSensor(RECT mon)
    {
        if (_sensor == null) return;
        if (_hiddenForFullscreen || !AutoHideConfig.Enabled) { _sensor.Hide(); return; }
        int t = AppConstants.AutoHide.SensorThicknessPx;
        _sensor.ShowAt(_m.Edge switch
        {
            DockEdge.Top => new RECT(mon.Left, mon.Top, mon.Right, mon.Top + t),
            DockEdge.Left => new RECT(mon.Left, mon.Top, mon.Left + t, mon.Bottom),
            DockEdge.Right => new RECT(mon.Right - t, mon.Top, mon.Right, mon.Bottom),
            _ => new RECT(mon.Left, mon.Bottom - t, mon.Right, mon.Bottom),
        });
    }

    private void OnSensorPointer(bool over)
    {
        _revealTimer?.Stop();
        if (!over || !_autoHidden) return;
        if (_revealTimer == null)
        {
            _revealTimer = new DispatcherTimer();
            _revealTimer.Tick += (_, _) =>
            {
                _revealTimer.Stop();
                // A pointer only passing over the edge (to a scrollbar, say) should not pull the dock out.
                if (_sensor?.IsMouseOver == true) Reveal(AutoHideConfig.HideDelayMs);
            };
        }
        _revealTimer.Interval = TimeSpan.FromMilliseconds(AutoHideConfig.RevealDelayMs);
        _revealTimer.Start();
    }

    /// <summary>Brings an auto-hidden dock out and keeps it up for at least <paramref name="keepMs"/>.</summary>
    public void Reveal(int keepMs)
    {
        if (!AutoHideConfig.Enabled || _hiddenForFullscreen || _hwnd == 0) return;
        KeepShown(keepMs);
        _hideCheck.Start();
        if (!_autoHidden) return;
        _autoHidden = false;
        _areaCheck.Stop();
        Log.Debug("Auto-hide: dock shown");
        ShowWindow(_hwnd, SW_SHOWNA);
        AnimateSlide(false, UpdateGlassBounds);
    }

    /// <summary>The Win key went down or up (from the keyboard hook).</summary>
    public void OnWinKey(bool down)
    {
        if (!AutoHideConfig.Enabled || !AutoHideConfig.ShowOnWinKey) return;
        _winHeld = down;
        if (down) Reveal(AutoHideConfig.WinKeyLingerMs);
        else KeepShown(AutoHideConfig.WinKeyLingerMs);
    }

    private void KeepShown(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        if (until > _keepUntil) _keepUntil = until;
    }

    private void OnHideCheck()
    {
        if (!AutoHideConfig.Enabled || _autoHidden) { _hideCheck.Stop(); return; }
        if (_hiddenForFullscreen) return;
        // Win+L and similar can swallow the Win key-up; trust the key state over a stale flag.
        if (_winHeld && (GetAsyncKeyState(VK_LWIN) & 0x8000) == 0 && (GetAsyncKeyState(VK_RWIN) & 0x8000) == 0) _winHeld = false;
        bool busy = _winHeld || PointerNearDock() || _host.IsFlyoutOpen || _host.IsMenuOpen || _dragging != null || _tileDrag != null || _movePressed
                    || _keyboardMode || _dropMarker.Visibility == Visibility.Visible || _dragHover != null;
        if (busy) KeepShown(AutoHideConfig.HideDelayMs);
        else if (DateTime.UtcNow >= _keepUntil) HideNow();
    }

    private bool PointerNearDock()
    {
        if (!GetCursorPos(out var pt)) return false;
        var r = PillScreenRect;
        double scale = _display.Scale;
        int m = (int)(AppConstants.AutoHide.HoverMarginDip * scale);
        // Away from the edge the dock also owns its headroom: magnified icons and labels live there.
        int away = (int)(_m.Headroom * scale) + m;
        var near = _m.Edge switch
        {
            DockEdge.Top => new RECT(r.Left - m, r.Top - m, r.Right + m, r.Bottom + away),
            DockEdge.Left => new RECT(r.Left - m, r.Top - m, r.Right + away, r.Bottom + m),
            DockEdge.Right => new RECT(r.Left - away, r.Top - m, r.Right + m, r.Bottom + m),
            _ => new RECT(r.Left - m, r.Top - away, r.Right + m, r.Bottom + m),
        };
        return pt.X >= near.Left && pt.X < near.Right && pt.Y >= near.Top && pt.Y < near.Bottom;
    }

    private void HideNow()
    {
        _hideCheck.Stop();
        _autoHidden = true;
        Log.Debug("Auto-hide: dock hidden");
        HideLabel();
        UpdateGlassBounds();
        AnimateSlide(true, () => { if (_autoHidden) ShowWindow(_hwnd, SW_HIDE); });
        StartAreaCheck();
    }

    /// <summary>How far the content moves to leave the screen: past the edge by the pill, its gap and a margin.</summary>
    private double SlideDistance => -_m.AwaySign * (_m.PillHeight + _m.EdgeGap + AppConstants.AutoHide.HoverMarginDip);

    private void AnimateSlide(bool hidden, Action done)
    {
        if (!_host.Settings.Current.Appearance.Animations) { SetSlide(hidden); done(); return; }
        var axis = _m.Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        var anim = new DoubleAnimation(hidden ? SlideDistance : 0, TimeSpan.FromMilliseconds(AppConstants.AutoHide.SlideMs))
        {
            EasingFunction = new CubicEase { EasingMode = hidden ? EasingMode.EaseIn : EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => done();
        _slide.BeginAnimation(axis, anim);
    }

    /// <summary>Jumps the slide offset to shown or hidden on the current edge's axis, clearing the other axis.</summary>
    private void SetSlide(bool hidden)
    {
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _slide.X = _m.Vertical && hidden ? SlideDistance : 0;
        _slide.Y = !_m.Vertical && hidden ? SlideDistance : 0;
    }

    // ---- external drag (pin files, bring app to front) ------------------------------------------------

    private DispatcherTimer? _dragActivateTimer;
    private AppButton? _dragHover;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _host.Settings.Current.Dock.Locked) { e.Effects = DragDropEffects.None; return; }
        var pos = e.GetPosition(_root);
        var over = FindAncestor<AppButton>(e.OriginalSource as DependencyObject);

        // Hovering a running app with files brings it to the front so the user can drop into it.
        if (over != null && over.Item.IsRunning)
        {
            _dropMarker.Visibility = Visibility.Collapsed;
            e.Effects = DragDropEffects.None;
            if (_dragHover != over)
            {
                _dragHover = over;
                _dragActivateTimer?.Stop();
                _dragActivateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _dragActivateTimer.Tick += (_, _) =>
                {
                    _dragActivateTimer!.Stop();
                    if (_dragHover?.Item.Windows.FirstOrDefault() is { } w) Input.ForegroundActivator.Activate(w.Handle);
                };
                _dragActivateTimer.Start();
            }
            return;
        }
        _dragHover = null;
        _dragActivateTimer?.Stop();
        e.Effects = DragDropEffects.Link;
        int idx = Math.Min(InsertIndexAt(pos), _host.Apps.PinnedCount);
        var buttons = AppButtons;
        var appsAt = _apps.TranslatePoint(new Point(0, 0), _root);
        double along = buttons.Count == 0
            ? _m.Main(appsAt)
            : idx < buttons.Count ? _m.Main(buttons[idx].TranslatePoint(new Point(0, 0), _root)) - _m.ItemSpacing / 2
            : _m.Main(buttons[^1].TranslatePoint(new Point(buttons[^1].ActualWidth, buttons[^1].ActualHeight), _root)) + _m.ItemSpacing / 2;
        if (_m.Vertical)
        {
            Canvas.SetTop(_dropMarker, along - 1.5);
            Canvas.SetLeft(_dropMarker, appsAt.X + (_apps.ActualWidth - _m.IconSize) / 2);
        }
        else
        {
            Canvas.SetLeft(_dropMarker, along - 1.5);
            Canvas.SetTop(_dropMarker, appsAt.Y + (_apps.ActualHeight - _m.IconSize) / 2);
        }
        _dropMarker.Visibility = Visibility.Visible;
    }

    private int InsertIndexAt(Point pos)
    {
        var buttons = AppButtons;
        for (int i = 0; i < buttons.Count; i++)
            if (_m.Main(pos) < _m.Main(buttons[i].TranslatePoint(new Point(buttons[i].ActualWidth / 2, buttons[i].ActualHeight / 2), _root))) return i;
        return buttons.Count;
    }

    private void EndExternalDrag()
    {
        _dropMarker.Visibility = Visibility.Collapsed;
        _dragActivateTimer?.Stop();
        _dragHover = null;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        int idx = Math.Min(InsertIndexAt(e.GetPosition(_root)), _host.Apps.PinnedCount);
        EndExternalDrag();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files)
        {
            var pin = PinStore.FromPath(f);
            if (pin == null) { Log.Warn($"Dropped item {f} cannot be pinned"); continue; }
            _host.Apps.AddPin(pin, idx++);
        }
    }

    // ---- keyboard mode (Win+T / Win+B) --------------------------------------------------------------

    public void EnterKeyboardMode(TileButton? focus = null)
    {
        Reveal(_host.Settings.Current.AutoHide.HideDelayMs);
        if (!_keyboardMode) _keyboardReturnTo = GetForegroundWindow();
        _keyboardMode = true;
        SetForegroundWindow(_hwnd);
        Activate();
        var target = focus ?? (TileButton?)AppButtons.FirstOrDefault() ?? _tiles.FirstOrDefault();
        target?.Focus();
        Keyboard.Focus(target);
    }

    /// <summary>Win+T pressed again: move to the next (or previous) app.</summary>
    public void CycleKeyboardFocus(bool backwards)
    {
        if (!_keyboardMode) { EnterKeyboardMode(); return; }
        MoveFocus(backwards ? -1 : 1);
    }

    private void LeaveKeyboardMode(bool restoreFocus)
    {
        if (!_keyboardMode) return;
        _keyboardMode = false;
        Keyboard.ClearFocus();
        if (restoreFocus && _keyboardReturnTo != 0) Input.ForegroundActivator.Activate(_keyboardReturnTo);
        _keyboardReturnTo = 0;
        HideLabel();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_keyboardMode) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var focused = Keyboard.FocusedElement as TileButton;
        switch (key)
        {
            case Key.Left: case Key.Up: MoveFocus(-1); break;
            case Key.Right: case Key.Down: MoveFocus(1); break;
            case Key.Home: _tiles.FirstOrDefault()?.Focus(); break;
            case Key.End: _tiles.LastOrDefault()?.Focus(); break;
            case Key.Enter:
            case Key.Space:
                LeaveKeyboardMode(restoreFocus: false);
                focused?.InvokeClick();
                break;
            case Key.Apps:
            case Key.F10 when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                focused?.InvokeRightClick();
                break;
            case Key.Escape:
                LeaveKeyboardMode(restoreFocus: true);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void MoveFocus(int delta)
    {
        if (_tiles.Count == 0) return;
        int cur = _tiles.IndexOf(Keyboard.FocusedElement as TileButton ?? _tiles[0]);
        int next = ((cur < 0 ? 0 : cur) + delta + _tiles.Count) % _tiles.Count;
        _tiles[next].Focus();
    }

    // ---- teardown -----------------------------------------------------------------------------------

    public void Shutdown()
    {
        _host.Apps.Items.CollectionChanged -= OnAppsChanged;
        _appBar?.Dispose();
        DisposeWidgets();
        _ghost?.Close();
        _hideCheck.Stop();
        _areaCheck.Stop();
        _sensor?.Close();
        if (_magnifying) CompositionTarget.Rendering -= OnMagnifyFrame;
        Close();
        _occlusion?.Dispose();
        _maximized?.Dispose();
        _band?.Dispose();
        _glass?.Dispose();
    }

    public void OnExplorerRestarted()
    {
        _appBar?.Reset();
        PlaceWindow();
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
