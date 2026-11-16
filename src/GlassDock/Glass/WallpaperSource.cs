using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using GlassDock.Logging;
using Microsoft.Win32;
using static GlassDock.Interop.Native;

namespace GlassDock.Glass;

/// <summary>
/// Reproduces what the desktop wallpaper shows inside a screen region, so the lens can refract it
/// without capturing the screen. Follows the per-monitor wallpaper and its fit mode.
/// </summary>
public static class WallpaperSource
{
    private const string TranscodedWallpaper = @"Microsoft\Windows\Themes\TranscodedWallpaper";

    /// <summary>Sharp and blurred copies of the wallpaper under <paramref name="region"/> (physical px, screen coordinates).</summary>
    public static (BitmapSource Sharp, BitmapSource Blurred)? Render(RECT monitor, RECT region, double blurRadiusPx)
    {
        var info = Query(monitor);
        BitmapSource? image = info.Path != null ? Load(info.Path) : null;
        Log.Info($"Wallpaper for liquid glass: {(image != null ? Path.GetFileName(info.Path) : "solid colour")} position {info.Position}");

        // Render with padding so the blur has real pixels at the edges instead of fading to transparent.
        int pad = (int)Math.Ceiling(blurRadiusPx * 2);
        var padded = new RECT(region.Left - pad, region.Top - pad, region.Right + pad, region.Bottom + pad);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(monitor.Left - padded.Left, monitor.Top - padded.Top));
            // Outside the monitor (bottom gap of the dock) keep extending the wallpaper's edge colour.
            dc.DrawRectangle(new SolidColorBrush(info.Background), null, new Rect(-pad, -pad, monitor.Width + 2 * pad, monitor.Height + 2 * pad));
            if (image != null) Draw(dc, image, info.Position, monitor);
            dc.Pop();
        }
        var full = new RenderTargetBitmap(padded.Width, padded.Height, 96, 96, PixelFormats.Pbgra32);
        full.Render(visual);
        full.Freeze();

        var blurHost = new System.Windows.Controls.Image
        {
            Source = full,
            Width = padded.Width,
            Height = padded.Height,
            Effect = new BlurEffect { Radius = blurRadiusPx, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality },
        };
        blurHost.Measure(new Size(padded.Width, padded.Height));
        blurHost.Arrange(new Rect(0, 0, padded.Width, padded.Height));
        var blurredFull = new RenderTargetBitmap(padded.Width, padded.Height, 96, 96, PixelFormats.Pbgra32);
        blurredFull.Render(blurHost);

        var inner = new Int32Rect(pad, pad, region.Width, region.Height);
        var sharp = new CroppedBitmap(full, inner);
        var blurred = new CroppedBitmap(blurredFull, inner);
        sharp.Freeze();
        blurred.Freeze();
        return (sharp, blurred);
    }

    private static void Draw(DrawingContext dc, BitmapSource image, WallpaperPosition position, RECT monitor)
    {
        double w = image.PixelWidth, h = image.PixelHeight, W = monitor.Width, H = monitor.Height;
        Rect Centered(double scale) => new((W - w * scale) / 2, (H - h * scale) / 2, w * scale, h * scale);
        switch (position)
        {
            case WallpaperPosition.Center: dc.DrawImage(image, Centered(1)); break;
            case WallpaperPosition.Stretch: dc.DrawImage(image, new Rect(0, 0, W, H)); break;
            case WallpaperPosition.Fit: dc.DrawImage(image, Centered(Math.Min(W / w, H / h))); break;
            case WallpaperPosition.Tile:
                // Tiles start at the virtual screen's origin, not at this monitor's.
                double ox = GetSystemMetrics(SM_XVIRTUALSCREEN) - monitor.Left, oy = GetSystemMetrics(SM_YVIRTUALSCREEN) - monitor.Top;
                var tile = new ImageBrush(image) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(ox, oy, w, h), Stretch = Stretch.Fill };
                dc.DrawRectangle(tile, null, new Rect(0, 0, W, H));
                break;
            case WallpaperPosition.Span:
                // One image filled across the whole virtual screen; this monitor shows its part of it.
                double vx = GetSystemMetrics(SM_XVIRTUALSCREEN) - monitor.Left, vy = GetSystemMetrics(SM_YVIRTUALSCREEN) - monitor.Top;
                double vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                double s = Math.Max(vw / w, vh / h);
                dc.DrawImage(image, new Rect(vx + (vw - w * s) / 2, vy + (vh - h * s) / 2, w * s, h * s));
                break;
            default: dc.DrawImage(image, Centered(Math.Max(W / w, H / h))); break; // Fill
        }
    }

    private static BitmapSource? Load(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or COMException)
        {
            Log.Warn($"Reading wallpaper {path} failed; using the background colour", ex);
            return null;
        }
    }

    private readonly record struct Info(string? Path, WallpaperPosition Position, Color Background);

    private static Info Query(RECT monitor)
    {
        string? path = null;
        var position = WallpaperPosition.Fill;
        var background = Colors.Black;
        try
        {
            var dw = (IDesktopWallpaper)new DesktopWallpaperClass();
            try
            {
                position = (WallpaperPosition)dw.GetPosition();
                uint bg = dw.GetBackgroundColor();
                background = Color.FromRgb((byte)bg, (byte)(bg >> 8), (byte)(bg >> 16));
                uint count = dw.GetMonitorDevicePathCount();
                for (uint i = 0; i < count; i++)
                {
                    string id = dw.GetMonitorDevicePathAt(i);
                    if (!dw.GetMonitorRECT(id).Equals(monitor)) continue;
                    path = dw.GetWallpaper(id);
                    break;
                }
            }
            finally { Marshal.ReleaseComObject(dw); }
        }
        catch (COMException ex)
        {
            Log.Warn("IDesktopWallpaper failed; falling back to the transcoded wallpaper", ex);
        }

        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return new(path, position, background);
        // Spotlight and some slideshow states report no file; the shell's own copy is still current.
        // An empty Wallpaper value means "solid colour", where the transcoded file may be stale.
        string configured = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", "") as string ?? "";
        string transcoded = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), TranscodedWallpaper);
        if (configured.Length > 0 && File.Exists(transcoded)) return new(transcoded, position, background);
        return new(null, position, background);
    }

    private enum WallpaperPosition { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

    [ComImport, Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass;

    // Only the leading methods are declared; COM dispatch is by vtable slot, so order matters.
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
        uint GetMonitorDevicePathCount();
        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
    }
}
