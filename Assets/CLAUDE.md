# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> **Start at [`../docs/README.md`](../docs/README.md)** — the documentation entry point. It is
> tier 1 of a five-tier structure (landing → roadmap → project state → per-system docs → today's
> plan) and says which document answers which question.
>
> One rule from there is binding across the whole project, not just `Assets/`: **always render a
> QR for network/URL joining; never ask someone to type or hunt for an address.** It is recorded
> in [`../docs/systems/HostingAndCertificates.md`](../docs/systems/HostingAndCertificates.md).

## What this is

A couch party game: Unity runs on a PC driving the TV, and players' phones are motion
controllers reached through a web page — no app download, Jackbox-style, modelled on Wii Play:
Motion's "Skip Skimmer". The full design/architecture doc is
[`../docs/systems/`](../docs/systems/) — one document per runtime-critical system. Read the
relevant one before touching networking, physics, the motion pipeline, or the event system; each
states the invariants that must not break and the traps that have already cost someone a day.
[`../docs/ProjectState.md`](../docs/ProjectState.md) says what is actually built right now.

Three independently-run pieces, only one of which lives under `Assets/`:

| Path | What it is |
|---|---|
| `Assets/` (this directory) | Unity 6 project (`6000.3.15f1`) — the game/TV screen |
| `../relay/` | Node WebSocket relay + HTTP server that also serves the controller page |
| `../controller/` | Phone controller web app (vanilla JS): sensors, throw gesture, join/choose screens |
| `../deploy/` | `setup-offline.ps1` — one-time elevated machine setup for Hotspot mode, run automatically by `HotspotService` on first use (one UAC prompt, no manual step); `LocalServices` hosts the relay+controller over HTTPS itself, on Play |
| `../docs/` | Documentation, five tiers, entry point `../docs/README.md`. Inert docs live in `../docs/archive/` and describe nothing current |

**The PC is authoritative for everything.** Phones never simulate; they send normalized
sensor deltas (`AIM`, `THROW`, `PICK_ROCK`, `PICK_COLOUR`, `ADVANCE`) and receive broadcast
state (`SPECTATE`). There is no Unity-to-Unity networking.

## Running and testing

There is no Unity CLI test runner set up for this project; verification is via in-editor menu
items and the relay's own Node test scripts.

**Play the game** — just press Play on `Bootstrap` in Unity. `LocalServices` obtains a
certificate (`Assets/Scripts/Net/CertificateProvider.cs`, `MagicDomain.cs`) and starts the
relay itself; no setup script needs to run first, including for Hotspot mode —
`HotspotService.EnsureOfflineSetup` runs `deploy/setup-offline.ps1` itself, elevated, the first
time this machine ever starts a hotspot (one UAC prompt; every session after that skips
straight past it). See [`../deploy/README.md`](../deploy/README.md) and
[`../docs/plans/magic-domain-replacement.md`](../docs/plans/magic-domain-replacement.md) if
that automatic run ever needs to be run by hand instead (e.g. to `-DryRun` it, or to
`-Uninstall` a test machine).
Desktop-only testing (no real sensors, two browser tabs) is documented in
[`../README.md`](../README.md).

**Relay tests** (routing, identity stamping, reconnect, `/qr`):

```bash
cd relay && npm test
```

Individually: `node test-relay.js`, `node test-dns.js`. Load test: `npm run loadtest`.

**Unity in-editor verification**, under the `RockSkipping` menu (`Assets/Editor/ProjectSetup.cs`
and `Assets/Scripts/Editor/*`):

| Menu item | What it does |
|---|---|
| `RockSkipping/Verify/Run Event System Verification` | Proves pub/sub survives scene loads; writes `verification-result.txt` at repo root. Headless: see [`../README.md`](../README.md) "Verifying the Unity event system" for the exact `-batchmode` invocation (note: no `-quit`, it enters Play mode). |
| `RockSkipping/Verify/Print Reality Check` | The R1–R10 checks against the published planing-disc model (Bocquet 2003, Clanet 2004, Nagahiro 2004); writes `skip-reality.txt` at repo root. **This is the physics gate now** — it measures the magic angle rather than asserting it. |
| `RockSkipping/Verify/Print Skip Curve` | Older per-rock speed/angle/roll sweep, writes `skip-curve.txt`. Largely superseded by the reality check; its central "throw speed must beat any per-rock multiplier" premise is moot now that rocks have no multipliers. |
| `RockSkipping/Rocks/Build Catalog from FBX` | Regenerates `Assets/Rocks/Rock_0N.asset` + `Resources/RockCatalog.asset` from `Resources/Fbx/RocksRemeshed.fbx`. Idempotent — preserves hand-edited display names/tints/tuning refs. |
| `RockSkipping/Rocks/Validate Catalog` | Names any rock with no mesh, no tuning, or an id another rock already uses. |
| `RockSkipping/Rebuild Audio Bank` (+ `(reset tuning)`) | Regenerates `Assets/Scripts/Audio/AudioBank`-driven procedural SFX. |
| `RockSkipping/Setup/Create Scenes And Build Settings`, `Regenerate Solution And Project Files` | One-time/CI-adjacent project scaffolding. |

There is no out-of-band C# compile check — Unity holds an exclusive project lock, so
`Unity -batchmode` cannot compile while the Editor has the project open. Don't try to build one;
verify by checking the open Editor's Console (a prior attempt at this,
`tools/check-compile.ps1`, was built and then deleted for exactly this reason — see the Build
log in `docs/plans/rock-choosing-system.md`, deleted in `0b7c500` and recoverable from git
history).

## Architecture

### Scenes and persistence

Build order **Bootstrap → Lobby → Game → Results** (`Bootstrap` must stay index 0).
`Bootstrap` (`Scripts/Managers/Bootstrap.cs`) creates every persistent manager and immediately
loads `Lobby`. Subscription order matters here: managers that ask another manager for state on
the broadcast path must be created after it — e.g. `RockSelection` after `ScoreManager`, before
`TurnManager` (see ordering comments in `Bootstrap.cs`).

Persistent (created once in `Bootstrap`, must never live scene-local): `EventManager`,
`RelayClient`, `RoomManager`/`RoomManager`-equivalent, `ScoreManager`, `TurnManager`,
`LocalServices`, `JoinScreen`. Dropping `RelayClient` on a scene transition disconnects every
phone and looks like a networking bug rather than a lifecycle one — this has bitten the project
before.

Every scene-local event subscriber must unsubscribe in `OnDestroy`:
`EventManager.Instance?.UnsubscribeFromAllEvents(this)`. `EventManager.DebugLoggingEnabled`
(exposed on `Bootstrap`) surfaces otherwise-swallowed publish failures — turn it on when wiring
new events.

### Event system (`Scripts/EventSystems/`, `Scripts/EventTypes/`)

Ported from another project (FactoryTM) and patched for multi-scene use — see
[`../README.md`](../README.md) "Notes for whoever picks this up" and
[`../docs/systems/EventSystem.md`](../docs/systems/EventSystem.md) for the fixes that had to land (persist-on-scene-load, Unity-null-aware
destroyed-subscriber detection, calling `base.OnDestroy()`). New event types live under
`Scripts/EventTypes/<Category>Events/`, namespaced `EventTypes.<Category>Events`, immutable
get-only properties set in the constructor — follow an existing file in that folder as the
template rather than writing from scratch. Publish/subscribe always through
`EventManager.Instance?.` (null-conditional).

### Networking (`Scripts/Net/`)

`RelayClient` connects to the Node relay (`ClientWebSocket`, chosen over NativeWebSocket to add
zero dependencies — sends are thread-affine, hence funneled through one send queue). All
traffic — `AIM`/`THROW` included — goes over that one relay socket now; the direct WebRTC
DataChannel transport (`PeerLink`, `com.unity.webrtc`) was removed, see
`../docs/archive/Findings-DirectTransportFlapping.md` for why it bought nothing once the relay and the
phone were already one hop apart. `LocalServices` owns the relay child process on this machine
when hosting locally; `HotspotService`/`CertificateProvider` support the PC-hosted-hotspot
fallback documented in `../deploy/README.md`. `Messages.cs` holds the serializable DTOs
mirroring the wire JSON —
extend it (message type + DTO + `RelayClient` event + `Dispatch` case) rather than sniffing new
fields out of `RawMessageReceived`.

### Motion pipeline

Phones normalize sensors *before* sending — Unity never sees a platform (iOS/Android)
difference. The governing rule and the iOS/Android landmines (no `acceleration` on iOS,
opposite-direction compass headings, `requestPermission()` gating) are in
[`../docs/systems/MotionPipeline.md`](../docs/systems/MotionPipeline.md); the current heading/attitude conversion used on both the throw path and rock
selection is `PhoneAttitude.HeadingDirection`/`HeadingDeg` (`Scripts/Game/Physics/PhoneAttitude.cs`) —
**one conversion, reused everywhere a heading is needed**, no ad-hoc Euler math at call sites.

### Skip physics (`Scripts/Game/Physics/SkipPhysics.cs`)

A custom, pure, side-effect-free stepping function (`SkipPhysics.Step`), not PhysX rigidbody
collision — needed both for precise per-bounce control and so the aim preview can run the same
math speculatively, many frames ahead, without side effects.

**There is no bounce formula.** Free flight is ballistic; while any part of the face is under
water the solver integrates the real pressure force and its torque (`DiscContact`, `RigidDisc`) at
a ~20 µs substep. Skip-versus-sink is an *outcome*, not a branch. Energy loss is the work the
pressure does and cannot be positive — pressure acts only where the face is advancing into the
water — so the old `MinLoss` clamp is gone as unnecessary rather than as a simplification. The
20° optimum appears nowhere in the solver; it emerges, and `SkipReality` measures it.

Two things follow that are easy to break by accident. Rotation belongs in the pressure law: the
local surface velocity of a rigid body is `v_cm + ω × r`, and evaluating the law only at the disc
centre is what silently removed the model's rotational damping. And the free precession in
`RigidDisc.Integrate` is **solved, not integrated** — it is an oscillator, and explicit Euler is
unconditionally unstable on one, which previously made a faster-spun stone *less* stable than a
slow one. See `docs/Findings-RealSkipPhysics.md` §9. **`Step` must never publish
events or otherwise touch anything observable** (score, audio, VFX, haptics) — only the caller
driving a *real* throw does that (`RockController`/`RockLauncher`), because the preview runs
`Step` continuously while a player merely aims and would otherwise "score" imaginary throws.

`SkipPhysics`, `PhoneAttitude`, `SkipConstants` and `SkipTuning` (all four under
`Scripts/Game/Physics/`) are the pure, dependency-free core of the skip model — they import
only `System.Collections.Generic`/`UnityEngine`, nothing project-specific — and live in their
own `RockSkipping.Physics` assembly (`RockSkipping.Physics.asmdef`) specifically so
`Assets/Tests/EditMode/` can reference them in isolation for NUnit EditMode tests. That asmdef
is `autoReferenced: true`, so the rest of the project (Assembly-CSharp) keeps compiling against
them unchanged, without adding an explicit reference.

### Rock catalog and tuning (`Assets/Rocks/`, `Scripts/Game/RockDefinition.cs`, `RockCatalog.cs`)

Six rock meshes (`Resources/Fbx/RocksRemeshed.fbx`), each with a `RockDefinition` asset holding
one `SkipTuning` reference (`Assets/Rocks/Tuning/<Id>.asset`, a flat folder).

**A rock owns its BODY and nothing else** — `Mass` (kg), `Radius` (m), `Thickness` (m), in SI
units. The three bounded multipliers (`SpinMultiplier`, `VerticalMultiplier`,
`HorizontalMultiplier`) are gone: they scaled the *output* of a bounce, so no rock could ever be
wrong, only feel wrong. The solver now integrates real fluid forces over the stone's geometry, so
what it needs from a rock is what the rock physically **is**, and those numbers have right answers
— a skipping stone is 5–8 cm across and 50–250 g, and anything outside that is an error rather
than an aggressive tuning choice.

Everything that is not a property of one rock lives on the single `Resources/SkipConstants.asset`:
gravity, water height, water density, the lift/friction coefficients, the two phone→stone
calibration scales (`SpeedScale`, `SpinScale`), the forgiveness bands, `ReferenceSpeed`, the fixed
step and the contact substep. That split is the constraint — **a global belongs in
`SkipConstants`, never on a rock** — and it is structural rather than validator-enforced, because
a rock has no field to disagree with.

**The bodies are not authored by hand.** All six currently carry the reference stone's dimensions
because deriving them from each mesh is `RockCatalogBuilder`'s job (plan phase 2) and typing six
plausible-looking bodies in by hand is exactly the guessing this rewrite exists to end. Presentation
(`WaterRipples`, `SfxDirector`, `RockTrail`) normalizes against `SkipConstants.ReferenceSpeed`,
which is the one speed every such system must agree on.

The **Authored/Derived tuning-set split is gone** (`docs/plans/Plan-ThrowDrivenSkipModel.md`,
deleted; in git history),
along with `MatchSettings.TuningSet` and the per-rock `MinSpeed`/`MaxSpeed`/`MinRetention`/
`MaxRetention`/`Restitution` fields; a stale `tuningSet` key in an old `match-settings.json`
parses harmlessly and is dropped on the next save.
[`../docs/systems/RockCatalogAndTuning.md`](../docs/systems/RockCatalogAndTuning.md) and
[`../docs/systems/SkipPhysics.md`](../docs/systems/SkipPhysics.md) cover the split and the
constants; the old `Reference-SkipTuning.md` / `Reference-Constants.md` field-by-field indexes
were deleted in `0b7c500` and were never rewritten — the `[Tooltip]` text on `SkipConstants` is
now the most complete per-field reference there is.

Full data-flow, wire format, and the shore-pointing selection UI are in
[`../docs/systems/RockCatalogAndTuning.md`](../docs/systems/RockCatalogAndTuning.md).

**All six rocks ship identical multipliers, and as of 2026-09-08 that is the design, not a gap.**
[`../docs/plans/presentation-and-ui.md`](../docs/plans/presentation-and-ui.md) D5 settles that
rocks differentiate by mesh and silhouette and are named on the scoreboard, while staying
mechanically identical; milestone 8's acceptance criterion was rewritten to match. Two rules that
follow, both easy to break by accident:

- **The player's colour always wins the tint.** `RockDefinition.Tint` exists and the catalog
  builder preserves hand-edits to it, but colour is player identity on a TV — never rock identity.
- **`Print Skip Curve` showing all six behaving identically is the expected result**, not a
  failing check. Its job here is the energy invariant.

### Scoring

Scoring is deliberately push-based: gameplay code (`RockController`, `RingGate`, `TurnManager`)
publishes well-named events (`SkipOccurredEvent`, `RingPassedEvent`, `ThrowCompletedEvent`) and
never computes points itself; only `ScoreManager` owns point values, subscribed to those events.

### Audio (`Scripts/Audio/`)

`AudioBank`/`ProceduralClips`/`SfxDirector` — procedurally generated SFX built by the
`RockSkipping/Rebuild Audio Bank` menu commands (backed by `tools/audio/generate-sfx.js`),
not authored clips checked in as assets.

## Working conventions specific to this repo

- **Docs drift fast and say so.** Several docs contain explicit
  "Superseded — see X" callouts where a later decision reversed an earlier one (e.g. hosted
  relay → local-PC relay; built-in render pipeline → URP). Read the callout, not just the
  original text, and check `docs/` for a more recent doc on the same topic before acting on an
  older one. Anything in `docs/archive/` describes nothing current, by definition.
- **`[SerializeField]` defaults are dead on components built via `AddComponent`.** Several
  tuning components (`AimFeedbackTuning`, `RockSelectTuning`, etc.) exist specifically because
  `Bootstrap`/runtime code builds objects with `AddComponent` rather than instantiating an
  Inspector-configured prefab, so field values must come from C# initializers, not the
  Inspector.
- **`Resources/` is intentionally a short, curated list** of things loaded by name at runtime
  (`SkipTuning`, `RockCatalog`, `RockObject.prefab`, …). New per-rock or per-asset data goes
  under `Assets/Rocks/` (or a similarly named folder) and is pulled in as a normal asset
  reference from something that *is* in `Resources/`, not added to `Resources/` itself.
- **The project renders through URP** (`com.unity.render-pipelines.universal` 17.3, every quality
  level in `ProjectSettings/QualitySettings.asset` points at `Resources/RockSkippingUrp.asset`).
  A Built-in surface shader renders **magenta** here. New shaders must be URP, checked in under
  `Assets/Resources/`, and loaded with `Resources.Load<Shader>` rather than `Shader.Find` — see the
  remarks in `Scripts/Game/PlaceholderArt.cs`, which is also the single material factory everything
  should go through.
- **`mesh.vertices` is unavailable** on the rock meshes (`RocksRemeshed.fbx.meta` sets
  `isReadable: 0`) — use `mesh.bounds`/`mesh.vertexCount` for any mesh-derived logic.

## Documented deviations from the coding standards

Recorded so a future pass doesn't "fix" these back — each is a deliberate call, not an oversight.

- **`[SerializeField] private` fields keep bare camelCase** (plain privates use `_camelCase`) —
  renaming risks silently dropping Inspector-set values across 70 fields for a naming-taste win.
- **`EventManager.Instance?.` is kept deliberately** — `SingletonBase.Instance`'s getter already
  uses Unity's overloaded `==` internally, so the fake-null window is narrow: only the
  `_isQuitting` teardown path can hand back a destroyed instance.
- **The `*Manager` class names stay** (`AudioManager`, `EventManager`, `ScoreManager`,
  `TurnManager`) — referenced by name across this file, `docs/systems/`, and the `Bootstrap`
  ordering comments; renaming is docs-wide churn for a naming-taste win.
- **`ok` / `resumed` / `reconnected` / `randomRocks` keep their non-question names** — they are
  wire-format field names (matched by string between `Assets/Scripts/Net/Messages.cs` and
  `relay/server.js`) and a persisted `match-settings.json` key, not internal identifiers;
  renaming them is a coordinated cross-stack change with a save-file migration.
- **The heavy comment density (roughly 30% of C# lines) is INTENTIONAL and must not be
  trimmed.** It is this codebase's best asset — comments explain *why*, not *what* — and a
  future pass reading "comment why, not what" could otherwise misread density as bloat and
  delete the gotcha notes that document real footguns.
