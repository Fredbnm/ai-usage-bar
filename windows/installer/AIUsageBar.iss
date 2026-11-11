; Inno Setup script for the Windows installer (per-user, no admin rights).
; Built by .github/workflows/windows.yml:
;   ISCC.exe /DAppVersion=0.9.0 installer\AIUsageBar.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6B0E4C1D-8A2F-4E7B-9C35-1F7D2A9E4B60}
AppName=AI Usage Bar
AppVersion={#AppVersion}
AppVerName=AI Usage Bar {#AppVersion}
AppPublisher=matheusvelloso
AppPublisherURL=https://github.com/matheusvelloso/ai-usage-bar
AppSupportURL=https://github.com/matheusvelloso/ai-usage-bar/issues
DefaultDirName={localappdata}\Programs\AI Usage Bar
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=AIUsageBar-Windows-Setup
SetupIconFile=..\src\AIUsageBar\AppIcon.ico
UninstallDisplayIcon={app}\AIUsageBar.exe
UninstallDisplayName=AI Usage Bar
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[CustomMessages]
en.StartWithWindows=Start AI Usage Bar when Windows starts
pt.StartWithWindows=Abrir o AI Usage Bar ao iniciar o Windows

[Tasks]
Name: "startup"; Description: "{cm:StartWithWindows}"

[Files]
Source: "..\publish\win-x64\AIUsageBar.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AI Usage Bar"; Filename: "{app}\AIUsageBar.exe"

[Registry]
; Same value the app's own "Start with Windows" option writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AIUsageBar"; \
  ValueData: """{app}\AIUsageBar.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\AIUsageBar.exe"; Description: "{cm:LaunchProgram,AI Usage Bar}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM AIUsageBar.exe /F"; Flags: runhidden; RunOnceId: "StopApp"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\AIUsageBar"
