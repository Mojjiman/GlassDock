using System.Windows.Interop;
using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Shell;

/// <summary>
/// Hidden top-level window for system broadcasts (message-only windows do not get them):
/// TaskbarCreated, display/settings changes, session and power events, hotkeys.
/// </summary>
public sealed class ShellHostWindow : IDisposable
{
    public const int MsgHotkeyDirect = WM_APP + 0x40;

    private readonly HwndSource _source;
    private readonly int _taskbarCreatedMsg;
    private readonly int _openSettingsMsg;
    private readonly int _quitMsg;

    public nint Handle => _source.Handle;

    public event Action? TaskbarCreated;
    public event Action? DisplayChanged;
    public event Action<string?>? SettingChanged;
    public event Action? WallpaperChanged;
    public event Action? SessionUnlocked;
    public event Action? SessionLocked;
    public event Action? Resumed;
    public event Action? SessionEnding;
    public event Action? OpenSettingsRequested;
    public event Action? QuitRequested;
    public Func<int, nint, bool>? HotkeyHandler;
    public Action<nuint, nint>? DirectHotkeyHandler;

    public ShellHostWindow()
    {
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        _openSettingsMsg = RegisterWindowMessage(AppConstants.OpenSettingsMessage);
        _quitMsg = RegisterWindowMessage(AppConstants.QuitMessage);
        var p = new HwndSourceParameters("GlassDock.ShellHost")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        if (!WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION))
            Log.Win32Failure("WTSRegisterSessionNotification");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMsg) { TaskbarCreated?.Invoke(); return 0; }
        if (msg == _openSettingsMsg) { OpenSettingsRequested?.Invoke(); handled = true; return 0; }
        if (msg == _quitMsg) { QuitRequested?.Invoke(); handled = true; return 0; }
        switch (msg)
        {
            case WM_HOTKEY:
                handled = HotkeyHandler?.Invoke((int)wParam, lParam) ?? false;
                break;
            case MsgHotkeyDirect:
                DirectHotkeyHandler?.Invoke((nuint)wParam, lParam);
                handled = true;
                break;
            case WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                break;
            case WM_SETTINGCHANGE:
                if ((int)wParam == SPI_SETDESKWALLPAPER) WallpaperChanged?.Invoke();
                SettingChanged?.Invoke(lParam != 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) : null);
                break;
            case WM_WTSSESSION_CHANGE:
                if ((int)wParam == WTS_SESSION_UNLOCK) SessionUnlocked?.Invoke();
                else if ((int)wParam == WTS_SESSION_LOCK) SessionLocked?.Invoke();
                break;
            case WM_POWERBROADCAST:
                if ((int)wParam == PBT_APMRESUMEAUTOMATIC) Resumed?.Invoke();
                break;
            case WM_ENDSESSION:
                if (wParam != 0) SessionEnding?.Invoke();
                break;
        }
        return 0;
    }

    public void Dispose()
    {
        WTSUnRegisterSessionNotification(Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
