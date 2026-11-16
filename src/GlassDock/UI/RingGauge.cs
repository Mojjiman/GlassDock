using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GlassDock.UI;

/// <summary>A circular gauge: a faint full ring with an arc over it from 12 o'clock, clockwise, for 0-100.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double StrokeThickness { get; init; } = 8;
    public Brush Track { get; init; } = Brushes.Transparent;
    public Brush Fill { get; set; } = Brushes.White;

    /// <summary>Sweeps from the current value to <paramref name="to"/>.</summary>
    public void AnimateTo(double to, int ms) =>
        BeginAnimation(ValueProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        double r = (size - StrokeThickness) / 2;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Track, StrokeThickness), c, r, r);
        double v = Math.Clamp(Value, 0, 100);
        if (v <= 0) return;
        var pen = new Pen(Fill, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 99.95) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double angle = v / 100 * 360;
        double rad = (angle - 90) * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r), false, false);
            g.ArcTo(new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad)), new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
