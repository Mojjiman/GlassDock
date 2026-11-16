using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Glass;

namespace GlassDock.UI;

public sealed class AppButton : TileButton
{
    private readonly Grid _iconHost;
    private readonly Image _icon;
    private readonly Image _badge;
    private readonly Border _indicator;
    private readonly TranslateTransform _bounce = new();
    private readonly bool _showIndicator;
    private readonly bool _showBadges;
    private readonly string _activeColor;

    public AppItem Item { get; }

    public AppButton(AppItem item, DockMetrics m, GlassLook look, AppsSettings apps) : base(m, look)
    {
        Item = item;
        _showIndicator = apps.ShowRunningIndicator;
        _showBadges = apps.ShowBadges;
        _activeColor = apps.ActiveIndicatorColor;
        Width = Height = m.TileSize;
        Label = item.DisplayName;
        AddCell();
        double iconSize = Math.Round(CellSize * AppConstants.Dock.CellIconFraction);

        _iconHost = new Grid
        {
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _bounce,
            IsHitTestVisible = false,
        };
        _icon = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        _badge = new Image
        {
            Width = iconSize * 0.45,
            Height = iconSize * 0.45,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -iconSize * 0.12, -iconSize * 0.08),
        };
        _iconHost.Children.Add(_icon);
        _iconHost.Children.Add(_badge);
        Children.Add(_iconHost);

        // Inside the cell, on the side facing the screen edge.
        double inset = (m.TileSize - CellSize) / 2 + 2.5;
        _indicator = new Border
        {
            CornerRadius = new CornerRadius(AppConstants.Dock.IndicatorHeightDip / 2),
            HorizontalAlignment = m.Edge switch { DockEdge.Left => HorizontalAlignment.Left, DockEdge.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Center },
            VerticalAlignment = m.Edge switch { DockEdge.Bottom => VerticalAlignment.Bottom, DockEdge.Top => VerticalAlignment.Top, _ => VerticalAlignment.Center },
            Margin = m.Vertical ? new Thickness(inset, 0, inset, 0) : new Thickness(0, inset, 0, inset),
            IsHitTestVisible = false,
        };
        // The bar lies along the dock: its thickness is fixed and its length shows running vs. in front.
        if (m.Vertical) { _indicator.Width = AppConstants.Dock.IndicatorHeightDip; _indicator.Height = AppConstants.Dock.IndicatorRunningWidthDip; }
        else { _indicator.Height = AppConstants.Dock.IndicatorHeightDip; _indicator.Width = AppConstants.Dock.IndicatorRunningWidthDip; }
        Children.Add(_indicator);

        item.PropertyChanged += OnItemChanged;
        Unloaded += (_, _) => item.PropertyChanged -= OnItemChanged;
        Loaded += (_, _) => { item.PropertyChanged -= OnItemChanged; item.PropertyChanged += OnItemChanged; Refresh(); };
        Refresh();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    public override void ApplyLook(GlassLook look)
    {
        base.ApplyLook(look);
        Refresh();
    }

    private void Refresh()
    {
        Label = Item.DisplayName;
        _icon.Source = Item.Icon;
        // A group's icon is a plate holding several apps; at app size each one would be tiny.
        _iconHost.Width = _iconHost.Height = Math.Round(CellSize * (Item.IsGroup ? AppConstants.Groups.IconFraction : AppConstants.Dock.CellIconFraction));
        _badge.Source = _showBadges ? Item.OverlayIcon : null;
        bool running = Item.IsRunning && _showIndicator;
        bool active = running && Item.IsActive;
        _indicator.Visibility = running ? Visibility.Visible : Visibility.Hidden;
        _indicator.Background = new SolidColorBrush(active ? ActiveColor() : Color.FromArgb(0xD0, Look.Foreground.R, Look.Foreground.G, Look.Foreground.B));
        var ms = TimeSpan.FromMilliseconds(AppConstants.Animation.IndicatorMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _indicator.BeginAnimation(Metrics.Vertical ? HeightProperty : WidthProperty, new DoubleAnimation(active ? AppConstants.Dock.IndicatorActiveWidthDip : AppConstants.Dock.IndicatorRunningWidthDip, ms) { EasingFunction = ease });
        SetCellState(active ? CellState.Active : running ? CellState.Running : CellState.Idle);
        UpdateAnimations();
    }

    private Color ActiveColor() => ActiveIndicatorColor(Look, _activeColor);

    /// <summary>The "app in front" bar colour from <see cref="AppsSettings.ActiveIndicatorColor"/>.</summary>
    public static Color ActiveIndicatorColor(GlassLook look, string setting) => setting switch
    {
        "foreground" => look.Foreground,
        "white" => Colors.White,
        "accent" => look.Accent,
        _ => GlassLook.ParseColor(setting) ?? look.Accent,
    };

    private bool _animating;

    private void UpdateAnimations()
    {
        bool want = Item.IsLaunching || Item.NeedsAttention;
        if (want == _animating) return;
        _animating = want;
        var axis = Metrics.Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        if (!want)
        {
            _bounce.BeginAnimation(axis, null);
            return;
        }
        // Bounce away from the screen edge, like a dock does while an app launches or wants attention.
        double dir = Metrics.AwaySign;
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(AppConstants.Animation.BounceMs * 2) };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(dir * Metrics.IconSize * 0.22, KeyTime.FromPercent(0.25), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.5), new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        _bounce.BeginAnimation(axis, anim);
    }
}
