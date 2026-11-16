using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDock.Config;
using GlassDock.Interop;
using GlassDock.Logging;
using static GlassDock.Interop.ShellInterop;

namespace GlassDock.Apps;

/// <summary>
/// Extracts high-resolution icons on a dedicated STA thread (shell COM objects can block or need an
/// STA). Results are frozen so the UI thread can use them directly.
/// </summary>
public sealed class IconProvider : IDisposable
{
    private readonly BlockingCollection<(string path, int px, bool thumb, TaskCompletionSource<ImageSource?> tcs)> _queue = new();
    private readonly ConcurrentDictionary<(string, int, bool), Task<ImageSource?>> _cache = new();
    private readonly Thread _thread;

    public IconProvider()
    {
        _thread = new Thread(Worker) { IsBackground = true, Name = "GlassDock.ShellCom" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Rounds a requested pixel size up to a size the shell caches.</summary>
    public static int SnapSize(double px)
    {
        foreach (var s in AppConstants.Icons.PixelSizes) if (s >= px) return s;
        return AppConstants.Icons.PixelSizes[^1];
    }

    // A source naming an icon inside a file (an .ico, or an exe/dll and its icon index) instead of a shell item.
    private const string ResourcePrefix = "iconres:";

    /// <summary>A source for <see cref="GetAsync"/> that draws icon <paramref name="index"/> of <paramref name="file"/>.</summary>
    public static string IconResource(string file, int index) => $"{ResourcePrefix}{index}|{file}";

    /// <param name="parsingName">File path, <c>shell:AppsFolder\AUMID</c> for packaged apps, or an <see cref="IconResource"/>.</param>
    /// <param name="thumbnail">Picture/video previews where the shell has them (stacks); app buttons want the plain icon.</param>
    public Task<ImageSource?> GetAsync(string parsingName, int pixelSize, bool thumbnail = false)
    {
        var key = (parsingName.ToLowerInvariant(), pixelSize, thumbnail);
        if (_cache.Count > AppConstants.Icons.CacheMaxEntries) _cache.Clear();
        return _cache.GetOrAdd(key, _ =>
        {
            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((parsingName, pixelSize, thumbnail, tcs));
            return tcs.Task;
        });
    }

    private void Worker()
    {
        foreach (var (path, px, thumb, tcs) in _queue.GetConsumingEnumerable())
        {
            try { tcs.SetResult(Extract(path, px, thumb)); }
            catch (Exception ex) when (ex is COMException or ArgumentException or FileNotFoundException or UnauthorizedAccessException)
            {
                Log.Debug($"Icon extraction failed for {path}: {ex.Message}");
                tcs.SetResult(null);
            }
        }
    }

    private static ImageSource? Extract(string path, int px, bool thumbnail)
    {
        if (path.StartsWith(ResourcePrefix, StringComparison.Ordinal)) return ExtractResource(path[ResourcePrefix.Length..], px);
        var iid = IID_IShellItemImageFactory;
        SHCreateItemFromParsingName(path, 0, ref iid, out object obj);
        try
        {
            var factory = (IShellItemImageFactory)obj;
            var flags = thumbnail ? SIIGBF.BiggerSizeOk : SIIGBF.IconOnly | SIIGBF.BiggerSizeOk;
            int hr = factory.GetImage(new Native.SIZE { cx = px, cy = px }, flags, out nint hbmp);
            if (hr != 0 || hbmp == 0) return null;
            try
            {
                var bits = ReadBitmap(hbmp, out int w, out int h);
                if (bits == null) return null;
                var format = LooksPremultiplied(bits) ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;
                var bmp = BitmapSource.Create(w, h, 96, 96, format, null, bits, w * 4);
                bmp.Freeze();
                return bmp;
            }
            finally { Native.DeleteObject(hbmp); }
        }
        finally { ReleaseCom(obj); }
    }

    // "index|file". A shell item for the file would add the shortcut arrow (for .lnk) or show the file
    // type's icon, so the icon is pulled straight from the file at the requested size.
    private static ImageSource? ExtractResource(string spec, int px)
    {
        int bar = spec.IndexOf('|');
        if (bar < 0 || !int.TryParse(spec[..bar], out int index)) throw new ArgumentException($"Bad icon resource {spec}");
        string file = spec[(bar + 1)..];
        int hr = SHDefExtractIconW(file, index, 0, out nint large, out nint small, (uint)px | ((uint)AppConstants.Icons.PixelSizes[0] << 16));
        if (small != 0) Native.DestroyIcon(small);
        if (hr != 0 || large == 0)
        {
            Log.Debug($"No icon {index} in {file} (0x{hr:X8})");
            return null;
        }
        try
        {
            var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(large, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        finally { Native.DestroyIcon(large); }
    }

    // The shell usually hands out premultiplied DIBs, but not always; a colour channel larger than
    // alpha can only occur in straight alpha.
    private static bool LooksPremultiplied(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a) return false;
        }
        return true;
    }

    public void Dispose() => _queue.CompleteAdding();
}
