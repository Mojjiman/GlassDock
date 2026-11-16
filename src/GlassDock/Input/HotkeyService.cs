using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Input;

public enum HotkeyAction { Slot, FocusApps, FocusTray }

public readonly record struct HotkeyCommand(HotkeyAction Action, int Slot, uint Modifiers)
{
    public bool Shift => (Modifiers & MOD_SHIFT) != 0;
    public bool Ctrl => (Modifiers & MOD_CONTROL) != 0;
    public bool Alt => (Modifiers & MOD_ALT) != 0;
}

/// <summary>
/// Owns the shortcut hotkeys and raises <see cref="Command"/> on the UI thread. Each shortcut is, in order
/// of preference, the real combination (Win+1 itself, taken over from Explorer, see
/// <see cref="ExplorerHotkeys"/>), or a private key (Win+F13..F24 with modifier variants) that the
/// keyboard hook translates the real combination into.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int SlotCount = 10;
    private const int IndexWinT = 10;
    private const int IndexWinB = 11;
    private const int IndexCount = 12;
    private static readonly uint[] SlotCombos = [0, MOD_SHIFT, MOD_CONTROL, MOD_ALT, MOD_CONTROL | MOD_SHIFT];
    private static readonly uint[] WinTCombos = [0, MOD_SHIFT];
    private static readonly uint[] WinBCombos = [0];

    private readonly nint _hwnd;
    private readonly int _directMessage;
    private readonly List<int> _registered = [];
    private readonly bool[] _direct = new bool[IndexCount];
    /// <summary>The index is registered as its real combination, so the hook leaves its key alone.</summary>
    private readonly bool[] _own = new bool[IndexCount];
    private readonly int[] _privateVk = new int[IndexCount];
    private readonly HashSet<int> _usedVks = [];
    private KeyboardHook? _hook;

    public event Action<HotkeyCommand>? Command;
    /// <summary>The Win key went down (true) or up (false); only while shortcuts are being translated.</summary>
    public event Action<bool>? WinKeyChanged;

    /// <param name="hwnd">Window that receives WM_HOTKEY (the shell host window).</param>
    /// <param name="directMessage">Message id the hook posts for indices without a registered hotkey.</param>
    public HotkeyService(nint hwnd, int directMessage)
    {
        _hwnd = hwnd;
        _directMessage = directMessage;
    }

    public void Apply(ShortcutSettings s)
    {
        Stop();
        var wanted = new List<(int Index, int Vk, uint[] Combos)>();
        if (s.WinNumberMode == "dock")
            for (int i = 0; i < SlotCount; i++) wanted.Add((i, i == 9 ? '0' : '1' + i, SlotCombos));
        if (s.InterceptWinT) wanted.Add((IndexWinT, 'T', WinTCombos));
        if (s.InterceptWinB) wanted.Add((IndexWinB, 'B', WinBCombos));

        if (wanted.Count == 0)
        {
            Log.Info("No shortcuts intercepted; Explorer handles all of them");
            return;
        }
        if (s.TakeOverExplorerHotkeys) RegisterOwn(wanted);
        var translated = wanted.Where(w => !_own[w.Index]).ToList();
        SlotsReal = translated.All(w => w.Index >= SlotCount);
        RegisterVerified([.. translated.Select(w => (w.Index, w.Combos))]);
        // Started even when every shortcut is a real hotkey: it also reports the Win key itself
        // (auto-hide reveal, closing the preview when Win is released).
        _hook = new KeyboardHook();
        _hook.Configure(translated.ToDictionary(w => w.Vk, w => w.Index), (int[])_privateVk.Clone(), (bool[])_direct.Clone(), _hwnd, _directMessage);
        _hook.Start();
        Log.Info($"Shortcuts: {wanted.Count - translated.Count} real hotkeys, {translated.Count} translated by the hook; private keys: {string.Join(",", translated.Select(w => _privateVk[w.Index].ToString("X2")))}; direct-dispatch indices: {string.Join(",", Enumerable.Range(0, IndexCount).Where(i => _direct[i]))}");
    }

    /// <summary>
    /// Win+1..0 do not depend on the hook, so it can pause in a fullscreen game without breaking them.
    /// Win+T and Win+B act on the dock, which is hidden there anyway.
    /// </summary>
    public bool SlotsReal { get; private set; }

    /// <summary>
    /// Registers each shortcut as its real combination. Those Explorer holds are released from Explorer
    /// first; any that still fail stay with the hook.
    /// </summary>
    private void RegisterOwn(List<(int Index, int Vk, uint[] Combos)> wanted)
    {
        var taken = wanted.Where(w => !TryRegisterReal(w.Index, w.Vk, w.Combos)).ToList();
        if (taken.Count == 0) return;
        ExplorerHotkeys.Release([.. taken.SelectMany(w => w.Combos.Select(c => (c, w.Vk)))]);
        foreach (var w in taken)
            if (!TryRegisterReal(w.Index, w.Vk, w.Combos))
                Log.Warn($"Win+{(char)w.Vk} is still held by another program; the keyboard hook translates it instead");
    }

    private bool TryRegisterReal(int index, int vk, uint[] combos)
    {
        var ids = new List<int>();
        for (int c = 0; c < combos.Length; c++)
        {
            int id = HotkeyId(index, c);
            if (!RegisterHotKey(_hwnd, id, MOD_WIN | MOD_NOREPEAT | combos[c], (uint)vk))
            {
                foreach (var done in ids) UnregisterHotKey(_hwnd, done);
                return false;
            }
            ids.Add(id);
        }
        _registered.AddRange(ids);
        _own[index] = true;
        return true;
    }

    /// <summary>
    /// Registers every index, then sends each private key once and moves indices whose WM_HOTKEY never
    /// arrives to the next candidate. RegisterHotKey succeeding is not enough: another program's
    /// keyboard hook can swallow the key (seen with Win+F20).
    /// </summary>
    private void RegisterVerified(List<(int Index, uint[] Combos)> wanted)
    {
        var silentKeys = new HashSet<int>();
        foreach (var (index, combos) in wanted) Register(index, combos, silentKeys);
        for (int round = 0; round < AppConstants.Input.ProbeRounds; round++)
        {
            var silent = Probe(wanted.Select(w => w.Index).Where(i => !_direct[i]));
            if (silent.Count == 0) return;
            foreach (var (index, combos) in wanted)
            {
                if (!silent.Contains(index)) continue;
                int vk = _privateVk[index];
                Log.Warn($"Win+0x{vk:X2} never arrives (another program takes it); moving shortcut index {index} to another key");
                silentKeys.Add(vk);
                Unregister(index, combos);
                Register(index, combos, silentKeys);
            }
        }
        Log.Warn("Some shortcut keys still could not be verified after several attempts");
    }

    private List<int> Probe(IEnumerable<int> indices)
    {
        var pending = new Dictionary<int, int>();
        foreach (int index in indices)
        {
            pending[HotkeyId(index, 0)] = index;
            InputInjector.Chord(VK_LWIN, _privateVk[index]);
        }
        long deadline = Environment.TickCount64 + AppConstants.Input.ProbeWaitMs;
        while (pending.Count > 0 && Environment.TickCount64 < deadline)
        {
            while (PeekMessage(out var msg, _hwnd, WM_HOTKEY, WM_HOTKEY, PM_REMOVE)) pending.Remove((int)msg.wParam);
            if (pending.Count > 0) Thread.Sleep(10);
        }
        return [.. pending.Values];
    }

    private void Unregister(int index, uint[] combos)
    {
        for (int c = 0; c < combos.Length; c++)
        {
            int id = HotkeyId(index, c);
            if (_registered.Remove(id) && !UnregisterHotKey(_hwnd, id)) Log.Win32Failure($"UnregisterHotKey({id})");
        }
        _usedVks.Remove(_privateVk[index]);
    }

    private void Register(int index, uint[] combos, HashSet<int> silentKeys)
    {
        foreach (int vk in AppConstants.Input.PrivateKeyCandidates)
        {
            if (_usedVks.Contains(vk) || silentKeys.Contains(vk)) continue;
            var ids = new List<int>();
            bool allOk = true;
            for (int c = 0; c < combos.Length; c++)
            {
                int id = HotkeyId(index, c);
                if (RegisterHotKey(_hwnd, id, MOD_WIN | MOD_NOREPEAT | combos[c], (uint)vk)) ids.Add(id);
                else { allOk = false; break; }
            }
            if (allOk)
            {
                _registered.AddRange(ids);
                _usedVks.Add(vk);
                _privateVk[index] = vk;
                _direct[index] = false;
                return;
            }
            foreach (var id in ids) UnregisterHotKey(_hwnd, id);
        }
        // Every candidate is taken: the hook posts the command itself (activation then relies on ForegroundActivator).
        Log.Warn($"No free private key for shortcut index {index}; using direct dispatch");
        _direct[index] = true;
    }

    private static int HotkeyId(int index, int combo) => 0x100 + index * 8 + combo;

    /// <summary>Handle WM_HOTKEY from the host window. Returns true when it was ours.</summary>
    public bool HandleHotkey(int id, nint lParam)
    {
        Log.Debug($"WM_HOTKEY id 0x{id:X} lParam 0x{lParam:X}");
        int rel = id - 0x100;
        if (rel < 0 || rel >= IndexCount * 8) return false;
        int index = rel / 8;
        uint mods = (uint)(lParam.ToInt64() & 0xFFFF) & ~MOD_WIN;
        Raise(index, mods);
        return true;
    }

    /// <summary>Handle the direct-dispatch message posted by the hook.</summary>
    public void HandleDirect(nuint wParam, nint lParam)
    {
        if (wParam == KeyboardHook.WinKeyIndex) WinKeyChanged?.Invoke(lParam != 0);
        else Raise((int)wParam, (uint)lParam);
    }

    private void Raise(int index, uint mods)
    {
        var cmd = index switch
        {
            < SlotCount => new HotkeyCommand(HotkeyAction.Slot, index, mods),
            IndexWinT => new HotkeyCommand(HotkeyAction.FocusApps, 0, mods),
            _ => new HotkeyCommand(HotkeyAction.FocusTray, 0, mods),
        };
        Command?.Invoke(cmd);
    }

    public bool Suspended
    {
        set { if (_hook != null) _hook.Suspended = value; }
    }

    public void ResetState() => _hook?.ResetState();

    private void Stop()
    {
        _hook?.Dispose();
        _hook = null;
        foreach (var id in _registered)
            if (!UnregisterHotKey(_hwnd, id)) Log.Win32Failure($"UnregisterHotKey({id})");
        _registered.Clear();
        _usedVks.Clear();
        Array.Clear(_direct);
        Array.Clear(_own);
        SlotsReal = false;
        Array.Clear(_privateVk);
    }

    public void Dispose() => Stop();
}
