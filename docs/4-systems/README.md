# System documentation

**Tier 4.** Runtime-critical systems: if one of these is wrong, the product does not work.

Each document covers the same four things, in this order:

1. **What it owns** — the single responsibility, stated so overlaps are visible
2. **How it works** — the design, at the depth needed to change it safely
3. **Invariants** — what must stay true. Breaking one of these is how the expensive bugs happened
4. **Traps** — specific things that have already cost someone a day

They do not restate the code; they give the shape the code's comments assume you already have.

## Systems AJ's Motion Controls reuses

Inherited from Rock Skipping, still accurate for the code in this repo, and the base the
milestones build on. Their wording still talks about "the game" in places.

| System | Doc | Primary source |
|---|---|---|
| Motion pipeline | [`MotionPipeline.md`](MotionPipeline.md) | `controller/js/sensors.js` |
| Phone controller | [`PhoneController.md`](PhoneController.md) | `controller/js/app.js`, `controller/index.html` |
| Networking | [`Networking.md`](Networking.md) | `relay/` |
| Hosting and certificates | [`HostingAndCertificates.md`](HostingAndCertificates.md) | `deploy/`, `hotspot/`, `relay/dns.js` |

## Systems built for AJ's Motion Controls

| System | Doc | Primary source |
|---|---|---|
| Controller stream (#2) | [`ControllerStream.md`](ControllerStream.md) | `controller/js/pad.js`, `controller/js/motion-sample.js`, `relay/pad-hub.js` |
| DSU server (#3; buttons #5) | [`DsuServer.md`](DsuServer.md) | `relay/dsu.js` |
| Launcher, join page, installer (#23) | [`Launcher.md`](Launcher.md) | `tools/launch.ps1`, `installer/` |
| On-screen gamepad (#4) | [`OnscreenGamepad.md`](OnscreenGamepad.md) | `controller/js/gamepad-state.js`, `layouts.js`, `gamepad-view.js` |

## Planned systems — no doc until built

A systems doc for code that does not exist would be invented. Each of these gets a doc here when
it is built; until then its design lives in the plan.

| System | Plan |
|---|---|
| Virtual Xbox controller | [`../plans/virtual-xbox-controller.md`](../plans/virtual-xbox-controller.md) |
| WebRTC input channel (replaces the stream's WebSocket for live input) | [`../plans/webrtc-input-channel.md`](../plans/webrtc-input-channel.md) |

## Rock Skipping game systems — not part of this product

The game's code is still in the repo and these still describe it, so they stay here rather than
in `archive/`. Whether the game code is removed is undecided
([`../6-decisions/Decisions.md`](../6-decisions/Decisions.md), 2026-10-08); if it is, these move to
`archive/` with it.

| System | Doc | Primary source |
|---|---|---|
| Event system | [`EventSystem.md`](EventSystem.md) | `Assets/Scripts/EventSystems/`, `EventTypes/` |
| Skip physics | [`SkipPhysics.md`](SkipPhysics.md) | `Assets/Scripts/Game/Physics/` |
| Scoring and turns | [`ScoringAndTurns.md`](ScoringAndTurns.md) | `Assets/Scripts/Managers/` |
| Rock catalog and tuning | [`RockCatalogAndTuning.md`](RockCatalogAndTuning.md) | `Assets/Scripts/Game/Rock*.cs`, `Assets/Rocks/` |
| Audio | [`Audio.md`](Audio.md) | `Assets/Scripts/Audio/`, `Managers/AudioManager.cs` |
| Presentation | [`Presentation.md`](Presentation.md) | `Assets/Scripts/Game/`, `Assets/Scripts/UI/` |
| Verification | [`Verification.md`](Verification.md) | `Assets/Scripts/Verification/`, `Assets/Tests/EditMode/` |
