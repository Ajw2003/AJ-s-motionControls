; Rock Skipping — Windows installer (Inno Setup)
;
; Bundles the Unity build, a Node.js runtime, relay/, and the seed certificate, and performs
; the elevated one-time setup that deploy/setup-offline.ps1 otherwise requires someone to run
; by hand — firewall rules (TCP 8443, TCP 80, UDP 53), the IP-forwarding-disable scheduled
; task, and the hosts entry. See docs/plans/magic-domain-replacement.md Phase 5 for why this
; exists: "download one thing, no manual steps" is the actual product experience being aimed
; at, and setup-offline.ps1 needing to be found and run by hand is the single largest
; remaining source of manual intervention.
;
; Inno Setup over WiX: one script, no separate toolchain, and it already runs the elevated
; step at install time (where elevation exists naturally, via UAC) rather than needing a
; second elevation prompt after the fact.
;
; STATUS — what this script can and cannot do yet, read before treating it as finished:
;
;   Cannot run or test: there is no Unity build output anywhere in this repo to point
;   SourceUnityBuild at, and no Node runtime has been bundled here — both are placeholder
;   paths below. UNTESTED: this .iss file has never been compiled with Inno Setup's ISCC,
;   because doing so requires the two directories above to exist first.
;
;   What IS real: the [Run] step that shells out to setup-offline.ps1 reuses that script
;   verbatim rather than reimplementing its logic — the elevated setup is proven code, only
;   invoked from a different place (install time, running as the installer's own elevated
;   process, instead of a person double-clicking the script after install).
;
;   Left to whoever builds the first real installer:
;     1. Produce a Unity Standalone Windows build and point SourceUnityBuild at its output
;        directory (the folder containing the .exe and the _Data folder).
;     2. Download a Node.js Windows binary distribution (node.exe plus its bundled npm) and
;        place it under a "node-runtime" folder next to this script, OR switch the [Files]
;        entry below to pull from wherever the build pipeline stages it.
;     3. Run `npm install --production` inside a staged copy of relay/ before packaging, so
;        node_modules ships inside the installer rather than being installed at first launch
;        (LocalServices' own dependency-install fallback still works if this step is
;        skipped, but then a fresh install needs internet on its very first launch, which
;        defeats part of the point of bundling Node at all).
;     4. Place a real server.pem/server.key under Assets/StreamingAssets/ before building the
;        Unity player, so CertificateProvider.TrySeedCache has something to fall back to.
;        Nothing here fabricates one — see CertificateProvider.cs's remarks.
;     5. Code-sign the installer and node.exe/Unity's player exe, or Windows SmartScreen and
;        several antivirus products will flag an unsigned installer that also unpacks a
;        second unsigned executable — this is called out as a real risk in
;        docs/ZeroSetupPlan.md's risk table (bundling node.exe adds an antivirus surface).

#define MyAppName "Rock Skipping"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Rock Skipping"
#define MyAppExeName "RockSkipping.exe"

; Placeholder paths — see STATUS above. Point these at real build output before compiling.
#define SourceUnityBuild "..\Build\Windows"
#define SourceNodeRuntime "node-runtime"
#define SourceRelay "..\relay"
#define SourceStreamingAssetsCert "..\Assets\StreamingAssets"

[Setup]
AppId={{9E9B9E9C-9D7A-4E51-8C9B-ROCKSKIPPING}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; The elevated setup below (firewall, scheduled task, hosts file) needs Administrator for
; the whole install, not just one step — Inno's per-file/per-run elevation model can't split
; "copy files as the user" from "touch system state as admin" within one run cleanly enough
; to be worth the complexity here.
PrivilegesRequired=admin
OutputBaseFilename=RockSkipping-Setup
Compression=lzma2
SolidCompression=yes
; UNTESTED: no icon has been produced for this project yet.
; SetupIconFile=rockskipping.ico
WizardStyle=modern
DisableProgramGroupPage=yes
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The Unity player build. Source directory is a placeholder — see STATUS above.
Source: "{#SourceUnityBuild}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

; A Node.js runtime, so a fresh install never needs "install Node 20 first" as a
; prerequisite — ZeroSetupPlan.md blocker 1. ProcessUtil.ResolveExecutable already searches
; PATH and C:\Program Files\nodejs; bundling under the install directory means neither is
; guaranteed to find this copy, so LocalServices would need a third search location added
; (the app's own install directory) for the bundled runtime to actually be picked up instead
; of a system Node — flagged here rather than silently assumed, since that is a real code
; change this installer's existence implies but does not itself make.
Source: "{#SourceNodeRuntime}\*"; DestDir: "{app}\node"; Flags: recursesubdirs createallsubdirs ignoreversion

; relay/, staged with node_modules already installed per STATUS step 3 — ZeroSetupPlan.md
; blocker 3 ("relay/ and controller/ are not in the build"). Excludes anything that should
; never ship: local dev overrides, and the relay's own gitignored per-machine config files.
Source: "{#SourceRelay}\*"; DestDir: "{app}\relay"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "relay-env.txt,*.log"

[Dirs]
; LocalServices resolves relay-url.txt, network-mode.txt and magic-domain.txt beside the
; executable (Application.dataPath's parent) — {app} is exactly that location for an
; installed build, so no path changes are needed on the Unity side for this to work.
Name: "{app}"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
; Reuses deploy/setup-offline.ps1 verbatim rather than reimplementing its firewall/scheduled
; task/hosts-file logic in Pascal Script — that script is proven, idempotent, and already
; handles half-configured machines correctly (see its own header comment). Running it here
; means a player never sees "run this PowerShell script as Administrator" as a separate,
; easy-to-skip step after unzipping something — it happens automatically, inside the same
; UAC elevation the installer itself already obtained.
;
; -ExecutionPolicy Bypass is scoped to this one process invocation (a PowerShell built-in,
; not a machine-wide policy change) and does not persist past this command.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\deploy\setup-offline.ps1"""; \
    WorkingDir: "{app}"; StatusMsg: "Configuring firewall, scheduled task and hosts entry for offline play…"; \
    Flags: runhidden waituntilterminated

; setup-offline.ps1 lives in the repo under deploy/ — bundle it too, so the [Run] step above
; has something to find. Not listed under [Files] above because it belongs at a fixed path
; regardless of where SourceUnityBuild/SourceRelay end up.
[Files]
Source: "setup-offline.ps1"; DestDir: "{app}\deploy"; Flags: ignoreversion

Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Uninstall's mirror of the [Run] step above — returns the machine to clean, same as
; setup-offline.ps1 -Uninstall does when run by hand. RunOnceId so this only fires once per
; uninstall even if Inno retries the step.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\deploy\setup-offline.ps1"" -Uninstall"; \
    WorkingDir: "{app}"; RunOnceId: "RockSkippingSetupOfflineUninstall"; Flags: runhidden waituntilterminated
