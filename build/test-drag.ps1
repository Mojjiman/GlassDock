# Drags with the real mouse from (X,Y) by Dx (and Dy) pixels, optionally screenshots mid-drag, then either drops
# there or returns to the start before releasing (-Return: no change). Restores the cursor afterwards.
# Usage: powershell -File build\test-drag.ps1 -X 1580 -Y 2098 -Dx 140 [-Return] [-Shot mid.png] [-HoldMs 800]
# -HoldMs: pause at the end point before the shot and drop; over 600 ms arms grouping onto the app there.
# -Dy: also move vertically, e.g. to drag the dock from empty glass to another screen edge.
param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y, [Parameter(Mandatory)][int]$Dx, [int]$Dy = 0,
      [switch]$Return, [string]$Shot = "", [int]$Steps = 24, [int]$StepMs = 12, [int]$HoldMs = 300)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GdMouse {
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint data; public uint flags; public uint time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public long p0; [FieldOffset(8)] public long p1; [FieldOffset(16)] public long p2; [FieldOffset(24)] public long p3; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public int type; public U u; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int size);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  public static void Button(bool down) {
    var i = new INPUT { type = 0 }; i.u.mi.flags = down ? 0x0002u : 0x0004u;
    if (SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))) != 1) throw new System.ComponentModel.Win32Exception();
  }
}
"@
[void][GdMouse]::SetProcessDpiAwarenessContext([IntPtr]-4)
$saved = New-Object GdMouse+POINT; [void][GdMouse]::GetCursorPos([ref]$saved)
function Glide([int]$fromX, [int]$fromY, [int]$toX, [int]$toY) {
    for ($s = 1; $s -le $Steps; $s++) {
        [void][GdMouse]::SetCursorPos([int]($fromX + ($toX - $fromX) * $s / $Steps), [int]($fromY + ($toY - $fromY) * $s / $Steps))
        Start-Sleep -Milliseconds $StepMs
    }
}
[void][GdMouse]::SetCursorPos($X, $Y); Start-Sleep -Milliseconds 150
[GdMouse]::Button($true)
try {
    Glide $X $Y ($X + $Dx) ($Y + $Dy)
    Start-Sleep -Milliseconds $HoldMs
    if ($Shot) { & (Join-Path $PSScriptRoot "screenshot.ps1") -Out $Shot -BottomPx 130 -CenterWidthPx 1300 | Out-Null }
    if ($Return) { Glide ($X + $Dx) ($Y + $Dy) $X $Y; Start-Sleep -Milliseconds 150 }
}
finally { [GdMouse]::Button($false); Start-Sleep -Milliseconds 100; [void][GdMouse]::SetCursorPos($saved.X, $saved.Y) }
"drag done"
