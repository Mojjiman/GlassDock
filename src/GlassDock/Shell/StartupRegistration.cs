using GlassDock.Config;
using GlassDock.Logging;
using Microsoft.Win32;

namespace GlassDock.Shell;

/// <summary>Launch at sign-in through HKCU\...\Run (visible and toggleable in Task Manager's Startup apps).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var value = $"\"{AppPaths.ExePath}\"";
                if (key.GetValue(AppConstants.AppName) as string != value) key.SetValue(AppConstants.AppName, value);
            }
            else if (key.GetValue(AppConstants.AppName) != null)
            {
                key.DeleteValue(AppConstants.AppName);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("Updating launch-at-startup registration failed", ex);
        }
    }
}
