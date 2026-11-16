using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GlassDock.Widgets;

/// <summary>
/// The common widget face: a glyph, a value and a caption under it. The value column has a fixed
/// width (measured from the widest expected text) so the dock does not jitter as numbers change.
/// In a vertical dock the glyph sits above the text and everything is centred.
/// </summary>
public sealed class MetricView : StackPanel
{
    private readonly TextBlock _glyph;
    private readonly TextBlock _value;
    private readonly TextBlock _caption;

    public MetricView(WidgetContext ctx, string glyph, string caption, string widestValue)
    {
        double icon = ctx.Metrics.IconSize;
        var fg = new SolidColorBrush(ctx.Look.Foreground);
        bool vertical = ctx.Metrics.Vertical;
        Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = vertical ? new Thickness(0, icon * 0.14, 0, icon * 0.14) : new Thickness(icon * 0.12, 0, icon * 0.16, 0);
        IsHitTestVisible = false;

        _glyph = new TextBlock
        {
            Text = glyph,
            FontFamily = WidgetFonts.Icons,
            FontSize = icon * 0.42,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = vertical ? new Thickness(0, 0, 0, icon * 0.06) : new Thickness(0, 0, icon * 0.1, 0),
        };
        _value = Text(fg, icon * 0.32, FontWeights.SemiBold, 1);
        _caption = Text(fg, icon * 0.24, FontWeights.Normal, 0.72);
        _caption.Text = caption;
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = Measure(widestValue, _value) };
        if (vertical)
        {
            _value.TextAlignment = TextAlignment.Center;
            _caption.TextAlignment = TextAlignment.Center;
        }
        column.Children.Add(_value);
        column.Children.Add(_caption);
        Children.Add(_glyph);
        Children.Add(column);
    }

    public string Glyph { set => _glyph.Text = value; }
    public string Value { set => _value.Text = value; }
    public string Caption { set => _caption.Text = value; }

    internal static TextBlock Text(Brush fg, double size, FontWeight weight, double opacity)
    {
        var t = new TextBlock
        {
            FontFamily = WidgetFonts.Text,
            FontSize = size,
            FontWeight = weight,
            Foreground = fg,
            Opacity = opacity,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Same-width digits keep "18%" and "81%" the same width.
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    internal static double Measure(string text, TextBlock like)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(like.FontFamily, like.FontStyle, like.FontWeight, like.FontStretch), like.FontSize, Brushes.Black, 1.0);
        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace);
    }
}

public static class WidgetFonts
{
    public static FontFamily Icons { get; } = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static FontFamily Text { get; } = new("Segoe UI Variable Text, Segoe UI");
}

/// <summary>Segoe Fluent Icons code points (learn.microsoft.com, "Segoe Fluent Icons font").</summary>
public static class WidgetGlyphs
{
    public const string Cpu = "";
    public const string Ram = "";
    public const string Network = "";
    public const string Mute = "";
    public const string Volume0 = "";
    public const string Volume1 = "";
    public const string Volume2 = "";
    public const string Volume3 = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Previous = "";
    public const string Next = "";
    public const string MusicNote = "";
    public const string BatteryUnknown = "";

    /// <summary>Battery0..Battery10 and BatteryCharging0..BatteryCharging10.</summary>
    public static string Battery(int percent, bool charging)
    {
        int step = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        if (charging) return step switch { 9 => "", 10 => "", _ => ((char)(0xE85A + step)).ToString() };
        return step == 10 ? "" : ((char)(0xE850 + step)).ToString();
    }
}
