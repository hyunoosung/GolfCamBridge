# Builds build\installer\GolfCamBridge-Setup-<version>.exe
# Requires: Inno Setup 6, and bin\softcam_golfcam1/2.dll from build-softcam.ps1.
#   .\build-installer.ps1 -Version 0.1.0

param([string]$Version = '0.1.0')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

foreach ($v in 1, 2) {
    if (-not (Test-Path (Join-Path $root "bin\softcam_golfcam$v.dll"))) { throw "bin\softcam_golfcam$v.dll missing - run build-softcam.ps1 first." }
}

# Inno Setup compiler: machine or per-user install (winget installs per-user by default)
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup).' }

& (Join-Path $root 'build-app.ps1')
& $iscc "/DAppVersion=$Version" (Join-Path $root 'installer\GolfCamBridge.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Write-Host "`nBuilt: $(Join-Path $root "build\installer\GolfCamBridge-Setup-$Version.exe")" -ForegroundColor Green
