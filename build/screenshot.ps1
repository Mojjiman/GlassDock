# Captures the primary screen (or its bottom strip) to a PNG for visual checks.
# -CenterWidthPx keeps only a centred horizontal slice, e.g. just the dock on a wide monitor.
param([string]$Out = "$env:TEMP\glassdock-shot.png", [int]$BottomPx = 0, [int]$CenterWidthPx = 0)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System.Runtime.InteropServices;
public static class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr v); }
"@
[Dpi]::SetProcessDpiAwarenessContext([System.IntPtr]-4) | Out-Null
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$y = if ($BottomPx -gt 0) { $b.Bottom - $BottomPx } else { $b.Top }
$h = if ($BottomPx -gt 0) { $BottomPx } else { $b.Height }
$w = if ($CenterWidthPx -gt 0) { [Math]::Min($CenterWidthPx, $b.Width) } else { $b.Width }
$x = $b.Left + [int](($b.Width - $w) / 2)
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output $Out
