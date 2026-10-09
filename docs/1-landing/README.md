# AJ's Motion Controls — documentation index

**Tier 1.** What this is and where everything is. Nobody should have to search the folder.

## What this is

A free, low-latency, all-in-one game controller built from what people already own. A phone
opens a web page (no app install, iPhone and Android) and becomes an on-screen gamepad **and** a
motion controller. A Windows PC receives the input over an offline hotspot it runs itself and
passes it on in two ways: over the DSU (CemuHook) protocol that emulators such as Dolphin, Cemu,
Ryujinx and the Citra forks already accept, and as a virtual Xbox controller any game can use.

The repo was forked from the Rock Skipping party game on 2026-10-08, because the game's phone
controller had already solved the hard parts: motion permission on iPhone, sensor differences
between platforms, local HTTPS, and offline hosting. Status: **0%** — see
[`3-state/ProjectState.md`](../3-state/ProjectState.md).

## The moving parts

| Part | Where | State |
|---|---|---|
| Phone page | `controller/` | Rock Skipping's controller, plus `pad.html`: the raw motion stream (#2). Gamepad layouts to come (#4) |
| PC relay (Node) | `relay/` | Rock Skipping's WebSocket relay and offline DNS, plus the pad hub (#2) and `monitor.html` readout |
| DSU server | — | Planned (#3, #5) |
| Virtual Xbox controller | — | Planned (#6) |
| Offline hosting | `deploy/`, `hotspot/` | Inherited: hotspot, certificate and setup scripts |
| Rock Skipping game | `Assets/`, `Packages/`, `ProjectSettings/` | Inherited, not part of this product; removal undecided |

## The tiers

| Tier | Document | Answers |
|---|---|---|
| **1** | **this file** | What is this, where is everything |
| **2** | [`2-roadmap/Roadmap.md`](../2-roadmap/Roadmap.md) | What 0–100% means, in seven milestones |
| **3** | [`3-state/ProjectState.md`](../3-state/ProjectState.md) | Where the project stands right now |
| **4** | [`4-systems/`](../4-systems/) | How each runtime-critical system works |
| **5** | [`5-today/Today.md`](../5-today/Today.md) | What is being worked on today, and why |
| **6** | [`6-decisions/Decisions.md`](../6-decisions/Decisions.md) | Why decisions were made, and what they replaced |

Tier 5 is rewritten each working session, tier 3 when a milestone's status changes, tier 2 only
when the definition of done changes. Tier 6 only grows.

## Tier 4 — systems

| System | Doc | Owns |
|---|---|---|
| Motion pipeline | [`4-systems/MotionPipeline.md`](../4-systems/MotionPipeline.md) | Phone sensors to normalised readings, iOS/Android differences |
| Phone controller | [`4-systems/PhoneController.md`](../4-systems/PhoneController.md) | The phone page's screens and streams |
| Networking | [`4-systems/Networking.md`](../4-systems/Networking.md) | The relay, the wire format, reconnection |
| Hosting and certificates | [`4-systems/HostingAndCertificates.md`](../4-systems/HostingAndCertificates.md) | The hotspot, the secure context, offline play |
| Controller stream | [`4-systems/ControllerStream.md`](../4-systems/ControllerStream.md) | Raw motion from each phone to the PC, controller slots, the monitor |

Planned systems and the game-only systems are listed in
[`4-systems/README.md`](../4-systems/README.md).

## Plans — one per issue

| Plan | Issue |
|---|---|
| [`plans/raw-motion-stream.md`](../plans/raw-motion-stream.md) | [#2](https://github.com/Ajw2003/AJ-s-motionControls/issues/2) |
| [`plans/dsu-server.md`](../plans/dsu-server.md) | [#3](https://github.com/Ajw2003/AJ-s-motionControls/issues/3) |
| [`plans/onscreen-gamepad.md`](../plans/onscreen-gamepad.md) | [#4](https://github.com/Ajw2003/AJ-s-motionControls/issues/4) |
| [`plans/dsu-buttons.md`](../plans/dsu-buttons.md) | [#5](https://github.com/Ajw2003/AJ-s-motionControls/issues/5) |
| [`plans/virtual-xbox-controller.md`](../plans/virtual-xbox-controller.md) | [#6](https://github.com/Ajw2003/AJ-s-motionControls/issues/6) |
| [`plans/webrtc-input-channel.md`](../plans/webrtc-input-channel.md) | [#7](https://github.com/Ajw2003/AJ-s-motionControls/issues/7) |
| [`plans/latency-measurement.md`](../plans/latency-measurement.md) | [#8](https://github.com/Ajw2003/AJ-s-motionControls/issues/8) |

The other files in `plans/` are Rock Skipping's, kept with the game.

## Live reference inherited from Rock Skipping

| Doc | Why it matters here |
|---|---|
| [`OfflineSetup.md`](../OfflineSetup.md) | What a PC needs for offline play, and what `deploy/setup-offline.ps1` does |
| [`DeviceMatrix.md`](../DeviceMatrix.md) | Which phones have been tested, and how; the model for the latency table |
| [`SecurityReview.md`](../SecurityReview.md) | Gaps in the relay and controller; a UDP DSU port adds to this |
| [`Findings-iPhoneWifiJoin-2026-10-02.md`](../Findings-iPhoneWifiJoin-2026-10-02.md) | How iPhones join and stay on the hotspot |
| [`Findings-HotspotForwardingAndInternet-2026-10-01.md`](../Findings-HotspotForwardingAndInternet-2026-10-01.md) | Why the hotspot shares the PC's internet |
| [`plans/aim-preview-lag-on-shared-radio.md`](../plans/aim-preview-lag-on-shared-radio.md) | The radio contention behind milestone 7 |
| [`6-decisions/Decision-OfflineHosting.md`](../6-decisions/Decision-OfflineHosting.md) | Why Mobile Hotspot beat Wi-Fi Direct |

Everything else at the top of `docs/` (other `Findings-*`, `ZeroSetupPlan.md`, `SkipSchool.html`,
`grant/`, `generated/`, `media/`, `mockups/`) belongs to the game. Rock Skipping's own index is
archived at [`archive/rock-skipping/Landing.md`](../archive/rock-skipping/Landing.md).

## Runbooks (outside `docs/`)

| Doc | What it is |
|---|---|
| [`../../README.md`](../../README.md) | How to run what exists today |
| [`../../deploy/README.md`](../../deploy/README.md) | Hosting runbook |

## Conventions

- Claims cite `file:line`.
- A milestone is done when its acceptance check has been run, not when the code exists.
- An inert document moves to `archive/`; it is never deleted.
- A decision that reverses is fixed in the body and logged in tier 6, not stacked as a note.
