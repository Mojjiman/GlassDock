# Stops the running dock (restoring the taskbar), rebuilds Debug and starts the new build.
# Usage: powershell -File build\dev-run.ps1 [-NoStart]
param([switch]$NoStart, [string]$DotnetRoot = "C:\GimojjiMan\Devs\dotnet")
$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "..\src\GlassDock"
$exe = Join-Path $proj "bin\Debug\net10.0-windows10.0.22621.0\GlassDock.exe"
# The Debug apphost is framework-dependent; point it at the side-by-side SDK instead of Program Files.
$env:DOTNET_ROOT = $DotnetRoot

if (Get-Process GlassDock -ErrorAction SilentlyContinue) {
    & $exe --quit
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process GlassDock -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Get-Process GlassDock -ErrorAction SilentlyContinue) { throw "GlassDock did not exit within 10 s" }
}

& (Join-Path $DotnetRoot "dotnet.exe") build $proj -c Debug -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }

if (-not $NoStart) {
    Start-Process $exe
    "started $exe"
}
