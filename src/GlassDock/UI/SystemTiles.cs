using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Glass;

namespace GlassDock.UI;

/// <summary>Start tile: a 2×2 grid of rounded squares in the accent colour.</summary>
public sealed class StartTile : TileButton
{
    // Proportions of the Windows 11 mark: four square panes, a thin gap, barely rounded corners.
    private const double LogoScale = 0.66;
    private const double GapFraction = 0.06;
    private const double CornerFraction = 0.035;
    private static readonly Color LogoLight = Color.FromRgb(0x86, 0xE0, 0xFF);
    private static readonly Color LogoDeep = Color.FromRgb(0x00, 0x48, 0xC0);

    private readonly System.Windows.Shapes.Path _logo = new() { IsHitTestVisible = false, Stretch = Stretch.None };

    public StartTile(DockMetrics m, GlassLook look) : base(m, look)
    {
        Width = Height = m.TileSize;
        Label = "시작";
        double s = Math.Round(m.IconSize * LogoScale);
        double gap = Math.Max(1, s * GapFraction), pane = (s - gap) / 2, r = s * CornerFraction;
        var g = new GeometryGroup();
        foreach (var (x, y) in new[] { (0.0, 0.0), (pane + gap, 0.0), (0.0, pane + gap), (pane + gap, pane + gap) })
            g.Children.Add(new RectangleGeometry(new Rect(x, y, pane, pane), r, r));
        g.Freeze();
        _logo.Data = g;
        _logo.Width = s;
        _logo.Height = s;
        _logo.HorizontalAlignment = HorizontalAlignment.Center;
        _logo.VerticalAlignment = VerticalAlignment.Center;
        // One gradient over the whole mark (not per pane), lit from the top-left.
        var fill = new LinearGradientBrush(LogoLight, LogoDeep, new Point(0, 0), new Point(1, 1));
        fill.Freeze();
        _logo.Fill = fill;
        Children.Add(_logo);
        ApplyLook(look);
    }
}

/// <summary>A tile showing one or more Segoe Fluent glyphs side by side (stacked in a vertical dock).</summary>
public sealed class GlyphTile : TileButton
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    private readonly string[] _glyphs;
    private readonly double _scale;

    public GlyphTile(DockMetrics m, GlassLook look, string label, double scale, params string[] glyphs) : base(m, look)
    {
        Label = label;
        _glyphs = glyphs;
        _scale = scale;
        if (m.Vertical) { _row.Orientation = Orientation.Vertical; _row.VerticalAlignment = VerticalAlignment.Center; }
        Children.Add(_row);
        Build();
        // Length follows the glyph count; keep at least a square tile.
        SetMinLength(m.TileSize * (glyphs.Length > 1 ? 0.6 + 0.45 * glyphs.Length : 1));
    }

    private void Build()
    {
        _row.Children.Clear();
        foreach (var g in _glyphs)
            _row.Children.Add(new Border { Padding = Metrics.Along(Metrics.IconSize * 0.1, Metrics.IconSize * 0.1), Child = Glyph(g, Metrics.IconSize * _scale) });
    }

    public override void ApplyLook(GlassLook look) { base.ApplyLook(look); Build(); }
}

/// <summary>
/// Shown while a newer release is waiting: an accent pill with a download arrow and "업데이트" (the arrow
/// alone in a vertical dock). It is not a tile spec; the dock adds it in front of the clock.
/// </summary>
public sealed class UpdateTile : TileButton
{
    private readonly Border _pill = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public UpdateTile(DockMetrics m, GlassLook look, Version version) : base(m, look)
    {
        Label = $"GlassDock {version} 업데이트";
        Tag = "update";
        Children.Add(_pill);
        ApplyLook(look);
    }

    public override void ApplyLook(GlassLook look)
    {
        base.ApplyLook(look);
        double h = Metrics.IconSize * AppConstants.Dock.UpdatePillHeight;
        _pill.Height = h;
        _pill.CornerRadius = new CornerRadius(h / 2);
        _pill.Background = new SolidColorBrush(look.Accent);
        _pill.Padding = Metrics.Vertical ? new Thickness(0) : new Thickness(h * 0.45, 0, h * 0.55, 0);
        if (Metrics.Vertical) _pill.Width = h;
        var arrow = Glyph(Glyphs.Download, h * 0.5);
        arrow.Foreground = Brushes.White;
        if (Metrics.Vertical) { _pill.Child = arrow; return; }
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(arrow);
        row.Children.Add(new TextBlock
        {
            Text = "업데이트",
            Foreground = Brushes.White,
            FontSize = h * 0.42,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(h * 0.22, 0, 0, 1),
        });
        _pill.Child = row;
    }
}

public sealed class ClockTile : TileButton
{
    private readonly TextBlock _time = new() { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _date = new() { HorizontalAlignment = HorizontalAlignment.Center, Opacity = AppConstants.Dock.ClockDateOpacity };
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private ClockSettings _settings;

    public ClockTile(DockMetrics m, GlassLook look, ClockSettings settings) : base(m, look)
    {
        _settings = settings;
        Label = "알림 센터 및 달력";
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(_time);
        stack.Children.Add(_date);
        if (m.Vertical)
        {
            // One tile wide: a 12-hour time or a long date shrinks to fit instead of widening the dock.
            stack.Margin = new Thickness(0, m.IconSize * 0.2, 0, m.IconSize * 0.2);
            Children.Add(new Viewbox { Child = stack, StretchDirection = StretchDirection.DownOnly, MaxWidth = CellSize, IsHitTestVisible = false });
        }
        else
        {
            stack.Margin = new Thickness(m.IconSize * 0.25, 0, m.IconSize * 0.25, 0);
            Children.Add(stack);
        }
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => Tick();
        Unloaded += (_, _) => _timer.Stop();
        ApplyLook(look);
    }

    public override void ApplyLook(GlassLook look)
    {
        base.ApplyLook(look);
        var fg = new SolidColorBrush(look.Foreground);
        _time.Foreground = fg;
        _date.Foreground = fg;
        _time.FontSize = Metrics.IconSize * AppConstants.Dock.ClockTimeFraction;
        _date.FontSize = Metrics.IconSize * AppConstants.Dock.ClockDateFraction;
        Tick();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        string fmt = _settings.Hour12 ? (_settings.ShowSeconds ? "h:mm:ss tt" : "h:mm tt") : (_settings.ShowSeconds ? "HH:mm:ss" : "HH:mm");
        _time.Text = now.ToString(fmt, culture);
        // A vertical dock has no room for the year; "M/d" uses the culture's date separator.
        _date.Text = now.ToString(Metrics.Vertical ? "M/d" : culture.DateTimeFormat.ShortDatePattern, culture);
        _date.Visibility = _settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
        // Fire exactly on the next second/minute boundary instead of polling.
        var next = _settings.ShowSeconds
            ? TimeSpan.FromMilliseconds(1000 - now.Millisecond + 5)
            : TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(5);
        _timer.Interval = next;
        _timer.Start();
    }
}

/// <summary>Thin divider across the dock between tile groups (pill shape).</summary>
public sealed class Divider : Border
{
    public Divider(DockMetrics m, GlassLook look)
    {
        Width = m.Vertical ? m.IconSize * 0.7 : 1;
        Height = m.Vertical ? 1 : m.IconSize * 0.7;
        Margin = m.Along(m.ItemSpacing, m.ItemSpacing);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        Background = new SolidColorBrush(Color.FromArgb(0x50, look.Foreground.R, look.Foreground.G, look.Foreground.B));
        IsHitTestVisible = false;
    }
}

public static class Glyphs
{
    public const string ChevronUp = "";
    public const string ChevronDown = "";
    public const string ChevronLeft = "";
    public const string ChevronRight = "";
    public const string Wifi = "";
    public const string Volume = "";
    public const string Battery = "";
    public const string Bell = "";
    public const string Desktop = "";
    public const string Settings = "";
    public const string Close = "";
    public const string Download = "";
}
