# Lists the top-level windows of a process with the flags that decide taskbar / Alt+Tab visibility.
# Usage: powershell -File build\list-windows.ps1 -Process Discord
param([Parameter(Mandatory)][string]$Process)
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class GdWin {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder b, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b, int n);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
  public static List<string> List(HashSet<uint> pids) {
    var rows = new List<string>();
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (!pids.Contains(pid)) return true;
      var t = new StringBuilder(256); GetWindowText(h, t, 256);
      var c = new StringBuilder(256); GetClassName(h, c, 256);
      long ex = GetWindowLongPtr(h, -20).ToInt64(), st = GetWindowLongPtr(h, -16).ToInt64();
      int cloak; DwmGetWindowAttribute(h, 14, out cloak, 4);
      rows.Add(string.Format("0x{0:X} pid={1} vis={2} min={3} cloak={4} owner=0x{5:X} style=0x{6:X8} ex=0x{7:X8} class={8} title={9}",
        h.ToInt64(), pid, IsWindowVisible(h), IsIconic(h), cloak, GetWindow(h, 4).ToInt64(), st, ex, c, t));
      return true;
    }, IntPtr.Zero);
    return rows;
  }
}
"@
$pids = New-Object 'System.Collections.Generic.HashSet[uint32]'
Get-Process -Name $Process -ErrorAction SilentlyContinue | ForEach-Object { [void]$pids.Add([uint32]$_.Id) }
if ($pids.Count -eq 0) { "no process named $Process"; exit 1 }
[GdWin]::List($pids) | Where-Object { $_ -match 'vis=True' -or $_ -match 'title=\S' }
