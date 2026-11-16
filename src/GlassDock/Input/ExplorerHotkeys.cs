using System.IO;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Taskbar;
using static GlassDock.Interop.Native;

namespace GlassDock.Input;

/// <summary>
/// Makes Explorer give up some of its own hotkeys (Win+1..0, Win+T, Win+B, ...) so we can register the same
/// combinations with RegisterHotKey. A real hotkey reaches us whatever window is in front, including an
/// elevated fullscreen game, where Windows (UIPI) stops calling our keyboard hook.
/// </summary>
/// <remarks>
/// Undocumented, done the same way as RetroBar: explorer.exe holds a table of 8-byte records
/// <c>{vk, 0, 0, 0, modifiers, 0, 0, 0}</c>, the taskbar registers record i under id 500 + i, and the
/// taskbar window unregisters an id on <c>WM_USER + 231</c>. There is no message that gives a key back,
/// so Explorer's own Win+N returns only when Explorer restarts. If a Windows update changes any of this,
/// <see cref="Release"/> finds nothing and the caller keeps translating through the hook.
/// </remarks>
public static class ExplorerHotkeys
{
    private static (string Path, DateTime Stamp, List<(uint Mods, int Vk)> Table)? _cache;

    /// <summary>Asks Explorer to unregister each of <paramref name="keys"/> it owns; returns how many it was asked to drop.</summary>
    public static int Release(IReadOnlyCollection<(uint Mods, int Vk)> keys)
    {
        nint tray = ExplorerTaskbars.FindPrimary();
        if (tray == 0)
        {
            Log.Warn("Explorer's taskbar window not found; its hotkeys stay registered");
            return 0;
        }
        var table = Table(GetProcessPath(GetWindowProcessId(tray)) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        if (table.Count == 0) return 0;
        int sent = 0;
        foreach (var (mods, vk) in keys)
        {
            int i = table.IndexOf((MOD_WIN | mods, vk));
            if (i < 0) continue;
            if (SendMessageTimeout(tray, WM_USER + AppConstants.Input.ExplorerUnregisterHotkeyOffset, (nuint)(AppConstants.Input.ExplorerHotkeyBaseId + i), 0,
                    SMTO_ABORTIFHUNG, AppConstants.Input.ExplorerMessageTimeoutMs, out _) == 0)
                Log.Win32Failure($"SendMessageTimeout(unregister Explorer hotkey {AppConstants.Input.ExplorerHotkeyBaseId + i})");
            else sent++;
        }
        Log.Info($"Asked Explorer to release {sent} of {keys.Count} hotkeys");
        return sent;
    }

    private static List<(uint Mods, int Vk)> Table(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_cache is { } c && c.Path == path && c.Stamp == stamp) return c.Table;
            var table = Scan(File.ReadAllBytes(path));
            // Win+1 is in every known version; a run without it is some other data that happens to fit.
            if (!table.Contains((MOD_WIN, '1')))
            {
                Log.Warn($"Explorer hotkey table not found in {path}; its hotkeys stay registered");
                table = [];
            }
            else Log.Info($"Explorer hotkey table: {table.Count} entries in {path}");
            _cache = (path, stamp, table);
            return table;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not read {path} for Explorer's hotkey table", ex);
            return [];
        }
    }

    /// <summary>The longest run of consecutive 8-byte-aligned records that look like Win+key entries.</summary>
    private static List<(uint Mods, int Vk)> Scan(byte[] b)
    {
        int bestStart = 0, bestLen = 0, start = 0, len = 0;
        for (int o = 0; o + 8 <= b.Length; o += 8)
        {
            bool record = b[o] != 0 && b[o + 1] == 0 && b[o + 2] == 0 && b[o + 3] == 0 && b[o + 5] == 0 && b[o + 6] == 0 && b[o + 7] == 0
                && (b[o + 4] & MOD_WIN) != 0 && (b[o + 4] & 0xF0) == 0;
            if (!record) { len = 0; continue; }
            if (len++ == 0) start = o;
            if (len > bestLen) (bestStart, bestLen) = (start, len);
        }
        var table = new List<(uint, int)>(bestLen);
        for (int i = 0; i < bestLen; i++) table.Add((b[bestStart + i * 8 + 4], b[bestStart + i * 8]));
        return table;
    }
}
