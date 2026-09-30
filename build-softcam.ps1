# Builds two independent softcam virtual webcams: "Golf Cam 1" and "Golf Cam 2".
# Requires: git, Visual Studio 2022 or 2026 (or Build Tools) with "Desktop development with C++".
# Output:   .\bin\softcam_golfcam1.dll, .\bin\softcam_golfcam2.dll

param(
    [switch]$Log,          # -Log: write DirectShow call logs to C:\Users\Public\Golf Cam N.log
    [string]$Toolset = ''  # e.g. -Toolset v145 to force one; default: v143 if installed, else the newest one found
)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$src    = Join-Path $root 'build\softcam'
$out    = Join-Path $root 'bin'
$commit = '5113173d22a6aac4c9f30c36bb2cf900afef05f4'   # tested upstream revision

# 1. Locate a Visual Studio instance that has the x64 C++ tools (not just any product with MSBuild, e.g. SSMS)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found. Install Visual Studio 2022/2026 with "Desktop development with C++".' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if (-not $vs) {
    throw 'No Visual Studio with the x64 C++ tools found. In Visual Studio Installer, add the workload ' +
          '"Desktop development with C++" (component "MSVC ... x64/x86 build tools") and run this script again.'
}
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) { throw "MSBuild not found in $vs" }

# The softcam projects ask for v143 (VS 2022). On VS 2026 without the v143 component, build with its own toolset.
$available = Get-ChildItem (Join-Path $vs 'MSBuild\Microsoft\VC\*\Platforms\x64\PlatformToolsets\*') -Directory -ErrorAction SilentlyContinue |
    ForEach-Object Name | Where-Object { $_ -match '^v14\d$' } | Sort-Object -Unique
if (-not $Toolset) {
    $Toolset = if ($available -contains 'v143') { 'v143' } else { $available | Sort-Object -Descending | Select-Object -First 1 }
}
if (-not $Toolset) { throw "No x64 C++ platform toolset found under $vs\MSBuild\Microsoft\VC. Repair the C++ workload." }
Write-Host "Visual Studio: $vs"
Write-Host "MSBuild:       $msbuild"
Write-Host "Toolset:       $Toolset   (installed: $($available -join ', '))"

# 2. Fetch upstream softcam at a pinned revision
if (-not (Test-Path $src)) {
    git clone https://github.com/tshino/softcam.git $src
}
git -C $src fetch --quiet origin
git -C $src checkout --quiet --force $commit
git -C $src clean -fdx --quiet

# 3. Overlay the multi-camera changes
Copy-Item (Join-Path $root 'softcam-multicam\src\*') (Join-Path $src 'src') -Recurse -Force

# 4. Build each variant and copy the DLL
New-Item -ItemType Directory -Force $out | Out-Null
$sln = Join-Path $src 'softcam.sln'
foreach ($v in 1, 2) {
    Write-Host "`n=== Building Golf Cam $v ===" -ForegroundColor Cyan
    & $msbuild $sln '/t:BaseClasses:Rebuild;softcamcore:Rebuild;softcam:Rebuild' `
        /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$Toolset" `
        "/p:SoftcamVariant=$v" "/p:SoftcamLog=$([int][bool]$Log)" /m /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed for variant $v" }
    Copy-Item (Join-Path $src 'dist\bin\x64\softcam.dll') (Join-Path $out "softcam_golfcam$v.dll") -Force
}

Write-Host "`nDone. Next: run register-cams.ps1 as Administrator." -ForegroundColor Green
Get-ChildItem $out
