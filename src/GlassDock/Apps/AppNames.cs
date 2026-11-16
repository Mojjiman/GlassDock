using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using GlassDock.Interop;
using GlassDock.Logging;

namespace GlassDock.Apps;

/// <summary>
/// The name Windows shows for an app id in Start and on the taskbar. A web app installed from Edge or
/// Chrome runs as msedge.exe/chrome.exe, so the exe's file description names the browser; the app id
/// (<c>shell:AppsFolder\&lt;id&gt;</c>) carries the web app's own name.
/// </summary>
public static class AppNames
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? ForAumid(string? aumid) =>
        AppIdentity.IsRealAumid(aumid) ? Cache.GetOrAdd(aumid!, Lookup) : null;

    private static string? Lookup(string aumid)
    {
        object? obj = null;
        try
        {
            var iid = ShellInterop.IID_IShellItem;
            ShellInterop.SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, 0, ref iid, out obj);
            ((ShellInterop.IShellItem)obj).GetDisplayName(ShellInterop.SIGDN_NORMALDISPLAY, out nint p);
            try { return Marshal.PtrToStringUni(p) is { Length: > 0 } name ? name : null; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or FileNotFoundException)
        {
            // Not every app id is listed (e.g. some helper windows); the caller falls back to the exe's name.
            Log.Debug($"No app list entry for {aumid}: {ex.Message}");
            return null;
        }
        finally
        {
            if (obj != null) Marshal.ReleaseComObject(obj);
        }
    }
}
