using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Taskbar;
using GlassDock.Watchdog;

namespace GlassDock;

public static class Program
{
    // Must branch before any WPF type is touched so the watchdog never loads PresentationFramework.
    [STAThread]
    public static int Main(string[] args)
    {
        AppPaths.EnsureCreated();
        if (args.Contains("--watchdog")) return WatchdogMain.Run(args);
        if (args.Contains("--restore-taskbar"))
        {
            Log.Initialize("cli");
            int rc = TaskbarRestorer.RunCli();
            Log.Flush(TimeSpan.FromSeconds(2));
            return rc;
        }
        if (args.Contains("--quit"))
        {
            // Asks a running dock to exit cleanly (used by scripts and an uninstaller).
            Interop.Native.PostMessage(0xFFFF /* HWND_BROADCAST */, Interop.Native.RegisterWindowMessage(AppConstants.QuitMessage), 0, 0);
            return 0;
        }
        return DockMain.Run(args);
    }
}
