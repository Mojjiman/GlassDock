# Shows an ordinary app window (taskbar button, Alt+Tab entry) for a while, then closes it.
# -Fullscreen makes it a borderless window covering the primary screen, like a game in borderless mode.
# Usage: powershell -File build\test-window.ps1 -Title "GD test A" -Seconds 15 [-Fullscreen]
param([string]$Title = "GlassDock test window", [int]$Seconds = 15, [switch]$Fullscreen)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = $Title
$form.StartPosition = "Manual"
$form.Location = New-Object System.Drawing.Point 200, 200
$form.Size = New-Object System.Drawing.Size 640, 360
$form.ShowInTaskbar = $true
if ($Fullscreen) {
    $form.FormBorderStyle = "None"
    $form.BackColor = [System.Drawing.Color]::FromArgb(30, 30, 40)
    $form.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
}
# The first ShowWindow of a process obeys the launcher's show flag (e.g. a hidden console);
# showing twice makes the second, real Show stick.
$form.Show(); $form.Hide(); $form.Show(); $form.Activate()
$until = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $until -and $form.Visible) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 30 }
$form.Close()
