using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;
using Lens = GlassDock.Config.AppConstants.Glass.Lens;

namespace GlassDock.Glass;

/// <summary>
/// The lens element at the bottom of the pill (tier G2). The wallpaper is rendered once for the dock
/// window's band of the screen; moving or resizing the pill only changes the brushes' viewbox, so
/// nothing is re-rendered while the dock changes width.
/// </summary>
public sealed class LiquidLensLayer
{
    private readonly ImageBrush _sharp = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    private readonly ImageBrush _blurred = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    private readonly LiquidLensEffect? _effect;
    private RECT _monitor, _band;
    private bool _bandValid;
    private bool _rendering;
    private bool _shown;

    public Rectangle Element { get; } = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, Opacity = 0 };

    /// <summary>False when the GPU or the shader compiler is unavailable; the dock then stays on frosted glass.</summary>
    public bool IsSupported => _effect != null;
    public bool HasWallpaper => _bandValid;
    /// <summary>True once the lens has fully faded in, i.e. nothing needs to show underneath any more.</summary>
    public bool IsOpaque { get; private set; }
    /// <summary>Raised when a fade finishes, so the owner can hide or show the frosted window.</summary>
    public event Action? FadeCompleted;

    public LiquidLensLayer()
    {
        if (!LiquidLensEffect.IsSupported) return;
        _effect = new LiquidLensEffect
        {
            Blurred = _blurred,
            Frost = Lens.Frost,
            Chroma = Lens.Chroma,
            RimGlow = Lens.RimGlow,
            Vibrancy = Lens.Vibrancy,
            RimFrost = Lens.RimFrost,
        };
        Element.Fill = _sharp;
        Element.Effect = _effect;
    }

    /// <summary>Forget the rendered wallpaper (wallpaper or display changed).</summary>
    public void Invalidate() => _bandValid = false;

    /// <summary>Make sure the wallpaper under <paramref name="band"/> (physical px) is rendered.</summary>
    public void EnsureBand(RECT monitor, RECT band, double scale)
    {
        if (_effect == null || _rendering || (_bandValid && band.Equals(_band) && monitor.Equals(_monitor))) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // The IDesktopWallpaper call pumps messages (STA), which can run a queued PlaceWindow in here again.
        _rendering = true;
        (System.Windows.Media.Imaging.BitmapSource Sharp, System.Windows.Media.Imaging.BitmapSource Blurred)? rendered;
        try { rendered = WallpaperSource.Render(monitor, band, Lens.BlurDip * scale); }
        finally { _rendering = false; }
        if (rendered == null) { _bandValid = false; return; }
        _sharp.ImageSource = rendered.Value.Sharp;
        _blurred.ImageSource = rendered.Value.Blurred;
        _monitor = monitor;
        _band = band;
        _bandValid = true;
        // The full-size decoded wallpaper (tens of MB of native memory) is only released by finalizers, and an
        // idle dock allocates too little for the GC to run on its own; collect once after this one-off render.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Log.Info($"Liquid glass wallpaper band {band} rendered in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Point the lens at the pill's screen rectangle (physical px).</summary>
    public void SetPill(RECT pill, double cornerRadiusPx, double scale)
    {
        if (_effect == null || !_bandValid) return;
        var vb = new Rect(pill.Left - _band.Left, pill.Top - _band.Top, Math.Max(1, pill.Width), Math.Max(1, pill.Height));
        _sharp.Viewbox = vb;
        _blurred.Viewbox = vb;
        _effect.PillSize = new Point(vb.Width, vb.Height);
        _effect.Radius = cornerRadiusPx;
        _effect.Bezel = Lens.BezelDip * scale;
        _effect.Refraction = Lens.RefractionDip * scale;
    }

    /// <summary>Cross-fades the lens in or out over the frosted window below it.</summary>
    public void Show(bool on, bool animate)
    {
        if (_effect == null) on = false;
        if (on == _shown) return;
        _shown = on;
        IsOpaque = false;
        if (on) Element.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(animate ? AppConstants.Glass.CrossFadeMs : 0));
        anim.Completed += (_, _) =>
        {
            if (_shown != on) return; // superseded by a newer fade
            if (on) IsOpaque = true;
            else Element.Visibility = Visibility.Collapsed;
            FadeCompleted?.Invoke();
        };
        Element.BeginAnimation(UIElement.OpacityProperty, anim);
    }
}
