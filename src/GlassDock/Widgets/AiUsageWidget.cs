using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Logging;

namespace GlassDock.Widgets;

/// <summary>
/// Claude and ChatGPT (its Codex limits), one row each: app icon, name, a bar of the 5-hour limit used, the time until it
/// resets and the share used. Claude shows its Claude Code tokens instead while its limits are unknown.
/// A vertical dock has no room for rows and shows two small columns of the numbers only.
/// </summary>
public sealed class AiUsageWidget : IWidget
{
    private IDisposable? _sub, _lease;
    private Row _claude = null!, _codex = null!;

    /// <summary>The parts of one row that change; the bar and time are absent in the vertical layout.</summary>
    private sealed class Row
    {
        public required TextBlock Value { get; init; }
        public TextBlock? Time { get; init; }
        public Border? Fill { get; init; }
        public double BarLength { get; init; }
    }

    // No hover label: the rows already show the numbers and a click opens the full usage flyout.
    public string Label => "";

    public FrameworkElement CreateView(WidgetContext ctx)
    {
        double icon = ctx.Metrics.IconSize;
        FrameworkElement view;
        if (ctx.Metrics.Vertical)
        {
            var panel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, icon * 0.14, 0, icon * 0.14),
                IsHitTestVisible = false,
            };
            _claude = Column(panel, ctx, "Claude", first: true);
            _codex = Column(panel, ctx, "ChatGPT", first: false);
            view = panel;
        }
        else
        {
            var grid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(icon * 0.2, 0, icon * 0.2, 0),
                IsHitTestVisible = false,
            };
            for (int c = 0; c < 5; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            _claude = GridRow(grid, 0, ctx, "Claude", AppConstants.AiUsage.ClaudeAppId);
            _codex = GridRow(grid, 1, ctx, "ChatGPT", AppConstants.AiUsage.CodexAppId);
            view = grid;
        }
        _lease ??= AiUsageTracker.Acquire();
        _sub = ctx.Scheduler.Every(TimeSpan.FromMilliseconds(AppConstants.AiUsage.WidgetRefreshMs), () => AiUsageTracker.Shared.Refresh(), Apply);
        return view;
    }

    private static Row GridRow(Grid grid, int row, WidgetContext ctx, string name, string appId)
    {
        double icon = ctx.Metrics.IconSize;
        var fg = new SolidColorBrush(ctx.Look.Foreground);
        double gap = icon * AppConstants.AiUsage.GapIcons;
        double top = row == 0 ? 0 : icon * AppConstants.AiUsage.RowGapIcons;
        double text = icon * AppConstants.AiUsage.TextIcons;

        double badge = icon * AppConstants.AiUsage.BadgeIcons;
        var image = new Image { Width = badge, Height = badge };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var letter = MetricView.Text(fg, badge * 0.7, FontWeights.Bold, 0.9);
        letter.Text = name[..1];
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        letter.VerticalAlignment = VerticalAlignment.Center;
        var badgeHost = new Grid { Width = badge, Height = badge, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top, 0, 0) };
        badgeHost.Children.Add(letter);
        badgeHost.Children.Add(image);
        LoadIcon(ctx, appId, badge, image, letter);
        Place(grid, badgeHost, row, 0);

        var label = MetricView.Text(fg, text, FontWeights.SemiBold, 1);
        label.Text = name;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(gap, top, gap, 0);
        Place(grid, label, row, 1);

        double barLength = icon * AppConstants.AiUsage.BarIcons, barThick = icon * AppConstants.AiUsage.BarThickIcons;
        var fill = new Border { Width = 0, CornerRadius = new CornerRadius(barThick / 2), HorizontalAlignment = HorizontalAlignment.Left };
        var c = ctx.Look.Foreground;
        Place(grid, new Border
        {
            Width = barLength,
            Height = barThick,
            CornerRadius = new CornerRadius(barThick / 2),
            Background = new SolidColorBrush(Color.FromArgb(AppConstants.AiUsage.TrackAlpha, c.R, c.G, c.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, top, gap, 0),
            Child = fill,
        }, row, 2);

        var time = MetricView.Text(fg, text, FontWeights.Normal, 0.65);
        time.TextAlignment = TextAlignment.Right;
        time.MinWidth = MetricView.Measure("4h 59m", time);
        time.VerticalAlignment = VerticalAlignment.Center;
        time.Margin = new Thickness(0, top, gap, 0);
        Place(grid, time, row, 3);

        var value = MetricView.Text(fg, icon * AppConstants.AiUsage.ValueIcons, FontWeights.Bold, 1);
        value.TextAlignment = TextAlignment.Right;
        // Fixed width so a changing number does not resize the dock.
        value.MinWidth = Math.Max(MetricView.Measure("100%", value), MetricView.Measure("99.9M", value));
        value.VerticalAlignment = VerticalAlignment.Center;
        value.Margin = new Thickness(0, top, 0, 0);
        value.Text = "–";
        Place(grid, value, row, 4);

        return new Row { Value = value, Time = time, Fill = fill, BarLength = barLength };
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// <summary>The installed app's own icon; the letter underneath stays when the app is not installed.</summary>
    private static async void LoadIcon(WidgetContext ctx, string appId, double sizeDip, Image image, TextBlock letter)
    {
        try
        {
            var source = await ctx.Icons.GetAsync(@"shell:AppsFolder\" + appId, IconProvider.SnapSize(sizeDip * ctx.Scale));
            if (source == null) return;
            image.Source = source;
            letter.Visibility = Visibility.Collapsed;
        }
        catch (InvalidOperationException ex) { Log.Debug($"AI widget icon for {appId} unavailable: {ex.Message}"); }
    }

    private static Row Column(Panel parent, WidgetContext ctx, string caption, bool first)
    {
        double icon = ctx.Metrics.IconSize;
        var fg = new SolidColorBrush(ctx.Look.Foreground);
        var value = MetricView.Text(fg, icon * 0.32, FontWeights.SemiBold, 1);
        value.Text = "–";
        var name = MetricView.Text(fg, icon * 0.24, FontWeights.Normal, 0.72);
        name.Text = caption;
        value.TextAlignment = name.TextAlignment = TextAlignment.Center;
        var column = new StackPanel();
        column.Children.Add(value);
        column.Children.Add(name);
        if (!first) column.Margin = new Thickness(0, icon * 0.12, 0, 0);
        parent.Children.Add(column);
        return new Row { Value = value };
    }

    private void Apply()
    {
        var s = AiUsageTracker.Shared.Snapshot;
        Show(_claude, s.ClaudeSession, AiUsageFormat.Tokens(s.ClaudeTokensSession));
        Show(_codex, s.CodexSession, "–");
    }

    private static void Show(Row row, LimitWindow? window, string unknown)
    {
        row.Value.Text = window != null ? AiUsageFormat.Percent(window) : unknown;
        if (row.Time != null) row.Time.Text = window != null ? AiUsageFormat.Remaining(window) : "";
        if (row.Fill == null) return;
        double used = Math.Clamp(window?.Current ?? 0, 0, 100);
        row.Fill.Width = row.BarLength * used / 100;
        row.Fill.Background = new SolidColorBrush(AiUsageFormat.LevelColor(used));
    }

    // A click opens the full usage flyout (AppHost.ToggleWidgetFlyout), which the dock wires up.

    public IEnumerable<(string Header, Action Action)> MenuItems =>
    [
        ("지금 새로 고침", RefreshNow),
        ("Claude 사용량 페이지 열기", () => AppLauncher.Execute(AppConstants.AiUsage.ClaudeUsagePage, null, null, null)),
        ("ChatGPT 사용량 페이지 열기", () => AppLauncher.Execute(AppConstants.AiUsage.CodexUsagePage, null, null, null)),
    ];

    private void RefreshNow()
    {
        var dispatcher = _claude.Value.Dispatcher;
        Task.Run(() =>
        {
            AiUsageTracker.Shared.Refresh(force: true);
            dispatcher.BeginInvoke(Apply);
        }).ContinueWith(t => Log.Warn("AI usage refresh failed", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose()
    {
        _sub?.Dispose();
        _lease?.Dispose();
    }
}

/// <summary>Text and colours shared by the AI usage widget and its flyout.</summary>
public static class AiUsageFormat
{
    public static string Percent(LimitWindow w) => $"{Math.Round(w.Current):0}%";

    public static Color LevelColor(double used) =>
        used >= AppConstants.AiUsage.DangerPercent ? AppConstants.AiUsage.DangerColor
        : used >= AppConstants.AiUsage.WarnPercent ? AppConstants.AiUsage.WarnColor : AppConstants.AiUsage.OkColor;

    /// <summary>Time left until the window starts over, "2h 14m" style (short enough for a dock row).</summary>
    public static string Remaining(LimitWindow w)
    {
        if (Left(w) is not { } left) return "";
        return left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h"
            : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m"
            : $"{Math.Max(1, left.Minutes)}m";
    }

    /// <summary>Two-colour sweep for gauges, in the level's hue (green, amber, red).</summary>
    public static (Color From, Color To) LevelGradient(double used) =>
        used >= AppConstants.AiUsage.DangerPercent ? (AppConstants.AiUsage.DangerColor, AppConstants.AiUsage.DangerColor2)
        : used >= AppConstants.AiUsage.WarnPercent ? (AppConstants.AiUsage.WarnColor, AppConstants.AiUsage.WarnColor2)
        : (AppConstants.AiUsage.OkColor, AppConstants.AiUsage.OkColor2);

    /// <summary>"4시간 41분 후" for the flyout; "초기화됨" once past; null when the reset time is unknown.</summary>
    public static string? LeftText(LimitWindow w)
    {
        if (w.ResetsAt == null) return null;
        if (Left(w) is not { } left) return "초기화됨";
        return left.TotalDays >= 1 ? $"{(int)left.TotalDays}일 {left.Hours}시간 후"
            : left.TotalHours >= 1 ? $"{(int)left.TotalHours}시간 {left.Minutes}분 후"
            : $"{Math.Max(1, left.Minutes)}분 후";
    }

    /// <summary>"19:20 초기화" / "11/20 02:00 초기화"; null when unknown or already past.</summary>
    public static string? ResetAtText(LimitWindow w) => w.ResetsAt is { } r && !w.Expired ? $"{When(r)} 초기화" : null;

    public static string Tokens(long n) => n switch
    {
        < 1000 => n.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => $"{n / 1000.0:0.#}K",
        < 1_000_000_000 => $"{n / 1_000_000.0:0.#}M",
        _ => $"{n / 1_000_000_000.0:0.#}B",
    };

    private static TimeSpan? Left(LimitWindow w) =>
        w.ResetsAt is { } r && !w.Expired ? r - DateTimeOffset.Now : null;

    private static string When(DateTimeOffset r)
    {
        var local = r.ToLocalTime();
        return local.Date == DateTime.Today ? $"{local:HH:mm}" : $"{local:M/d HH:mm}";
    }
}
