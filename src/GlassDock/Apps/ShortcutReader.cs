using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using GlassDock.Logging;
using static GlassDock.Interop.ShellInterop;

namespace GlassDock.Apps;

/// <param name="IconPath">The icon the shortcut names (environment variables expanded), or null when it uses its target's.</param>
public sealed record ShortcutInfo(string? Target, string? Arguments, string? WorkingDir, string? Aumid, string DisplayName,
    string? IconPath = null, int IconIndex = 0);

public static class ShortcutReader
{
    public static ShortcutInfo? Read(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);
            var sl = (IShellLinkW)link;
            var sb = new StringBuilder(1024);
            sl.GetPath(sb, sb.Capacity, 0, 0);
            string? target = sb.Length > 0 ? sb.ToString() : null;
            sb.Clear();
            sl.GetArguments(sb, sb.Capacity);
            string? args = sb.Length > 0 ? sb.ToString() : null;
            sb.Clear();
            sl.GetWorkingDirectory(sb, sb.Capacity);
            string? wd = sb.Length > 0 ? sb.ToString() : null;
            sb.Clear();
            sl.GetIconLocation(sb, sb.Capacity, out int iconIndex);
            string? icon = sb.Length > 0 ? Environment.ExpandEnvironmentVariables(sb.ToString()) : null;
            string? aumid = null;
            if (link is IPropertyStore store) aumid = GetString(store, PKEY_AppUserModel_ID);
            return new ShortcutInfo(target, args, wd, string.IsNullOrEmpty(aumid) ? null : aumid,
                Path.GetFileNameWithoutExtension(lnkPath), icon, iconIndex);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or InvalidCastException)
        {
            Log.Warn($"Could not read shortcut {lnkPath}", ex);
            return null;
        }
        finally { ReleaseCom(link); }
    }
}
