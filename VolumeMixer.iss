; ── Volume Mixer — Inno Setup Script ─────────────────────────────────────────
; Install Inno Setup from: https://jrsoftware.org/isinfo.php
; To build: open this file in Inno Setup Compiler → Compile (Ctrl+F9)
; Output: a single Setup.exe your friends download and run once.

#define AppName    "Volume Mixer"
#define AppVersion "2.0.5"
#define AppPublisher "generallouay"
#define AppURL     "https://github.com/generallouay/custom-volume-mixer"
#define AppExe     "Volume Mixer.exe"
#define AppSrc     "bin\Release\Volume Mixer.exe"

[Setup]
AppId={{F3A2B1C4-9D7E-4F2A-8B3C-1D5E6F7A8B9C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=.\installer-output
OutputBaseFilename=VolumeMixer-Setup-v{#AppVersion}
SetupIconFile=3721701.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExe}

; Minimum Windows 10
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "startupentry"; Description: "Start automatically with Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; Main executable — pulled straight from Release build
Source: "{#AppSrc}"; DestDir: "{app}"; Flags: ignoreversion
; App icon
Source: "3721701.ico"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; Start Menu
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
; Desktop (optional)
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Optional startup with Windows entry
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"""; \
  Flags: uninsdeletevalue; Tasks: startupentry

[Run]
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser skipifsilent
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall

[UninstallDelete]
; Clean up any leftover files on uninstall
Type: filesandordirs; Name: "{app}"
