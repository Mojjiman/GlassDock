# Opens the Alt+Tab switcher, screenshots the screen while it is up, then cancels with Esc so the
# foreground window does not change.
# Usage: powershell -File build\test-alttab.ps1 -Out alttab.png
param([string]$Out = "alttab.png", [int]$WaitMs = 900)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GdAltTab {
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public long p0; [FieldOffset(8)] public long p1; [FieldOffset(16)] public long p2; [FieldOffset(24)] public long p3; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public int type; public U u; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int size);
  public static void Key(ushort vk, bool up) {
    var i = new INPUT { type = 1 }; i.u.ki.vk = vk; i.u.ki.flags = up ? 2u : 0u;
    if (SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))) != 1) throw new System.ComponentModel.Win32Exception();
  }
}
"@
$VK_MENU = 0x12; $VK_TAB = 0x09; $VK_ESCAPE = 0x1B
[GdAltTab]::Key($VK_MENU, $false)
try {
    [GdAltTab]::Key($VK_TAB, $false); [GdAltTab]::Key($VK_TAB, $true)
    Start-Sleep -Milliseconds $WaitMs
    & (Join-Path $PSScriptRoot "screenshot.ps1") -Out $Out | Out-Null
    [GdAltTab]::Key($VK_ESCAPE, $false); [GdAltTab]::Key($VK_ESCAPE, $true)
}
finally { [GdAltTab]::Key($VK_MENU, $true) }
"saved $Out"
