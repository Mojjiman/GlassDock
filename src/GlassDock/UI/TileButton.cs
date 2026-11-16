using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDock.Config;
using GlassDock.Glass;

namespace GlassDock.UI;

/// <summary>Base for every clickable dock element: hover plate, press animation, label, click events.</summary>
public enum CellState { Idle, Running, Active }

public enum DropState { None, Aimed, Armed }

public class TileButton : Grid
{
    protected readonly Border HoverPlate;
    protected readonly ScaleTransform Scale = new(1, 1);
    /// <summary>Offset used by drag-reorder animations, kept apart from the press scale.</summary>
    public TranslateTransform Shift { get; } = new();
    // Magnification has its own scale and offset so it composes with presses and drags instead of fighting them.
    private readonly ScaleTransform _magnify = new(1, 1);
    private readonly TranslateTransform _magnifyShift = new();
    protected GlassLook Look;
    protected DockMetrics Metrics;
    private bool _pressed;

    public string Label { get; set; } = "";
    public event Action<TileButton>? Clicked;
    public event Action<TileButton>? RightClicked;
    public event Action<TileButton>? MiddleClicked;
    public event Action<TileButton, bool>? HoverChanged;

    public TileButton(DockMetrics metrics, GlassLook look)
    {
        Metrics = metrics;
        Look = look;
        // Across the dock every tile is one tile thick; along it a tile may be longer (clock, widgets).
        if (metrics.Vertical) { Width = metrics.TileSize; MinHeight = metrics.TileSize; }
        else { Height = metrics.TileSize; MinWidth = metrics.TileSize; }
        Background = Brushes.Transparent; // hit-testable everywhere in the tile
        // Presses and drop targets scale from the screen edge, so the tile never pokes over it.
        RenderTransformOrigin = metrics.Edge switch
        {
            DockEdge.Top => new Point(0.5, 0),
            DockEdge.Left => new Point(0, 0.5),
            DockEdge.Right => new Point(1, 0.5),
            _ => new Point(0.5, 1),
        };
        RenderTransform = new TransformGroup { Children = { Scale, _magnify, _magnifyShift, Shift } };
        HoverPlate = new Border
        {
            CornerRadius = new CornerRadius(metrics.TileSize * 0.28),
            Background = new SolidColorBrush(Colors.Transparent),
            Margin = new Thickness(1),
        };
        Children.Add(HoverPlate);
        Focusable = true;
        FocusVisualStyle = null;
    }

    public virtual void ApplyLook(GlassLook look) => Look = look;

    /// <summary>Sets the tile's size along the dock (width in a horizontal dock, height in a vertical one).</summary>
    public void SetLength(double length, double? min = null)
    {
        if (Metrics.Vertical) { Height = length; MinHeight = min ?? length; }
        else { Width = length; MinWidth = min ?? length; }
    }

    /// <summary>Sets the minimum size along the dock.</summary>
    public void SetMinLength(double min)
    {
        if (Metrics.Vertical) MinHeight = min; else MinWidth = min;
    }

    private Border? _cell;
    private GradientStop? _cellEdgeTop, _cellEdgeBottom;

    /// <summary>Inner size of the glass cell (DIP), or the whole tile when there is none.</summary>
    protected double CellSize => Metrics.TileSize - 2 * Metrics.TileSize * AppConstants.Dock.CellGapFraction;

    /// <summary>
    /// Adds an outline-only cell around the icon, hidden until <see cref="SetCellState"/> says the app
    /// runs; the hover plate takes the cell's shape.
    /// </summary>
    protected void AddCell()
    {
        var radius = new CornerRadius(Metrics.TileSize * AppConstants.Dock.CellCornerFraction);
        _cellEdgeTop = new GradientStop(Black(AppConstants.Dock.CellShadeTop), 0);
        _cellEdgeBottom = new GradientStop(White(AppConstants.Dock.CellLightBottom), 1);
        var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        edge.GradientStops.Add(_cellEdgeTop);
        edge.GradientStops.Add(_cellEdgeBottom);
        _cell = new Border
        {
            Width = CellSize,
            Height = CellSize,
            CornerRadius = radius,
            BorderBrush = edge,
            Opacity = 0,
            BorderThickness = new Thickness(AppConstants.Dock.CellEdgeDip),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Children.Insert(0, _cell);
        HoverPlate.Width = CellSize;
        HoverPlate.Height = CellSize;
        HoverPlate.Margin = new Thickness(0);
        HoverPlate.CornerRadius = radius;
    }

    protected void SetCellState(CellState state)
    {
        if (_cell == null) return;
        var d = TimeSpan.FromMilliseconds(AppConstants.Animation.IndicatorMs);
        bool active = state == CellState.Active;
        _cell.BeginAnimation(OpacityProperty, new DoubleAnimation(state == CellState.Idle ? 0 : 1, d));
        _cellEdgeTop!.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Black(active ? AppConstants.Dock.CellShadeActiveTop : AppConstants.Dock.CellShadeTop), d));
        _cellEdgeBottom!.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(White(active ? AppConstants.Dock.CellLightActiveBottom : AppConstants.Dock.CellLightBottom), d));
    }

    private static Color White(byte alpha) => Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);
    private static Color Black(byte alpha) => Color.FromArgb(alpha, 0, 0, 0);

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        SetHover(true);
        HoverChanged?.Invoke(this, true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(false);
        _pressed = false;
        AnimateScale(1);
        HoverChanged?.Invoke(this, false);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); SetHover(true); HoverChanged?.Invoke(this, true); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); SetHover(false); HoverChanged?.Invoke(this, false); }

    protected void SetHover(bool on)
    {
        var target = on ? (Look.IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00)) : Colors.Transparent;
        var brush = (SolidColorBrush)HoverPlate.Background;
        if (brush.IsFrozen) HoverPlate.Background = brush = brush.Clone();
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(AppConstants.Animation.HoverMs)));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Left) { _pressed = true; AnimateScale(0.9); }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        switch (e.ChangedButton)
        {
            case MouseButton.Left when _pressed:
                _pressed = false;
                AnimateScale(1);
                Clicked?.Invoke(this);
                e.Handled = true;
                break;
            case MouseButton.Right:
                RightClicked?.Invoke(this);
                e.Handled = true;
                break;
            case MouseButton.Middle:
                MiddleClicked?.Invoke(this);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Cancels a pending click (used when a press turns into a drag).</summary>
    public void CancelPress() { _pressed = false; AnimateScale(1); }

    private Border? _dropRing;

    /// <summary>
    /// Feedback while another icon is held over this one: <see cref="DropState.Aimed"/> (a faint accent
    /// ring while the hold counts down) and <see cref="DropState.Armed"/> (a solid ring, a tinted plate and
    /// a bigger tile: releasing now groups the two).
    /// </summary>
    public void SetDropState(DropState state)
    {
        if (_dropRing == null)
        {
            _dropRing = new Border
            {
                Width = CellSize + AppConstants.Groups.RingOutsetDip * 2,
                Height = CellSize + AppConstants.Groups.RingOutsetDip * 2,
                CornerRadius = new CornerRadius(Metrics.TileSize * AppConstants.Dock.CellCornerFraction + AppConstants.Groups.RingOutsetDip),
                BorderThickness = new Thickness(AppConstants.Groups.RingDip),
                BorderBrush = new SolidColorBrush(Look.Accent),
                Background = new SolidColorBrush(Color.FromArgb(AppConstants.Groups.ArmedFillAlpha, Look.Accent.R, Look.Accent.G, Look.Accent.B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Opacity = 0,
            };
            Children.Insert(Children.IndexOf(HoverPlate) + 1, _dropRing);
        }
        var d = TimeSpan.FromMilliseconds(AppConstants.Animation.HoverMs);
        _dropRing.BeginAnimation(OpacityProperty, new DoubleAnimation(state switch { DropState.Armed => 1, DropState.Aimed => AppConstants.Groups.AimedRingOpacity, _ => 0 }, d));
        _dropRing.Background.Opacity = state == DropState.Armed ? 1 : 0;
        SetHover(state != DropState.None);
        AnimateScale(state switch { DropState.Armed => AppConstants.Groups.MergeTargetScale, DropState.Aimed => AppConstants.Groups.AimedScale, _ => 1 });
    }

    /// <summary>The dragged icon shrinks a little while it is about to go into a group.</summary>
    public void SetSinking(bool on) => AnimateScale(on ? AppConstants.Groups.SinkScale : 1);

    public void InvokeClick() => Clicked?.Invoke(this);
    public void InvokeRightClick() => RightClicked?.Invoke(this);

    /// <summary>Grows the tile away from the screen edge and moves it <paramref name="along"/> the dock to make room.</summary>
    public void SetMagnification(double scale, double along)
    {
        _magnify.ScaleX = _magnify.ScaleY = scale;
        if (Metrics.Vertical) _magnifyShift.Y = along; else _magnifyShift.X = along;
    }

    /// <summary>Eases <see cref="Shift"/> to the given offset (DIP).</summary>
    public void AnimateShift(double x, double y, int ms)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }

    /// <summary>Eases <see cref="Shift"/> to an offset <paramref name="along"/> the dock and <paramref name="across"/> it.</summary>
    public void AnimateShiftAlong(double along, double across, int ms) =>
        AnimateShift(Metrics.Vertical ? across : along, Metrics.Vertical ? along : across, ms);

    /// <summary>
    /// Jumps the offset along the dock, cancelling its animation; the offset across the dock (a drag lift)
    /// keeps animating.
    /// </summary>
    public void SetShiftAlong(double along)
    {
        var p = Metrics.Vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        Shift.BeginAnimation(p, null);
        Shift.SetValue(p, along);
    }

    protected void AnimateScale(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(AppConstants.Animation.PressMs)) { EasingFunction = new QuadraticEase() };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    protected static FontFamily IconFont { get; } = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    protected TextBlock Glyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = size,
        Foreground = new SolidColorBrush(Look.Foreground),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };
}
