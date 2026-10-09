# Project state

**Tier 3.** Where AJ's Motion Controls stands right now, measured against
[`Roadmap.md`](../2-roadmap/Roadmap.md). Last updated 2026-10-09.

## Headline: 0%

No milestone's acceptance check has been run yet, so nothing counts as done. Milestone 1's code is
built and checked headless; its acceptance needs real phones. Everything else builds on Rock
Skipping's phone-controller stack, forked on 2026-10-08.

| # | Milestone | Status | Issue |
|---|---|---|---|
| 1 | Raw motion stream | Built; checked headless; real-phone check outstanding | [#2](https://github.com/Ajw2003/AJ-s-motionControls/issues/2) |
| 2 | Motion in emulators over DSU | Not started | [#3](https://github.com/Ajw2003/AJ-s-motionControls/issues/3) |
| 3 | On-screen gamepad | Not started | [#4](https://github.com/Ajw2003/AJ-s-motionControls/issues/4) |
| 4 | Whole controller over DSU | Not started | [#5](https://github.com/Ajw2003/AJ-s-motionControls/issues/5) |
| 5 | Virtual Xbox controller | Not started | [#6](https://github.com/Ajw2003/AJ-s-motionControls/issues/6) |
| 6 | Latency measured | Not started | [#8](https://github.com/Ajw2003/AJ-s-motionControls/issues/8) |
| 7 | Low-latency channel | Not started | [#7](https://github.com/Ajw2003/AJ-s-motionControls/issues/7) |

---

## Milestone 1 — raw motion stream

**Built** (see [`4-systems/ControllerStream.md`](../4-systems/ControllerStream.md)): `pad.html`
streams every `devicemotion` reading to the PC's `/pad` socket; the pad hub gives up to four phones
a slot each and publishes samples; `monitor.html` shows them on the PC.

**Checked, 2026-10-09, in the sandbox, not on a phone:**
- `relay` tests: 73 passed, 0 failed (18 new in `relay/test-pad-hub.js`), run twice.
- `controller` tests: all green, including 9 new in `controller/test-motion-sample.js`.
- `tools/verify-pad-stream.js`, run twice: an emulated iPhone 13 and Pixel 7 fed synthetic motion
  at 60 Hz reached slots 1 and 2 at 62-63 Hz with 0 lost and 0 rejected. Screenshots:
  [`media/verify/pad-0-streaming.png`](../media/verify/pad-0-streaming.png),
  [`media/verify/monitor-two-phones.png`](../media/verify/monitor-two-phones.png).

**What that cannot show:** a real phone's sensor rate, iOS's permission prompt, or the stream over
the hotspot. Synthetic events prove the page, socket, hub and readout, not the sensors.

**Outstanding for acceptance:** one real iPhone and one real Android phone streaming at their
native rate into `monitor.html` on the Windows PC, with screenshots. Needs the page over HTTPS;
see the Traps in the systems doc.

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
