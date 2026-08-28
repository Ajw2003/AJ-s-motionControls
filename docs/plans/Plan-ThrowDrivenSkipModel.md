# Plan — invert the skip model: the throw drives, the rock modulates

## Context

Skip count falls out of the solver as:

```
skips = ln(vEnd / v0) / ln(speedKept)
```

Throw speed (`v0`) enters through a **logarithm**. The rock's retention constant (`speedKept`)
enters through **1/ln()**, which goes hyperbolic near 1. Measured against the current baseline
and confirmed against `skip-curve.txt`:

| change | effect on skips |
|---|---|
| Throw twice as hard (30 -> 60 m/s) | 35 -> 48 (+39%) |
| `MaxRetention` 0.95 -> 0.96 | 48 -> 61 (+26%) |

A one-point edit to a rock constant is worth roughly a 60% change in how hard a player throws.
The tuning drives the game and the throw modulates it — the opposite of the intent.

Compounding it, of the three things a player controls:

- **Speed** is logarithmic, and only reaches the sim after being normalised to 0..1 on the phone.
- **Spin** has *zero* physical effect — it is drawn and discarded.
- **Levelness** is the only input with real leverage, and it is double-counted: the same
  `ToleranceDeg` divides both `quality` (lift) and `angleFactor` (retention).

Intended outcome: the raw phone signal drives the simulation, the constant count drops hard, and
each rock carries exactly three bounded knobs. 18 per-rock fields become 3.

## The model

### 1. Raw pass-through by default

| quantity | today | after |
|---|---|---|
| direction | `PhoneAttitude.HeadingDirection` | unchanged — already raw |
| toss pitch | `LaunchPitchDeg` constant | `PhoneAttitude.FaceAngleDeg` — the phone's tilt *is* the toss angle |
| speed | `lerp(MinSpeed, MaxSpeed, power)`, power normalised on the phone | `swingSpeed * SpeedScale` |
| spin | visual only | real, via forgiveness below |

`SpeedScale` is the one constant that cannot be removed — it converts hand m/s to stone m/s. It
replaces three abstractions: `MinSpeed`, `MaxSpeed`, and the phone's
`minSwingSpeed`/`maxSwingSpeed` normalisation.

### 2. Energy loss falls out of geometry, not an authored constant

The crux of the inversion. Today `speedKept` is authored per rock and dominates. Instead a bounce
loses energy according to **how steeply the stone hits** and **how far its face is from its
velocity** — both already computable inside `SkipPhysics.Step` from state it holds.

A level release then skips far because it is genuinely shallow, not because a constant said so.
One global coefficient remains (how lossy water is), replacing two per-rock fields.

### 3. What each axis does to a real skipping stone

Grounding for the model below. The reference is Clanet, Hersen & Bocquet, *Secrets of successful
stone-skipping* (Nature, 2004), and the follow-up JFM work on spin stabilisation.

**Pitch — attack angle. The dominant variable.** Lift is the water's reaction on the tilted
underside, planing like a hull: roughly `rho * v^2 * A * sin(attack)`. There is a well-established
optimum near **20 degrees**, where bounce count peaks and is most tolerant of error. The failure
is **asymmetric** — too shallow gives weak lift and a gradual skimming decay that a run can
survive; too steep and the stone plows in and stops abruptly. The current symmetric tent around
`BestFaceAngleDeg` gets this wrong in both directions.

**Spin — a stabiliser, contributing no lift whatsoever.** Angular momentum holds the stone's
attack angle through each impact. Without spin the stone tumbles, attack angle randomises, and
the run dies in one or two bounces; above a threshold rate the attitude holds. This is the
empirical justification for "spin buys tolerance" — it is the entirety of what spin does, so
tying it to tolerance and nothing else is the physically honest choice as well as the legible one.

**Roll — splits the lift vector.** The reaction is normal to the face, so banking the face banks
the force: vertical lift falls as `cos(roll)`, a lateral component appears as `sin(roll)` and the
stone curves, and past a threshold the leading edge digs in and it sinks on contact.

**Yaw — aim.** Where the stone points is where it goes. Physically interesting only when yaw
disagrees with the velocity vector, which produces sideslip drag.

**No hidden neutral-pose offset.** Today the real ~20 degrees is secretly remapped onto a
phone-neutral 0 via `BestFaceAngleDeg`, so "hold it flat" is silently rewritten into "you are at
the optimum". That is precisely the kind of buried decision this rewrite exists to delete, and it
is removed: **the physics gets the raw angle off the phone.** The only thing that softens it is
the per-axis forgiveness band, which is visible, tunable, and separately settable for pitch, roll
and yaw rather than being one constant standing in for all three.

Two honest consequences:

- **A phone at the tap pose has a 0 degree attack angle and will not skip well.** Players have to
  actually tilt the phone toward the optimum, the way you angle a real stone. This is the point —
  `PhoneAttitude` already states the project's principle as "the phone is the rock", and raw
  pass-through is more faithful to that than the offset was.
- **"Raw" is still relative to the tap pose**, because the attitude on the wire is the rotation
  *from where the player was holding the phone when they tapped* to where it is now. So each
  player calibrates against their own grip rather than against a constant someone authored. That
  is a player-owned reference frame, not a hidden offset.

### 4. Roll is a physical input, not just an axis to be judged

The stone's mesh is already drawn from the raw quaternion, so **the player's roll is already
visible on screen** — and the solver ignores it completely. `SkipPhysics` reads only
`FaceAngleDeg` (pitch) and `HeadingDirection` (yaw). A banked stone looks banked and then skips
as though it were flat.

That is a see/feel disagreement of the exact kind `PhoneAttitude`'s remarks exist to prevent
("what the player sees is what the water reacts to"). Roll therefore drives the bounce directly:

- **Lift falls off with bank** — a stone on its edge planes less than a flat one, so the vertical
  kick scales by `cos(roll)`. Rolled far enough it catches an edge and dies, which is what a real
  stone does.
- **The lift that remains is deflected sideways**, so a banked throw curves. This is the reason a
  player would ever want to control roll rather than merely avoid it.

Both come from the same rotated face normal, so this is one calculation, not two special cases,
and past the edge-catch threshold the same normal is what says the stone digs in.

**The camera stays level.** Several people watch one TV in a couch game, so the chase camera in
`RockLauncher.FollowActiveRock` is left alone — the stone rolls, the screen does not.

### 5. Forgiveness — three global opt-in bands

Global, not per-rock, so "how forgiving is this game" is tuned in one place and rocks cannot
quietly become *easier* rather than *stronger*.

- `ForgivenessPitchDeg` — nose up/down (`FaceAngleDeg`)
- `ForgivenessRollDeg` — bank (**needs a new `PhoneAttitude.RollDeg`; no accessor exists**)
- `ForgivenessYawDeg` — aim left/right (`HeadingDeg`)

Within a band, error counts as zero; outside, only the excess counts. A band of `0` skips that
axis entirely; all three `0` skips forgiveness via a single early-out.

These three bands are now the **only** softening anywhere in the pipeline. With
`BestFaceAngleDeg`, `OptimalAngle` and `ToleranceDeg` gone, there is no other place where a
constant quietly stands between the player's wrist and the water — which is the whole point of
having three of them instead of one.

The band is a *forgiveness* window applied on top of the physics, not a replacement for it — the
asymmetry in section 3 lives in the bounce itself, so a stone that is too steep still plows in
even inside a generous band. Forgiveness widens what counts as "on target"; it does not make
plowing survivable.

**Spin buys tolerance only**: `effectiveBand = band + spinRate * SpinToleranceScale`. Speed stays
the single driver of distance, which is what the earlier spin revert protected (see the remarks
in `Assets/Scripts/Game/SkipTuning.cs`). A band authored at `0` stays off regardless of spin — the
skip-it test reads the authored value.

### 6. Per-rock — exactly three multipliers

`SpinMultiplier`, `VerticalMultiplier` (hop height / lift), `HorizontalMultiplier` (forward
carry). Bounded, so a good throw with a weak rock still beats a bad throw with a strong one.

### Fields removed

`MinSpeed`, `MaxSpeed`, `LaunchPitchDeg`, `BestFaceAngleDeg`, `OptimalAngle`, `ToleranceDeg`,
`MinRetention`, `MaxRetention`, `SkipExitAngleDeg`, `OpeningHopAngleDeg`, `OpeningHopSkips`,
`Restitution`, `MinSkipSpeed`.

Remaining global: `Gravity`, `WaterY`, `FixedStep`, `PredictSeconds` (simulation), `SpeedScale`,
`WaterLossCoefficient`, three forgiveness bands, `SpinToleranceScale`, `ReferenceSpeed`.

## Deprecation — what comes out

Done on this branch as deletions, not `[Obsolete]` shims. Nothing outside this repo consumes
these types, so a compile error is a better migration signal than a warning.

### The Authored/Derived A/B apparatus — remove entirely

Two tuning sets exist to answer one question: *are mesh bounds enough to tell rocks apart?* That
question dies with the model. Rock character becomes three explicit multipliers, so there is
nothing left to A/B, and `docs/Findings-RockSeparation.md` already records that the Authored side
was six byte-identical copies — the comparison never had two sides.

| file | action |
|---|---|
| `Assets/Scripts/Game/TuningSet.cs` | delete the enum |
| `Assets/Scripts/Editor/RockTuningGenerator.cs` | delete — both menu items (`Seed Authored Tunings`, `Generate Derived Tunings`), `Apply`, `WarnOnAngleSplit`, the flatness/size/elongation `Shape` normaliser |
| `Assets/Rocks/Tuning/Authored/`, `Assets/Rocks/Tuning/Derived/` | delete both folders — 12 assets and metas |
| `Assets/Scripts/Game/RockDefinition.cs:69-96` | `AuthoredTuning`/`DerivedTuning`/`Tuning(TuningSet)` collapse to one `Tuning` reference; `IsUsable` simplifies |
| `Assets/Scripts/Game/RockCatalog.cs:80,154` | drop the per-set loops |
| `Assets/Scripts/Managers/MatchSettings.cs:54-60,89` | delete the `EnumSetting<TuningSet>` — **this removes a user-facing match setting**, the only deletion here that is visible in play |
| `Assets/Scripts/Managers/MatchSettingsStore.cs:31-90` | drop `tuningSet` from the saved JSON and its parse/save path; tolerate the stale key in existing `match-settings.json` rather than erroring |
| `RockSelection.ActiveTuningSet` | delete the property and its `Bootstrap` ordering comment (`Bootstrap.cs:114`) |

### The separation-testing machinery — remove

Built to score the A/B. With one tuning set it has nothing to compare.

In `Assets/Scripts/Verification/SkipCurve.cs`: the `AUTHORED vs DERIVED` section, `Comparison()`,
`Separation()`, `DescribeSpread()`, `SkipSpread()`, `Reading`, the `readings` dictionary,
`anySetSeparates`, `MinSkipSpread`, `TooCloseSkips`, `TooCloseMetres`, and the "which set
separates better" verdict. This also retires the §7 summary-line bug from the findings doc
without fixing it, and voids findings §1, §2 and §6.

### The shared-field validator — probably remove

`RockCatalog.SharedFieldNames` and the `Validate` comparison loop (`RockCatalog.cs:110-175`) exist
because `MinSpeed`/`MaxSpeed`/`Gravity`/`WaterY`/`FixedStep` had to agree across six rocks. After
this change those fields are not per-rock at all — they live once, globally, so disagreement is
unrepresentable. Delete the loop; keep the `IsEmpty`/usable-rock checks, which still catch real
breakage.

### Phone-side normalisation — remove

`controller/js/throw.js`: `minSwingSpeed`, `maxSwingSpeed`, `minPeakAccel`, `maxPeakAccel` and the
`powerMetric` switch (`ThrowTuning`, lines 32-73, 184-186). The phone sends raw `swingSpeed` and
raw `spinRate`; Unity does the one conversion. The `power` field on `ThrowMessage` and
`AimMessage` goes with it, along with the debug-HUD readouts that print both metrics side by side
(`Assets/Scripts/Net/RelayDebugHud.cs:164,193`) and the `peakAccel` diagnostic if nothing else
wants it.

Update the tests that assert the old normalisation: `controller/test-sensors.js` (the `power`
derivation checks around lines 489-497 and the `buildAimPayload` power cases) and any
`relay/test-relay.js` case that stamps `power`.

### Telemetry columns

`Assets/Scripts/Verification/RockTelemetry.cs:41-42` — header becomes
`timestamp,matchSeed,rockId,player,swingSpeed,spinRate,pitchDeg,rollDeg,yawDeg,skips,distance`:
`tuningSet` goes, `power` becomes raw `swingSpeed`, and the single `faceAngleDeg` becomes the
three axes now being judged. Existing `rock-throws.csv` rows are from the old model and are not
comparable — archive the file rather than appending across the change.

### Docs to supersede

- `docs/Reference-SkipTuning.md` — documents the model being deleted. Rewrite against the new one;
  its derivations are the old solver and will be wrong, not merely stale.
- `docs/Findings-RockSeparation.md` — §1, §2, §6, §7 are voided by the removals above. Add a
  header note pointing here rather than silently leaving decisions that no longer exist.
- `docs/plans/rock-choosing-system.md` Part 6 — the A/B comparison it specifies is being removed;
  needs a "Superseded" callout in the house style.

### Keep

`SkipPhysics.Predict`, the `Step` purity contract, `PhoneAttitude` (extended, not replaced),
`RockCatalogBuilder`'s FBX-to-catalog build, the audio/ripple/trail systems (they need the
`ReferenceSpeed` swap, not removal), and the relay/WebRTC layer, which never touched tuning.

## Execution order

**0. Copy this plan to `docs/plans/Plan-ThrowDrivenSkipModel.md`** so it is tracked in the repo.

**1. `Assets/Scripts/Game/PhoneAttitude.cs`** — add `RollDeg`. All rotation work belongs here;
the file explicitly forbids sign fixes at call sites.

**2. `Assets/Scripts/Game/SkipTuning.cs`** — reduce to the three per-rock multipliers plus
`DisplayName`. Move the global constants to a new `SkipConstants` (or keep on the baseline asset,
decided at implementation).

**3. `Assets/Scripts/Game/SkipPhysics.cs`** — the real work.
- `CreateLaunch`: take raw swing speed and attitude; pitch from `FaceAngleDeg`; delete the
  `angleFactor`/`powerFactor`/`DynamicSpeedRetention` block.
- `Step`: derive per-bounce loss from incidence and face-vs-velocity; apply the three multipliers;
  apply forgiveness with the early-outs.
- **Carry the face normal, not just angles.** Roll's two effects (lift scaled by `cos(roll)`, lift
  deflected sideways) both fall out of the rotated face normal, so `Step` should work from that
  vector rather than from decomposed pitch/roll scalars. Decomposing and recomposing is precisely
  what `PhoneAttitude`'s remarks blame for the old three-angle bug.
- Keep it **pure** — no events, no scene access. The aim preview runs it speculatively.

**4. Replace the `MaxSpeed` reference scale.** Bigger than it looks: `MaxSpeed` is the normaliser
for four presentation systems, all of which read it once at startup —
`Assets/Scripts/Game/WaterRipples.cs:79`, `Assets/Scripts/Audio/SfxDirector.cs:60`,
`Assets/Scripts/Game/RockTrail.cs:118`, and `TrailTuning`. Introduce an explicit global
`ReferenceSpeed` for them. Also `Assets/Scripts/Game/RockController.cs:130` recovers `power` by
`InverseLerp(MinSpeed, MaxSpeed, …)` for `RockLaunchedEvent` — that becomes raw speed over
`ReferenceSpeed`.

**5. Tear out the A/B apparatus** — the "Authored/Derived" table in the deprecation section
above, in one commit. Do this *before* rewriting `SkipCurve`, so the report is rewritten against
the shape it will actually have rather than being edited twice. `RockCatalog.SharedFieldNames`
and its comparison loop go here too; keep `IsEmpty` and the usable-rock checks.

**6. Aim path, raw end to end.** Add `swingSpeed` to `AimMessage`
(`Assets/Scripts/Net/Messages.cs`) and to the controller's aim payload
(`controller/js/throw.js`, `buildAimPayload`); consume it in
`Assets/Scripts/Game/AimPreview.cs:331`. Keeps preview and throw driven by the same quantity
rather than disagreeing by construction. Update `relay/test-relay.js` and
`controller/test-sensors.js` for the new field.

**7. Strip phone-side normalisation** — `ThrowTuning` in `controller/js/throw.js`, the `power`
field on both messages, and the debug-HUD dual-metric readouts. Fix
`controller/test-sensors.js` and `relay/test-relay.js` in the same commit so the suites stay
green rather than being left red across several steps.

**8. Set the six rocks' multipliers by hand.** With `RockTuningGenerator` deleted there is no
generator to run — three numbers per rock is small enough to author directly, which is the point
of the reduction. If a mesh-derived starting guess is still wanted later, flatness -> vertical,
size -> horizontal, elongation -> spin is the natural mapping, but do not rebuild the generator
before finding out whether hand-authoring is simply easier.

**9. `Assets/Scripts/Verification/SkipCurve.cs`** — rewrite what survives step 5: sweep raw swing
speed instead of `power` 0..1, sweep all three axes rather than face angle alone, and re-baseline
every acceptance check. Fix the contact-detector off-by-one documented in
`docs/Reference-SkipTuning.md` here — pitch now comes from the phone and will frequently sit near
zero, which is exactly the case that triggers it.

**10. `RockTelemetry` columns, then the docs** — the header change above, then the supersede
notes on the three affected documents.

## Verification

1. **Compile** — check the open Editor's Console. There is no out-of-band compile check; Unity
   holds an exclusive project lock (see CLAUDE.md).
2. **Relay and controller tests**, which cover the new `AimMessage` field:
   ```bash
   cd relay && npm test
   ```
3. **`RockSkipping/Rocks/Validate Catalog`** — must still pass on the reduced validator, and
   `RockSkipping/Rocks/Build Catalog from FBX` must still rebuild the catalog with one tuning
   reference per rock. **Confirm the two deleted menu items are gone** from the `RockSkipping`
   menu rather than throwing when clicked.
4. **`RockSkipping/Verify/Print Skip Curve`** — regenerate `skip-curve.txt`. The acceptance
   criterion for this whole change: **throw speed must move skip count more than any per-rock
   multiplier does.** Concretely, doubling swing speed should beat driving a multiplier to its
   limit. That comparison is the thing to read off the new report.
5. **Play it** — `pwsh ./deploy/local-https.ps1` from the repo root, then Play on `Bootstrap`.
   The felt test is the one that started this: does throwing better matter more than which rock
   is held.

6. **Grep for stragglers** before calling it done — `TuningSet`, `AuthoredTuning`,
   `DerivedTuning`, `MinRetention`, `MaxRetention`, `MaxSpeed`, `ToleranceDeg`, `powerMetric`
   should each return either nothing or only the supersede notes in `docs/`.

## Risks

- **Retuning from scratch.** Deriving loss from geometry means every existing feel value is void;
  the game will need re-tuning against the new model. This is the intended cost, not a surprise.
- **One user-facing feature is deleted**, not just refactored: the Authored/Derived toggle in
  match settings. Everything else in the deprecation list is internal.
- **The empirical corpus resets.** `rock-throws.csv` and `skip-curve.txt` are both measurements of
  the old model. The 73 logged throws from commit `34932cc` do not carry over — archive them,
  because a mixed file silently averages two different games.
- **`MaxSpeed` removal is the sharpest edge** — four systems silently normalise against it, and a
  missed one degrades presentation without erroring.
- **The empirical model is harder than the current one, and removing the 20-degree offset adds to
  that.** Asymmetric pitch failure and roll edge-catch punish sloppiness more than a symmetric
  tent does, and players must now genuinely find the attack angle rather than being handed it at
  the neutral pose. The three forgiveness bands are the dial, and they exist precisely so
  difficulty can be dropped without burying a constant in the physics again. Expect the first
  playtest to want a wide pitch band; that is the system working, not the model failing.
- **Roll starts mattering.** No player has been scored on bank before, and it ships **on** — a
  band of `0` would leave the stone visibly rolled and physically flat, which is the disagreement
  this is fixing. Expect the felt difficulty to rise: there is now a third axis to hold steady.
  The roll forgiveness band is the dial for that, and it is the first number to tune against real
  players.
