using System.Diagnostics;
using System.Text.Json;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Taskbar;
using static GlassDock.Interop.Native;

namespace GlassDock.Watchdog;

/// <summary>
/// <c>GlassDock.exe --watchdog --pid N</c>: a tiny process (never loads WPF) that waits for the dock to
/// exit, restores the taskbar if the dock did not, and relaunches it after a crash.
/// </summary>
public static class WatchdogMain
{
    public static int Run(string[] args)
    {
        Log.Initialize("watchdog");
        int idx = Array.IndexOf(args, "--pid");
        if (idx < 0 || idx + 1 >= args.Length || !uint.TryParse(args[idx + 1], out uint pid))
        {
            Log.Error("Watchdog started without --pid");
            return 1;
        }

        nint h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0)
        {
            Log.Win32Failure($"OpenProcess({pid})");
            TaskbarRestorer.RecoverIfStale();
            return 1;
        }

        Log.Info($"Watching dock pid {pid}");
        WaitForSingleObject(h, INFINITE);
        GetExitCodeProcess(h, out uint code);
        CloseHandle(h);
        Log.Info($"Dock pid {pid} exited with code {code}");

        var journal = TaskbarJournal.TryLoad();
        if (journal != null && !journal.IsOwnerAlive())
            TaskbarRestorer.Restore(journal, $"dock exited with code {code}");

        if (code != AppConstants.ExitCodes.UserQuit && code != AppConstants.ExitCodes.AlreadyRunning)
        {
            if (CrashLoop.ShouldRestart())
            {
                Log.Info("Relaunching dock after abnormal exit");
                try
                {
                    Process.Start(new ProcessStartInfo(AppPaths.ExePath, "--relaunched") { UseShellExecute = false });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Log.Error("Relaunch failed", ex);
                }
            }
            else
            {
                Log.Error($"Dock crashed {AppConstants.CrashLoop.MaxRestarts} times within {AppConstants.CrashLoop.WindowSeconds}s; not restarting");
                ShowCrashLoopMessage();
            }
        }
        Log.Flush(TimeSpan.FromSeconds(2));
        return 0;
    }

    private static void ShowCrashLoopMessage()
    {
        // MessageBox from user32 keeps the watchdog free of WPF/WinForms.
        MessageBoxW(0,
            $"GlassDock이 짧은 시간에 여러 번 종료되어 다시 시작하지 않았습니다.\n작업 표시줄은 원래대로 복원했습니다.\n\n로그: {AppPaths.LogsDir}",
            "GlassDock", 0x30);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}

/// <summary>Restart timestamps persisted across watchdog instances (each relaunch gets a new watchdog).</summary>
internal static class CrashLoop
{
    public static bool ShouldRestart()
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(AppConstants.CrashLoop.WindowSeconds);
        List<DateTime> stamps = [];
        try
        {
            if (File.Exists(AppPaths.CrashLoopFile))
                stamps = JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(AppPaths.CrashLoopFile)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn("Crash-loop file unreadable; starting a new count", ex);
        }
        stamps = stamps.Where(s => now - s < window).ToList();
        if (stamps.Count >= AppConstants.CrashLoop.MaxRestarts) return false;
        stamps.Add(now);
        try
        {
            Directory.CreateDirectory(AppPaths.StateDir);
            AppPaths.WriteAtomic(AppPaths.CrashLoopFile, JsonSerializer.Serialize(stamps));
        }
        catch (IOException ex) { Log.Warn("Writing crash-loop file failed", ex); }
        return true;
    }
}
