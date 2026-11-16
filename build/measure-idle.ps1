# Restarts the dock with a given style and reports idle CPU and memory of the dock process.
# Usage: powershell -File build\measure-idle.ps1 -Style frosted [-Seconds 10] [-Warmup 8]
param([string]$Style = "", [int]$Seconds = 10, [int]$Warmup = 8, [string]$DotnetRoot = "C:\GimojjiMan\Devs\dotnet")
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\src\GlassDock\bin\Debug\net10.0-windows10.0.22621.0\GlassDock.exe"
$env:DOTNET_ROOT = $DotnetRoot
$settings = Join-Path $env:APPDATA "GlassDock\settings.json"

if ($Style) {
    if (Get-Process GlassDock -ErrorAction SilentlyContinue) {
        & $exe --quit
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Process GlassDock -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    }
    # Edit after the dock has exited: it saves its own copy on quit.
    $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    $json.appearance.style = $Style
    [System.IO.File]::WriteAllText($settings, ($json | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))
    Start-Process $exe
}
Start-Sleep -Seconds $Warmup
# The dock is the larger of the two GlassDock processes (the other is the watchdog).
$p = Get-Process GlassDock | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
$c1 = $p.TotalProcessorTime
Start-Sleep -Seconds $Seconds
$p.Refresh()
"style={0} cpu={1:N1}% ws={2:N0}MB private={3:N0}MB" -f $(if ($Style) { $Style } else { "current" }), (($p.TotalProcessorTime - $c1).TotalMilliseconds / ($Seconds * 10)), ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB)
