# Registers (or with -Uninstall, unregisters) the Golf Cam virtual webcams.
# Run as Administrator. The DLLs stay where they are - do not move them after registering.

param([switch]$Uninstall)

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $psArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $psArgs += '-Uninstall' }
    Start-Process powershell -Verb RunAs -ArgumentList $psArgs
    exit
}

$regsvr = Join-Path $env:WINDIR 'System32\regsvr32.exe'   # 64-bit regsvr32
foreach ($v in 1, 2) {
    $dll = Join-Path $PSScriptRoot "bin\softcam_golfcam$v.dll"
    if (-not (Test-Path $dll)) { Write-Warning "Missing: $dll (run build-softcam.ps1 first)"; continue }
    $argList = if ($Uninstall) { @('/s', '/u', "`"$dll`"") } else { @('/s', "`"$dll`"") }
    $p = Start-Process $regsvr -ArgumentList $argList -Wait -PassThru
    $action = if ($Uninstall) { 'Unregistered' } else { 'Registered' }
    if ($p.ExitCode -eq 0) { Write-Host "$action Golf Cam $v" -ForegroundColor Green }
    else { Write-Warning "regsvr32 failed for $dll (exit $($p.ExitCode))" }
}
Read-Host 'Press Enter to close'
