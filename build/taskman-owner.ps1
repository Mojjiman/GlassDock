# Prints which process owns the shell's "taskman" window. ITaskbarList AddTab/DeleteTab calls from
# apps are delivered there, so it must be Explorer's taskbar or Alt+Tab and the taskbar miss them.
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class GdTaskman {
  [DllImport("user32.dll")] public static extern IntPtr GetTaskmanWindow();
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetProp(IntPtr h, string name);
}
"@
$h = [GdTaskman]::GetTaskmanWindow()
if ($h -eq [IntPtr]::Zero) { "taskman window: none"; exit }
$pid2 = [uint32]0; [void][GdTaskman]::GetWindowThreadProcessId($h, [ref]$pid2)
$cls = New-Object System.Text.StringBuilder 256; [void][GdTaskman]::GetClassName($h, $cls, 256)
$name = (Get-Process -Id $pid2 -ErrorAction SilentlyContinue).ProcessName
$band = [GdTaskman]::GetProp($h, "TaskbandHWND")
"taskman window: 0x{0:X} alive={1} process={2} ({3}) class={4} TaskbandHWND=0x{5:X}" -f $h.ToInt64(), [GdTaskman]::IsWindow($h), $name, $pid2, $cls, $band.ToInt64()
