# Shows a test window over the dock for a few seconds, takes a screenshot while it is there, then closes it.
# The dock log should show "Window behind the dock" and, after the window closes, "Nothing behind the dock".
# Usage: powershell -File build\test-occlusion.ps1 -Out shot.png
param([string]$Out = "occlusion.png", [int]$HoldMs = 1500)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$form = New-Object System.Windows.Forms.Form
$form.Text = "GlassDock occlusion test"
$form.StartPosition = "Manual"
$form.BackColor = [System.Drawing.Color]::OrangeRed
$form.FormBorderStyle = "None"
$form.ShowInTaskbar = $false
$form.Bounds = New-Object System.Drawing.Rectangle ([int]($screen.Width * 0.3)), ($screen.Bottom - 260), ([int]($screen.Width * 0.4)), 220
$form.Show()
$until = (Get-Date).AddMilliseconds($HoldMs)
while ((Get-Date) -lt $until) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
& (Join-Path $PSScriptRoot "screenshot.ps1") -Out $Out -BottomPx 300 | Out-Null
$form.Close()
"saved $Out"
