# Roadmap — what 0% to 100% means

**Tier 2.** This document defines *done*. Where the project stands is
[`ProjectState.md`](../3-state/ProjectState.md); the two are kept apart so progress can move
without the target moving with it.

Seven milestones, one per GitHub issue under the parent issue
[#1](https://github.com/Ajw2003/AJ-s-motionControls/issues/1). Each has an acceptance criterion
someone else could check. **A milestone is done when that check has actually been run**, not when
the code exists.

---

## What 100% is

**On a Windows PC, a person opens a web page on the phone they already own and uses it as a
complete controller — on-screen buttons and sticks plus motion — in Dolphin and in an ordinary PC
game, for free, with no app installed, and with the latency measured and written down.**

**What 100% is not, yet.** Packaging for a stranger's PC (one download, no setup) is not on this
roadmap: no issue exists for it. Rock Skipping solved most of it for its own installer
(`deploy/installer.iss`, `deploy/setup-offline.ps1`), so it is a likely next roadmap, but adding
it is a decision to make, not something to slip in here.

---

## The milestones

| # | % | Milestone | Issue | One line |
|---|---|---|---|---|
| 1 | 15 | Raw motion stream | [#2](https://github.com/Ajw2003/AJ-s-motionControls/issues/2) | The phone sends raw gyro and accelerometer readings at its native rate |
| 2 | 30 | Motion in emulators over DSU | [#3](https://github.com/Ajw2003/AJ-s-motionControls/issues/3) | Dolphin sees the phone's motion |
| 3 | 45 | On-screen gamepad | [#4](https://github.com/Ajw2003/AJ-s-motionControls/issues/4) | Buttons, D-pad, sticks and triggers on the phone, with layouts |
| 4 | 55 | Whole controller over DSU | [#5](https://github.com/Ajw2003/AJ-s-motionControls/issues/5) | Dolphin takes the phone as a full controller from one connection |
| 5 | 70 | Virtual Xbox controller | [#6](https://github.com/Ajw2003/AJ-s-motionControls/issues/6) | Windows and any game see the phone as an Xbox controller |
| 6 | 85 | Latency measured | [#8](https://github.com/Ajw2003/AJ-s-motionControls/issues/8) | Real touch-to-PC and motion-to-PC numbers per phone |
| 7 | 100 | Low-latency channel | [#7](https://github.com/Ajw2003/AJ-s-motionControls/issues/7) | Input moves to a WebRTC data channel, gain measured |

Milestone 6 comes before 7 on purpose: the WebRTC change is only worth keeping if it measurably
beats the WebSocket, and that needs the measurement first.

---

### 1 — Raw motion stream (15%)

**Contains.** A phone page mode that sends gyro (deg/s) and accelerometer (g) readings at the rate
the browser delivers them, each with a phone-side timestamp, reusing the permission prompt and the
iOS/Android normalisation in `controller/js/sensors.js`. A PC-side endpoint that receives it,
separate from Rock Skipping's rate-limited game messages. Plan:
[`plans/raw-motion-stream.md`](../plans/raw-motion-stream.md).

**Acceptance.** Node unit tests for the sample format pass, and a real iPhone and a real Android
phone each show a steady stream at their native rate in a debug readout on the PC, with a
screenshot of each.

### 2 — Motion in emulators over DSU (30%)

**Contains.** A DSU (CemuHook) server on UDP 26760 that answers version, controller-info and
pad-data requests from the phone's motion. Up to four phones in the four DSU slots. Plan:
[`plans/dsu-server.md`](../plans/dsu-server.md).

**Acceptance.** A scripted DSU client in Node receives CRC-correct pad data driven by a fake phone,
and on the Windows PC Dolphin's DSU client shows a real phone's motion moving (screenshot or
video).

### 3 — On-screen gamepad (45%)

**Contains.** Touch controls on the phone page with layouts for a sideways Wii Remote, GameCube,
Switch-style dual stick and a general Xbox layout. Fires on touch-down, multi-touch, state sent on
change, motion keeps streaming. Plan: [`plans/onscreen-gamepad.md`](../plans/onscreen-gamepad.md).

**Acceptance.** Every layout works on a real iPhone and a real Android phone, with simultaneous
presses reaching the PC in a debug readout; screenshots of each layout.

### 4 — Whole controller over DSU (55%)

**Contains.** Buttons, sticks, triggers and touch filled into DSU pad data, with a mapping per
layout, and a written record of which emulators read buttons from DSU and which take motion only.
Plan: [`plans/dsu-buttons.md`](../plans/dsu-buttons.md).

**Acceptance.** The scripted DSU client sees correct button bits and stick values, and Dolphin
accepts the phone as a full controller on the Windows PC.

### 5 — Virtual Xbox controller (70%)

**Contains.** One virtual Xbox 360 controller per phone through the free ViGEmBus driver, its
install folded into a setup script, and a chosen way for Node to drive it. Motion stays on DSU.
Plan: [`plans/virtual-xbox-controller.md`](../plans/virtual-xbox-controller.md).

**Acceptance.** On the Windows PC, a phone appears in Windows' game controller settings as an Xbox
controller and its buttons register (screenshot).

### 6 — Latency measured (85%)

**Contains.** Network round-trip measurement with clock-offset estimation, reported as median and
95th percentile, plus a repeatable slow-motion-video method for the full touch-to-PC time. Plan:
[`plans/latency-measurement.md`](../plans/latency-measurement.md).

**Acceptance.** Numbers for at least one iPhone and one Android phone on the Windows PC's hotspot
are in the docs, with the method beside them.

### 7 — Low-latency channel (100%)

**Contains.** The live input stream moved to a WebRTC data channel set to unordered with no
retransmits, sending full state each time so a lost packet is corrected by the next. The WebSocket
stays for setup and as a fallback. Plan:
[`plans/webrtc-input-channel.md`](../plans/webrtc-input-channel.md).

**Acceptance.** Milestone 6's measurement, rerun, shows the data channel against the WebSocket on
the same phone and hotspot.
