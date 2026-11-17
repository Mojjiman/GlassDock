# Prints who receives apps' taskbar registrations. ITaskbarList (AddTab/DeleteTab, overlay badges,
# progress) sends to the TaskbandHWND property of the topmost Shell_TrayWnd. With GlassDock running that
# is its fake tray, pointed at GlassDock's relay, which passes AddTab/DeleteTab on to Explorer's task band.
# If they stop reaching Explorer, windows drop out of Alt+Tab and the native taskbar.
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class GdTaskman {
  [DllImport("user32.dll")] public static extern IntPtr GetTaskmanWindow();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetProp(IntPtr h, string name);
}
"@
function Describe([IntPtr]$h) {
    if ($h -eq [IntPtr]::Zero) { return "none" }
    $p = [uint32]0; [void][GdTaskman]::GetWindowThreadProcessId($h, [ref]$p)
    $cls = New-Object System.Text.StringBuilder 256; [void][GdTaskman]::GetClassName($h, $cls, 256)
    "0x{0:X} alive={1} process={2} ({3}) class={4}" -f $h.ToInt64(), [GdTaskman]::IsWindow($h), (Get-Process -Id $p -ErrorAction SilentlyContinue).ProcessName, $p, $cls
}
"taskman window: " + (Describe ([GdTaskman]::GetTaskmanWindow()))
$tray = [IntPtr]::Zero
$first = $true
while (($tray = [GdTaskman]::FindWindowEx([IntPtr]::Zero, $tray, "Shell_TrayWnd", $null)) -ne [IntPtr]::Zero) {
    $label = if ($first) { "Shell_TrayWnd (topmost, used by ITaskbarList)" } else { "Shell_TrayWnd" }
    $first = $false
    "${label}: " + (Describe $tray)
    "  TaskbandHWND -> " + (Describe ([GdTaskman]::GetProp($tray, "TaskbandHWND")))
}
