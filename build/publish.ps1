# Builds the package for other PCs: a self-contained single-file GlassDock.exe (no .NET install needed)
# plus the Korean README, zipped as artifacts\GlassDock-<version>-<runtime>.zip.
# Usage: powershell -File build\publish.ps1 [-Runtime win-x64]
param([string]$Runtime = "win-x64", [string]$DotnetRoot = "C:\GimojjiMan\Devs\dotnet")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$proj = Join-Path $root "src\GlassDock\GlassDock.csproj"
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw "No <Version> in Directory.Build.props" }

$name = "GlassDock-$version-$Runtime"
$artifacts = Join-Path $root "artifacts"
$stage = Join-Path $artifacts $name
$zip = "$stage.zip"
# Both live under artifacts\ (git-ignored build output) and are rebuilt every run.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

# WPF cannot be trimmed. Its native DLLs go inside the exe (extracted on first run) so the folder holds one file.
# No compression and no ReadyToRun (measured 2026-11-20, 0.1.4): a compressed bundle inflates every assembly it
# loads into private memory, in the dock and again in the watchdog (dock 269 -> 153 MB, watchdog 147 -> 7 MB).
# ReadyToRun only added memory and download size with no measurable startup gain. The zip is smaller too (74 MB).
& (Join-Path $DotnetRoot "dotnet.exe") publish $proj -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -p:GenerateDocumentationFile=false -o $stage -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $PSScriptRoot "dist\README.txt") (Join-Path $stage "README.txt")
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Get-ChildItem $stage | ForEach-Object { "{0,-24} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
"{0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
