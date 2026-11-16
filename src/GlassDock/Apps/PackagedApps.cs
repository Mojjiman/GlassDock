using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Apps;

/// <summary>
/// Desktop apps installed from a package (Spotify, Claude, ChatGPT from the Store) live under a
/// WindowsApps folder. Users may not start those exes directly (ShellExecute fails with "access denied"),
/// their windows carry no app id, and the folder name changes with every update. The app id
/// (<c>family!appId</c>) is what starts them and stays the same, so it is read from the package manifest.
/// </summary>
public static class PackagedApps
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app id of the packaged app whose exe is <paramref name="exePath"/>, or null if it is not packaged.</summary>
    public static string? AumidForPath(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        int at = exePath.IndexOf(AppConstants.Packages.RootFolder, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        return Cache.GetOrAdd(exePath, _ => Resolve(exePath, at + AppConstants.Packages.RootFolder.Length));
    }

    private static string? Resolve(string exePath, int packageStart)
    {
        int slash = exePath.IndexOf('\\', packageStart);
        if (slash < 0) return null;
        string fullName = exePath[packageStart..slash];
        string relExe = exePath[(slash + 1)..];
        string? family = FamilyName(fullName);
        if (family == null) return null;
        var manifest = Path.Combine(exePath[..slash], AppConstants.Packages.ManifestName);
        try
        {
            var apps = XDocument.Load(manifest).Descendants().Where(e => e.Name.LocalName == "Application").ToList();
            // The entry exe is often a launcher that starts the real one: Spotify's app "Spotify" runs
            // SpotifyMigrator.exe, whose window then belongs to Spotify.exe. So after an exact match, take
            // the app named like the exe, then a package's only app.
            var app = apps.FirstOrDefault(e => string.Equals(((string?)e.Attribute("Executable"))?.Replace('/', '\\'), relExe, StringComparison.OrdinalIgnoreCase))
                ?? apps.FirstOrDefault(e => string.Equals((string?)e.Attribute("Id"), Path.GetFileNameWithoutExtension(relExe), StringComparison.OrdinalIgnoreCase))
                ?? (apps.Count == 1 ? apps[0] : null);
            if ((string?)app?.Attribute("Id") is not { Length: > 0 } id)
            {
                Log.Warn($"No application for {relExe} in {manifest}");
                return null;
            }
            return family + "!" + id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Log.Warn($"Reading {manifest} failed", ex);
            return null;
        }
    }

    private static string? FamilyName(string packageFullName)
    {
        uint length = 0;
        if (PackageFamilyNameFromFullName(packageFullName, ref length, null) != ERROR_INSUFFICIENT_BUFFER) return null;
        var sb = new StringBuilder((int)length);
        int err = PackageFamilyNameFromFullName(packageFullName, ref length, sb);
        if (err == 0) return sb.ToString();
        Log.Warn($"PackageFamilyNameFromFullName({packageFullName}) failed with error {err}");
        return null;
    }
}
