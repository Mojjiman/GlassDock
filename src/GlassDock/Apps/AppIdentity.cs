using System.Text.RegularExpressions;
using GlassDock.Config;
using GlassDock.Logging;

namespace GlassDock.Apps;

/// <summary>How a pin and a running window are recognised as the same app.</summary>
public static class AppIdentity
{
    public static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToLowerInvariant();
        }
    }

    // Squirrel installs (Discord, Slack) run from <root>\app-<version>\, a folder that changes with every update,
    // so a pin made before the update named a folder the running app no longer comes from.
    private static readonly Regex SquirrelVersionDir = new(@"\\app-\d+(\.\d+)*\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary><see cref="NormalizePath"/> with a Squirrel version folder made version-free, for telling apps apart.</summary>
    public static string IdentityPath(string path) => SquirrelVersionDir.Replace(NormalizePath(path), @"\app-*\", 1);

    /// <summary>
    /// The same file in the newest <c>app-&lt;version&gt;</c> folder that has it, for a path inside a Squirrel
    /// install; any other path unchanged. A pin keeps the version it was made with, which the app's updater
    /// leaves outdated and later deletes.
    /// </summary>
    public static string CurrentSquirrelPath(string path)
    {
        var m = SquirrelVersionDir.Match(path);
        if (!m.Success) return path;
        string root = path[..m.Index], rest = path[(m.Index + m.Length)..];
        try
        {
            string? best = null;
            Version? bestVersion = null;
            foreach (var dir in Directory.EnumerateDirectories(root, "app-*"))
            {
                if (!Version.TryParse(Path.GetFileName(dir)[4..], out var v) || (bestVersion != null && v <= bestVersion)) continue;
                var candidate = Path.Combine(dir, rest);
                if (File.Exists(candidate)) { best = candidate; bestVersion = v; }
            }
            return best ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Could not look for a newer Squirrel version of {path}: {ex.Message}");
            return path;
        }
    }

    /// <summary>ManagedShell reports an exe path as the "AUMID" for apps without an explicit one.</summary>
    public static bool IsRealAumid(string? id) => !string.IsNullOrEmpty(id) && !id.Contains('\\') && !id.Contains('/');

    /// <summary>
    /// File Explorer windows set no app id, so ManagedShell reports explorer.exe's path, while the File
    /// Explorer pin (imported from the taskbar) has only the app id. Windows itself treats such windows
    /// as <see cref="AppConstants.Explorer.Aumid"/>; so do we.
    /// </summary>
    /// <remarks>Windows of packaged desktop apps (Spotify, Claude) set no app id either; see <see cref="PackagedApps"/>.</remarks>
    public static string? EffectiveAumid(string? aumid, string? exePath) =>
        IsRealAumid(aumid) ? aumid
        : !string.IsNullOrEmpty(exePath) && NormalizePath(exePath) == ExplorerPath ? AppConstants.Explorer.Aumid
        : PackagedApps.AumidForPath(exePath) ?? aumid;

    /// <summary>Pins made before packaged apps were recognised hold the exe path inside the package.</summary>
    private static string? PinAumid(PinnedItem pin) =>
        IsRealAumid(pin.Aumid) ? pin.Aumid : pin.Shortcut == null ? PackagedApps.AumidForPath(pin.Target) : null;

    private static readonly string ExplorerPath =
        NormalizePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), AppConstants.Explorer.ExeName));

    public static string KeyForWindow(string? aumid, string? exePath)
    {
        aumid = EffectiveAumid(aumid, exePath);
        return IsRealAumid(aumid) ? "aumid:" + aumid!.ToLowerInvariant()
            : !string.IsNullOrEmpty(exePath) ? "path:" + IdentityPath(exePath)
            : "unknown";
    }

    public static string KeyForPin(PinnedItem pin) =>
        PinAumid(pin) is { } aumid ? "aumid:" + aumid.ToLowerInvariant()
        : !string.IsNullOrEmpty(pin.Target) ? "path:" + IdentityPath(pin.Target)
        : "pin:" + pin.Id;

    /// <summary>Does this running window belong to the pinned app?</summary>
    public static bool Matches(PinnedItem pin, string? windowAumid, string? exePath)
    {
        windowAumid = EffectiveAumid(windowAumid, exePath);
        var pinAumid = PinAumid(pin);
        if (IsRealAumid(pinAumid) && IsRealAumid(windowAumid))
            return string.Equals(pinAumid, windowAumid, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(pin.Target)) return false;

        var exe = NormalizePath(exePath);
        if (IdentityPath(pin.Target) == IdentityPath(exePath)) return true;

        // Squirrel/Electron installers pin "Update.exe --processStart App.exe"; the real process
        // lives in a versioned folder below Update.exe's directory.
        if (pin.Arguments is { } args && args.Contains("--processStart", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int i = Array.FindIndex(parts, p => p.Equals("--processStart", StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < parts.Length)
            {
                var name = parts[i + 1].Trim('"').ToLowerInvariant();
                var root = Path.GetDirectoryName(NormalizePath(pin.Target)) ?? "";
                return Path.GetFileName(exe) == name && exe.StartsWith(root, StringComparison.Ordinal);
            }
        }
        return false;
    }
}
