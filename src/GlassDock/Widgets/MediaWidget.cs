using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Logging;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace GlassDock.Widgets;

/// <summary>
/// Now playing, from the system media sessions (the same source as the Windows media flyout).
/// Event-driven; the art, title and controls follow the current session.
/// </summary>
public sealed class MediaWidget : IWidget
{
    private Dispatcher _dispatcher = null!;
    private WidgetContext _ctx = null!;
    private SessionManager? _manager;
    private Session? _session;
    private Border _art = null!;
    private TextBlock _artGlyph = null!, _title = null!, _artist = null!, _playPause = null!;
    private StackPanel _controls = null!;
    private string _titleText = "", _artistText = "";
    private bool _playing, _disposed;

    /// <summary>Raised on the UI thread after the session, its track or its play state changed.</summary>
    public event Action? Changed;
    public Session? Current => _session;
    public string Title => _titleText;
    public string Artist => _artistText;
    public bool Playing => _playing;
    /// <summary>The current track's art; a new reference each time the app updates the track.</summary>
    public Windows.Storage.Streams.IRandomAccessStreamReference? Thumbnail { get; private set; }

    public string Label => _session == null ? "재생 중인 미디어 없음"
        : string.IsNullOrEmpty(_artistText) ? _titleText : $"{_titleText} — {_artistText}";

    public FrameworkElement CreateView(WidgetContext ctx)
    {
        _ctx = ctx;
        _dispatcher = Dispatcher.CurrentDispatcher;
        double icon = ctx.Metrics.IconSize;
        var fg = new SolidColorBrush(ctx.Look.Foreground);

        _art = new Border
        {
            Width = icon * 0.86,
            Height = icon * 0.86,
            CornerRadius = new CornerRadius(icon * 0.16),
            Background = new SolidColorBrush(Color.FromArgb(0x26, ctx.Look.Foreground.R, ctx.Look.Foreground.G, ctx.Look.Foreground.B)),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _artGlyph = new TextBlock
        {
            Text = WidgetGlyphs.MusicNote,
            FontFamily = WidgetFonts.Icons,
            FontSize = icon * 0.38,
            Foreground = fg,
            Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _art.Child = _artGlyph;

        _title = MetricView.Text(fg, icon * 0.29, FontWeights.SemiBold, 1);
        _artist = MetricView.Text(fg, icon * 0.24, FontWeights.Normal, 0.72);
        var text = new StackPanel
        {
            Width = icon * AppConstants.Widgets.MediaTextWidthIcons,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(icon * 0.18, 0, icon * 0.1, 0),
            IsHitTestVisible = false,
        };
        text.Children.Add(_title);
        text.Children.Add(_artist);

        // A vertical dock is one tile wide: art over the controls, and the title only in the hover label.
        bool vertical = ctx.Metrics.Vertical;
        if (vertical) { text.Visibility = Visibility.Collapsed; _art.HorizontalAlignment = HorizontalAlignment.Center; }
        _controls = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = vertical ? new Thickness(0, icon * 0.08, 0, 0) : new Thickness(0),
        };
        _controls.Children.Add(Control(WidgetGlyphs.Previous, icon, fg, s => s.TrySkipPreviousAsync().AsTask()));
        var pp = Control(WidgetGlyphs.Play, icon, fg, s => s.TryTogglePlayPauseAsync().AsTask());
        _playPause = (TextBlock)pp.Child;
        _controls.Children.Add(pp);
        _controls.Children.Add(Control(WidgetGlyphs.Next, icon, fg, s => s.TrySkipNextAsync().AsTask()));

        var row = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Margin = vertical ? new Thickness(0, icon * 0.14, 0, icon * 0.14) : new Thickness(icon * 0.12, 0, icon * 0.12, 0),
        };
        row.Children.Add(_art);
        row.Children.Add(text);
        row.Children.Add(_controls);
        ShowNothing();
        _ = InitAsync();
        return row;
    }

    /// <summary>A small glyph button inside the tile; it handles its own press so the tile does not also click.</summary>
    private Border Control(string glyph, double icon, Brush fg, Func<Session, Task<bool>> command)
    {
        var b = new Border
        {
            Width = icon * 0.62,
            Height = icon * 0.62,
            CornerRadius = new CornerRadius(icon * 0.31),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = WidgetFonts.Icons,
                FontSize = icon * 0.3,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var hover = new SolidColorBrush(Color.FromArgb(0x30, _ctx.Look.Foreground.R, _ctx.Look.Foreground.G, _ctx.Look.Foreground.B));
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_session is { } s) _ = Run(() => command(s), "media command");
        };
        return b;
    }

    // A click opens the now-playing flyout (AppHost.ToggleWidgetFlyout), which the dock wires up.

    private void TogglePlay()
    {
        if (_session is { } s) _ = Run(() => s.TryTogglePlayPauseAsync().AsTask(), "play/pause");
    }

    public IEnumerable<(string Header, Action Action)> MenuItems => _session == null ? [] :
    [
        (_playing ? "일시 정지" : "재생", TogglePlay),
        ("다음 곡", () => { if (_session is { } s) _ = Run(() => s.TrySkipNextAsync().AsTask(), "next"); }),
        ("이전 곡", () => { if (_session is { } s) _ = Run(() => s.TrySkipPreviousAsync().AsTask(), "previous"); }),
    ];

    private async Task InitAsync()
    {
        try
        {
            var manager = await SessionManager.RequestAsync();
            if (_disposed) return;
            _manager = manager;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            await AttachAsync(_manager.GetCurrentSession());
        }
        catch (Exception ex) { Log.Warn("Media sessions unavailable", ex); }
    }

    // WinRT events arrive on worker threads; everything below runs on the dispatcher.
    private void OnCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args) =>
        _dispatcher.BeginInvoke(async () => { if (!_disposed) await AttachAsync(sender.GetCurrentSession()); });

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        _dispatcher.BeginInvoke(async () => { if (!_disposed && sender == _session) await RefreshAsync(); });

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) =>
        _dispatcher.BeginInvoke(() => { if (!_disposed && sender == _session) ShowPlayback(); });

    private async Task AttachAsync(Session? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        _session = session;
        if (session == null) { ShowNothing(); return; }
        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        await RefreshAsync();
    }

    // Newer refreshes win: two for the same session (title, then art changed) can finish out of order.
    private int _refreshGeneration;

    private const int ErrorNotReady = unchecked((int)0x80070015);

    private async Task RefreshAsync(int attempt = 0)
    {
        var session = _session;
        if (session == null) return;
        int generation = ++_refreshGeneration;
        bool Stale() => _disposed || session != _session || generation != _refreshGeneration;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (Stale()) return;
            _titleText = props.Title ?? "";
            _artistText = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            Thumbnail = props.Thumbnail;
            _title.Text = string.IsNullOrEmpty(_titleText) ? "알 수 없는 제목" : _titleText;
            _artist.Text = _artistText;
            _controls.Visibility = Visibility.Visible;
            ShowPlayback();
            var art = props.Thumbnail == null ? null : await LoadArtAsync(props.Thumbnail, AppConstants.Widgets.MediaArtPx);
            if (Stale()) return;
            ShowArt(art);
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == ErrorNotReady && attempt < AppConstants.Widgets.MediaReadRetries)
        {
            // Spotify answers "not ready" while it switches tracks; the old title and art stayed until the next event.
            Log.Debug($"Media properties not ready (attempt {attempt + 1}); retrying");
            await Task.Delay(AppConstants.Widgets.MediaReadRetryMs);
            if (!Stale()) await RefreshAsync(attempt + 1);
        }
        catch (Exception ex) { Log.Warn("Could not read media properties", ex); }
    }

    private void ShowPlayback()
    {
        var status = _session?.GetPlaybackInfo()?.PlaybackStatus;
        _playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        _playPause.Text = _playing ? WidgetGlyphs.Pause : WidgetGlyphs.Play;
        Changed?.Invoke();
    }

    private void ShowNothing()
    {
        _titleText = _artistText = "";
        Thumbnail = null;
        _title.Text = "재생 중 아님";
        _artist.Text = "미디어";
        _controls.Visibility = Visibility.Collapsed;
        ShowArt(null);
        Changed?.Invoke();
    }

    private void ShowArt(ImageSource? art)
    {
        _art.Background = art != null
            ? new ImageBrush(art) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromArgb(0x26, _ctx.Look.Foreground.R, _ctx.Look.Foreground.G, _ctx.Look.Foreground.B));
        _artGlyph.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static async Task<ImageSource?> LoadArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference, int px)
    {
        using var stream = await reference.OpenReadAsync();
        using var source = stream.AsStreamForRead();
        var buffer = new MemoryStream();
        await source.CopyToAsync(buffer);
        buffer.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = px;
        bmp.StreamSource = buffer;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    internal static async Task Run(Func<Task<bool>> command, string what)
    {
        try
        {
            if (!await command()) Log.Debug($"Media {what} was refused by the app");
        }
        catch (Exception ex) { Log.Warn($"Media {what} failed", ex); }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_manager != null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        _manager = null;
        _session = null;
    }
}
