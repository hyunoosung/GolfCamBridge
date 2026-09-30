# Builds the test tools in this folder into ..\build\tools (git-ignored).
#   reopen.exe - DirectShow open / close / reopen check for Golf Cam 1/2 (see the header of reopen.cpp)
# Requires the same Visual Studio C++ tools as build-softcam.ps1.

$ErrorActionPreference = 'Stop'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found. Install Visual Studio 2022/2026 with "Desktop development with C++".' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if (-not $vs) { throw 'No Visual Studio with the x64 C++ tools found (workload "Desktop development with C++").' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

$out = Join-Path $PSScriptRoot '..\build\tools'
New-Item -ItemType Directory -Force $out | Out-Null
$src = Join-Path $PSScriptRoot 'reopen.cpp'
cmd /c "`"$vcvars`" >nul && cl /nologo /EHsc /O2 /W3 `"$src`" /Fo`"$out\\`" /Fe`"$out\reopen.exe`" ole32.lib oleaut32.lib strmiids.lib"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "`nBuilt: $(Resolve-Path (Join-Path $out 'reopen.exe'))" -ForegroundColor Green
