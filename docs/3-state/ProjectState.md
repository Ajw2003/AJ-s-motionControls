# Project state

**Tier 3.** Where AJ's Motion Controls stands right now, measured against
[`Roadmap.md`](../2-roadmap/Roadmap.md). Last updated 2026-10-09.

## Headline: 0%

Nothing emulator-specific is built. No milestone's acceptance check has been run. What exists is
Rock Skipping's phone-controller stack, which this repo was forked from on 2026-10-08 and which
the milestones build on.

| # | Milestone | Status | Issue |
|---|---|---|---|
| 1 | Raw motion stream | Not started | [#2](https://github.com/Ajw2003/AJ-s-motionControls/issues/2) |
| 2 | Motion in emulators over DSU | Not started | [#3](https://github.com/Ajw2003/AJ-s-motionControls/issues/3) |
| 3 | On-screen gamepad | Not started | [#4](https://github.com/Ajw2003/AJ-s-motionControls/issues/4) |
| 4 | Whole controller over DSU | Not started | [#5](https://github.com/Ajw2003/AJ-s-motionControls/issues/5) |
| 5 | Virtual Xbox controller | Not started | [#6](https://github.com/Ajw2003/AJ-s-motionControls/issues/6) |
| 6 | Latency measured | Not started | [#8](https://github.com/Ajw2003/AJ-s-motionControls/issues/8) |
| 7 | Low-latency channel | Not started | [#7](https://github.com/Ajw2003/AJ-s-motionControls/issues/7) |

---

## What is inherited and already works

Checked on real phones for Rock Skipping, per [`DeviceMatrix.md`](../DeviceMatrix.md). None of it
has been checked in its new role yet.

- **Motion permission and sensor reading on iPhone and Android.**
  `requestMotionPermission` ([`controller/js/sensors.js:355`](../../controller/js/sensors.js)),
  accelerometer read at [`sensors.js:292`](../../controller/js/sensors.js), gyro read at
  [`sensors.js:308`](../../controller/js/sensors.js).
- **The phone page served over local HTTPS**, which browsers require before they hand out motion
  data. See [`4-systems/HostingAndCertificates.md`](../4-systems/HostingAndCertificates.md).
- **The offline hotspot the PC runs**, with phones joining by QR code. Same doc, and
  [`OfflineSetup.md`](../OfflineSetup.md).
- **A Node WebSocket relay** between phones and the PC
  ([`relay/server.js`](../../relay/server.js)). See
  [`4-systems/Networking.md`](../4-systems/Networking.md).

## What is not what it looks like

**The phone already "sends motion", but not motion an emulator can use.** Rock Skipping's phone
sends a processed attitude at 20 Hz
([`controller/js/app.js:388`](../../controller/js/app.js)) plus one summarised THROW event, and
the relay caps those messages at 60 a second
([`relay/server.js:41`](../../relay/server.js)). Emulators need raw gyro and accelerometer at the
phone's full rate. Milestone 1 exists because of this gap; it is new output, not a settings change.

## Unchecked assumptions

Each is a belief from knowledge of the tools, not a test. Each milestone that depends on one checks
it first.

| Assumption | Checked in |
|---|---|
| Dolphin and Cemu read buttons from DSU; Ryujinx takes only motion from it | Milestone 4 |
| ViGEmBus (maintenance-ended in 2023) still installs and works on current Windows | Milestone 5 |
| Touch-to-PC latency is roughly 25-65 ms over the hotspot | Milestone 6 |

## Cross-cutting issues that belong to no milestone

- **The game is still in the repo.** The Unity project (`Assets/`, `Packages/`,
  `ProjectSettings/`), the game's relay rooms and the game-only systems docs. Whether and when to
  remove them is undecided; see [`6-decisions/Decisions.md`](../6-decisions/Decisions.md),
  2026-10-08.
- **Packaging for a stranger's PC** is not on the roadmap. See the roadmap's "What 100% is not,
  yet".
