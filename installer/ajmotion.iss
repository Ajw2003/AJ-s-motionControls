; AJ Motion installer (#25). Built by .github/workflows/release.yml from installer\stage\,
; which installer\stage.ps1 fills. Unsigned for now (#27).
#define MyAppName "AJ Motion"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#define PowerShell "{sys}\WindowsPowerShell\v1.0\powershell.exe"

[Setup]
; Fixed forever, so an upgrade replaces the old install instead of sitting beside it.
AppId={{8AD57C62-5266-4FC9-AFDD-F221A7EA55FD}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=AJ
DefaultDirName={autopf}\AJ Motion
DefaultGroupName=AJ Motion
DisableProgramGroupPage=yes
OutputBaseFilename=AJ-Motion-Setup
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Files]
Source: "stage\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\AJ Motion"; Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\launch.ps1"""; WorkingDir: "{app}"; IconFilename: "{app}\node\node.exe"
Name: "{group}\Stop AJ Motion"; Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\launch.ps1"" -Stop"; WorkingDir: "{app}"
Name: "{autodesktop}\AJ Motion"; Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\launch.ps1"""; WorkingDir: "{app}"; IconFilename: "{app}\node\node.exe"; Tasks: desktopicon

[Run]
Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\firewall.ps1"""; Flags: runhidden waituntilterminated; StatusMsg: "Allowing phones through Windows Firewall..."
Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\launch.ps1"""; WorkingDir: "{app}"; Description: "Start AJ Motion now"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\launch.ps1"" -Stop"; Flags: runhidden waituntilterminated; RunOnceId: "StopAJMotion"
Filename: "{#PowerShell}"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\firewall.ps1"" -Remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewall"
