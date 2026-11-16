using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Apps;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Widgets;
using F = GlassDock.Config.AppConstants.Flyouts;
using W = GlassDock.Config.AppConstants.Widgets;

namespace GlassDock.UI;

/// <summary>
/// Opened from the volume widget: the output device's name and level, a mute button, and a thick pill
/// slider. It follows the widget, so keyboard volume keys and the system flyout move it too. Below it,
/// one slider per app that has sound, like the system volume mixer.
/// </summary>
public sealed class VolumeFlyout : FlyoutWindow
{
    private readonly VolumeWidget _volume;
    private readonly IconProvider _icons;
    private readonly Button _mute;
    private readonly TextBlock _name, _percent;
    private readonly PillSlider _slider;
    private readonly StackPanel _apps = new();
    private readonly FrameworkElement _appsBox;
    private readonly List<AppRow> _rows = [];
    private readonly DispatcherTimer _appsTick = new() { Interval = TimeSpan.FromMilliseconds(W.AppVolumeRefreshMs) };

    public VolumeFlyout(DockWindow dock, VolumeWidget volume, IconProvider icons) : base(dock)
    {
        _volume = volume;
        _icons = icons;
        _mute = IconButton(volume.Glyph, "음소거", volume.ToggleMute, W.VolumeButtonDip, W.VolumeButtonGlyphSize, filled: true);
        _percent = new TextBlock { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0) };
        _name = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 0, 0),
        };
        DockPanel.SetDock(_mute, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(_percent, System.Windows.Controls.Dock.Right);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(_mute);
        header.Children.Add(_percent);
        header.Children.Add(_name);

        _slider = new PillSlider(W.VolumeSliderHitDip, W.VolumeSliderBarDip,
            ForegroundAlpha(F.SliderTrackAlpha), new SolidColorBrush(Look.Accent), W.VolumeWheelStep);
        _slider.ValueChanged += v => _volume.SetLevel((float)v);

        var appsBox = new StackPanel { Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
        appsBox.Children.Add(new Border { Height = 1, Background = ForegroundAlpha(F.SliderTrackAlpha), Margin = new Thickness(0, 0, 0, 10) });
        appsBox.Children.Add(new TextBlock { Text = "앱 볼륨", FontSize = 12, Foreground = ForegroundAlpha(F.MutedTextAlpha), Margin = new Thickness(0, 0, 0, 4) });
        appsBox.Children.Add(SlimScrollViewer(_apps, W.MixerMaxHeightDip));
        _appsBox = appsBox;

        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14), Width = W.VolumeFlyoutWidth };
        root.Children.Add(header);
        root.Children.Add(_slider);
        root.Children.Add(_appsBox);
        Content = Tinted(root);

        Update();
        RefreshApps();
        _volume.Changed += Update;
        _appsTick.Tick += (_, _) => RefreshApps();
        Loaded += (_, _) => _appsTick.Start();
        Closed += (_, _) =>
        {
            _volume.Changed -= Update;
            _appsTick.Stop();
        };
    }

    private void Update()
    {
        bool on = _volume.HasDevice;
        _name.Text = on ? _volume.DeviceName : "출력 장치 없음";
        _name.ToolTip = _name.Text;
        int percent = (int)Math.Round(_volume.Level * 100);
        _percent.Text = on ? $"{percent}%" : "–";
        SetGlyph(_mute, _volume.Glyph);
        _mute.ToolTip = _volume.Muted ? "음소거 해제" : "음소거";
        _mute.IsEnabled = _slider.IsEnabled = on;
        // A muted device keeps its level; the grey fill says it is not playing at that level.
        _slider.Fill = _volume.Muted ? ForegroundAlpha(F.SliderMutedAlpha) : new SolidColorBrush(Look.Accent);
        // The callback echoes our own drag a moment later; moving the bar back under the pointer would jitter.
        if (!_slider.Dragging) _slider.Value = _volume.Level;
    }

    /// <summary>
    /// Sessions have no change event here (other apps start and stop playing, and change their own
    /// levels), so the list is read again every tick. Rows are rebuilt only when the set of apps changes.
    /// </summary>
    private void RefreshApps()
    {
        var apps = AppAudio.All();
        var byKey = apps.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        if (apps.Count == _rows.Count && _rows.All(r => byKey.ContainsKey(r.Key)))
        {
            // Same apps: keep their order, so rows do not jump while an app's sound comes and goes.
            foreach (var row in _rows) row.Show(byKey[row.Key]);
            return;
        }
        if (_rows.Any(r => r.Dragging)) return; // rebuilding would drop the slider from under the pointer
        // Apps making sound now first, then ones with an open stream, then the rest; system sounds last.
        apps = apps.OrderBy(a => a.IsSystemSounds ? 3 : a.Playing ? 0 : a.Active ? 1 : 2)
            .ThenBy(AppName, StringComparer.CurrentCultureIgnoreCase).ToList();
        _apps.Children.Clear();
        _rows.Clear();
        foreach (var app in apps)
        {
            var row = new AppRow(this, app);
            _rows.Add(row);
            _apps.Children.Add(row.Element);
        }
        _appsBox.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Log.Debug($"Mixer rows: {string.Join(", ", apps.Select(AppName))}");
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    private static string AppName(AppAudio app)
    {
        if (app.IsSystemSounds) return "시스템 소리";
        if (app.SessionName is { } session) return session;
        if (app.ExePath is not { } path) return "알 수 없는 앱";
        // Asked on every rebuild; the version resource does not change while the exe runs.
        if (!Names.TryGetValue(path, out var name)) Names[path] = name = ReadName(path);
        return name;
    }

    private static string ReadName(string path)
    {
        try
        {
            // Some exes ship a blank description (SteelSeries Sonar has a single space).
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var candidate in new[] { info.FileDescription, info.ProductName })
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
        }
        catch (FileNotFoundException ex) { Log.Debug($"No version info for {path}: {ex.Message}"); }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>One app: its icon (click to mute), name, level slider and percent.</summary>
    private sealed class AppRow
    {
        private readonly VolumeFlyout _owner;
        private readonly PillSlider _slider;
        private readonly TextBlock _percent;
        private readonly Border _iconFace;
        private AppAudio _audio;
        private bool _muted;

        public string Key { get; }
        public FrameworkElement Element { get; }
        public bool Dragging => _slider.Dragging;

        public AppRow(VolumeFlyout owner, AppAudio audio)
        {
            _owner = owner;
            _audio = audio;
            Key = audio.Key;
            string name = AppName(audio);

            _iconFace = new Border
            {
                Width = F.IconButtonDip,
                Height = F.IconButtonDip,
                CornerRadius = new CornerRadius(F.IconButtonDip / 2),
                Background = Brushes.Transparent,
            };
            if (audio.ExePath is { } exe)
            {
                var image = new Image { Width = W.AppVolumeIconDip, Height = W.AppVolumeIconDip };
                _iconFace.Child = image;
                _ = LoadIconAsync(image, exe);
            }
            else
            {
                _iconFace.Child = new TextBlock
                {
                    Text = WidgetGlyphs.Volume3,
                    FontFamily = IconFont,
                    FontSize = W.VolumeButtonGlyphSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            var hover = owner.ForegroundAlpha(F.IconButtonHoverAlpha);
            var icon = new Button
            {
                Content = _iconFace,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
                Cursor = Cursors.Hand,
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-4, 0, 6, 0),
            };
            icon.MouseEnter += (_, _) => _iconFace.Background = hover;
            icon.MouseLeave += (_, _) => _iconFace.Background = Brushes.Transparent;
            icon.Click += (_, _) => { _audio.SetMute(!_muted); Show(_audio); };

            _percent = new TextBlock { FontSize = 12, Foreground = owner.ForegroundAlpha(F.MutedTextAlpha), MinWidth = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            // No wheel step: the wheel scrolls the list, and a row passing under the pointer must not change its app's level.
            _slider = new PillSlider(W.AppVolumeHitDip, W.AppVolumeBarDip, owner.ForegroundAlpha(F.SliderTrackAlpha), new SolidColorBrush(owner.Look.Accent), 0);
            _slider.ValueChanged += v =>
            {
                _audio.SetLevel((float)v);
                // Like the system mixer, moving an app's level unmutes it.
                if (_muted && v > 0) _audio.SetMute(false);
                Show(_audio);
            };

            var label = new TextBlock { Text = name, ToolTip = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 2) };
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var sliderRow = new DockPanel();
            DockPanel.SetDock(_percent, System.Windows.Controls.Dock.Right);
            sliderRow.Children.Add(_percent);
            sliderRow.Children.Add(_slider);
            right.Children.Add(label);
            right.Children.Add(sliderRow);

            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(right);
            Element = row;
            Show(audio);
        }

        /// <summary>Takes the freshly read sessions (an app can open new ones) and shows their level and mute.</summary>
        public void Show(AppAudio audio)
        {
            _audio = audio;
            if (audio.Level is not { } level) return; // gone; the next refresh drops the row
            _muted = audio.Muted ?? false;
            _iconFace.Opacity = _muted ? 0.45 : 1;
            _iconFace.ToolTip = _muted ? "음소거 해제" : "음소거";
            _slider.Fill = _muted ? _owner.ForegroundAlpha(F.SliderMutedAlpha) : new SolidColorBrush(_owner.Look.Accent);
            if (!_slider.Dragging) _slider.Value = level;
            _percent.Text = _muted ? "음소거" : $"{(int)Math.Round((_slider.Dragging ? _slider.Value : level) * 100)}%";
        }

        private async Task LoadIconAsync(Image image, string exe) =>
            image.Source = await _owner._icons.GetAsync(exe, IconProvider.SnapSize(W.AppVolumeIconDip * 2));
    }
}
