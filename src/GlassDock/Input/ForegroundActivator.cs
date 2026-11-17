using System.Windows.Threading;
using GlassDock.Config;
using GlassDock.Logging;
using GlassDock.Shell;
using static GlassDock.Interop.Native;

namespace GlassDock.Input;

/// <summary>
/// Brings a window to the foreground despite the foreground lock. Tries the cheap documented path
/// first and escalates; logs which step worked so field issues can be diagnosed.
/// </summary>
public static class ForegroundActivator
{
    // One Esc per closing of Start: the press on the dock and the click that follows both ask, and a
    // second Esc after Start has gone would land in the window behind it (a game's menu).
    private static long _dismissedUntil;

    /// <summary>The activation in progress; a second click on the same window while it runs is ignored.</summary>
    private static Attempt? _pending;

    /// <summary>
    /// Starts bringing <paramref name="hwnd"/> forward and returns at once. Each step gets
    /// <see cref="AppConstants.Input.ActivateStepMs"/> to show in <c>GetForegroundWindow</c> before the next,
    /// stronger one, polled on the UI thread's timer: waiting in place froze the dock for up to 0.7 s,
    /// and a game that is restoring its fullscreen mode often takes longer than a short wait.
    /// </summary>
    public static void Activate(nint hwnd)
    {
        if (!IsWindow(hwnd)) return;
        if (_pending is { } p)
        {
            if (p.Hwnd == hwnd) return;
            p.Stop();
        }
        if (DismissLauncher()) { ActivateOnceLauncherGone(hwnd, "window"); return; }
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
        _pending = new Attempt(hwnd);
    }

    private sealed class Attempt
    {
        public readonly nint Hwnd;
        private readonly DispatcherTimer _timer;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private int _step;
        private long _stepEnds;

        public Attempt(nint hwnd)
        {
            Hwnd = hwnd;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppConstants.Input.ActivatePollMs) };
            _timer.Tick += (_, _) => Tick();
            RunStep();
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            if (_pending == this) _pending = null;
        }

        private void Tick()
        {
            if (!IsWindow(Hwnd)) { Stop(); return; }
            if (GetForegroundWindow() == Hwnd)
            {
                Stop();
                if (_step > 0 || _clock.ElapsedMilliseconds > AppConstants.Input.ActivateStepMs)
                    Log.Debug($"Activated 0x{Hwnd:X} at step {_step} after {_clock.ElapsedMilliseconds} ms");
                return;
            }
            if (_clock.ElapsedMilliseconds < _stepEnds) return;
            if (_step == LastStep)
            {
                Stop();
                Log.Warn($"Could not activate {Describe(Hwnd)} in {_clock.ElapsedMilliseconds} ms; foreground {Describe(GetForegroundWindow())}");
                return;
            }
            _step++;
            RunStep();
        }

        private const int LastStep = 3;

        private void RunStep()
        {
            // A step whose call already failed moves on at the next tick instead of waiting out its time.
            bool sent = _step switch
            {
                0 => SetForegroundWindow(Hwnd),
                1 => AttachedSet(Hwnd),
                // An Alt press counts as user input and lifts the foreground lock (AutoHotkey's last resort).
                2 => AltSet(Hwnd),
                _ => Switch(Hwnd),
            };
            long wait = _step == LastStep ? AppConstants.Input.ActivateGiveUpMs : AppConstants.Input.ActivateStepMs;
            _stepEnds = sent ? _clock.ElapsedMilliseconds + wait : 0;
        }

        private static bool AttachedSet(nint hwnd)
        {
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint me = GetCurrentThreadId();
            if (fgThread == 0 || fgThread == me || !AttachThreadInput(me, fgThread, true)) return false;
            try
            {
                BringWindowToTop(hwnd);
                return SetForegroundWindow(hwnd);
            }
            finally { AttachThreadInput(me, fgThread, false); }
        }

        private static bool AltSet(nint hwnd)
        {
            InputInjector.Tap(VK_MENU);
            return SetForegroundWindow(hwnd);
        }

        private static bool Switch(nint hwnd)
        {
            SwitchToThisWindow(hwnd, true);
            return true;
        }
    }

    /// <summary>
    /// Closes Start or search if it is in front. While it is open, every way of taking the foreground
    /// fails (seen with Win in a game, then a click on the dock) and it stays on top of our popup.
    /// Esc is what a user would press; it goes to Start, which is foreground, so it cannot reach a game.
    /// Returns at once; the caller waits for it to go with <see cref="ActivateOnceLauncherGone"/>.
    /// </summary>
    /// <returns>Whether Start or search was in front.</returns>
    public static bool DismissLauncher()
    {
        if (!FullscreenMonitor.IsLauncher(GetForegroundWindow())) return false;
        long now = Environment.TickCount64;
        if (now > _dismissedUntil)
        {
            InputInjector.Tap(VK_ESCAPE);
            _dismissedUntil = now + AppConstants.Fullscreen.LauncherCloseWaitMs;
        }
        return true;
    }

    private static string Describe(nint hwnd) =>
        $"0x{hwnd:X} class={GetClassName(hwnd)} exe={Path.GetFileName(GetProcessPath(GetWindowProcessId(hwnd)))}";

    /// <summary>
    /// After <see cref="DismissLauncher"/>: Start fades out for about 200 ms before it lets go
    /// of the foreground. Polls until it has, then activates <paramref name="hwnd"/>.
    /// </summary>
    public static void ActivateOnceLauncherGone(nint hwnd, string what, Func<bool>? cancelled = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppConstants.Fullscreen.LauncherPollMs) };
        poll.Tick += (_, _) =>
        {
            if (cancelled?.Invoke() == true) { poll.Stop(); return; }
            nint fg = GetForegroundWindow();
            if (FullscreenMonitor.IsLauncher(fg))
            {
                if (clock.ElapsedMilliseconds < AppConstants.Fullscreen.LauncherCloseWaitMs) return;
                poll.Stop();
                Log.Warn($"Start or search did not close on Esc; {what} left behind it ({Describe(fg)})");
                return;
            }
            poll.Stop();
            if (fg != hwnd) Activate(hwnd);
            Log.Debug($"Start gone after {clock.ElapsedMilliseconds} ms ({what}); foreground {Describe(GetForegroundWindow())}");
        };
        poll.Start();
    }
}
