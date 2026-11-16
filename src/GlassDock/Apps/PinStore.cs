using GlassDock.Config;
using GlassDock.Logging;

namespace GlassDock.Apps;

/// <summary>Creates pins from dropped files and imports Explorer's taskbar pins once (read-only).</summary>
public static class PinStore
{
    private static string TaskbarPinsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>
    /// Imports .lnk files from Explorer's pinned folder. Order: file creation time, because the real
    /// order lives in an undocumented registry blob (design E7). Nothing is ever written back.
    /// </summary>
    public static List<PinnedItem> ImportTaskbarPins()
    {
        var result = new List<PinnedItem>();
        var dir = new DirectoryInfo(TaskbarPinsFolder);
        if (!dir.Exists) return result;
        foreach (var f in dir.GetFiles("*.lnk").OrderBy(f => f.CreationTimeUtc))
        {
            var pin = FromPath(f.FullName);
            if (pin != null) result.Add(pin);
        }
        Log.Info($"Imported {result.Count} taskbar pins from {dir.FullName}");
        return result;
    }

    /// <summary>Builds a pin for a dropped .lnk, .exe, folder or any file.</summary>
    public static PinnedItem? FromPath(string path)
    {
        if (Directory.Exists(path))
            return new PinnedItem { Kind = "folder", Path = path, DisplayName = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path };
        if (!File.Exists(path)) return null;

        var pin = new PinnedItem { Kind = "app" };
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var info = ShortcutReader.Read(path);
            if (info == null) return null;
            // Copy the shortcut so the pin survives the original being deleted.
            var copy = Path.Combine(AppPaths.PinsDir, pin.Id + ".lnk");
            try
            {
                Directory.CreateDirectory(AppPaths.PinsDir);
                File.Copy(path, copy, overwrite: true);
                pin.Shortcut = Path.GetRelativePath(AppPaths.Root, copy);
            }
            catch (IOException ex) { Log.Warn($"Copying shortcut {path} failed; pin keeps the target only", ex); }
            pin.Target = info.Target;
            pin.Arguments = info.Arguments;
            pin.WorkingDir = info.WorkingDir;
            pin.Aumid = info.Aumid;
            pin.DisplayName = info.DisplayName;
            pin.IconPath = info.IconPath;
            pin.IconIndex = info.IconIndex;
        }
        else
        {
            pin.Target = path;
            pin.DisplayName = Path.GetFileNameWithoutExtension(path);
        }
        return pin;
    }

    public static PinnedItem FromRunning(string? aumid, string? exePath, string displayName)
    {
        // A packaged app's exe path breaks on its next update and cannot be started directly anyway.
        if (!AppIdentity.IsRealAumid(aumid)) aumid = PackagedApps.AumidForPath(exePath);
        return new()
        {
            Kind = "app",
            Aumid = AppIdentity.IsRealAumid(aumid) ? aumid : null,
            Target = AppIdentity.IsRealAumid(aumid) ? null : exePath,
            DisplayName = AppNames.ForAumid(aumid) ?? displayName,
        };
    }

    /// <summary>
    /// Pins made from a running window took the exe's description, which for an Edge or Chrome web app is
    /// the browser's name. Pins that hold only an app id were made that way (an app pin cannot be renamed),
    /// so their name is refreshed from the app list.
    /// </summary>
    public static int RefreshAppNames(IEnumerable<PinnedItem> pins)
    {
        int changed = 0;
        foreach (var pin in pins)
        {
            if (pin.Children != null) changed += RefreshAppNames(pin.Children);
            if (pin.Kind != "app" || pin.Shortcut != null || pin.Target != null) continue;
            if (AppNames.ForAumid(pin.Aumid) is not { } name || name == pin.DisplayName) continue;
            Log.Info($"Pin {pin.Aumid}: name {pin.DisplayName} -> {name}");
            pin.DisplayName = name;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Rewrites pins that point at an exe inside a package to the package's app id, while that version is
    /// still installed; after the app's next update the old path would be gone.
    /// </summary>
    public static int UpgradePackagedPins(IEnumerable<PinnedItem> pins)
    {
        int changed = 0;
        foreach (var pin in pins)
        {
            if (pin.Children != null) changed += UpgradePackagedPins(pin.Children);
            if (pin.Kind != "app" || pin.Shortcut != null || AppIdentity.IsRealAumid(pin.Aumid)) continue;
            if (PackagedApps.AumidForPath(pin.Target) is not { } aumid) continue;
            Log.Info($"Pin {pin.DisplayName}: {pin.Target} -> {aumid}");
            pin.Aumid = aumid;
            pin.Target = null;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Shortcut pins made before pins kept the shortcut's icon: reads it from the copied shortcut. Pins whose
    /// shortcut names no icon are read again each start, which costs one small file per pin.
    /// </summary>
    public static int ReadShortcutIcons(IEnumerable<PinnedItem> pins)
    {
        int changed = 0;
        foreach (var pin in pins)
        {
            if (pin.Children != null) changed += ReadShortcutIcons(pin.Children);
            if (pin.Kind != "app" || pin.Shortcut == null || pin.IconPath != null) continue;
            var copy = Path.Combine(AppPaths.Root, pin.Shortcut);
            if (!File.Exists(copy) || ShortcutReader.Read(copy) is not { IconPath: { } icon } info) continue;
            Log.Info($"Pin {pin.DisplayName}: icon {icon},{info.IconIndex}");
            pin.IconPath = icon;
            pin.IconIndex = info.IconIndex;
            changed++;
        }
        return changed;
    }

    public static void DeletePinFiles(PinnedItem pin)
    {
        if (pin.Shortcut == null) return;
        try { File.Delete(Path.Combine(AppPaths.Root, pin.Shortcut)); }
        catch (IOException ex) { Log.Warn($"Deleting pin shortcut {pin.Shortcut} failed", ex); }
    }
}
