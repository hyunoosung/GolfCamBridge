; GolfCamBridge installer (Inno Setup 6). Build with ..\build-installer.ps1, which passes /DAppVersion.
;
; Installs the same layout as the repo, so golfcam.json's default SoftcamDir (..\bin) works unchanged:
;   {app}\app\GolfCamBridge.exe, golfcam.json, SpinnakerNET_v140.dll
;   {app}\bin\softcam_golfcam1.dll, softcam_golfcam2.dll   (registered as "Golf Cam 1" / "Golf Cam 2")
;
; Uninstall unregisters the virtual cameras BEFORE deleting the DLLs (regserver flag), so no ghost
; "Golf Cam" devices are left behind. Only {app}\app\golfcam.json (the user's settings) stays.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; softcam DLLs built with toolset v145 need the VC++ runtime 14.50+; with v143 set this to 30.
#ifndef MinVcMinor
  #define MinVcMinor 50
#endif
; GolfCamBridge.exe is bound to exactly this SpinnakerNET_v140 version (strong-named, not in the GAC).
#define SpinnakerVersion "4.4.0.246"

[Setup]
AppId={{D6C53DA6-1E59-4C1E-A097-D33195363A8E}
AppName=GolfCamBridge
AppVersion={#AppVersion}
AppPublisherURL=https://github.com/hyunoosung/GolfCamBridge
AppSupportURL=https://github.com/hyunoosung/GolfCamBridge/issues
DefaultDirName={autopf}\GolfCamBridge
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; the running bridge holds the DLLs and the exe (TrayContext/Program.cs single-instance mutex)
AppMutex=Local\GolfCamBridge.SingleInstance
CloseApplications=yes
UninstallDisplayIcon={app}\app\GolfCamBridge.exe
UninstallDisplayName=GolfCamBridge
OutputDir=..\build\installer
OutputBaseFilename=GolfCamBridge-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The autostart value lives in HKCU of the user running setup (same key the tray "Start with Windows" uses).
UsedUserAreasWarning=no

[Tasks]
Name: autostart; Description: "Start GolfCamBridge when I sign in to Windows"

[Files]
Source: "..\app\GolfCamBridge.exe";        DestDir: "{app}\app"; Flags: ignoreversion
Source: "..\app\GolfCamBridge.exe.config"; DestDir: "{app}\app"; Flags: ignoreversion
; Settings: never overwritten on upgrade, left in place on uninstall (a reinstall picks them up again);
; writable by users so Settings > Save and the tray toggles work.
Source: "..\GolfCamBridge\golfcam.json";   DestDir: "{app}\app"; Flags: onlyifdoesntexist uninsneveruninstall; Permissions: users-modify
; Not redistributed: copied from this PC's Spinnaker SDK so it always matches the installed SDK.
Source: "{code:SpinnakerBin}\SpinnakerNET_v140.dll"; DestDir: "{app}\app"; Flags: external ignoreversion
; Virtual cameras. restartreplace: Premier/Chrome/Slack may have them loaded during an upgrade.
Source: "..\bin\softcam_golfcam1.dll"; DestDir: "{app}\bin"; Flags: ignoreversion regserver restartreplace uninsrestartdelete
Source: "..\bin\softcam_golfcam2.dll"; DestDir: "{app}\bin"; Flags: ignoreversion regserver restartreplace uninsrestartdelete

; MIT requires the notices to travel with the binaries.
Source: "..\LICENSE";                 DestDir: "{app}"; DestName: "LICENSE.txt";                Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md";  DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt";    Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GolfCamBridge"; Filename: "{app}\app\GolfCamBridge.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GolfCamBridge"; \
  ValueData: """{app}\app\GolfCamBridge.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\app\GolfCamBridge.exe"; Description: "Start GolfCamBridge now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
function SpinnakerDir(): String;
begin
  Result := GetEnv('SPINNAKER_INSTALL_PATH');
  if Result = '' then
    Result := ExpandConstant('{commonpf64}\Teledyne\Spinnaker\');
  Result := AddBackslash(Result);
end;

function SpinnakerBin(Param: String): String;
begin
  Result := SpinnakerDir() + 'bin64\vs2015';
end;

function InitializeSetup(): Boolean;
var
  release, installed, minor: Cardinal;
  dll, ver: String;
begin
  Result := False;

  // .NET Framework 4.8 or later (Release >= 528040)
  if not RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', release)
     or (release < 528040) then
  begin
    MsgBox('GolfCamBridge needs .NET Framework 4.8 or later.'#13#10'Install it from https://dotnet.microsoft.com/download/dotnet-framework and run setup again.', mbError, MB_OK);
    Exit;
  end;

  // Spinnaker SDK, the exact version GolfCamBridge.exe was built against
  dll := SpinnakerBin('') + '\SpinnakerNET_v140.dll';
  if not FileExists(dll) then
  begin
    MsgBox('Spinnaker SDK was not found (' + dll + ').'#13#10'Install Spinnaker SDK {#SpinnakerVersion} (x64) from Teledyne FLIR first.', mbError, MB_OK);
    Exit;
  end;
  if not GetVersionNumbersString(dll, ver) or (ver <> '{#SpinnakerVersion}') then
  begin
    MsgBox('Spinnaker SDK ' + ver + ' is installed, but this build of GolfCamBridge needs {#SpinnakerVersion}.', mbError, MB_OK);
    Exit;
  end;

  // Visual C++ 2015-2022+ x64 runtime, new enough for the softcam DLLs
  if not RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Installed', installed)
     or (installed <> 1)
     or not RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Minor', minor)
     or (minor < {#MinVcMinor}) then
  begin
    MsgBox('GolfCamBridge needs the latest Microsoft Visual C++ Redistributable (x64), version 14.{#MinVcMinor} or newer.'#13#10'Download: https://aka.ms/vs/17/release/vc_redist.x64.exe', mbError, MB_OK);
    Exit;
  end;

  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  run: String;
begin
  // The tray "Start with Windows" toggle may have set this even if the task was not chosen at install.
  if (CurUninstallStep = usUninstall) and
     RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GolfCamBridge', run) and
     (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(run)) > 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GolfCamBridge');
end;
