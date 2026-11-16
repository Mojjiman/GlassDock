using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Widgets;
using Windows.Storage.Streams;
using F = GlassDock.Config.AppConstants.Flyouts;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using W = GlassDock.Config.AppConstants.Widgets;

namespace GlassDock.UI;

/// <summary>
/// Opened from the now-playing widget: large album art, the track, a timeline that seeks, the transport
/// buttons, and the playing app's own volume (its slider in the Windows volume mixer).
/// </summary>
public sealed class MediaFlyout : FlyoutWindow
{
    private readonly MediaWidget _media;
    private readonly AppsModel _apps;
    private readonly IconProvider _icons;
    private readonly Border _art;
    private readonly TextBlock _artGlyph, _title, _artist, _elapsed, _total;
    private readonly PillSlider _seek;
    private readonly FrameworkElement _timeline;
    private readonly Button _prev, _playPause, _next;
    private readonly FrameworkElement _appRow;
    private readonly Image _appIcon;
    private readonly TextBlock _appName, _appPercent, _appNote;
    private readonly PillSlider _appSlider;
    private readonly DispatcherTimer _timelineTick = new() { Interval = TimeSpan.FromMilliseconds(W.MediaTimelineMs) };
    private readonly DispatcherTimer _audioTick = new() { Interval = TimeSpan.FromMilliseconds(W.AppVolumeRefreshMs) };
    private Session? _session;
    private IRandomAccessStreamReference? _shownThumbnail;
    private int _artVersion;
    private AppAudio? _audio;

    public MediaFlyout(DockWindow dock, MediaWidget media, AppsModel apps, IconProvider icons) : base(dock)
    {
        _media = media;
        _apps = apps;
        _icons = icons;
        var muted = ForegroundAlpha(F.MutedTextAlpha);
        var faint = ForegroundAlpha(F.FaintTextAlpha);
        var track = ForegroundAlpha(F.SliderTrackAlpha);

        _artGlyph = new TextBlock
        {
            Text = WidgetGlyphs.MusicNote,
            FontFamily = IconFont,
            FontSize = W.MediaFlyoutArtDip / 4,
            Foreground = faint,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _art = new Border
        {
            Width = W.MediaFlyoutArtDip,
            Height = W.MediaFlyoutArtDip,
            CornerRadius = new CornerRadius(12),
            Background = ForegroundAlpha(F.FilledButtonAlpha),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _artGlyph,
        };

        _title = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 14, 0, 0) };
        _artist = new TextBlock { FontSize = 13, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };

        _seek = new PillSlider(W.MediaSeekHitDip, W.MediaSeekBarDip, track, Foreground, 0);
        _seek.ValueChanged += _ => UpdateTimeline();
        _seek.Released += Seek;
        _elapsed = new TextBlock { FontSize = 11, Foreground = faint };
        _total = new TextBlock { FontSize = 11, Foreground = faint, HorizontalAlignment = HorizontalAlignment.Right };
        var times = new Grid();
        times.Children.Add(_elapsed);
        times.Children.Add(_total);
        var timeline = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        timeline.Children.Add(_seek);
        timeline.Children.Add(times);
        _timeline = timeline;

        _prev = IconButton(WidgetGlyphs.Previous, "이전 곡", () => Command(s => s.TrySkipPreviousAsync().AsTask(), "previous"), W.MediaSkipButtonDip, W.MediaSkipGlyphSize);
        _playPause = IconButton(WidgetGlyphs.Play, "재생/일시 정지", () => Command(s => s.TryTogglePlayPauseAsync().AsTask(), "play/pause"), W.MediaPlayButtonDip, W.MediaPlayGlyphSize, filled: true);
        _next = IconButton(WidgetGlyphs.Next, "다음 곡", () => Command(s => s.TrySkipNextAsync().AsTask(), "next"), W.MediaSkipButtonDip, W.MediaSkipGlyphSize);
        _playPause.Margin = new Thickness(18, 0, 18, 0);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        controls.Children.Add(_prev);
        controls.Children.Add(_playPause);
        controls.Children.Add(_next);

        _appIcon = new Image { Width = W.AppVolumeIconDip, Height = W.AppVolumeIconDip, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _appPercent = new TextBlock { FontSize = 12, Foreground = muted, MinWidth = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _appSlider = new PillSlider(W.AppVolumeHitDip, W.AppVolumeBarDip, track, new SolidColorBrush(Look.Accent), W.VolumeWheelStep);
        _appSlider.ValueChanged += v =>
        {
            _audio?.SetLevel((float)v);
            ShowAppPercent(v);
        };
        DockPanel.SetDock(_appIcon, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(_appPercent, System.Windows.Controls.Dock.Right);
        var sliderRow = new DockPanel();
        sliderRow.Children.Add(_appIcon);
        sliderRow.Children.Add(_appPercent);
        sliderRow.Children.Add(_appSlider);
        _appName = new TextBlock { FontSize = 12, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) };
        _appNote = new TextBlock
        {
            Text = "웹 앱은 브라우저 안에서 소리가 나서, 브라우저 전체 소리가 함께 바뀌어요.",
            FontSize = 11,
            Foreground = faint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var appBox = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        appBox.Children.Add(_appName);
        appBox.Children.Add(sliderRow);
        appBox.Children.Add(_appNote);
        _appRow = appBox;

        var root = new StackPanel { Margin = new Thickness(16, 16, 16, 14), Width = W.MediaFlyoutArtDip };
        root.Children.Add(_art);
        root.Children.Add(_title);
        root.Children.Add(_artist);
        root.Children.Add(_timeline);
        root.Children.Add(controls);
        root.Children.Add(_appRow);
        Content = Tinted(root);

        Sync();
        _media.Changed += Sync;
        _timelineTick.Tick += (_, _) => UpdateTimeline();
        _audioTick.Tick += (_, _) => RefreshAudio();
        Loaded += (_, _) => { _timelineTick.Start(); _audioTick.Start(); };
        Closed += (_, _) =>
        {
            _media.Changed -= Sync;
            _timelineTick.Stop();
            _audioTick.Stop();
        };
    }

    /// <summary>Follows the widget: a new track, play state or app; closes when nothing is playing any more.</summary>
    private void Sync()
    {
        var s = _media.Current;
        if (s == null) { SafeClose(); return; }
        _title.Text = string.IsNullOrEmpty(_media.Title) ? "알 수 없는 제목" : _media.Title;
        _title.ToolTip = _title.Text;
        _artist.Text = _media.Artist;
        SetGlyph(_playPause, _media.Playing ? WidgetGlyphs.Pause : WidgetGlyphs.Play);
        var controls = Read(() => s.GetPlaybackInfo()?.Controls, "playback controls");
        _prev.IsEnabled = controls?.IsPreviousEnabled ?? true;
        _next.IsEnabled = controls?.IsNextEnabled ?? true;
        _seek.IsHitTestVisible = controls?.IsPlaybackPositionEnabled ?? false;
        if (s != _session)
        {
            Log.Info($"Now playing from {s.SourceAppUserModelId}");
            _session = s;
            _audio = null;
            RefreshAudio();
        }
        if (_media.Thumbnail != _shownThumbnail)
        {
            _shownThumbnail = _media.Thumbnail;
            _ = ShowArtAsync(_media.Thumbnail);
        }
        UpdateTimeline();
    }

    private async Task ShowArtAsync(IRandomAccessStreamReference? thumbnail)
    {
        int version = ++_artVersion;
        ImageSource? art = null;
        if (thumbnail != null)
        {
            try { art = await MediaWidget.LoadArtAsync(thumbnail, W.MediaFlyoutArtPx); }
            catch (Exception ex) { Log.Warn("Could not load album art", ex); }
        }
        if (version != _artVersion) return;
        _art.Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : ForegroundAlpha(F.FilledButtonAlpha);
        _artGlyph.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The app reports its position now and then; between reports it is extrapolated from the report time.</summary>
    private (TimeSpan Position, TimeSpan Length)? Timeline()
    {
        var s = _session;
        var t = s == null ? null : Read(s.GetTimelineProperties, "timeline");
        if (t == null) return null;
        var length = t.EndTime - t.StartTime;
        if (length <= TimeSpan.Zero) return null;
        var position = t.Position - t.StartTime;
        if (_media.Playing)
        {
            double rate = Read(() => s!.GetPlaybackInfo()?.PlaybackRate, "playback rate") ?? 1;
            position += TimeSpan.FromTicks((long)((DateTimeOffset.Now - t.LastUpdatedTime).Ticks * rate));
        }
        return (TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, length.Ticks)), length);
    }

    private void UpdateTimeline()
    {
        if (Timeline() is not { } t) { _timeline.Visibility = Visibility.Collapsed; return; }
        _timeline.Visibility = Visibility.Visible;
        // While dragging, the bar shows where the drop will seek to.
        if (!_seek.Dragging) _seek.Value = t.Position / t.Length;
        _elapsed.Text = Format(_seek.Dragging ? t.Length * _seek.Value : t.Position);
        _total.Text = Format(t.Length);
    }

    private void Seek(double fraction)
    {
        var s = _session;
        var t = s == null ? null : Read(s.GetTimelineProperties, "timeline");
        if (s == null || t == null) return;
        long ticks = (t.StartTime + (t.EndTime - t.StartTime) * fraction).Ticks;
        _ = MediaWidget.Run(() => s.TryChangePlaybackPositionAsync(ticks).AsTask(), "seek");
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void Command(Func<Session, Task<bool>> command, string what)
    {
        if (_session is { } s) _ = MediaWidget.Run(() => command(s), what);
    }

    /// <summary>
    /// The playing app's mixer slider. Its audio session may only exist once sound starts, and goes away
    /// when the app restarts, so a missing one is looked up again on the next tick.
    /// </summary>
    private void RefreshAudio()
    {
        var s = _session;
        if (s == null) return;
        if (_audio == null)
        {
            string id = s.SourceAppUserModelId;
            _audio = AppAudio.Find(id, _apps.ExePathForAumid(id));
            if (_audio == null) { _appRow.Visibility = Visibility.Collapsed; return; }
            _appName.Text = $"{AppName(id, _audio.ExePath)} 볼륨";
            _appNote.Visibility = _audio.Hosted ? Visibility.Visible : Visibility.Collapsed;
            _ = ShowAppIconAsync(id, _audio.ExePath);
            _appRow.Visibility = Visibility.Visible;
        }
        if (_appSlider.Dragging) return;
        if (_audio.Level is not { } level) { _audio = null; return; }
        _appSlider.Value = level;
        ShowAppPercent(level);
    }

    private void ShowAppPercent(double level) => _appPercent.Text = $"{(int)Math.Round(level * 100)}%";

    private static string AppName(string id, string? exePath) =>
        AppNames.ForAumid(id)
        ?? (exePath != null && FileVersionInfo.GetVersionInfo(exePath).FileDescription is { Length: > 0 } d ? d : null)
        ?? (exePath != null ? Path.GetFileNameWithoutExtension(exePath) : id);

    private async Task ShowAppIconAsync(string id, string? exePath)
    {
        string? source = AppNames.ForAumid(id) != null ? @"shell:AppsFolder\" + id : exePath;
        if (source == null) return;
        _appIcon.Source = await _icons.GetAsync(source, IconProvider.SnapSize(W.AppVolumeIconDip * 2));
    }

    /// <summary>A session whose app has just closed throws from its getters; the widget moves on to the next one.</summary>
    private static T? Read<T>(Func<T?> get, string what)
    {
        try { return get(); }
        catch (Exception ex)
        {
            Log.Debug($"Media {what} unavailable: {ex.Message}");
            return default;
        }
    }
}
