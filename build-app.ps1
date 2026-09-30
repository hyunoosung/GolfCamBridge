# Builds GolfCamBridge.exe into .\app  (requires .NET SDK 8+; targets .NET Framework 4.8)
$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'GolfCamBridge\GolfCamBridge.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "`nBuilt: $(Join-Path $PSScriptRoot 'app\GolfCamBridge.exe')" -ForegroundColor Green
