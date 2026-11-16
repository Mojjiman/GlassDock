using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Apps;

public static class AppLauncher
{
    public static bool Launch(PinnedItem pin, bool elevated = false)
    {
        if (pin.Kind == "folder" && pin.Path != null) return Execute(pin.Path, null, null, null);
        if (AppIdentity.IsRealAumid(pin.Aumid) && pin.Shortcut == null && pin.Target == null)
            return Execute(@"shell:AppsFolder\" + pin.Aumid, null, null, elevated ? "runas" : null);
        if (pin.Shortcut == null && PackagedApps.AumidForPath(pin.Target) is { } packaged)
            return Execute(@"shell:AppsFolder\" + packaged, null, null, elevated ? "runas" : null);
        if (pin.Shortcut != null)
        {
            var lnk = Path.Combine(AppPaths.Root, pin.Shortcut);
            if (File.Exists(lnk)) return Execute(lnk, null, null, elevated ? "runas" : null);
            Log.Warn($"Pinned shortcut missing: {lnk}; falling back to target");
        }
        if (pin.Target != null) return Execute(pin.Target, pin.Arguments, pin.WorkingDir, elevated ? "runas" : null);
        if (AppIdentity.IsRealAumid(pin.Aumid)) return Execute(@"shell:AppsFolder\" + pin.Aumid, null, null, null);
        Log.Warn($"Pin {pin.Id} has nothing to launch");
        return false;
    }

    /// <summary>Starts a new instance of a running app that is not pinned.</summary>
    public static bool LaunchNew(string? aumid, string? exePath, bool elevated = false)
    {
        if (!AppIdentity.IsRealAumid(aumid)) aumid = PackagedApps.AumidForPath(exePath);
        if (AppIdentity.IsRealAumid(aumid)) return Execute(@"shell:AppsFolder\" + aumid, null, null, elevated ? "runas" : null);
        if (!string.IsNullOrEmpty(exePath)) return Execute(exePath, null, null, elevated ? "runas" : null);
        return false;
    }

    public static void OpenFileLocation(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Error($"Opening location of {path} failed", ex); }
    }

    public static bool Execute(string file, string? args, string? dir, string? verb)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOASYNC,
            lpVerb = verb,
            lpFile = file,
            lpParameters = args,
            lpDirectory = string.IsNullOrEmpty(dir) ? null : dir,
            nShow = SW_SHOW,
        };
        if (ShellExecuteEx(ref info)) return true;
        int err = Marshal.GetLastPInvokeError();
        // 1223 = the user cancelled the UAC prompt; not an error worth a warning.
        if (err != 1223) Log.Warn($"ShellExecuteEx({file} {args}) failed with Win32 error {err}");
        return false;
    }
}
