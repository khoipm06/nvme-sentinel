; NVMe Sentinel - Inno Setup Installer Script for Windows
; Produces a native, single-file Setup.exe with desktop/start-menu shortcuts and uninstaller

#define MyAppName "NVMe Sentinel"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "Khoi Pham"
#define MyAppURL "https://github.com/khoipm06/nvme-sentinel"
#define MyAppExeName "nvme-sentinel.exe"
#define MyDaemonExeName "nvme-sentinel-daemon.exe"

[Setup]
AppId={{D41A28A8-5E24-4B29-91B1-209211C5B562}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\..\dist
OutputBaseFilename=nvme-sentinel-v{#MyAppVersion}-win-x64-setup
SetupIconFile=..\..\assets\icons\nvme-sentinel.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Allows standard user to install without UAC Administrator prompt (installs to %LocalAppData%\Programs)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start NVMe Sentinel monitoring automatically at login"; GroupDescription: "Background Service:"

[Files]
; Standalone Windows publish output
Source: "..\..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\assets\icons\nvme-sentinel.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\nvme-sentinel.ico"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\nvme-sentinel.ico"; Tasks: desktopicon

[Registry]
; Optional autostart at Windows user login
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NvmeSentinelDaemon"; ValueData: """{app}\{#MyDaemonExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Option to launch app after installation completes
Filename: "{app}\{#MyDaemonExeName}"; Description: "Start background monitor"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
; Stop background daemon before uninstalling
Filename: "taskkill.exe"; Parameters: "/F /IM {#MyDaemonExeName} /T"; Flags: runhidden
