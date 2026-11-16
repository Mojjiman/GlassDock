using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassDock.UI;

/// <summary>
/// A rounded bar slider (0..1) in the style of the system volume pill: press anywhere on it and drag,
/// or use the wheel. The control can be taller than the visible bar so a thin bar is still easy to hit.
/// </summary>
public sealed class PillSlider : Grid
{
    private readonly Grid _track;
    private readonly Border _fill;
    private readonly double _wheelStep;
    private double _value;

    /// <summary>Every change the user makes, while dragging too.</summary>
    public event Action<double>? ValueChanged;
    /// <summary>The user let go (or turned the wheel): for changes that should not repeat while dragging, like seeking.</summary>
    public event Action<double>? Released;

    public bool Dragging { get; private set; }

    public PillSlider(double height, double barHeight, Brush track, Brush fill, double wheelStep)
    {
        _wheelStep = wheelStep;
        Height = height;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        _fill = new Border { Background = fill, HorizontalAlignment = HorizontalAlignment.Left };
        _track = new Grid { Height = barHeight, VerticalAlignment = VerticalAlignment.Center, Background = track };
        _track.Children.Add(_fill);
        Children.Add(_track);
        _track.SizeChanged += (_, _) => Layout();

        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Dragging = true;
            CaptureMouse();
            SetFrom(e);
        };
        MouseMove += (_, e) => { if (Dragging) SetFrom(e); };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!Dragging) return;
            Dragging = false;
            ReleaseMouseCapture();
            Released?.Invoke(_value);
        };
        LostMouseCapture += (_, _) => Dragging = false;
        MouseWheel += (_, e) =>
        {
            if (_wheelStep <= 0) return;
            e.Handled = true;
            Set(_value + e.Delta / 120.0 * _wheelStep);
            Released?.Invoke(_value);
        };
    }

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Layout(); }
    }

    public Brush Fill { set => _fill.Background = value; }

    private void SetFrom(MouseEventArgs e) => Set(e.GetPosition(_track).X / Math.Max(1, _track.ActualWidth));

    private void Set(double v)
    {
        v = Math.Clamp(v, 0, 1);
        if (v == _value) return;
        Value = v;
        ValueChanged?.Invoke(v);
    }

    private void Layout()
    {
        double w = _track.ActualWidth, h = _track.ActualHeight;
        _fill.Width = w * _value;
        // Clipping the square fill to the rounded track gives a round start and a straight edge at the value.
        _track.Clip = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
    }
}
