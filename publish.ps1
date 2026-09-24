# Builds a self-contained, single-file Windows executable and zips it up for sharing.
#
#   .\publish.ps1
#
# Result:  dist\osu_collection_manager.exe   (+ the frontend folder and appsettings.json next to it)
#          dist\osu_collection_manager-win-x64.zip   (the same thing, zipped)
#
# Self-contained means the person running it does NOT need .NET installed.

param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$appDir = Join-Path $PSScriptRoot "dist\app"
$zipPath = Join-Path $PSScriptRoot "dist\osu_collection_manager-$Runtime.zip"

if (Test-Path "dist") { Remove-Item "dist" -Recurse -Force }

dotnet publish backend -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o $appDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The Development settings only matter when running from source.
Remove-Item (Join-Path $appDir "appsettings.Development.json") -ErrorAction SilentlyContinue

Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $zipPath
Write-Host ""
Write-Host "Done."
Write-Host "  Run:   $appDir\osu_collection_manager.exe"
Write-Host "  Share: $zipPath"
