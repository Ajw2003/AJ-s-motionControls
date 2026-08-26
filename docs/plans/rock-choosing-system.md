# Rock Choosing System — implementation plan

## Context

`Resources/Fbx/RocksRemeshed.fbx` holds six rock meshes, but only one of them is ever
seen: `Resources/RockObject.prefab` is a variant of the *old* `Rock.fbx` with a hard-wired
`m_Mesh` override pointing at a single mesh out of the remeshed file. Nothing in
`Assets/Scripts` selects among the six — a grep for `MeshFilter|sharedMesh|meshIndex`
returns no rock hits. The other five are imported and dead.

At the same time there is exactly one `SkipTuning` asset for the whole game, loaded by name
from `Resources/SkipTuning` in four places, so every throw by every player uses identical
physics. Stone choice currently cannot mean anything.

This plan makes stone choice real in three parts:

1. **Six rocks, six tunings — twice over.** A catalog binds each mesh to a display name, a
   scale, and *two* `SkipTuning` assets: a hand-authored archetype and one derived from the
   mesh's own bounds. Both sets get built so they can be compared against real data rather
   than argued about.
2. **Choosing by pointing.** In the lobby, the six stones lie on the shore. Every player
   points their phone and taps to claim one, reusing the existing phone calibration, the
   existing `PhoneAttitude` conversion, and the existing 20 Hz→smooth Slerp. The picked
   stone is theirs for the whole match.
3. **An expandable host settings panel.** Between matches the host can flip *Random rocks*,
   swap *Authored ↔ Derived* tunings (the comparison switch), and change rounds per match —
   from a panel driven by a list, so a seventh setting is one line and no UI edit.

The alternative pointing scheme is **documented, not built** — see the last section.

---

## Constraints this has to respect

These are load-bearing rules the existing code states explicitly. Breaking one is how this
feature silently goes wrong.

**The launch speed range must stay identical across all six rocks.** `WaterRipples.Bind`,
`SfxDirector.Awake` ([SfxDirector.cs:57](Scripts/Audio/SfxDirector.cs:57)) and `RockTrail`
normalise their thresholds against `SkipTuning.MaxSpeed`, and two of the three read the
*global* `Resources/SkipTuning` once at startup. If per-rock `MaxSpeed` varied, those
normalisers would be wrong for five of six rocks — the exact failure `WaterRipples` already
carries a comment about (thresholds hardcoded at 4 and 25 against a `MaxSpeed` of 26, so
raising launch speed clamped every impact to full strength). It also keeps *power is the
only driver of distance* intact: if rocks differed in top speed, "how hard did I throw"
would mean something different per rock.

So `Gravity`, `WaterY`, `MinSpeed`, `MaxSpeed`, `FixedStep` are **catalog-wide**. Rock
character comes from `MinRetention`/`MaxRetention`, `BestFaceAngleDeg`, `OptimalAngle`,
`ToleranceDeg`, `MinSkipSpeed`, `SkipExitAngleDeg`, `OpeningHopAngleDeg`, `OpeningHopSkips`,
`Restitution`. The catalog builder validates this and refuses to write a set that violates
it, naming the offending rock.

**One quaternion, one conversion.** Pointing must go through
[`PhoneAttitude.HeadingDirection`](Scripts/Game/PhoneAttitude.cs:62) — the same function the
launch uses. No Euler angles, no sign flips at the call site.

**`AimPreview` must not run in the lobby.** Its AIM filter is
`if (!string.IsNullOrEmpty(active) && aim.playerId != active) return;` and
`TurnManager.ActivePlayer` is **null outside Playing** — so today every lobby AIM frame from
every phone passes through and yanks the ghost. Latent now, visible the moment phones start
streaming AIM during selection. Gate the ghost and arc on `Phase == Playing`.

**Mesh bounds only, not vertices.** `RocksRemeshed.fbx.meta` has `isReadable: 0`, so
`mesh.vertices` is not safely available. `mesh.bounds` and `mesh.vertexCount` are, and are
all the derivation needs.

---

## Part 1 — Rock data model

### New assets and scripts

| File | What it is |
|---|---|
| `Scripts/Game/RockDefinition.cs` | ScriptableObject: `Id`, `DisplayName`, `Blurb`, `Mesh`, `LocalScale`, `MeshRotation`, `Tint`, `AuthoredTuning`, `DerivedTuning`. Method `Tuning(TuningSet set)` with fallback to whichever exists. |
| `Scripts/Game/RockCatalog.cs` | ScriptableObject at `Resources/RockCatalog`. `RockDefinition[] Rocks`, `ById(string)`, `IndexOf`, `Count`, `MaxPredictSteps` (max across every tuning in the catalog), `CreateDefault()` — the same three-step load-or-warn pattern `AimPreview.Awake` uses for `SkipTuning`. |
| `Scripts/Game/TuningSet.cs` | `enum TuningSet { Authored, Derived }`. |

Only the catalog lives in `Resources/`. The definitions and their tunings live in
`Assets/Rocks/` and are pulled in as asset dependencies — `Resources` stays a short list of
things loaded by name, which is what it is for today.

### Editor tooling — `Assets/Editor/RockCatalogBuilder.cs`

Menu `RockSkipping/Rocks/Build Catalog from FBX`:

- `AssetDatabase.LoadAllAssetsAtPath("Assets/Resources/Fbx/RocksRemeshed.fbx")`, keep the
  `Mesh` objects, sort by name for a stable order (they import as `NurbsCurve.001` …
  `NurbsCurve.007` — leftover Blender curve names).
- Create or update `Assets/Rocks/Rock_01.asset` … `Rock_06.asset`, assigning the mesh.
- **Normalise scale.** Compute a `LocalScale` per rock that brings each mesh's footprint to
  the same target diameter the current stone has (`RockObject.prefab` hand-corrects with
  `0.75, 0.39, 0.75`). Raw meshes out of Blender will differ wildly in size; without this the
  six read as "one rock and five mistakes". Height is scaled proportionally so genuine
  thickness differences survive — that difference is what the derived tunings key off.
- **Idempotent.** Re-running refreshes the mesh reference and the scale, and leaves
  hand-edited `DisplayName`, `Blurb`, `Tint` and both tuning references alone.
- Writes/updates `Assets/Resources/RockCatalog.asset`.

### Editor tooling — `Assets/Editor/RockTuningGenerator.cs`

Menu `RockSkipping/Rocks/Generate Derived Tunings` and
`RockSkipping/Rocks/Seed Authored Tunings`.

Both start from `Resources/SkipTuning.asset` as the baseline, so neither set is an invention
— they are perturbations of numbers already known to pass `SkipCurve`'s verdict.

**Derived** (`Assets/Rocks/Tuning/Derived/<Id>.asset`, overwritten on every run):

```
flatness  = 1 - height / max(width, depth)          clamped 0..1
size      = max(width, depth), normalised across the catalog
elongation= |width - depth| / max(width, depth)
```

- `flatness` → `MaxRetention` and `ToleranceDeg` up, `SkipExitAngleDeg` down.
  A flatter stone keeps more speed, forgives more angle error, and leaves the water shallower.
- `size` → `MinSkipSpeed` up, `Restitution` up. A bigger stone needs more speed to stay up
  and slaps harder when it fails.
- `elongation` → offsets `BestFaceAngleDeg` and `OptimalAngle` together. Note these two
  currently disagree by 5° in the shipped asset (`OptimalAngle: 10` vs `BestFaceAngleDeg: 5`)
  and govern the two halves of a bounce; the generator writes them as a matched pair and
  **logs a warning if the authored set has them apart**, so the split is at least visible.
- Everything on the catalog-wide list above is copied verbatim from the baseline.

**Authored** (`Assets/Rocks/Tuning/Authored/<Id>.asset`, written **only if the file does not
exist**): a straight copy of the baseline, then hand-tuned in the Inspector. Targets for the
six archetypes — starting points, not final numbers:

| Rock | Character | Levers |
|---|---|---|
| Skimmer | Highest ceiling, punishes bad angle | `MaxRetention` high, `ToleranceDeg` tight |
| Slab | Reliable mid runs | `ToleranceDeg` wide, retention modest |
| Pebble | **The control** — baseline unchanged | — |
| Nugget | Few, dramatic skips | Retention low, `OpeningHopAngleDeg` high, `Restitution` high |
| Shard | Long tail of tiny skips | Retention very high, `MinSkipSpeed` very low |
| Boulder | High risk, big distance | `MinSkipSpeed` high, `ToleranceDeg` tight |

Leaving Pebble as an untouched control is what makes the authored-vs-derived comparison
readable — one row in the report is guaranteed to be today's game.

---

## Part 2 — Selection state

### `Scripts/Managers/RockSelection.cs` (new singleton)

Created in `Bootstrap` **after `ScoreManager`, before `TurnManager`** — `TurnManager` will
ask it for the active player's rock name on the broadcast path, and `EventManager` delivers
in subscription order. See the ordering comments in
[Bootstrap.cs](Scripts/Managers/Bootstrap.cs).

- Loads `Resources/RockCatalog` with the load-or-warn pattern.
- `Dictionary<string,string> _picks` — playerId → rockId.
- `RockDefinition For(string playerId)` — the pick, else the random assignment, else
  `Rocks[0]`. Never returns null.
- `SkipTuning TuningFor(string playerId)` → `For(playerId).Tuning(MatchSettings.TuningSet)`.
- Subscribes `PlayerJoinedEvent` (seed a default), `PlayerLeftEvent` (drop the pick),
  `MatchPhaseChangedEvent` (entering `Playing` freezes picks; returning to `Lobby` clears).
- Publishes a new `EventTypes/MatchEvents/RockPickedEvent(playerId, rockId, index)`.
- **Random mode**: when `MatchSettings.RandomRocks` is on, `StartMatch` assigns every player
  a rock from a `System.Random` seeded with a per-match seed that is logged and written into
  telemetry — so a surprising match can be reproduced. Phone picks are ignored while it is on
  and the shore shows a "random" banner rather than pretending the pointer matters.

### Wire format

Add to [Messages.cs](Scripts/Net/Messages.cs), following the existing `AimMessage` shape:

- `MessageType.PickRock = "PICK_ROCK"`, `[Serializable] class PickRockMessage { string playerId; }`
- `RelayClient`: a `PickRockReceived` event and a `Dispatch` case, alongside `AimReceived` /
  `ThrowReceived`. First-class rather than sniffed out of `RawMessageReceived`, so the
  server-side `playerId` stamping and the `IngestFromPeer` override apply unchanged.
- `OutboundMessages.BuildSpectate` gains `rockId` and `rockName` per player so idle phones can
  show what everyone claimed. `rockName` already exists as a single global field
  ([TurnManager.cs:325](Scripts/Managers/TurnManager.cs:325)) — that cached global read is
  replaced by `RockSelection.For(ActivePlayer).DisplayName`, and the cache goes with it since
  the value is per-player now.

The payload carries **no rock id from the phone**. The TV owns the highlight (it has the
shore layout; the phone does not), and the tap means "confirm what is highlighted". A
`PICK_ROCK` arriving outside `Lobby` is dropped, mirroring the guard in
[RockLauncher.OnThrow](Scripts/Game/RockLauncher.cs:70).

---

## Part 3 — Pointing at the shore

### `Scripts/Game/ShoreRocks.cs` (new)

Active only while `Phase == Lobby`. Built by `AimPreview` alongside the placeholder water, so
it shares the same rig and camera framing.

**Layout.** One instance per catalog rock, in an arc on the near shore at a fixed radius from
`AimPreview.ThrowOrigin`, spread across a yaw fan (default ±38°). Each gets the definition's
mesh, `LocalScale`, `MeshRotation` and `PlaceholderArt.Create(Tint, StoneQueue)`. The name
and blurb float above it.

**The pointer.** For each player's latest AIM frame:

```
attitude = PhoneAttitude.ToUnity(qw, qx, qy, qz)      // the one conversion
yaw      = PhoneAttitude.HeadingDeg(attitude)          // already exists, readouts + this
```

smoothed with the same frame-rate-independent `1 - exp(-k·dt)` Slerp `AimPreview.Update`
uses, then mapped to whichever rock's yaw slot contains it. **Yaw only** — pitch is
deliberately ignored, because a player thinking about their choice naturally lets the phone
drop, and a 2-D pick would lose their selection when they did.

**Hysteresis.** A rock keeps the highlight until the pointer is `StickyMarginDeg` (default 3°)
past the boundary. Without it a pointer resting on a boundary flickers at 20 Hz — the same
class of problem `AimFeedbackTuning.ColourSmoothing` exists to solve for the arc colour.

**Why the calibration carries over for free.** The phone's identity quaternion means
"however you were holding it when you tapped" — so heading 0° is the centre of the fan by
construction, and flat is flat. Nothing new is calibrated. What is reused, unchanged:
`MotionSource.calibrate()` on the phone (`throw.js` `arm()`), `PhoneAttitude.ToUnity`,
`PhoneAttitude.HeadingDeg`, and the exponential Slerp.

**The honest limitation.** `webkitCompassHeading` is deliberately ignored project-wide
(absolute headings disagree across platforms), so the fan is centred on wherever the player
happened to point when they tapped — not on the TV. Every player still gets a working ±38°
fan, but the TV cannot draw "you are pointing at me". Mitigation: an explicit **RE-CENTRE**
control on the phone that calls `motion.calibrate()` again. This is a real constraint of the
approach, not a bug to be fixed later.

**Multiple players at once.** Everyone in the lobby points simultaneously. Each gets their own
labelled marker on the shore; a stone may be claimed by more than one player. No exclusivity —
stone identity is not a scarce resource, and contention would need a whole conflict UI to
solve a problem nobody has.

**No-sensor clients cannot point.** The desktop fallback path in `throw.js` produces no
orientation, so a desktop test client keeps the default rock (or a random one). Stated
plainly rather than papered over; the documented alternative below is what would close it.

### `Scripts/Game/RockSelectTuning.cs` + `Resources/RockSelectTuning.asset`

Because `Bootstrap` builds these components with `AddComponent`, `[SerializeField]` values sit
at their C# initialisers forever — the exact reason `AimFeedbackTuning` exists. Fields:
`FanDegrees` 38, `ShoreRadius`, `ShoreHeight`, `StickyMarginDeg` 3, `PointerSmoothing`,
`HighlightScale`, `HighlightColour`, `ConfirmedColour`, `PointerStaleSeconds`.

### Phone — `controller/index.html` + `controller/js/app.js`

- New `<section id="chooseScreen">`: a stone button labelled **CONFIRM**, a small
  **RE-CENTRE** link, and a line showing the highlighted stone's name (from `SPECTATE`).
- `showScreen()` gains `choose` in its screen list.
- On `SPECTATE` with `phase === 'LOBBY'`: show `choose`, call `motion.calibrate()` once on
  entry, and start streaming AIM frames — `ThrowController.arm()` already does exactly this
  pair, so drive it through a lobby mode on the controller rather than a second implementation.
- CONFIRM sends `{ type: 'PICK_ROCK' }` through `sendInput` (peer if open, relay otherwise).
- RE-CENTRE calls `motion.calibrate()` and nothing else.
- Leaving `LOBBY` returns to the existing wait/throw screens untouched.

**Throw safety:** the lobby aim mode must not be able to fire a throw. `ThrowController`'s
release detection runs per-sample inside `_onSample`, so the lobby mode has to suppress
`_fire()` outright rather than relying on the screen being different. This is the one place
in the phone change that can break gameplay if it is got wrong.

---

## Part 4 — Wiring the chosen rock into play

| File | Change |
|---|---|
| [Scripts/Game/AimPreview.cs](Scripts/Game/AimPreview.cs) | `Tuning` resolves from `RockSelection.TuningFor(ActivePlayer)`, cached per turn — **not** re-resolved per frame. Size `_path` from `catalog.MaxPredictSteps`, not one tuning's. Gate ghost + arc on `Phase == Playing`. On `TurnStartedEvent`, apply the active rock to the ghost: swap `MeshFilter.sharedMesh` and `localScale`, then **re-capture `_ghostBaseScale`** — `UpdateGhostCharge` multiplies it, so a stale base makes the ghost jump size on the first wind-up. Build `ShoreRocks` next to the placeholder water. |
| [Scripts/Game/RockLauncher.cs](Scripts/Game/RockLauncher.cs:148) | `CreateRockObject(SkipTuning)` → `CreateRockObject(RockDefinition)`: apply mesh, scale, tint, then `RockTrail.Attach(go, rock.Tuning(set))` as today. The stale XML doc calling the stone "a flat box… not a scaled sphere" is now doubly wrong — rewrite it. |
| [Scripts/Managers/TurnManager.cs:325](Scripts/Managers/TurnManager.cs:325) | `RockDisplayName` → per-player lookup, cache removed. `roundsPerMatch` moves behind `MatchSettings`. |
| `Resources/RockObject.prefab` | Becomes the plain shell (transform + MeshFilter + MeshRenderer); the mesh is always replaced at spawn. Drop the `MeshCollider` override — it is destroyed at spawn anyway and points at a mesh that is about to be swapped. |

The ghost is **not** tinted per rock. `UpdateGhostCharge` writes `_ghostMaterial.color` every
frame from `IdleColour`/`ChargedColour`; a per-rock tint would have to multiply into those and
would cost the charge feedback its meaning. The rock is identified by silhouette, by the name
on the phone, and by the tint on the live stone and the shore.

---

## Part 5 — Host settings panel

### `Scripts/Managers/MatchSettings.cs` (new singleton)

Settings are a **list**, not fields, so adding one is a single line:

```csharp
abstract class Setting { string Key, Label, Help; abstract string Display { get; }
                         abstract void Next(); abstract void Prev(); }
class ToggleSetting : Setting
class EnumSetting<T> : Setting
class IntSetting : Setting          // min, max, step
```

`MatchSettings.All` is an ordered `List<Setting>`. Values persist to `PlayerPrefs` keyed by
`Setting.Key`, so the host's choices survive a restart. Day one:

1. **Random rocks** — `ToggleSetting`, default off.
2. **Tuning set** — `EnumSetting<TuningSet>`, Authored / Derived. *This is the comparison switch.*
3. **Rounds per match** — `IntSetting` 1..10, default 5. Included specifically to prove the
   panel is generic rather than three special cases in a trenchcoat.

Changes are refused while `Phase == Playing` — a mid-match tuning-set swap would make half a
match's telemetry unattributable.

### `Scripts/UI/MatchSettingsScreen.cs` (new)

OnGUI, no Canvas, matching `JoinScreen`/`ResultsScreen`. Renders `MatchSettings.All` in a
loop, so it never needs editing when a setting is added. Up/Down moves the cursor,
Left/Right changes the value, **F4** toggles the panel.

Discoverability without a layout fight: `JoinScreen` and `ResultsScreen` each gain **one line**
— a compact summary of current settings plus `Press F4 for match settings`. `JoinScreen`'s
full-screen panel already occupies the centre 60% at
[JoinScreen.cs:324](Scripts/UI/JoinScreen.cs:324), so the settings panel draws as a centred
modal over it and suppresses the start prompt while open, rather than competing for the
margins.

---

## Part 6 — The comparison, with real data

Two mechanisms, because the offline sweep and real players measure different things.

### a. Offline — extend [`Verification/SkipCurve.cs`](Scripts/Verification/SkipCurve.cs)

`Build()` currently reads `Resources/SkipTuning` and prints one curve. Change it to loop
**catalog × both tuning sets**, keeping the existing per-tuning power sweep, face-angle sweep
and `Verdict()` per rock — so a badly authored rock fails loudly against the same acceptance
checks that already exist. Then add a separation table:

```
                    authored                   derived
  rock          skips@0.8   dist        skips@0.8   dist       delta
  Skimmer            14     182.4            11     147.2        -3
  ...
```

And one new verdict check, which is the actual point of building two sets:

- **"the six rocks are distinguishable"** — across the six, `max - min` of skips at power 0.8
  must be ≥ 4, and no two rocks may be within 1 skip **and** 5 m of each other.

Run per set, so the report states outright which set separates better. Output stays
`skip-curve.txt` at the repo root via the existing `ResultPath`.

### b. In play — `Scripts/Verification/RockTelemetry.cs` (new)

The offline sweep uses ideal face angles. Players do not — that gap is exactly why the two
sets need real data. Subscribe to `RockLaunchedEvent` (origin, power) and
`ThrowCompletedEvent` (player, skips, distance), look up rock and set from `RockSelection` and
`MatchSettings`, and append one CSV row to `rock-throws.csv` beside `skip-curve.txt`:

```
timestamp, matchSeed, tuningSet, rockId, player, power, faceAngleDeg, skips, distance
```

`faceAngleDeg` comes from `PhoneAttitude.FaceAngleDeg` on the throw attitude — the number the
bounce actually cared about. Append-only, flushed per throw, so a crashed session keeps its
data. Play the same group through one match on Authored and one on Derived and the two files
answer the question.

---

## Verification

Run in order. Each step is checkable on its own.

**1. Assets exist and are sane.** In the Editor, run
`RockSkipping/Rocks/Build Catalog from FBX`, then `Seed Authored Tunings`, then
`Generate Derived Tunings`. Expect six `Rock_0N.asset`, twelve tuning assets, and a populated
`Resources/RockCatalog.asset`. Deliberately edit one derived tuning's `MaxSpeed` and re-run the
builder — it must refuse and name that rock.

**2. Physics separates.** Run the existing `SkipCurve` menu entry, then:

```bash
cat skip-curve.txt
```

Every rock must PASS its own verdict on both sets, and the separation check must PASS on at
least one set. If Derived fails separation, that is a result, not a blocker — it is the answer
to "are mesh bounds enough".

**3. Selection, single phone.** Start the relay, launch from the Bootstrap scene, scan in with
one phone. In the lobby: six stones on the shore, a marker tracking the phone's yaw, CONFIRM
placing a tick under a stone, RE-CENTRE re-zeroing the fan. Swinging the phone in the lobby
must **not** launch a rock — check the console for any `RockLaunchedEvent`.

**4. Selection, two phones.** Two markers, independently tracked, both able to claim the same
stone. Confirm each phone's `SPECTATE` shows its own stone name.

**5. It reaches the water.** Start the match. The ghost and the flying stone must both be the
claimed mesh, and `RelayDebugHud`'s predicted skips must differ between two players who chose
different rocks. Press F4 mid-match — settings must refuse to change.

**6. Random mode.** Return to the lobby, F4, turn *Random rocks* on, start. Every player gets
an assigned stone, the shore shows the random banner, and the seed is in the console.

**7. Telemetry.** After a full match:

```bash
cat rock-throws.csv
```

One row per throw, with the right `rockId` and `tuningSet`. Flip *Tuning set* between matches
and confirm the column changes.

---

## Alternative pointing scheme — documented, not built

**Phone-screen stone carousel.** The choose screen renders the six stones as cards on the
phone itself — a horizontal strip, name and blurb under each, swipe or tap arrows to move,
tap the card to claim. The TV shows the shore as a display only, highlighting each player's
current card as a mirror rather than as the input surface. `SPECTATE` would carry the catalog
(ids, names, blurbs) once on join so the phone can render without shipping the meshes.

**What it gets you.** No sensors, no calibration, no fan, no hysteresis, and no dependence on
which way the player happened to be facing. It works on the desktop fallback client, which the
pointing scheme cannot — that is a real hole in the chosen approach, not a hypothetical one.
It works for a player sitting sideways to the TV, or holding the phone under a table. It is
also the only version that can show a stone's *stats* legibly, because a phone screen is
inches from the eye and a TV is across a room.

**What it costs.** It is a menu. The premise of the whole control scheme is that the phone is
the rock — you hold it, you tilt it, you throw it — and picking your stone off a list is the
one moment that premise is set aside. It also moves real UI weight onto the phone: six card
layouts, a swipe gesture, a scroll container, and catalog data on the wire, where the pointing
version adds no phone rendering at all beyond one button and one label.

**A hybrid worth noting.** These are not exclusive. The carousel could ship purely as the
automatic fallback when the phone reports no motion sensors or an insecure context — the same
condition `app.js` already detects in `isSecureContextOk()` and already surfaces as *"Needs
HTTPS"*. That would close the desktop-client hole without ever showing a menu to a player
whose phone works. If the pointing scheme turns out to be fiddly on real hardware, this is the
cheapest place to retreat to, because the catalog-on-the-wire piece is the only part not
already built by this plan.

---

## Build log

**2026-08-26 — Part 4 (wiring into play) built. Not yet verified in-editor.**
`AimPreview` now resolves `Tuning`/`CurrentRock` from `RockSelection.TuningFor`/`For` on
`TurnStartedEvent` (cached per turn, not per frame), sizes `_path` from
`RockCatalog.MaxPredictSteps`, and swaps the ghost's mesh + scale (re-capturing
`_ghostBaseScale`) to match. `RockLauncher.CreateRockObject` now takes the `RockDefinition`
too and applies mesh/scale/tint, falling back to the original plain box when one is missing.
`TurnManager`'s cached global `RockDisplayName` is gone — replaced with `RockDisplayNameFor`
per player — and `BroadcastSpectate` now also sends a `"rocks"` map (id + name per connected
player) via a new `OutboundMessages.BuildSpectate` parameter, so the phone's choose screen
(added in Part 3) can show "On: <rock>" once resolved. `RockPickedEvent` now triggers an
immediate `BroadcastSpectate` — without it a confirmed pick could sit invisible until some
unrelated broadcast happened to go out.

Deliberately **not** applied: `RockDefinition.MeshRotation`. Both the ghost and the live rock
have their rotation overwritten wholesale every frame from phone/solver attitude, with nowhere
to compose a static per-mesh correction without also changing what the solver reads as face
angle — see the remarks on `AimPreview.OnTurnStarted`. Every mesh built so far imports flat, so
this is a real but so-far-unobserved gap, not a fix that got skipped.

Also skipped: hand-editing `Resources/RockObject.prefab` to drop its `MeshCollider` override.
Functionally moot — `RockLauncher`/`AimPreview` both `Destroy()` whatever collider the instance
has at spawn regardless of type — and not worth the risk of hand-editing prefab YAML for a
cosmetic cleanup.

**2026-08-26 — Part 2 (selection state) built; Part 1 confirmed compiling.**
`RockSelection.cs` (new singleton), `RockPickedEvent.cs`, and the `PICK_ROCK` wire format
(`Messages.cs` + `RelayClient.cs`) are in, wired into `Bootstrap` after `RelayClient` and
before `TurnManager`. Confirmed compiling clean in the open Editor (Console checked by hand —
no errors), which also retroactively confirms Part 1's assets/scripts from the entry below.

Deviation from the plan text: `RandomRocks`/`ActiveTuningSet` live as plain fields on
`RockSelection` rather than on a `MatchSettings` singleton, since Part 5 hasn't been built yet
and RockSelection can't function without reading them from somewhere. Part 5 should read/write
these properties rather than duplicate the state. Also, `RockSelection` does not yet subscribe
to `PickRockReceived` — `PICK_ROCK` carries no rock id (a tap confirms whatever's highlighted),
and only Part 3's `ShoreRocks` will know what that is, so that wiring is deferred to Part 3.

**2026-08-26 — scrapped an unasked-for compile checker.**
While implementing Part 1 I built `tools/check-compile.ps1`, a script that compiled every
script under `Assets/` out-of-band (Unity holds an exclusive project lock, so
`Unity -batchmode` cannot run while the Editor is open). It was never requested. It has been
deleted along with its output under `Temp/CompileCheck/`; `tools/audio/generate-sfx.js` was
untouched. **Part 1's C# has therefore not been compiled** - the Unity Editor is the only
thing that has compiled it, and only for whichever files it had picked up at the time.
Verify by alt-tabbing to the Editor and reading the Console.

Two findings from that detour, kept because they cost real time:

- A `.ps1` written as UTF-8 **without** a BOM is read as ANSI by PowerShell 5.1. A single
  em-dash inside a comment was enough to break the tokenizer with a misleading
  "Missing closing '}'" pointing 70 lines away from the real character.
- Unity's bundled Roslyn (`Editor/Data/MonoBleedingEdge/.../Roslyn/csc.exe`) is a Mono
  assembly and will not start under plain .NET Framework - it dies on a missing
  `System.Text.Encoding.CodePages` before reading any source.
