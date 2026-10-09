# Project state

**Tier 3.** Where AJ's Motion Controls stands right now, measured against
[`Roadmap.md`](../2-roadmap/Roadmap.md). Last updated 2026-10-09.

## Headline: 0%

No milestone's acceptance check has been run yet, so nothing counts as done. Milestone 1's code is
built and checked headless, and so are milestones 2's and 3's; their acceptance needs real phones and Dolphin. Everything else builds on Rock
Skipping's phone-controller stack, forked on 2026-10-08.

| # | Milestone | Status | Issue |
|---|---|---|---|
| 1 | Raw motion stream | Built; checked headless; real-phone check outstanding | [#2](https://github.com/Ajw2003/AJ-s-motionControls/issues/2) |
| 2 | Motion in emulators over DSU | Built; checked with a scripted DSU client; Dolphin check outstanding | [#3](https://github.com/Ajw2003/AJ-s-motionControls/issues/3) |
| 3 | On-screen gamepad | Built; checked headless with emulated multi-touch; real-phone check outstanding | [#4](https://github.com/Ajw2003/AJ-s-motionControls/issues/4) |
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

## Milestone 2 — motion in emulators over DSU

**Built** (see [`4-systems/DsuServer.md`](../4-systems/DsuServer.md)): a DSU server on UDP
127.0.0.1:26760 inside the relay answers version, port-info and pad-data requests, and sends one
100-byte pad-data packet per phone sample to each subscribed emulator. Motion only; buttons are
milestone 4.

**Checked, 2026-10-09, in the sandbox:**
- `relay` tests: 83 passed, 0 failed (10 new in `relay/test-dsu.js`, including CRC-32 against the
  standard check value `cbf43926` and the relay serving a scripted client end to end), run twice.
- `tools/verify-dsu.js`, run twice: an emulated Pixel 7 streaming synthetic pitching motion; the
  DSU client `tools/dsu-client.js` received 312 and 313 pad packets in 5 s, 0 malformed, with the
  pitch showing on DSU gyro pitch. Screenshot:
  [`media/verify/monitor-dsu-client.png`](../media/verify/monitor-dsu-client.png).

**What that cannot show:** whether Dolphin reads these packets the same way, and whether the axis
signs are right. Both tests and client were written from the same reading of the protocol, so a
shared misreading would pass them all.

**Outstanding for acceptance:** Dolphin's DSU client on the Windows PC showing a real phone's
motion, and the axis signs confirmed or corrected in `phoneToDsuAxes`.

## Milestone 3 — on-screen gamepad

**Built** (see [`4-systems/OnscreenGamepad.md`](../4-systems/OnscreenGamepad.md)): Xbox, Switch,
GameCube and sideways Wii Remote layouts on the phone page; multi-touch; a `PAD` message with the
whole state on every change and every 250 ms; the PC validates it and the monitor shows it.
Motion keeps streaming alongside.

**Checked, 2026-10-09, in the sandbox:**
- `controller` tests green twice, including `controller/test-gamepad-state.js`: sticks, D-pad, the
  wire order of buttons, and every layout free of overlaps at three phone aspect ratios.
- `relay` tests: 91 passed, 0 failed (8 new for `PAD` messages), run twice.
- `tools/verify-gamepad.js`, run more than twice: every layout on an emulated iPhone 13 and Pixel 7
  in landscape; three touches held at once (A, left stick up-right, D-pad up) reached the PC as
  `buttons 2049` (south + up) and sticks `0.707, 0.707`, and all returned to zero on release.
  Screenshots: [`media/verify/gamepad-three-touches-phone.png`](../media/verify/gamepad-three-touches-phone.png),
  [`media/verify/gamepad-three-touches-monitor.png`](../media/verify/gamepad-three-touches-monitor.png),
  and `media/verify/gamepad-*.png` for each layout.
- Bugs the screenshots caught and fixed: overlapping controls on three layouts, the layout button
  covering the C-stick, and the setup screen not scrolling in landscape.

**What that cannot show:** how it feels under real thumbs, real touch latency, whether iOS keeps
landscape without a lock, or vibration on Android. Chrome's touch emulation is not a touchscreen.

**Outstanding for acceptance:** every layout on a real iPhone and a real Android phone, with
simultaneous presses reaching `monitor.html`.

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
