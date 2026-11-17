using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Widgets;

namespace GlassDock.UI;

/// <summary>
/// Full AI usage, opened from the AI usage widget: a glass card per service with a ring gauge for every
/// limit window it reports (Claude: 5 hours and weekly; ChatGPT/Codex: 5 hours and weekly when the plan
/// has one), the time left and the reset time under each. Built once, then updated in place while open so
/// the rings sweep to new values instead of redrawing.
/// </summary>
public sealed class AiUsageFlyout : FlyoutWindow
{
    private const double CardWidth = 344;
    private static readonly FontFamily NumberFont = new("Segoe UI Variable Display, Segoe UI");
    private const string RefreshGlyph = "";
    private const string OpenGlyph = "";

    private readonly IconProvider _icons;
    private readonly StackPanel _cards = new();
    private readonly TextBlock _updated;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(AppConstants.AiUsage.FlyoutRefreshMs) };
    private readonly Color _fg;
    private readonly Brush _muted, _faint;
    private string? _shape;
    private readonly List<Action<AiUsageSnapshot, bool>> _updates = [];

    public AiUsageFlyout(DockWindow dock, IconProvider icons) : base(dock)
    {
        _icons = icons;
        var lease = AiUsageTracker.Acquire();
        Closed += (_, _) => lease.Dispose();
        _fg = Look.IsDark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
        _muted = Alpha(0xA8);
        _faint = Alpha(0x70);

        var title = new TextBlock { Text = "AI 사용량", FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = NumberFont };
        _updated = new TextBlock { FontSize = 11, Foreground = _faint, Margin = new Thickness(0, 1, 0, 0) };
        var titles = new StackPanel();
        titles.Children.Add(title);
        titles.Children.Add(_updated);
        var refresh = IconButton(RefreshGlyph, "새로 고침", () => Refresh(force: true));
        DockPanel.SetDock(refresh, System.Windows.Controls.Dock.Right);
        var header = new DockPanel { Margin = new Thickness(6, 2, 2, 12) };
        header.Children.Add(refresh);
        header.Children.Add(titles);

        var root = new StackPanel { Margin = new Thickness(14, 12, 14, 14), Width = CardWidth };
        root.Children.Add(header);
        root.Children.Add(_cards);
        Content = Tinted(root);

        _tick.Tick += (_, _) => Render();
        Loaded += (_, _) => { Render(); _tick.Start(); Refresh(force: false); };
        Closed += (_, _) => _tick.Stop();
    }

    private SolidColorBrush Alpha(byte a) => new(Color.FromArgb(a, _fg.R, _fg.G, _fg.B));

    /// <summary>Asks the tracker for fresh numbers off the UI thread, then redraws.</summary>
    private void Refresh(bool force)
    {
        Task.Run(() => AiUsageTracker.Shared.Refresh(force))
            .ContinueWith(t =>
            {
                if (t.IsFaulted) Log.Warn("AI usage refresh failed", t.Exception);
                else Dispatcher.BeginInvoke(Render);
            }, TaskScheduler.Default);
    }

    private void Render()
    {
        var s = AiUsageTracker.Shared.Snapshot;
        _updated.Text = $"{DateTime.Now:HH:mm} 기준";
        // Rebuild only when which windows exist changes; otherwise update in place.
        string shape = $"{s.Services}|{s.ClaudeSession != null}{s.ClaudeWeek != null}{s.ClaudeNote}|{s.CodexSession != null}{s.CodexWeek != null}";
        bool fresh = shape != _shape;
        if (fresh)
        {
            _shape = shape;
            _cards.Children.Clear();
            _updates.Clear();
            Build(s);
        }
        foreach (var update in _updates) update(s, fresh);
    }

    private void Build(AiUsageSnapshot s)
    {
        // Someone who uses only one of the two gets only its card; neither found yet shows both, as hints.
        bool showClaude = s.Services != AiServices.Codex, showCodex = s.Services != AiServices.Claude;
        if (showClaude) BuildClaude(s);
        if (showCodex) BuildCodex(s);
    }

    private void BuildClaude(AiUsageSnapshot s)
    {
        var claude = Card("Claude", AppConstants.AiUsage.ClaudeAppId, AppConstants.AiUsage.ClaudeUsagePage);
        Gauges(claude, s, ("5시간", x => x.ClaudeSession), ("주간", x => x.ClaudeWeek));
        if (s.ClaudeNote != null) claude.Children.Add(Note(s.ClaudeNote));
        claude.Children.Add(Chip(x => $"Claude Code 토큰   5시간 {AiUsageFormat.Tokens(x.ClaudeTokensSession)}   ·   오늘 {AiUsageFormat.Tokens(x.ClaudeTokensToday)}"));
    }

    private void BuildCodex(AiUsageSnapshot s)
    {
        var gpt = Card("ChatGPT", AppConstants.AiUsage.CodexAppId, AppConstants.AiUsage.CodexUsagePage);
        Gauges(gpt, s, ("5시간", x => x.CodexSession), ("주간", x => x.CodexWeek));
        gpt.Children.Add(Chip(x => x.CodexSeen is { } seen ? $"Codex 기준   ·   마지막 사용 {seen.ToLocalTime():M/d HH:mm}" : "Codex 사용 기록이 없습니다"));
    }

    /// <summary>A glass card with the service's icon and name and a button to its usage page; returns the body.</summary>
    private StackPanel Card(string name, string appId, string page)
    {
        var icon = new Image { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        LoadIcon(appId, icon);
        var label = new TextBlock { Text = name, FontSize = 15.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var open = IconButton(OpenGlyph, $"{name} 사용량 페이지", () => { SafeClose(); AppLauncher.Execute(page, null, null, null); });
        DockPanel.SetDock(open, System.Windows.Controls.Dock.Right);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(open);
        head.Children.Add(icon);
        head.Children.Add(label);

        var body = new StackPanel();
        body.Children.Add(head);
        // A faint top highlight over a darker base, like the dock's own glass.
        _cards.Children.Add(new Border
        {
            Background = new LinearGradientBrush(Color.FromArgb(Look.IsDark ? (byte)0x1C : (byte)0x14, _fg.R, _fg.G, _fg.B),
                Color.FromArgb(Look.IsDark ? (byte)0x0A : (byte)0x06, _fg.R, _fg.G, _fg.B), 90),
            BorderBrush = Alpha(Look.IsDark ? (byte)0x1E : (byte)0x14),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12, 14, 14),
            Margin = new Thickness(0, _cards.Children.Count == 0 ? 0 : 10, 0, 0),
            Child = body,
        });
        return body;
    }

    /// <summary>One ring per window the service reports, side by side; a note when it reports none.</summary>
    private void Gauges(StackPanel body, AiUsageSnapshot s, params (string Name, Func<AiUsageSnapshot, LimitWindow?> Get)[] windows)
    {
        var present = windows.Where(w => w.Get(s) != null).ToList();
        if (present.Count == 0)
        {
            body.Children.Add(Note("사용 한도 정보가 아직 없습니다"));
            return;
        }
        var row = new UniformGrid { Columns = present.Count, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (name, get) in present) row.Children.Add(Gauge(name, get));
        body.Children.Add(row);
    }

    private FrameworkElement Gauge(string name, Func<AiUsageSnapshot, LimitWindow?> get)
    {
        double size = AppConstants.AiUsage.RingDip;
        var ring = new RingGauge { Width = size, Height = size, StrokeThickness = AppConstants.AiUsage.RingStrokeDip, Track = Alpha(0x22) };
        var percent = new TextBlock { FontSize = 24, FontWeight = FontWeights.Bold, FontFamily = NumberFont, HorizontalAlignment = HorizontalAlignment.Center };
        Typography.SetNumeralAlignment(percent, FontNumeralAlignment.Tabular);
        var caption = new TextBlock { Text = name, FontSize = 11.5, Foreground = _muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -3, 0, 0) };
        var centre = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        centre.Children.Add(percent);
        centre.Children.Add(caption);
        var dial = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        dial.Children.Add(ring);
        dial.Children.Add(centre);

        var left = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        var at = new TextBlock { FontSize = 11, Foreground = _faint, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
        var cell = new StackPanel();
        cell.Children.Add(dial);
        cell.Children.Add(left);
        cell.Children.Add(at);

        _updates.Add((s, fresh) =>
        {
            if (get(s) is not { } w) return;
            double used = Math.Clamp(w.Current, 0, 100);
            percent.Text = $"{Math.Round(used):0}%";
            var (from, to) = AiUsageFormat.LevelGradient(used);
            ring.Fill = new LinearGradientBrush(from, to, 45);
            if (fresh) ring.Value = 0;
            ring.AnimateTo(used, AppConstants.AiUsage.RingSweepMs);
            left.Text = AiUsageFormat.LeftText(w) ?? "";
            at.Text = AiUsageFormat.ResetAtText(w) ?? "";
            left.Visibility = left.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            at.Visibility = at.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        return cell;
    }

    /// <summary>A small rounded strip at the bottom of a card for secondary facts.</summary>
    private Border Chip(Func<AiUsageSnapshot, string> text)
    {
        var t = new TextBlock { FontSize = 11, Foreground = _muted, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        _updates.Add((s, _) => t.Text = text(s));
        return new Border
        {
            Background = Alpha(0x0E),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 12, 0, 0),
            Child = t,
        };
    }

    private TextBlock Note(string text) =>
        new() { Text = text, FontSize = 11.5, Foreground = _muted, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };

    /// <summary>A round, borderless glyph button that lights up under the pointer.</summary>
    private async void LoadIcon(string appId, Image image)
    {
        try
        {
            double scale = VisualTreeHelper.GetDpi(Dock).DpiScaleX;
            image.Source = await _icons.GetAsync(@"shell:AppsFolder\" + appId, IconProvider.SnapSize(image.Width * scale));
        }
        catch (InvalidOperationException ex) { Log.Debug($"AI usage flyout icon for {appId} unavailable: {ex.Message}"); }
    }
}
