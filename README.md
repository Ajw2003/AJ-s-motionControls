# AJ's Motion Controls

Use a phone as a motion controller for emulators (Dolphin, Cemu, Ryujinx and the Yuzu forks,
the Citra forks) — no app install, iPhone and Android, over the offline hotspot connection.
The phone streams raw gyro and accelerometer readings to the PC, which re-sends them over the
DSU/CemuHook protocol the emulators already accept.

Forked from the Rock Skipping party game (Ajw2003/RockSkipping, `main` at `3c68ac2`,
2026-10-07) for its phone controller, local HTTPS and hotspot setup. The game itself (the Unity
project and its phone page) was removed on 2026-10-10; it lives on in git history and in that
repo. Why: [docs/6-decisions/Decisions.md](docs/6-decisions/Decisions.md).

**Start with [docs/1-landing/README.md](docs/1-landing/README.md)** — the documentation entry point.

## Layout

| Path | What it is |
|---|---|
| `controller/` | Phone pages: `pad.html` (gamepad + motion), `join.html` (QR codes on the PC), `monitor.html` (live readout) |
| `relay/` | Node HTTPS/WebSocket server, pad hub, DSU server (`dsu.js`), offline DNS |
| `hotspot/`, `deploy/` | Offline hotspot control, certificates, one-time machine setup |
| `installer/` | Windows installer build |
| `tools/` | Launcher (`launch.ps1`), DSU and pad test clients, `verify.ps1` |
| `docs/` | Documentation |

## Checking a change

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1
```

Runs the coding-standards audit, then relay and controller tests and lint. Run `npm ci` in
`relay/` and `controller/` first.
