# Sends physical-looking (untagged) Win+<digit> chords and reports the foreground window afterwards.
# Usage: powershell -File build\test-winkeys.ps1 -Digit 5 [-Shift]
param([int]$Digit = 1, [int]$Raw = 0, [switch]$Shift, [int]$WaitMs = 700)
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class GdKeys {
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public long pad0; [FieldOffset(8)] public long pad1; [FieldOffset(16)] public long pad2; [FieldOffset(24)] public long pad3; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public int type; public U u; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int size);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder b, int n);
  static INPUT K(ushort vk, bool up) { var i = new INPUT { type = 1 }; i.u.ki.vk = vk; i.u.ki.flags = (uint)((up ? 2 : 0) | ((vk == 0x5B) ? 1 : 0)); return i; }
  public static void Chord(ushort[] keys) {
    var list = new System.Collections.Generic.List<INPUT>();
    foreach (var k in keys) list.Add(K(k, false));
    for (int j = keys.Length - 1; j >= 0; j--) list.Add(K(keys[j], true));
    uint sent = SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT))); if (sent != list.Count) throw new System.ComponentModel.Win32Exception();
  }
  public static string Fg() {
    var h = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(h, out pid);
    var c = new StringBuilder(256); GetClassName(h, c, 256); var t = new StringBuilder(256); GetWindowText(h, t, 256);
    string name = ""; try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch {}
    return name + " | " + c + " | " + t;
  }
}
"@
$keys = @([uint16]0x5B)
if ($Shift) { $keys += [uint16]0x10 }
if ($Raw -gt 0) { $keys += [uint16]$Raw } else { $keys += [uint16](0x30 + $Digit) }
[GdKeys]::Chord($keys)
Start-Sleep -Milliseconds $WaitMs
"foreground after Win+$Digit$(if ($Shift) {' (Shift)'}): $([GdKeys]::Fg())"


