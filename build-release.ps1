# MiMi release build script
# Produces a portable (framework-dependent, single-file) Release build in dist/
# and a downloadable zip next to it for GitHub Releases.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build-release.ps1 [-SelfContained] [-Verify] [-OutputDirectory path]
#
# Framework-dependent build (~90 MB runtime not included): requires the .NET 10
# Desktop Runtime on the target machine: https://dotnet.microsoft.com/download
# -SelfContained embeds the runtime (~180 MB) and runs with nothing installed.

param(
    [switch]$SelfContained,
    [switch]$Verify,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$rid = "win-x64"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "dist"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $root $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

if ($Verify) {
    Write-Host "== Running release tests ==" -ForegroundColor Cyan
    dotnet test "$root\src\MiMi.Tests\MiMi.Tests.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "TESTS FAILED" -ForegroundColor Red; exit 1 }
}

Write-Host "== Building MiMi (Release) ==" -ForegroundColor Cyan
Push-Location "$root\src\MiMi.App"
if ($SelfContained) {
    dotnet publish MiMi.App.csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o "$OutputDirectory"
} else {
    dotnet publish MiMi.App.csproj -c Release -r $rid --self-contained false `
        -p:PublishSingleFile=true -o "$OutputDirectory"
}
Pop-Location
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

# Companion CLI next to the app (hook installers + adapters resolve this path)
# The CLI is published as a single file: ImpressiveBot runs %LOCALAPPDATA%\MiMi\MiMi.AgentCli.exe
# on its own, so an exe that needs a sibling .dll would never start.
dotnet publish "$root\src\MiMi.AgentCli\MiMi.AgentCli.csproj" -c Release -r $rid --self-contained $SelfContained.IsPresent `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$OutputDirectory" --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "CLI BUILD FAILED" -ForegroundColor Red; exit 1 }

# Docs and README images, so extracted copies render the same screenshots.
Copy-Item "$root\README.md" "$OutputDirectory\" -Force
Copy-Item "$root\images" "$OutputDirectory\images" -Recurse -Force

if ($Verify) {
    Write-Host "== Running published selftest ==" -ForegroundColor Cyan
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $OutputDirectory "MiMi.exe"
    $startInfo.Arguments = "--selftest"
    $startInfo.UseShellExecute = $false
    $selftest = [System.Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $selftest.WaitForExit(30000)) {
            $selftest.Kill()
            throw "Published selftest timed out after 30 seconds."
        }
        if ($selftest.ExitCode -ne 0) { throw "Published selftest failed with exit code $($selftest.ExitCode)." }
        Write-Host "Published selftest passed." -ForegroundColor Green
    } finally {
        $selftest.Dispose()
    }
}

# Stable filenames make the checksums portable to another download location.
$hashes = foreach ($name in @("MiMi.exe", "MiMi.AgentCli.exe", "README.md")) {
    $stream = [System.IO.File]::OpenRead((Join-Path $OutputDirectory $name))
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace("-", "").ToLowerInvariant()
        "$hash  $name"
    } finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}
$hashes | Set-Content (Join-Path $OutputDirectory "SHA256SUMS.txt") -Encoding ASCII

# Downloadable archive for GitHub Releases. Keep it outside the output folder so a
# second publish cannot accidentally include its own zip.
$zipName = if ($SelfContained) { "MiMi-win-x64-selfcontained.zip" } else { "MiMi-win-x64.zip" }
$zipPath = Join-Path (Split-Path -Parent $OutputDirectory) $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -LiteralPath @(
    (Join-Path $OutputDirectory "MiMi.exe"),
    (Join-Path $OutputDirectory "MiMi.AgentCli.exe"),
    (Join-Path $OutputDirectory "README.md"),
    (Join-Path $OutputDirectory "SHA256SUMS.txt"),
    (Join-Path $OutputDirectory "images")
) -DestinationPath $zipPath -CompressionLevel Optimal

$zipStream = [System.IO.File]::OpenRead($zipPath)
$zipSha = [System.Security.Cryptography.SHA256]::Create()
try {
    $zipHash = [System.BitConverter]::ToString($zipSha.ComputeHash($zipStream)).Replace("-", "").ToLowerInvariant()
    Add-Content -LiteralPath (Join-Path $OutputDirectory "SHA256SUMS.txt") -Value "$zipHash  $zipName" -Encoding ASCII
} finally {
    $zipSha.Dispose()
    $zipStream.Dispose()
}

Write-Host ""
Write-Host "== $OutputDirectory contents ==" -ForegroundColor Cyan
Get-ChildItem "$OutputDirectory" | Format-Table Name, @{n="MB";e={[math]::Round($_.Length/1MB, 1)}} -AutoSize
Write-Host "Release zip: $zipPath" -ForegroundColor Green
Write-Host "Launch: $OutputDirectory\MiMi.exe  (no admin, no installer; settings live in %LOCALAPPDATA%\MiMi)"
