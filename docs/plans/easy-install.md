# Plan: one installer, one icon, two QR codes

**Date:** 2026-10-10. **Issue:** #23 (part of #1). **Approved by aj:** approach A, 2026-10-10.

## Why

The first real-phone run (2026-10-10) worked end to end. A phone on the offline hotspot opened
the secure controller page, and Sudachi read its buttons, sticks and motion in Mario Party
Jamboree with negligible lag. Getting there took four hand steps:

- running `tools/test-pad.ps1`;
- typing an address on the phone (the wrong one at first);
- editing Sudachi's settings;
- knowing that Node had to be installed first.

aj wants it to work the way Rock Skipping was meant to: one download, then two QR codes. Friends
will use it soon.

## What a friend does

1. Downloads `AJ-Motion-Setup.exe` from the GitHub Release page. The page says: *Windows may say
   it protected your PC: click More info, then Run anyway* (unsigned for now; signing is #27).
2. Installs it. There's one admin prompt, during install only, for the firewall rules.
3. Clicks **AJ Motion** on the desktop. The join page opens full-screen with two QR codes.
4. On the phone: scans QR 1 to join the Wi-Fi, scans QR 2 to open the controller, then taps
   **Start controller**.
5. In the emulator: picks **UDP Controller 0** (Sudachi) or the **AJ Motion** profile (Dolphin),
   and plays.

The first launch needs internet once, so the relay can download and cache the certificate.
After that, it works offline.

## Pieces, in build order

| # | Piece | Where | Issue |
|---|---|---|---|
| 1 | Launcher | `tools/launch.ps1` (grows out of `tools/test-pad.ps1`) | #12 |
| 2 | Join page | `controller/join.html`, served by the relay | #14 |
| 3 | Emulator auto-setup | `tools/emulators.ps1`, run by the launcher | #24 |
| 4 | Installer | `installer/ajmotion.iss` (Inno Setup) | #25 |
| 5 | Release build | `.github/workflows/release.yml` | #26 |

### 1. Launcher (#12)

In order:

1. Make a random hotspot name and password (`AJ Motion XXXX`, 12-character password) on the
   first run, and remember them per PC, as Rock Skipping did
   (`Assets/Scripts/Net/HotspotCredentialsStore.cs:100`).
2. Run the emulator setup (piece 3).
3. Start the relay with the bundled Node (or the system Node when run from a checkout). Wait
   until it listens on 8443 and answers DNS on 53. The hotspot must not start before DNS is
   bound (`Assets/Scripts/Net/HotspotService.cs:142`).
4. Start the hotspot with no uplink (`hotspot/hotspot-control.ps1`).
5. Open the join page in the default browser.

Closing the launcher window stops the hotspot and the relay. A failure shows on the join page in
plain words, not only in the console. Two Windows PowerShell 5.1 lessons from `test-pad.ps1`
carry over:

- call `npm.cmd`, not `npm`;
- don't let a native tool's stderr become a terminating error.

### 2. Join page (#14)

It's served by the relay, so it can show live state. The QR images come from the relay's
existing `/qr?data=` endpoint (`relay/server.js:616`), so no new dependency is needed. It shows:

- **Wi-Fi QR:** `WIFI:T:WPA;S:<ssid>;P:<pass>;;`, using the escaping from
  `Assets/Scripts/UI/JoinScreen.cs:138`;
- **Controller QR:** `https://pad.skiprocking.ca:8443/pad.html`;
- the same details as text, for a camera that won't scan;
- live slot status from `/api/pads` ("Phone 1 connected");
- a short emulator note: what the auto-setup did, or what to do by hand if it skipped one.

Only the PC itself may load the page, because it shows the Wi-Fi password. The relay already
limits some routes to loopback (`relay/server.js:643`); this page follows the same rule.

### 3. Emulator auto-setup (#24)

| Emulator | Detected by | Changes |
|---|---|---|
| Sudachi | `%APPDATA%\sudachi\config\qt-config.ini` | `enable_udp_controller=true`; `udp_input_servers` kept at `127.0.0.1:26760`; an `AJ Motion` input profile added |
| Dolphin | `%APPDATA%\Dolphin Emulator\Config` | DSU server entry in `DSUClient.ini`; `AJ Motion` profiles in `Profiles\GCPad` and `Profiles\Wiimote` (motion included) |

Rules, each one tested:

- **Backup first:** a dated copy of every file before it's changed.
- **Line endings kept.** Both files use Windows line endings. A Git Bash `sed -i` rewrote every
  line of Sudachi's config on 2026-10-10, so edits go through PowerShell with the original
  newline style preserved.
- **Existing player mappings never change.** The phone arrives as a new profile, not a
  replacement for aj's Pro controller.
- **Emulator running means skip it.** Sudachi rewrites its config on exit, so an edit made while
  it's open is lost. The join page says "close Sudachi, then click Retry".
- **Already set up means no change.** Running it twice writes nothing the second time.

### 4. Installer (#25)

Inno Setup, which is free and open source, and is what Rock Skipping drafted in
`deploy/installer.iss` (never compiled). It bundles:

- a pinned portable Node for Windows, at the same major version as the relay's
  `engines.node` (20 or newer);
- `relay/` with its production packages already installed, so the first launch doesn't depend on
  npm;
- `controller/`, `hotspot/` and the launcher scripts.

At install time, behind the installer's own admin prompt, it adds firewall rules for TCP 8443,
TCP 80 and UDP 53, adapted from `deploy/setup-offline.ps1`. It adds Start-menu and desktop
shortcuts named **AJ Motion**. Uninstall removes the files, the shortcuts and the firewall
rules.

The certificate isn't bundled. The relay already downloads, checks and caches it
(`relay/pad-cert.js`). A bundled key would also go against the `*.pem` rule in `.gitignore`.

### 5. Release build (#26)

A GitHub Actions workflow on a free Windows runner, triggered by a version tag:

1. Download the pinned Node and check its checksum.
2. Run `npm ci --omit=dev` in `relay/`.
3. Compile the installer with Inno Setup's command-line compiler.
4. Attach `AJ-Motion-Setup.exe` to the Release, with the SmartScreen note.

## Testing

- **Automated:**
  - the Wi-Fi QR text, escaping included;
  - the emulator edits, run against copies of aj's real Sudachi and Dolphin files (backup made,
    byte diff limited to the intended lines, line endings kept, second run a no-op, running
    emulator skipped);
  - the join page, checked headless against a live relay.
- **Real, on aj's PC:**
  - install from the built `AJ-Motion-Setup.exe`;
  - a phone that has forgotten the hotspot joins using only the two QR codes;
  - Sudachi reads it with no hand edits;
  - uninstall leaves no files or firewall rules.
- **aj's other PC:** a fresh install from the Release download.

## Not in this round

| What | Issue |
|---|---|
| Code signing (SmartScreen warning) | #27 |
| Stick feel in Sudachi | #22 |
| Upright phone layout for Dolphin | #28 |
| Pre-flight "which link broke" check | #15 |
| Launcher nudges for the certificate watch job; working without a valid certificate | #19, #16 |
| iPhone check | after #23 |

## Risks

- **Antivirus or SmartScreen flags an unsigned installer that unpacks `node.exe`.** That's
  already called out in Rock Skipping's `docs/ZeroSetupPlan.md`. The mitigation is #27; until
  then, the Release notes explain the click-through.
- **Emulator config formats differ between versions.** The edits target only the keys seen on
  aj's PC (2026-10-10). An unknown layout is skipped and reported, never guessed at.
- **Hotspot address.** The join page and the relay's DNS assume `192.168.137.1`, which held on
  aj's PC on 2026-10-10 (`Get-NetIPAddress` showed it on the hotspot adapter, `Local Area Connection* 12`).
