using System.Windows;
using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Taskbar;
using static GlassDock.Interop.Native;

namespace GlassDock;

public static class DockMain
{
    private static AppHost? _host;
    private static int _crashed;
    private static bool _running;
    private static readonly Queue<long> UiFaultTimes = new();

    public static int Run(string[] args)
    {
        Log.Initialize("main");
        Update.UpdateService.WaitForPreviousInstance(args);
        using var mutex = new Mutex(true, AppConstants.SingleInstanceMutex, out bool first);
        if (!first)
        {
            // Second launch: ask the running dock to open its settings, then leave.
            PostMessage(0xFFFF /* HWND_BROADCAST */, RegisterWindowMessage(AppConstants.OpenSettingsMessage), 0, 0);
            Log.Info("Already running; asked the existing instance to open settings");
            Log.Flush(TimeSpan.FromSeconds(1));
            return AppConstants.ExitCodes.AlreadyRunning;
        }

        Log.Info($"Starting GlassDock {typeof(DockMain).Assembly.GetName().Version} (args: {string.Join(' ', args)})");
        TaskbarRestorer.RecoverIfStale();
        Update.UpdateService.CleanUpAfterUpdate();

        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnFatal(e.ExceptionObject as Exception, "AppDomain");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001 // Fluent theme for menus and the settings window
        app.ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            if (_running && Survive(e.Exception)) return;
            OnFatal(e.Exception, "Dispatcher");
            app.Shutdown(AppConstants.ExitCodes.Crash);
        };
        app.Startup += (_, _) =>
        {
            try
            {
                _host = new AppHost();
                _host.Start();
                _running = true;
            }
            catch (Exception ex)
            {
                OnFatal(ex, "Startup");
                app.Shutdown(AppConstants.ExitCodes.Crash);
            }
        };
        app.Exit += (_, _) =>
        {
            try { _host?.Dispose(); }
            catch (Exception ex) { Log.Error("Error during shutdown", ex); TaskbarRestorer.RecoverIfStale(); }
        };

        int code = app.Run();
        Log.Info($"Exit code {code}");
        Log.Flush(TimeSpan.FromSeconds(2));
        return code;
    }

    /// <summary>
    /// An exception from a click handler, a timer or a widget once the dock is running: log it and carry on.
    /// Restarting for each one brought the Windows taskbar back over a game (seven times on one day).
    /// </summary>
    /// <returns>False when too many came too fast; the caller then takes the crash path.</returns>
    private static bool Survive(Exception ex)
    {
        long now = Environment.TickCount64;
        while (UiFaultTimes.Count > 0 && now - UiFaultTimes.Peek() > AppConstants.UiFaults.WindowSeconds * 1000L) UiFaultTimes.Dequeue();
        UiFaultTimes.Enqueue(now);
        if (UiFaultTimes.Count > AppConstants.UiFaults.MaxSurvived) return false;
        Log.Error($"Unhandled exception on the UI thread; carrying on ({UiFaultTimes.Count} of {AppConstants.UiFaults.MaxSurvived} in {AppConstants.UiFaults.WindowSeconds} s)", ex);
        try { _host?.RecoverFromFault(); }
        catch (Exception recoverEx) { Log.Error("Recovering from the exception failed", recoverEx); return false; }
        return true;
    }

    /// <summary>Last line of defence: log and put the taskbar back before the process goes away.</summary>
    private static void OnFatal(Exception? ex, string source)
    {
        if (Interlocked.Exchange(ref _crashed, 1) == 1) return;
        Log.Error($"Unhandled exception ({source})", ex);
        try
        {
            var journal = TaskbarJournal.TryLoad();
            if (journal != null) TaskbarRestorer.Restore(journal, "crash");
        }
        catch (Exception restoreEx)
        {
            Log.Error("Restoring taskbar after crash failed; the watchdog will retry", restoreEx);
        }
        Log.Flush(TimeSpan.FromSeconds(2));
    }
}
