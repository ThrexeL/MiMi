# MiMi release build script
# Produces a portable (framework-dependent, single-file) Release build in dist/
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build-release.ps1 [-SelfContained]
#
# Framework-dependent build (~90 MB runtime not included): requires the .NET 10
# Desktop Runtime on the target machine â€” https://dotnet.microsoft.com/download
# -SelfContained embeds the runtime (~180 MB) and runs with nothing installed.

param([switch]$SelfContained)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$rid = "win-x64"

Write-Host "== Building MiMi (Release) ==" -ForegroundColor Cyan
Push-Location "$root\src\MiMi.App"
if ($SelfContained) {
    dotnet publish MiMi.App.csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o "$root\dist"
} else {
    dotnet publish MiMi.App.csproj -c Release -r $rid --self-contained false `
        -p:PublishSingleFile=true -o "$root\dist"
}
Pop-Location
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

# Companion CLI next to the app (hook installers + adapters resolve this path)
# The CLI is published as a single file: ImpressiveBot runs %LOCALAPPDATA%\MiMi\MiMi.AgentCli.exe
# on its own, so an exe that needs a sibling .dll would never start.
dotnet publish "$root\src\MiMi.AgentCli\MiMi.AgentCli.csproj" -c Release -r $rid --self-contained false `
    -p:PublishSingleFile=true -o "$root\dist" --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "CLI BUILD FAILED" -ForegroundColor Red; exit 1 }

# Docs
Copy-Item "$root\README.md" "$root\dist\" -Force
Copy-Item "$root\PROJECT_STATE.md" "$root\dist\" -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "== dist/ contents ==" -ForegroundColor Cyan
Get-ChildItem "$root\dist" | Format-Table Name, @{n="MB";e={[math]::Round($_.Length/1MB, 1)}} -AutoSize
Write-Host "Launch: dist\MiMi.exe  (no admin, no installer; settings live in %LOCALAPPDATA%\MiMi)"
