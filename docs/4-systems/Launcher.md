# Launcher, join page and installer

**Tier 4.** How a PC goes from "downloaded one file" to "phone in the emulator" (#23).

## What it owns

Starting and stopping everything on the PC in the right order, showing the two QR codes, turning
on DSU input in Sudachi and Dolphin, and packaging all of that as one Windows installer.

## How it works

| Piece | File | Job |
|---|---|---|
| Launcher | [`tools/launch.ps1`](../../tools/launch.ps1) | Credentials → emulators → relay → wait for DNS → hotspot → join page; stops all of it when the relay exits |
| Emulator setup | [`tools/emulators.ps1`](../../tools/emulators.ps1) | Sudachi `enable_udp_controller=true`; Dolphin `DSUClient.ini` entry for `127.0.0.1:26760` |
| Join listener | `relay/server.js` (`JOIN_PORT`, `/api/join`, `/api/stop`) | Plain HTTP on `127.0.0.1:8780` only |
| Join page | [`controller/join.html`](../../controller/join.html), `controller/js/join.js` | Wi-Fi QR, controller QR, live "Phone N connected", Stop button |
| Installer | [`installer/ajmotion.iss`](../../installer/ajmotion.iss), `stage.ps1`, `firewall.ps1` | Bundles Node 22.19.0 (checksum-checked), relay, page, scripts; firewall rules TCP 8443, TCP 80, UDP 53 |
| Release | [`.github/workflows/release.yml`](../../.github/workflows/release.yml) | A `v*` tag builds the installer and attaches it; a tag with `-` is a pre-release |

The hotspot name and password are made once (`AJ Motion XXXX`, 12 characters) and kept in
`%LOCALAPPDATA%\AJsMotionControls\hotspot.json`, next to the certificate cache.

The relay's `/qr` returns the QR module grid as JSON, not an image (Rock Skipping drew it in
Unity), so the join page draws it on a canvas with a 4-module quiet border.

## Invariants

- **The Wi-Fi password only leaves the PC as a QR code on its own screen.** `/api/join` answers
  loopback only, and the join listener binds `127.0.0.1`. `join.html` itself holds no secrets.
- **DNS is bound before the hotspot starts.** The launcher waits for `/health` to report
  `dns.bound`, or the first phone gets no answers (`HotspotService.cs:142` in Rock Skipping).
- **Emulator setup never changes a player mapping.** It touches only the DSU keys, takes a dated
  backup, keeps the file's line endings, skips a running emulator, and changes nothing on a
  second run (`tools/test-emulators.ps1`, 15 checks).
- **The launcher works from a checkout and from an install.** It uses `..\node\node.exe` if
  present, otherwise Node on PATH.

## Traps

- **Windows PowerShell 5.1 under strict mode:** `npm` resolves to `npm.ps1`, which breaks; call
  `npm.cmd`. A native tool's stderr becomes a terminating error under `Stop`; relax it inside the
  function that calls the tool.
- **Git Bash `sed -i` rewrites CRLF to LF across the whole file.** It did that to Sudachi's
  config on 2026-10-10. Emulator edits go through PowerShell with the newline style kept.
- **Closing the launcher window can leave the hotspot on.** The relay dies with the console, but
  the `finally` block may not run. The next launch turns the hotspot off first; the join page's
  Stop button, or the Start-menu **Stop AJ Motion** shortcut, is the clean way out.
- **2.4 GHz stalls the motion stream near busy Wi-Fi.** The launcher forces 5 GHz; see
  `6-decisions/Decisions.md`, 2026-10-10. A phone that can't see 5 GHz can't join.
- **The repo is private,** so only aj can download Release assets while it stays private.
