# Prints the state GlassDock changes: native taskbar visibility, auto-hide flag, primary work area.
# Usage: powershell -NoProfile -File build\verify.ps1 [-WatchSeconds 30]
param([int]$WatchSeconds = 0)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GdProbe {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int msg; public int edge; public RECT rc; public IntPtr lParam; }
  [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT mon; public RECT work; public int flags; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, int f);
  [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO i);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(int m, ref APPBARDATA d);
}
"@
[GdProbe]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null

function Get-State {
    $abd = New-Object GdProbe+APPBARDATA
    $abd.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($abd)
    $state = [GdProbe]::SHAppBarMessage(4, [ref]$abd).ToUInt32()
    $mi = New-Object GdProbe+MONITORINFO
    $mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($mi)
    [GdProbe]::GetMonitorInfo([GdProbe]::MonitorFromPoint((New-Object GdProbe+POINT), 1), [ref]$mi) | Out-Null
    $proc = Get-Process GlassDock -ErrorAction SilentlyContinue
    [pscustomobject]@{
        TrayVisible = [GdProbe]::IsWindowVisible([GdProbe]::FindWindow("Shell_TrayWnd", $null))
        AutoHide    = ($state -band 1) -ne 0
        Monitor     = "$($mi.mon.L),$($mi.mon.T)-$($mi.mon.R),$($mi.mon.B)"
        WorkArea    = "$($mi.work.L),$($mi.work.T)-$($mi.work.R),$($mi.work.B)"
        GlassDock   = ($proc | ForEach-Object { "pid $($_.Id) $([math]::Round($_.PrivateMemorySize64 / 1MB)) MB" }) -join "; "
    }
}

if ($WatchSeconds -le 0) { Get-State | Format-List; exit }

# Samples tray visibility at ~20 Hz; any visible sample is a peek/flicker.
$visible = 0; $samples = 0
$end = (Get-Date).AddSeconds($WatchSeconds)
$h = [GdProbe]::FindWindow("Shell_TrayWnd", $null)
while ((Get-Date) -lt $end) {
    if ([GdProbe]::IsWindowVisible($h)) { $visible++ }
    $samples++
    Start-Sleep -Milliseconds 50
}
"samples=$samples visible=$visible"
