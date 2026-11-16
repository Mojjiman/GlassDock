using System.Runtime.InteropServices;
using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Input;

/// <summary>
/// WH_KEYBOARD_LL hook that turns Win+digit / Win+T / Win+B into Win+(private key), which we own through
/// RegisterHotKey. Explorer never sees the original combination, the Start menu does not open on Win
/// release (another key was seen while Win was down), and the UI thread receives a genuine WM_HOTKEY
/// that carries foreground-activation rights. If this process dies the hook disappears and Explorer's
/// native behaviour returns by itself.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int MsgTranslate = WM_APP + 1;
    private const int MsgReinstall = WM_APP + 2;
    private const int MsgStop = WM_APP + 3;
    private const int MsgFlush = WM_APP + 4;
    /// <summary>Unassigned VK used as a "menu mask" key when a slot falls back to direct dispatch.</summary>
    private const int VkMask = 0xE8;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Timer _refresh;
    private readonly Timer _flushTimer;
    private LowLevelKeyboardProc? _proc;
    private nint _hook;
    private uint _threadId;
    private volatile bool _winDown;
    private uint _winDownTime;
    private readonly bool[] _held = new bool[256];
    // From the swallowed digit until our injected private key comes back through this hook, modifier
    // key-ups are held back and replayed afterwards. When Win-up is already queued behind the digit
    // (fast typing, macro tools), SendInput lands the private key after it; holding Win-up keeps the
    // system's Win state down so the hotkey still matches.
    private volatile int _pendingTranslations;
    private readonly int[] _deferredUps = new int[8];
    private int _deferredCount;

    // vk → private key index (-1 = not ours). Replaced atomically by Configure().
    private volatile int[] _map = Enumerable.Repeat(-1, 256).ToArray();
    private volatile bool[] _directDispatch = new bool[12];
    private volatile int[] _privateVk = new int[12];
    private nint _directTarget;
    private int _directMessage;
    private volatile bool _suspended;

    /// <summary>
    /// Index posted with the direct message (lParam 1 = down, 0 = up) when the Win key changes state,
    /// so an auto-hidden dock can come out while Win is held.
    /// </summary>
    public const nuint WinKeyIndex = 0xFF;

    public KeyboardHook()
    {
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "GlassDock.KeyboardHook", Priority = ThreadPriority.Highest };
        _refresh = new Timer(_ => PostThreadMessage(_threadId, MsgReinstall, 0, 0), null, Timeout.Infinite, Timeout.Infinite);
        _flushTimer = new Timer(_ => PostThreadMessage(_threadId, MsgFlush, 1, 0), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Sets which keys are translated. <paramref name="vkToIndex"/> maps a virtual key to a private-key
    /// index (0..11). <paramref name="directTarget"/> receives <paramref name="directMessage"/>(index, modifiers)
    /// for indices whose hotkey could not be registered.
    /// </summary>
    public void Configure(IReadOnlyDictionary<int, int> vkToIndex, int[] privateVk, bool[] directDispatch, nint directTarget, int directMessage)
    {
        var map = Enumerable.Repeat(-1, 256).ToArray();
        foreach (var (vk, idx) in vkToIndex) map[vk] = idx;
        _directTarget = directTarget;
        _directMessage = directMessage;
        _directDispatch = directDispatch;
        _privateVk = privateVk;
        _map = map;
    }

    public void Start()
    {
        _thread.Start();
        _ready.Wait();
        var period = TimeSpan.FromMinutes(AppConstants.Input.HookRefreshMinutes);
        _refresh.Change(period, period);
    }

    /// <summary>Temporarily stop translating (e.g. fullscreen game) without tearing down the thread.</summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            Log.Info(value ? "Keyboard hook suspended" : "Keyboard hook resumed");
            PostThreadMessage(_threadId, MsgReinstall, 0, 0);
        }
    }

    /// <summary>Win+L etc. can swallow the Win key-up; call on session unlock/resume.</summary>
    public void ResetState()
    {
        _winDown = false;
        Array.Clear(_held);
        PostThreadMessage(_threadId, MsgReinstall, 0, 0);
    }

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookProc;
        Install();
        // Ensure the thread has a message queue before Start() returns.
        _ready.Set();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            switch (msg.message)
            {
                case MsgTranslate:
                    Log.Debug($"Translating shortcut index {(int)msg.wParam}");
                    InjectPrivateKey((int)msg.wParam);
                    break;
                case MsgFlush:
                    if (msg.wParam != 0 && _pendingTranslations > 0)
                        Log.Warn("Injected shortcut key did not come back through the hook in time; releasing held keys");
                    Flush();
                    break;
                case MsgReinstall:
                    Install();
                    break;
                case MsgStop:
                    Uninstall();
                    return;
                default:
                    TranslateMessage(msg);
                    DispatchMessage(msg);
                    break;
            }
        }
        Uninstall();
    }

    private void Install()
    {
        // Windows silently drops a low-level hook that times out too often; re-installing periodically
        // restores it without needing a health check (synthetic pings would reset the idle timer).
        nint old = _hook;
        if (_suspended)
        {
            _hook = 0;
        }
        else
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc!, GetModuleHandle(null), 0);
            if (_hook == 0) Log.Win32Failure("SetWindowsHookEx(WH_KEYBOARD_LL)");
            else Log.Debug("Keyboard hook installed");
        }
        if (old != 0 && !UnhookWindowsHookEx(old)) Log.Win32Failure("UnhookWindowsHookEx");
    }

    private void Uninstall()
    {
        if (_hook != 0 && !UnhookWindowsHookEx(_hook)) Log.Win32Failure("UnhookWindowsHookEx");
        _hook = 0;
    }

    // Runs for every key press system-wide: no allocation, no logging, no locks.
    private nint HookProc(int nCode, nuint wParam, nint lParam)
    {
        if (nCode < 0) return CallNextHookEx(0, nCode, wParam, lParam);
        ref readonly var k = ref Unsafe.AsKbd(lParam);
        int message = (int)wParam;
        bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        uint vk = k.vkCode;
        if (k.dwExtraInfo == InputInjector.Tag)
        {
            // The key-up of our injected key means the system has already matched the hotkey.
            if (!down && _pendingTranslations > 0 && IsEcho(vk) && --_pendingTranslations == 0)
                PostThreadMessage(_threadId, MsgFlush, 0, 0);
            return CallNextHookEx(0, nCode, wParam, lParam);
        }

        if (!down && _pendingTranslations > 0 && IsModifier(vk) && _deferredCount < _deferredUps.Length)
        {
            _deferredUps[_deferredCount++] = (int)vk;
            if (vk is VK_LWIN or VK_RWIN && _winDown) { _winDown = false; PostMessage(_directTarget, _directMessage, WinKeyIndex, 0); }
            return 1;
        }
        if (vk is VK_LWIN or VK_RWIN)
        {
            if (down && !_winDown) _winDownTime = k.time;
            if (down != _winDown) PostMessage(_directTarget, _directMessage, WinKeyIndex, down ? 1 : 0);
            _winDown = down;
            return CallNextHookEx(0, nCode, wParam, lParam); // never swallow Win key-down
        }
        if (vk > 255) return CallNextHookEx(0, nCode, wParam, lParam);
        // The matching key-up is swallowed even if Win was released first.
        if (!down && _held[vk]) { _held[vk] = false; return 1; }
        if (!_winDown) return CallNextHookEx(0, nCode, wParam, lParam);

        int idx = _map[vk];
        if (idx < 0) return CallNextHookEx(0, nCode, wParam, lParam);

        if (down)
        {
            // Win+L and similar can leave _winDown stale, so cross-check the async state when Win went
            // down a while ago. Not for a fresh press: within one input batch the async state lags.
            if (k.time - _winDownTime > AppConstants.Input.WinStaleCheckMs
                && (GetAsyncKeyState(VK_LWIN) & 0x8000) == 0 && (GetAsyncKeyState(VK_RWIN) & 0x8000) == 0)
            {
                _winDown = false;
                return CallNextHookEx(0, nCode, wParam, lParam);
            }
            if (!_held[vk])
            {
                _held[vk] = true;
                _pendingTranslations++;
                PostThreadMessage(_threadId, MsgTranslate, (nuint)idx, 0);
            }
            return 1; // swallow, including auto-repeat
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private void InjectPrivateKey(int idx)
    {
        // The echo of this tap in HookProc ends the deferral; the timer covers a tap that never returns.
        _flushTimer.Change(AppConstants.Input.EchoTimeoutMs, Timeout.Infinite);
        if (idx < 0 || idx >= _directDispatch.Length) { Flush(); return; }
        if (_directDispatch[idx])
        {
            PostMessage(_directTarget, _directMessage, (nuint)idx, CurrentModifiers());
            InputInjector.Tap(VkMask);
            return;
        }
        InputInjector.Tap(_privateVk[idx]);
    }

    // Hook thread only (the hook callback runs on this thread too), so no locking is needed.
    private void Flush()
    {
        _pendingTranslations = 0;
        for (int i = 0; i < _deferredCount; i++) InputInjector.KeyUp(_deferredUps[i]);
        _deferredCount = 0;
    }

    private bool IsEcho(uint vk)
    {
        if (vk == VkMask) return true;
        var keys = _privateVk;
        for (int i = 0; i < keys.Length; i++) if (keys[i] == vk) return true;
        return false;
    }

    private static bool IsModifier(uint vk) =>
        vk is VK_LWIN or VK_RWIN or VK_SHIFT or VK_CONTROL or VK_MENU or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static nint CurrentModifiers()
    {
        uint mods = 0;
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) mods |= MOD_SHIFT;
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) mods |= MOD_CONTROL;
        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) mods |= MOD_ALT;
        return (nint)mods;
    }

    public void Dispose()
    {
        _refresh.Dispose();
        _flushTimer.Dispose();
        if (_threadId != 0) PostThreadMessage(_threadId, MsgStop, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    private static unsafe class Unsafe
    {
        public static ref readonly KBDLLHOOKSTRUCT AsKbd(nint p) => ref *(KBDLLHOOKSTRUCT*)p;
    }
}
