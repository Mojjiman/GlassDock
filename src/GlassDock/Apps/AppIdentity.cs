using GlassDock.Config;

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
            : !string.IsNullOrEmpty(exePath) ? "path:" + NormalizePath(exePath)
            : "unknown";
    }

    public static string KeyForPin(PinnedItem pin) =>
        PinAumid(pin) is { } aumid ? "aumid:" + aumid.ToLowerInvariant()
        : !string.IsNullOrEmpty(pin.Target) ? "path:" + NormalizePath(pin.Target)
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
        if (NormalizePath(pin.Target) == exe) return true;

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
