# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> Also read [`../CLAUDE.md`](../CLAUDE.md) at the repo root — it points at
> [`../docs/Rule-RenderQR.md`](../docs/Rule-RenderQR.md), a binding rule (always render a QR for
> network/URL joining, never ask someone to type or hunt for one) that applies across the whole
> project, not just `Assets/`.

## What this is

A couch party game: Unity runs on a PC driving the TV, and players' phones are motion
controllers reached through a web page — no app download, Jackbox-style, modelled on Wii Play:
Motion's "Skip Skimmer". The full design/architecture doc is
[`../docs/PLAN.md`](../docs/PLAN.md) — read it before touching networking, physics, or the
event system; large parts of it are marked "Superseded" by later docs (in particular
[`../docs/HostingPlan.md`](../docs/HostingPlan.md)), so check for a superseding note before
trusting a section.

Three independently-run pieces, only one of which lives under `Assets/`:

| Path | What it is |
|---|---|
| `Assets/` (this directory) | Unity 6 project (`6000.3.10f1`) — the game/TV screen |
| `../relay/` | Node WebSocket relay + HTTP server that also serves the controller page |
| `../controller/` | Phone controller web app (vanilla JS): sensors, throw gesture, join/choose screens |
| `../deploy/` | `local-https.ps1` — the one command that hosts the relay+controller over HTTPS from this PC |
| `../docs/` | Design docs, plans, findings — check dates/"Superseded" notes, this project changes hosting/physics decisions often |

**The PC is authoritative for everything.** Phones never simulate; they send normalized
sensor deltas (`AIM`, `THROW`, `PICK_ROCK`) and receive broadcast state (`SPECTATE`). There is
no Unity-to-Unity networking.

## Running and testing

There is no Unity CLI test runner set up for this project; verification is via in-editor menu
items and the relay's own Node test scripts.

**Play the game** — start hosting from the repo root, then press Play on `Bootstrap` in Unity:

```bash
pwsh ./deploy/local-https.ps1
```

This detects the LAN IP, borrows a `*.local-ip.sh` certificate, and writes `relay-url.txt` /
`relay-env.txt` at the repo root (gitignored) for `RelayClient`/`LocalServices` to read. Ctrl+C
tears it all back down. Full runbook, including known-broken step 6 and its workaround:
[`../deploy/README.md`](../deploy/README.md). Desktop-only testing (no real sensors, two
browser tabs) is also documented there and in [`../README.md`](../README.md).

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
| `RockSkipping/Verify/Print Skip Curve` | Runs `SkipPhysics` power/angle sweeps per rock × tuning set, writes `skip-curve.txt` at repo root — the acceptance check for "is this rock's tuning any good". |
| `RockSkipping/Rocks/Build Catalog from FBX` | Regenerates `Assets/Rocks/Rock_0N.asset` + `Resources/RockCatalog.asset` from `Resources/Fbx/RocksRemeshed.fbx`. Idempotent — preserves hand-edited display names/tints/tuning refs. |
| `RockSkipping/Rocks/Seed Authored Tunings` / `Generate Derived Tunings` | Write `Assets/Rocks/Tuning/{Authored,Derived}/<Id>.asset` from the baseline `Resources/SkipTuning.asset`. Authored only writes files that don't exist yet; Derived always overwrites. |
| `RockSkipping/Rocks/Validate Catalog` | Refuses (and names the offender) if a rock's tuning varies a catalog-wide field — see Tuning rules below. |
| `RockSkipping/Rebuild Audio Bank` (+ `(reset tuning)`) | Regenerates `Assets/Scripts/Audio/AudioBank`-driven procedural SFX. |
| `RockSkipping/Setup/Create Scenes And Build Settings`, `Regenerate Solution And Project Files` | One-time/CI-adjacent project scaffolding. |

There is no out-of-band C# compile check — Unity holds an exclusive project lock, so
`Unity -batchmode` cannot compile while the Editor has the project open. Don't try to build one;
verify by checking the open Editor's Console (a prior attempt at this,
`tools/check-compile.ps1`, was built and then deleted for exactly this reason — see the Build
log in [`../docs/plans/rock-choosing-system.md`](../docs/plans/rock-choosing-system.md)).

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
[`../README.md`](../README.md) "Notes for whoever picks this up" and PLAN.md's "Event system"
section for the three fixes that had to land (persist-on-scene-load, Unity-null-aware
destroyed-subscriber detection, calling `base.OnDestroy()`). New event types live under
`Scripts/EventTypes/<Category>Events/`, namespaced `EventTypes.<Category>Events`, immutable
get-only properties set in the constructor — follow an existing file in that folder as the
template rather than writing from scratch. Publish/subscribe always through
`EventManager.Instance?.` (null-conditional).

### Networking (`Scripts/Net/`)

`RelayClient` connects to the Node relay (`ClientWebSocket`, chosen over NativeWebSocket to add
zero dependencies — sends are thread-affine, hence funneled through one send queue). `AIM`/
`THROW` traffic prefers a direct WebRTC DataChannel per phone (`PeerLink`) and falls back to the
relay socket per-phone if negotiation fails; roster/turn/score/spectator state always goes over
the relay. `LocalServices` owns the relay child process on this machine when hosting locally;
`HotspotService`/`CertificateProvider` support the PC-hosted-hotspot fallback documented in
`../deploy/README.md`. `Messages.cs` holds the serializable DTOs mirroring the wire JSON —
extend it (message type + DTO + `RelayClient` event + `Dispatch` case) rather than sniffing new
fields out of `RawMessageReceived`.

### Motion pipeline

Phones normalize sensors *before* sending — Unity never sees a platform (iOS/Android)
difference. The governing rule and the iOS/Android landmines (no `acceleration` on iOS,
opposite-direction compass headings, `requestPermission()` gating) are in PLAN.md's "The motion
pipeline" section; the current heading/attitude conversion used on both the throw path and rock
selection is `PhoneAttitude.HeadingDirection`/`HeadingDeg` (`Scripts/Game/PhoneAttitude.cs`) —
**one conversion, reused everywhere a heading is needed**, no ad-hoc Euler math at call sites.

### Skip physics (`Scripts/Game/SkipPhysics.cs`)

A custom, pure, side-effect-free stepping function (`SkipPhysics.Step`), not PhysX rigidbody
collision — needed both for precise per-bounce control and so the aim preview can run the same
math speculatively, many frames ahead, without side effects. **`Step` must never publish
events or otherwise touch anything observable** (score, audio, VFX, haptics) — only the caller
driving a *real* throw does that (`RockController`/`RockLauncher`), because the preview runs
`Step` continuously while a player merely aims and would otherwise "score" imaginary throws.

### Rock catalog and tuning (`Assets/Rocks/`, `Scripts/Game/RockDefinition.cs`, `RockCatalog.cs`)

Six rock meshes (`Resources/Fbx/RocksRemeshed.fbx`), each with its own `RockDefinition` asset
carrying two `SkipTuning` variants — **Authored** (hand-tuned archetype) and **Derived**
(computed from mesh bounds: flatness/size/elongation) — switchable per-match via
`MatchSettings.TuningSet` for A/B comparison. **Hard constraint, enforced by
`RockCatalogBuilder`'s validator:** `Gravity`, `WaterY`, `MinSpeed`, `MaxSpeed`, `FixedStep`
must be identical across all six rocks in both tuning sets — several systems
(`WaterRipples`, `SfxDirector`) normalize thresholds against the *global* `MaxSpeed` read once
at startup, and per-rock variance there would silently desync those. Rock character comes from
`MinRetention`/`MaxRetention`, angle/tolerance fields, `MinSkipSpeed`, `Restitution` instead.
Full data-flow, wire format, and the shore-pointing selection UI are in
[`../docs/plans/rock-choosing-system.md`](../docs/plans/rock-choosing-system.md) — this is the
plan the current `RockChoosing` branch implements; check its "Build log" section for
in-progress state and gotchas before assuming something described there is finished.

### Scoring

Scoring is deliberately push-based: gameplay code (`RockController`, `RingGate`, `TurnManager`)
publishes well-named events (`SkipOccurredEvent`, `RingPassedEvent`, `ThrowCompletedEvent`) and
never computes points itself; only `ScoreManager` owns point values, subscribed to those events.

### Audio (`Scripts/Audio/`)

`AudioBank`/`ProceduralClips`/`SfxDirector` — procedurally generated SFX built by the
`RockSkipping/Rebuild Audio Bank` menu commands (backed by `tools/audio/generate-sfx.js`),
not authored clips checked in as assets.

## Working conventions specific to this repo

- **Docs drift fast and say so.** Several docs (`PLAN.md` especially) contain explicit
  "Superseded — see X" callouts where a later decision reversed an earlier one (e.g. hosted
  relay → local-PC relay; URP → built-in render pipeline). Read the callout, not just the
  original text, and check `docs/` for a more recent doc on the same topic before acting on an
  older one.
- **`[SerializeField]` defaults are dead on components built via `AddComponent`.** Several
  tuning components (`AimFeedbackTuning`, `RockSelectTuning`, etc.) exist specifically because
  `Bootstrap`/runtime code builds objects with `AddComponent` rather than instantiating an
  Inspector-configured prefab, so field values must come from C# initializers, not the
  Inspector.
- **`Resources/` is intentionally a short, curated list** of things loaded by name at runtime
  (`SkipTuning`, `RockCatalog`, `RockObject.prefab`, …). New per-rock or per-asset data goes
  under `Assets/Rocks/` (or a similarly named folder) and is pulled in as a normal asset
  reference from something that *is* in `Resources/`, not added to `Resources/` itself.
- **`mesh.vertices` is unavailable** on the rock meshes (`RocksRemeshed.fbx.meta` sets
  `isReadable: 0`) — use `mesh.bounds`/`mesh.vertexCount` for any mesh-derived logic.
