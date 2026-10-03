# RockSkipping — documentation

The entry point. Every other document is reachable from here, and this file says which one
answers which question. Read the tier you need; you should never have to search the folder.

---

## What this project is

A couch party game. Unity runs on a PC driving the TV; players' phones are motion controllers
reached through a web page, with no app to download — Jackbox-style, modelled on Wii Play:
Motion's *Skip Skimmer*. Up to eight players take turns throwing a stone across water, and the
throw is a real throw: the phone's orientation and swing are the input, and the stone on screen
matches the phone in the player's hand.

**The PC is authoritative for everything.** Phones never simulate. They send normalized sensor
data (`AIM`, `THROW`, `PICK_ROCK`, `PICK_COLOUR`, `SET_NAME`, `ADVANCE`) and receive broadcast
state (`SPECTATE`, `TURN`). There is no Unity-to-Unity networking.

The hard problem this project solved, and the reason it is interesting beyond being a game:
browser motion sensors require a secure context, a secure context requires a certificate, a
certificate requires a resolvable public name — and none of that exists on a living-room network
with no internet. It works anyway. See [`4-systems/HostingAndCertificates.md`](../4-systems/HostingAndCertificates.md).

### The three moving parts

Only one of them lives under `Assets/`.

| Path | What it is |
|---|---|
| `Assets/` | Unity 6 project (`6000.3.10f1`), URP — the game and the TV screen |
| `relay/` | Node WebSocket relay + HTTP/HTTPS server; also serves the controller page and answers DNS |
| `controller/` | The phone controller web app (vanilla JS) — sensors, throw gesture, join and choose screens |
| `deploy/`, `hotspot/` | Machine setup, installer, hotspot control, certificate verification |

---

## The six tiers

Read top-down the first time. After that, go straight to the tier that answers your question.

| Tier | Document | Answers |
|---|---|---|
| **1** | **this file** (`1-landing/`) | What is this, where is everything |
| **2** | [`2-roadmap/Roadmap.md`](../2-roadmap/Roadmap.md) | What 0–100% means, in ten milestones. What "done" looks like |
| **3** | [`3-state/ProjectState.md`](../3-state/ProjectState.md) | Where the project stands *right now* against tier 2 |
| **4** | [`4-systems/`](../4-systems/) | How each runtime-critical system actually works |
| **5** | [`5-today/Today.md`](../5-today/Today.md) | What is being worked on today, and why that and not something else |
| **6** | [`6-decisions/Decisions.md`](../6-decisions/Decisions.md) | Why a decision was made, and what it replaced |

Tier 5 is rewritten each working session. Tier 3 is updated when a milestone's status changes.
Tier 2 changes only when the definition of done changes — which should be rare, and is worth
noticing when it happens. Tier 6 is never rewritten, only appended to.

---

## Tier 4 — system documentation

Seven systems, chosen because each is runtime-critical: if it is wrong, the game does not work.
Each doc describes the design, the invariants you must not break, and the specific traps that
have already cost someone a day.

| System | Doc | Owns |
|---|---|---|
| Event system | [`4-systems/EventSystem.md`](../4-systems/EventSystem.md) | Publish/subscribe across four scenes, subscriber lifetime |
| Networking | [`4-systems/Networking.md`](../4-systems/Networking.md) | The relay, the wire format, `RelayClient`, reconnection |
| Motion pipeline | [`4-systems/MotionPipeline.md`](../4-systems/MotionPipeline.md) | Phone sensors → normalized quaternion → the stone's rotation |
| Skip physics | [`4-systems/SkipPhysics.md`](../4-systems/SkipPhysics.md) | The bounce solver, the purity rule, the forgiveness bands |
| Scoring and turns | [`4-systems/ScoringAndTurns.md`](../4-systems/ScoringAndTurns.md) | Match phases, the roster, whose turn it is, where points come from |
| Hosting and certificates | [`4-systems/HostingAndCertificates.md`](../4-systems/HostingAndCertificates.md) | The hotspot, the secure context, the certificate, offline play |
| Rock catalog and tuning | [`4-systems/RockCatalogAndTuning.md`](../4-systems/RockCatalogAndTuning.md) | The six rocks, the rock-vs-global split, selection |
| Audio | [`4-systems/Audio.md`](../4-systems/Audio.md) | SoundId, AudioBank, procedural clips, AudioManager |
| Presentation | [`4-systems/Presentation.md`](../4-systems/Presentation.md) | The aim ghost and arc, scenery, materials and render queues, trail, ripples, TV screens |
| Verification | [`4-systems/Verification.md`](../4-systems/Verification.md) | The in-editor checks, scene capture, throw telemetry, EditMode tests |

---

## Everything else

### Live reference

| Doc | What it is |
|---|---|
| [`SkipSchool.html`](../SkipSchool.html) | Player-facing onboarding page: the tap/swing/stop gesture, why the tap doubles as calibration, and an interactive diagram of how swing speed and release angle/spin turn a throw strong-and-far vs weak-and-sinking. Standalone HTML — open it in a browser |
| [`plans/presentation-and-ui.md`](../plans/presentation-and-ui.md) | **The plan for most of what is left.** Art direction, the in-play HUD, retiring IMGUI for UI Toolkit, rock differentiation, and the order to do it in. Drives milestone 9, and it rewrote milestone 8's acceptance criterion |
| [`ZeroSetupPlan.md`](../ZeroSetupPlan.md) | The plan for shipping to someone else's PC. Milestone 10; still the authority on the four blockers |
| [`plans/magic-domain-replacement.md`](../plans/magic-domain-replacement.md) | Why the certificate is what it is. Executed, and still the reference for the design |
| [`6-decisions/Decision-OfflineHosting.md`](../6-decisions/Decision-OfflineHosting.md) | Why Mobile Hotspot beat Wi-Fi Direct. Resolved decision, kept for the reasoning — logged in [`6-decisions/Decisions.md`](../6-decisions/Decisions.md) |
| [`OfflineSetup.md`](../OfflineSetup.md) | What a machine needs for fully-offline play, and what `setup-offline.ps1` does to it |
| [`SecurityReview.md`](../SecurityReview.md) | Pre-ship gap check against the relay, controller, Unity networking, and the installer — what's already solid and what's still open |
| [`DeviceMatrix.md`](../DeviceMatrix.md) | Which phones have actually been tested, and on what |
| [`Findings-DocumentationAudit.md`](../Findings-DocumentationAudit.md) | The 2026-09-08 audit this structure came out of |
| [`Findings-NetworkAndThrowStability.md`](../Findings-NetworkAndThrowStability.md) | 2026-09-10 review of the phone → relay → Unity pipeline: dropped/rejected AIM, broken rounds, over-eager throws, security holes. Findings only, no fixes decided |
| [`Findings-HotspotForwardingAndInternet-2026-10-01.md`](../Findings-HotspotForwardingAndInternet-2026-10-01.md) | Findings on hotspot internet forwarding; superseded 2026-10-02 (sharing is now the default) — see [`6-decisions/Decisions.md`](../6-decisions/Decisions.md) |
| [`Findings-InputPathAudit-2026-10-01.md`](../Findings-InputPathAudit-2026-10-01.md) | Audit of the input path (2026-10-01) |
| [`Findings-PerFrameAudit-2026-10-01.md`](../Findings-PerFrameAudit-2026-10-01.md) | Per-frame audit (2026-10-01) |
| [`Findings-PhoneUiAudit.md`](../Findings-PhoneUiAudit.md) | Audit of the phone controller UI |
| [`Findings-iPhoneWifiJoin-2026-10-02.md`](../Findings-iPhoneWifiJoin-2026-10-02.md) | iPhone Wi-Fi join findings, network-priority research and test plan (2026-10-02) |
| [`Findings-RealSkipPhysics.md`](../Findings-RealSkipPhysics.md) | Step 0 of [`plans/real-skip-physics.md`](../plans/real-skip-physics.md): the published stone-skipping physics the new solver is built from and validated against |
| [`plans/Plan-NetworkAndThrowFixes.md`](../plans/Plan-NetworkAndThrowFixes.md) | The priority order for fixing those findings, and the decisions it rests on (seats survive drops, no idle timeout, SpeedScale 1.0, layered throw guard) |
| [`plans/Impl-NetworkAndThrowFixes.md`](../plans/Impl-NetworkAndThrowFixes.md) | Step-by-step outline for each fix group, with the red-first regression tests and hardware checks that keep each fix landed |

### Runbooks (outside `docs/`)

| Doc | What it is |
|---|---|
| [`../../README.md`](../../README.md) | How to run what exists today, including desktop-only testing with no phone |
| [`../../deploy/README.md`](../../deploy/README.md) | Day-to-day hosting runbook |
| [`../../deploy/RECONNECT-TEST.md`](../../deploy/RECONNECT-TEST.md) | Manual reconnect test — three distinct paths, testing one proves little |
| [`../../deploy/LOCAL-HTTPS-SPIKE.md`](../../deploy/LOCAL-HTTPS-SPIKE.md) | Measurements and accepted costs behind local HTTPS |
| [`../../deploy/spike/README.md`](../../deploy/spike/README.md) | The offline-DNS spike that made offline play possible |

### Other folders under `docs/`

[`media/`](../media/) and [`mockups/`](../mockups/) hold images and UI mockups used by the documents; they are not documents themselves.

### Grant kit — [`grant/`](../grant)

Eleven documents making the funding case for the *plugin* this game turned out to contain,
working name **HandOff**. A different reader and a different purpose from everything above; start
at [`grant/README.md`](../grant/README.md). Not engineering documentation — do not treat
`grant/09-fundability-roadmap.md` as the project plan. Tier 2 and tier 3 own that.

### Archive — [`archive/`](../archive)

Documents that were correct when written and are now inert: shipped plans, fixed bugs, and one
insurance policy for a disaster that happened and was answered a different way. Kept because the
reasoning is often the only record of why something is the way it is. **Nothing in `archive/`
describes current behaviour** — do not build against it. See [`archive/README.md`](../archive/README.md).

### Generated — [`generated/`](../generated)

Tool-produced HTML reports (`doc-stack.html`, `milestones.html`). Not hand-edited — if one needs
to change, it gets regenerated, not patched.

---

## Conventions this folder keeps

Carried forward deliberately, because they are why the docs were salvageable at all:

- **Cite claims to `file:line`.** Most assertions here link to the code they describe. This is
  what makes an audit mechanical instead of a matter of opinion.
- **Say what a measurement cannot show.** The findings docs that state their own limits are the
  reason a ten-round hardware dead end is not being reopened on a hunch.
- **When a decision reverses, put the pointer at the top of the old doc and fix the body.**
  A "Superseded — see X" box stacked on top of a table that still says the old thing is how four
  documents ended up describing a certificate authority that no longer exists.
- **A doc that has gone inert moves to `archive/`.** It does not get deleted, and it does not sit
  in `docs/` looking live. Deleting 34 documents that other documents pointed at is what broke
  the link graph in twenty places; the fix is to move things, not remove them.
