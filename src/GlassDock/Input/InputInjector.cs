using System.Runtime.InteropServices;
using GlassDock.Config;
using GlassDock.Logging;
using static GlassDock.Interop.Native;

namespace GlassDock.Input;

/// <summary>Synthetic keyboard input, tagged so our own low-level hook passes it through untouched.</summary>
public static class InputInjector
{
    public static nuint Tag => AppConstants.Input.InjectTag;

    /// <summary>Presses the keys in order and releases them in reverse, e.g. Chord(VK_LWIN, 'X').</summary>
    public static void Chord(params int[] vks)
    {
        var inputs = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++)
        {
            inputs[i] = Key(vks[i], up: false);
            inputs[inputs.Length - 1 - i] = Key(vks[i], up: true);
        }
        Send(inputs);
    }

    public static void Tap(int vk) => Send([Key(vk, false), Key(vk, true)]);

    public static void KeyUp(int vk) => Send([Key(vk, true)]);

    private static INPUT Key(int vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (vk is VK_LWIN or VK_RWIN or 0xA3 or 0xA5) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags, dwExtraInfo = Tag } },
        };
    }

    private static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) Log.Win32Failure($"SendInput ({sent}/{inputs.Length})");
    }
}
