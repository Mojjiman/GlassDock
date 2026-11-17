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
/// When only one of them has been used on this PC, its two rows are that one's 5-hour and weekly limits.
/// A vertical dock has no room for rows and shows two small columns of the numbers only.
/// </summary>
public sealed class AiUsageWidget : IWidget
{
    private IDisposable? _sub, _lease;
    private Row _first = null!, _second = null!;
    private WidgetContext _ctx = null!;
    private readonly Border _host = new();
    private AiServices _mode;

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
        _ctx = ctx;
        // Checked here as well as in the tracker's pass, so the first view already has the right rows.
        Build(Mode(AiUsageTracker.Detect()));
        _lease ??= AiUsageTracker.Acquire();
        _sub = ctx.Scheduler.Every(TimeSpan.FromMilliseconds(AppConstants.AiUsage.WidgetRefreshMs), () => AiUsageTracker.Shared.Refresh(), Apply);
        return _host;
    }

    /// <summary>Exactly one tool used: that one. Both, or neither yet: a row for each.</summary>
    private static AiServices Mode(AiServices found) => found is AiServices.Claude or AiServices.Codex ? found : AiServices.Both;

    private static string NameOf(AiServices s) => s == AiServices.Codex ? "ChatGPT" : "Claude";
    private static string AppIdOf(AiServices s) => s == AiServices.Codex ? AppConstants.AiUsage.CodexAppId : AppConstants.AiUsage.ClaudeAppId;

    private void Build(AiServices mode)
    {
        _mode = mode;
        var ctx = _ctx;
        double icon = ctx.Metrics.IconSize;
        // Both: one row per tool. One tool: its 5-hour and weekly limits, both rows with its icon.
        bool both = mode == AiServices.Both;
        var first = both ? ("Claude", "Claude", AppConstants.AiUsage.ClaudeAppId) : ("5시간", NameOf(mode), AppIdOf(mode));
        var second = both ? ("ChatGPT", "ChatGPT", AppConstants.AiUsage.CodexAppId) : ("주간", NameOf(mode), AppIdOf(mode));
        if (ctx.Metrics.Vertical)
        {
            var panel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, icon * 0.14, 0, icon * 0.14),
                IsHitTestVisible = false,
            };
            _first = Column(panel, ctx, first.Item1, first: true);
            _second = Column(panel, ctx, second.Item1, first: false);
            _host.Child = panel;
            return;
        }
        var grid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(icon * 0.2, 0, icon * 0.2, 0),
            IsHitTestVisible = false,
        };
        for (int c = 0; c < 5; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        _first = GridRow(grid, 0, ctx, first.Item1, first.Item2, first.Item3);
        _second = GridRow(grid, 1, ctx, second.Item1, second.Item2, second.Item3);
        _host.Child = grid;
    }

    /// <param name="caption">The row's text: the tool's name, or 5시간/주간 when the widget shows one tool.</param>
    /// <param name="name">The tool, for the letter shown while its app icon is missing.</param>
    private static Row GridRow(Grid grid, int row, WidgetContext ctx, string caption, string name, string appId)
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
        label.Text = caption;
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
        // Someone starts using the other tool (or stops): switch layouts in place; the dock follows the size.
        if (Mode(s.Services) != _mode) Build(Mode(s.Services));
        switch (_mode)
        {
            case AiServices.Claude:
                Show(_first, s.ClaudeSession, AiUsageFormat.Tokens(s.ClaudeTokensSession));
                Show(_second, s.ClaudeWeek, "–");
                break;
            case AiServices.Codex:
                Show(_first, s.CodexSession, "–");
                Show(_second, s.CodexWeek, "–");
                break;
            default:
                Show(_first, s.ClaudeSession, AiUsageFormat.Tokens(s.ClaudeTokensSession));
                Show(_second, s.CodexSession, "–");
                break;
        }
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

    public IEnumerable<(string Header, Action Action)> MenuItems
    {
        get
        {
            yield return ("지금 새로 고침", RefreshNow);
            if (_mode.HasFlag(AiServices.Claude))
                yield return ("Claude 사용량 페이지 열기", () => AppLauncher.Execute(AppConstants.AiUsage.ClaudeUsagePage, null, null, null));
            if (_mode.HasFlag(AiServices.Codex))
                yield return ("ChatGPT 사용량 페이지 열기", () => AppLauncher.Execute(AppConstants.AiUsage.CodexUsagePage, null, null, null));
        }
    }

    private void RefreshNow()
    {
        var dispatcher = _host.Dispatcher;
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
