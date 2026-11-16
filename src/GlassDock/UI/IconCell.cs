using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDock.Config;
using GlassDock.Glass;

namespace GlassDock.UI;

/// <summary>An icon over a two-line name: one item in the stack and group flyouts.</summary>
public sealed class IconCell : Border
{
    private readonly Border? _indicator;
    private readonly GlassLook _look;

    public Image Icon { get; }

    /// <param name="withIndicator">Reserve a running-app bar under the icon (group flyout).</param>
    /// <param name="size">Icon size, cell width and height, and name font size; the stack flyout's by default.</param>
    public IconCell(string name, string? tooltip, GlassLook look, bool withIndicator = false, (double Icon, double Width, double Height, double Font)? size = null)
    {
        _look = look;
        var (iconDip, width, height, font) = size ?? (AppConstants.Stacks.IconDip, AppConstants.Stacks.CellWidthDip, AppConstants.Stacks.CellHeightDip, 11.5);
        double iconBottom = withIndicator ? 1 : 4;
        Icon = new Image { Width = iconDip, Height = iconDip, Margin = new Thickness(0, 8, 0, iconBottom) };
        RenderOptions.SetBitmapScalingMode(Icon, BitmapScalingMode.HighQuality);
        var stack = new StackPanel();
        stack.Children.Add(Icon);
        if (withIndicator)
        {
            _indicator = new Border
            {
                Height = AppConstants.Dock.IndicatorHeightDip,
                Width = AppConstants.Dock.IndicatorRunningWidthDip,
                CornerRadius = new CornerRadius(AppConstants.Dock.IndicatorHeightDip / 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4 - iconBottom),
                Visibility = Visibility.Hidden,
            };
            stack.Children.Add(_indicator);
        }
        stack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = font,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            // Two lines at most.
            MaxHeight = Math.Ceiling(font * 2.8),
            Margin = new Thickness(4, 0, 4, 4),
        });

        Width = width;
        Height = height;
        CornerRadius = new CornerRadius(8);
        Background = Brushes.Transparent;
        Child = stack;
        ToolTip = tooltip;

        var hover = new SolidColorBrush(look.IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0, 0, 0));
        MouseEnter += (_, _) => Background = hover;
        MouseLeave += (_, _) => Background = Brushes.Transparent;
    }

    /// <summary>Same bar as on the dock: short when the app runs, long and coloured when it is in front.</summary>
    public void SetIndicator(bool running, bool active, Color activeColor)
    {
        if (_indicator == null) return;
        _indicator.Visibility = running ? Visibility.Visible : Visibility.Hidden;
        _indicator.Width = active ? AppConstants.Dock.IndicatorActiveWidthDip : AppConstants.Dock.IndicatorRunningWidthDip;
        var fg = _look.Foreground;
        _indicator.Background = new SolidColorBrush(active ? activeColor : Color.FromArgb(0xD0, fg.R, fg.G, fg.B));
    }
}
