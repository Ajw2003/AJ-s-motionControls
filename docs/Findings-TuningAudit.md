# Findings — tuning audit

**Date:** 2026-08-26 · **Branch:** RockChoosing · **Status:** findings only, no code changed.

Every file:line below was resolved against the working tree at the time of writing.
Asset values were read out of the `.asset` YAML on disk, not inferred.

---

## 0. The headline

Three things, in order of how much they cost:

1. **The C# defaults have drifted from the shipped asset.** Eleven fields differ.
   Anyone reading `SkipTuning.cs` to understand the game's feel is reading numbers
   that nothing runs. This has to be reconciled before any fitting work, or a fit
   gets validated against fiction.
2. **The numbers that most deserve to be derived are the ones nobody can hand-tune
   correctly** — `minSwingSpeed` / `maxSwingSpeed` in `controller/js/throw.js`.
   The comment there already says calibration should replace them per player.
   It was never written. **These are fittable from the data already on the wire**
   — see §3.0 for what capture does and does not currently support.
3. **Genuine redundancy exists but it is small.** Two "best angle" fields, one
   triplicated speed fraction, one dead method, one dead subsystem. Most of the
   tuning surface is legitimately distinct.

---

## 1. Catalogue

### 1.1 `SkipTuning` — the physics asset

Class `Assets/Scripts/Game/SkipTuning.cs`. Live asset `Assets/Resources/SkipTuning.asset`,
loaded by name in `AimPreview.Awake` and `SfxDirector.Awake`.

| Field | Line | Code default | **Shipped asset** | Read by |
|---|---|---|---|---|
| `Gravity` | 39 | 9.81 | 9.81 | `SkipPhysics.Step`; guarded by `RockCatalog.SharedFieldNames` |
| `WaterY` | 40 | 0 | 0 | `SkipPhysics.Step` (surface crossing, clamp); `AimPreview.BuildPlaceholderScene` |
| `MinSpeed` | 44 | 5 | 5 | `SkipPhysics.CreateLaunch` speed lerp; `RockController.Launch` inverse-lerp |
| `MaxSpeed` | 46 | 40 | **45** ⚠ | speed lerp; `powerFactor`; `WaterRipples.Bind`; `SfxDirector.Awake`; `RockTrail.Attach` |
| `LaunchPitchDeg` | 48 | 11 | **5** ⚠ | `SkipPhysics.CreateLaunch` only |
| `BestFaceAngleDeg` | 59 | 0 | **-5** ⚠ | `SkipPhysics.Step` per-bounce quality; `RockTuningGenerator`; `SkipCurve` |
| `ToleranceDeg` | 68 | 30 | **20** ⚠ | divides *both* angle terms — `CreateLaunch` and `Step` |
| `MinRetention` | 72 | 0.75 | 0.75 | `SkipPhysics.CreateLaunch` |
| `MaxRetention` | 75 | 0.99 | **0.95** ⚠ | `SkipPhysics.CreateLaunch` |
| `OptimalAngle` | 78 | 15 | **0** ⚠ | `SkipPhysics.CreateLaunch` only |
| `MinSkipSpeed` | 81 | 4 | **7** ⚠ | `SkipPhysics.Step` sink test |
| `SkipExitAngleDeg` | 93 | 5 | 5 | `SkipPhysics.Step` settled exit angle |
| `OpeningHopAngleDeg` | 99 | 11 | **7** ⚠ | `SkipPhysics.Step` opening pop |
| `OpeningHopSkips` | 102 | 3 | 3 | `SkipPhysics.Step` settle ramp; `SkipCurve` |
| `Restitution` | 107 | 0.4 | **0.3** ⚠ | `SkipPhysics.Step` bounce lift |
| `FixedStep` | 110 | 0.02 | **0.005** ⚠ | `SkipPhysics.Predict`; `RockController.Update` accumulator |
| `PredictSeconds` | 116 | 22 | **60** ⚠ | only via `MaxPredictSteps` |
| `MaxPredictSteps` | 118 | derived | **12000** | `SkipPhysics.Predict`; `AimPreview.Awake` list capacity |
| `DisplayName` | 122 | "Pebble" | "Pebble" | `TurnManager` phone HUD label |

⚠ = code default disagrees with what actually ships. Eleven of nineteen.

Two constants in the same solver are deliberately not fields:
`SkipPhysics.MaxIncidenceDeg = 45f` and `MinExitRiseSpeed = 0.3f`.

### 1.2 `AimFeedbackTuning` — `Assets/Scripts/Game/AimFeedbackTuning.cs`

Asset `Assets/Resources/AimFeedbackTuning.asset`, loaded in `AimPreview.Awake`.
Consumed by `AimPreview` and nothing else.

| Field | Line | Default | Asset |
|---|---|---|---|
| `ColourByPredictedSkips` | 36 | true | true |
| `PoorSkips` | 41 | 2 | **8** ⚠ |
| `GoodSkips` | 45 | 9 | **18** ⚠ |
| `PoorColour` / `FairColour` / `GoodColour` | 48 / 54 / 57 | red / amber / green | as default |
| `TailAlpha` | 61 | 0.15 | 0.15 |
| `ColourSmoothing` | 69 | 7 | 7 |
| `ChargeGlow` | 74 | true | true |
| `IdleScale` / `ChargedScale` | 77 / 81 | 1 / 1.16 | as default |
| `IdleColour` / `ChargedColour` | 85 / 88 | grey / pale blue | as default |
| `ChargedEmission` | 95 | (0.45, 0.85, 1) HDR | as default |
| `PulseHz` / `PulseAmount` | 98 / 103 | 3.2 / 0.045 | as default |

### 1.3 `TrailTuning` — `Assets/Scripts/Game/TrailTuning.cs`

Asset `Assets/Resources/TrailTuning.asset`, loaded in `RockTrail.Attach`. Consumed
by `RockTrail` only.

| Field | Line | Default | Asset |
|---|---|---|---|
| `Style` | 54 | Ribbon | Ribbon |
| `Time` | 61 | 0.55 | 0.55 |
| `WidthMultiplier` | 64 | 0.18 | **1** ⚠ |
| `WidthCurve` | 68 | linear 1→0 | linear 1→0 |
| `MinVertexDistance` | 73 | 0.15 | 0.15 |
| `Colour` (Gradient) | 78 | white→pale | grey→pale |
| `ColourBySpeed` | 82 | true | true |
| `MinSpeedFraction` / `MaxSpeedFraction` | 86 / 87 | 0.15 / 0.96 | as default |
| `SlowTint` / `FastTint` | 90 / 93 | slate / white | as default |
| `WakeWidthScale` / `WakeAlphaScale` | 97 / 100 | 4 / 0.35 | as default |
| `DashLength` / `DashDutyCycle` | 105 / 108 | 0.6 / 0.5 | as default |
| `StopEmittingOnSink` | 117 | true | true |

`Assets/Resources/TrailTuningDef.asset` and `TrailTuningGreyDef.asset` also exist
and are referenced by no code.

### 1.4 Loose serialized fields

| Field | File:line | Default | Note |
|---|---|---|---|
| `previewPower` | `AimPreview.cs:47` | 0.45 | floor on the drawn arc power |
| `attitudeSmoothing` | `AimPreview.cs:51` | 18 | smooths **both** ghost rotation and arc power — see §2.7 |
| `staleAfterSeconds` | `AimPreview.cs:54` | 0.5 | aim-frame staleness cutoff |
| `targetFinalSpinRetention` | `RockController.cs:21` | 0.001 | `pow(x, 1/totalSkips)` per-bounce spin decay |
| `despawnDelay` | `RockController.cs:23` | 2.5 | |
| `sinkDuration` / `sinkDepth` | `RockController.cs:27,31` | 0.6 / 1.5 | |
| `cameraFollowSmoothing` | `RockLauncher.cs:18` | 0.25 | camera offsets and slerp rates nearby are hardcoded magic numbers, not fields |
| `pointsPerSkip` / `pointsPerMetre` | `ScoreManager.cs:18,19` | 10 / 1 | |
| `minImpactSpeedFraction` / `max…` | `WaterRipples.cs:33,34` | 0.15 / 0.96 | see §2.2 |
| `minImpactSpeedFraction` / `max…` | `SfxDirector.cs:29,30` | 0.15 / 0.96 | see §2.2 |

`WaterRipples` also holds `rippleLifeSeconds = 2.5`, `rippleSpeed = 6` and a
`MaxRipples = 16` const that must match `WaterRipple.hlsl`. `SfxDirector` holds
`milestones = {5,10,15,20}`, `scoreDelay = 0.35`, `turnDelay = 1.2`.

### 1.5 Phone side — `controller/js/throw.js`, `controller/js/sensors.js`

`ThrowTuning` is a frozen module-level literal. Nothing writes it at runtime.

| Field | Line | Value | Read by |
|---|---|---|---|
| `minPeakAccel` / `maxPeakAccel` | 35 / 36 | 6 / 35 | `computeThrowDescriptor:185` — **only when `powerMetric === 'peakAccel'`** |
| `minValidPeakAccel` | 39 | 3 | fumble rejection, always active |
| `swingAccel` | 50 | 14 | `ThrowController._onSample` — swing entry |
| `stopAccel` | 53 | 4 | stop detection; `peakSwingSpeed:250`; `_trackWindup` |
| `stopSamples` | 56 | 3 | consecutive calm samples to call a stop |
| `maxSwingMs` | 59 | 900 | fallback fire if the phone never settles |
| `powerMetric` | 67 | `'swingSpeed'` | branch at 184 |
| `minSwingSpeed` / `maxSwingSpeed` | 72 / 73 | 0.9 / 6.5 | release power (186) and live arc power (490) |
| `spinEnvelopeTauMs` | 83 | 250 | `decaySpinEnvelope:299`; sites at 327, 514, 528 |

Also `aimHz = 20` (`throw.js:375` → `aimIntervalMs:380`) and a hardcoded 250 ms
dt-reject window.

`SimulatedThrow` (desktop stand-in): `minHoldMs 150`, `maxHoldMs 1200`,
`minPower 0.2`, `maxPower 0.95`, `pitchJitterDeg 6`, `yawJitterDeg 10`,
`spinRate 900`.

Sensor constants: `GRAVITY_MS2 = 9.80665` (`sensors.js:177`), `SAMPLE_HZ = 60`
and `BUFFER_SECONDS = 1.5` (362-363, ring buffer = 90 samples),
`GravityFilter` `tauMs = 75` default (215), `MotionSource` `gravityK = 0.8`
(385, overridable via `?gravityK=` in `app.js:39`).

---

## 2. Redundant, dead, or stale

### 2.1 `OptimalAngle` duplicates `BestFaceAngleDeg` — the real redundancy

Two independent "best angle" fields, both divided by the same `ToleranceDeg`:

- `OptimalAngle` (`SkipTuning.cs:78`) → `SkipPhysics.CreateLaunch` sets the
  per-throw `dynamicSpeedRetention`.
- `BestFaceAngleDeg` (`:59`) → `SkipPhysics.Step` sets per-bounce quality.

The shipped asset has `OptimalAngle: 0` and `BestFaceAngleDeg: -5`, so **no throw
angle can score best on both**. `RockTuningGenerator.WarnOnAngleSplit` already
detects this, warns, and explicitly declines to fix it. This is the one case where
"redundant" is the right word — the two want to be one field, and the split is
currently producing a contradiction rather than a nuance.

### 2.2 The 0.15 / 0.96 speed fraction, three times

`WaterRipples.cs:33-34`, `SfxDirector.cs:29-30`, `TrailTuning.cs:86-87`. Three
independent fields for one concept — "fraction of `MaxSpeed` where the effect
starts / saturates" — all three carrying the identical pair. Each multiplies
`SkipTuning.MaxSpeed` in exactly the same way. Retuning one silently desyncs the
audio, the ripples, and the trail from each other.

### 2.3 The whole rock-selection layer is unwired at runtime

`RockCatalog`, `RockDefinition`, `TuningSet`, and all twelve
`Assets/Rocks/Tuning/{Authored,Derived}/rock_0N.asset` files are referenced
**only** from `Scripts/Editor/RockCatalogBuilder.cs` and
`Scripts/Editor/RockTuningGenerator.cs` (verified by repo-wide grep: outside those
two editor files and the class definitions themselves, the only other hit is a
doc-comment in `RockTrail.cs:59`).

`RockLauncher.OnThrow` uses `_preview.Tuning`; `AimPreview.Awake` loads the single
`Resources/SkipTuning`. There is no rock id anywhere on the wire — `Messages.cs`
has no such field on `ThrowMessage` or `AimMessage`.

Dead at runtime as a consequence: `RockDefinition.Blurb`, `Tint`, `Mesh`,
`LocalScale`, `MeshRotation`, `AuthoredTuning`, `DerivedTuning`, `Tuning(set)`,
`IsUsable`; `RockCatalog.At` / `ById` / `IndexOf` / `MaxPredictSteps` /
`LoadOrWarn`; the entire `TuningSet` enum.

This is scaffolding ahead of the feature, not rot — but it is blocking, because
per-rock derived tuning cannot be validated end to end until a rock id crosses
the wire.

### 2.4 `RockController.VisualSpin` is dead

`MaxVisualSpinDegPerSec = 1200f` (`RockController.cs:48`) and `VisualSpin`
(`:53`) are `internal` and called by nothing — grep finds the definition and one
doc-comment reference in `RockTrail.cs:59`, no call site. `Update` applies
`_spinRate` unclamped, so the wagon-wheel guard the comment describes is not in
effect.

### 2.5 `minPeakAccel` / `maxPeakAccel` are inert

`throw.js:184` branches on `powerMetric`, which is `'swingSpeed'`. Both values
still ride the wire as diagnostics (`peakAccel` is a `ThrowMessage` field), which
is the right call — but they are not tuning anything today.

### 2.6 A stale invariant in `SkipTuning`'s own header

The class remarks say power is the only driver of distance. `CreateLaunch`
computes `dynamicSpeedRetention = MinRetention + range * (angleFactor*0.6 +
powerFactor*0.4)`, so angle drives distance twice (retention *and* bounce
quality) and power drives it twice (launch speed *and* retention). The comment
and the code disagree; the code is what runs.

### 2.7 `attitudeSmoothing` does double duty

`AimPreview.cs:51` smooths ghost rotation and arc power from one field, even
though `AimFeedbackTuning.ColourSmoothing` (`:69`) argues at length that these
rates should be separable.

### 2.8 Cost note, not redundancy

`PredictSeconds 60 / FixedStep 0.005` = **12 000 solver steps per `Predict`**.
`AimPreview.UpdateArc` runs that every frame while aiming and hands all ~12 001
points to the `LineRenderer`; `RockController.Launch` runs it again per throw
purely to count skips for the spin-decay exponent. `SkipTuning`'s own comment
warns the horizon moves whenever `FixedStep` does — which it has, by 4×.

---

## 3. Migration candidates: hand-tuned → derived

The test applied to each: **does the phone actually measure this, or is it a
choice about how the game should feel?** Only the first kind can be derived. A
value that is a taste judgement does not become better by being fitted to data.

### 3.0 How `power` works today, and what that means for capture

`power` is a unitless 0..1 value, and that is a deliberate boundary rather than a
unit choice. The phone measures something physical — hand speed in m/s. Unity
needs a launch speed in world units. `power` is the contract between them, so
neither side has to know the other's numbers.

Phone side, `throw.js:186`:

```js
power = normalize01(swingSpeed, tuning.minSwingSpeed, tuning.maxSwingSpeed)  // 0.9, 6.5
```

`normalize01` (`sensors.js:172`) is `clamp((v - min) / (max - min), 0, 1)`.

Unity side, `SkipPhysics.cs:113`:

```csharp
var speed = Mathf.Lerp(tuning.MinSpeed, tuning.MaxSpeed, power);  // 5, 45
```

**The 0..1 scale is not the problem and should be kept.** The problem is that
`minSwingSpeed` / `maxSwingSpeed` are hardcoded and `normalize01` clamps. A player
whose hardest swing peaks below 6.5 m/s can never reach `power = 1.0` — the top of
the range is physically unreachable for them. A player who exceeds 6.5 m/s
saturates early and loses all fine control above that point. The same applies at
the 0.9 floor. That pair is a guess at a human body, which is exactly the kind of
number that should be measured.

**What can be fitted from data already on the wire.** `ThrowMessage`
(`Messages.cs:150-152`) already carries `peakAccel`, `swingSpeed` and
`sampleCount` alongside `power`, and `RelayDebugHud` is their only consumer.

- `minSwingSpeed` / `maxSwingSpeed` — **fittable today, no wire change.** Every
  throw reports its raw `swingSpeed` in m/s. Log a few hundred and take
  percentiles.
- `minValidPeakAccel` — **fittable today.** `peakAccel` is on the wire.
- `maxSwingMs` — **needs a small addition.** Swing duration is not reported;
  it is the gap between the peak sample and the stop sample inside
  `ThrowController._onSample`. One extra descriptor field.
- `swingAccel` / `stopAccel` — **not fittable from descriptors.** These are
  per-sample thresholds tested against `|linear|` inside `_onSample`. The
  descriptor carries only the maximum over the buffer (`peakAccel`), not the time
  series. Re-fitting "what threshold should have fired here" needs the samples the
  threshold was tested against, and those never leave the phone —
  `RingBuffer.toArray()` (`sensors.js:273`) is consumed locally in `_fire` and
  discarded.

So capture is not blocked. It is staged: the highest-value setting in the project
can be fitted from what already ships, and only the two detection thresholds need
the raw buffer on the wire.

### Tier A — derive. The sensor already measures these; the constant is a guess at it.

| Setting | File:line | Current | Derive from | Capturable today? |
|---|---|---|---|---|
| `minSwingSpeed` / `maxSwingSpeed` | `throw.js:72-73` | 0.9 / 6.5 m/s | percentiles of that player's own `peakSwingSpeed`; population percentiles as the default | **yes** — `swingSpeed` is on the wire |
| `minValidPeakAccel` | `throw.js:39` | 3 m/s² | population low percentile over real (non-fumble) gestures | **yes** — `peakAccel` is on the wire |
| `maxSwingMs` | `throw.js:59` | 900 ms | high percentile of measured swing durations | one extra descriptor field |
| `swingAccel` | `throw.js:50` | 14 m/s² | a fraction of the player's observed peak `\|linear\|`, not an absolute | no — needs the raw sample buffer |
| `stopAccel` | `throw.js:53` | 4 m/s² | same — relative to that player's swing, not absolute | no — needs the raw sample buffer |

`minSwingSpeed` / `maxSwingSpeed` are the highest-value pair in the project.
They set the entire mapping from a human arm to `power ∈ [0,1]`, and throwing
strength varies far more between people than one pair of constants can cover —
`throw.js:69-71` says exactly this and then hardcodes them anyway. A player whose
hard swing peaks at 4 m/s can never reach full power; one who peaks at 9 m/s
saturates halfway through their range and loses all fine control above it.

`swingAccel` / `stopAccel` are the same problem in the detection stage rather
than the power stage: an absolute m/s² threshold means a gentle thrower can fail
to trigger a swing at all, and a violent one can fail to register a stop.

### Tier B — derive offline only. Population-level; no per-player meaning.

| Setting | File:line | Derive from |
|---|---|---|
| `GravityFilter.tauMs` / `gravityK` | `sensors.js:215`, `app.js:39` | recorded hardware-`acceleration` vs filter-estimate error, minimised across devices |
| `spinEnvelopeTauMs` | `throw.js:83` | measured wrist-flick duration distribution |

These are properties of the sensors and of human wrists in general, not of the
individual holding the phone. Fit them once, bake them in.

### Tier C — keep hand-tuned. These are authored feel, and should stay that way.

`Restitution`, `OpeningHopAngleDeg`, `OpeningHopSkips`, `SkipExitAngleDeg`,
`MinSkipSpeed`, `MinRetention` / `MaxRetention`, `MinSpeed` / `MaxSpeed`, all of
`AimFeedbackTuning`, all of `TrailTuning`, `ScoreManager` points, `RockController`
sink and despawn timings, `RockLauncher` camera smoothing.

There is no phone measurement that tells you how bouncy a stone *should* be. A
fit against real-world stone-skipping footage would produce a physically accurate
game, which is not the same as a good one. Listing these explicitly matters as
much as the Tier A list — the failure mode of a derivation project is deriving
things that were never measurements.

`MinSpeed` / `MaxSpeed` sit in Tier C but deserve a recheck after Tier A lands:
once power is calibrated per player, the speed range stops absorbing calibration
error and can be retuned purely for feel.

### Tier D — blocked. Needs a decision or a wire change first, not a fit.

- **`LaunchPitchDeg`** (`SkipTuning.cs:48`, shipped 5°) is a fixed launch
  elevation while the phone's actual tilt is reduced to the scalar
  `FaceAngleDeg` and spent on energy retention. Making elevation follow the
  phone's real tilt is a design decision about whether players should aim in the
  vertical, not something a dataset answers.
- **`peakAccel`, `swingSpeed`, `sampleCount`** already cross the wire
  (`Messages.cs:150-152`) and are consumed only by `RelayDebugHud`. The data for
  Tier A is *already arriving in Unity* and being displayed and discarded.
- **`simulated`** is set by the phone and has no field on `ThrowMessage`, so
  `JsonUtility` drops it silently. Any corpus will mix real and desktop-simulated
  throws with no way to tell them apart until this field is added. **Fix this
  before capturing anything.**
- **Per-rock derived tuning** cannot be validated until a rock id crosses the
  wire — see §2.3.

---

## 4. Proposed staging

Not started; proposal only.

**Stage 0 — capture.** Nothing can be fitted without a corpus. Log every `THROW`
descriptor, plus the raw ring buffer behind it, to JSONL. The relay is the natural
tap: `relay/server.js` already sees every message on the `default:` forwarding
branch and already stamps a trusted `playerId`. Prerequisites: add the
`simulated` field to `ThrowMessage`, and decide whether the raw per-sample
`linear` vector needs to cross the wire (today only its scalar reductions do —
`peakSwingSpeed` is computed on the phone and only its result is sent, so a
corpus of descriptors alone cannot re-fit `swingAccel` / `stopAccel`).

**Stage 1 — offline fit.** A script over the corpus emitting Tier A and Tier B
population defaults, written back into `ThrowTuning` and the `.asset` values.
Reviewable as a diff in git, which is the main argument for doing this before
Stage 2.

**Stage 2 — runtime per-player.** Implement the calibration `throw.js:69-71`
already promises, layered on the Stage 1 defaults. `MotionSource.calibrate()`
(`sensors.js:443`) is already called from `ThrowController.arm()`
(`throw.js:428`) and currently only zeroes attitude — that is the right place to
also seed the power range. Design question to settle first: does the player
perform an explicit calibration swing, or does the range adapt silently over
their first few throws? The second is friendlier and the first is predictable;
a hybrid (start from population defaults, widen on observation, never narrow
mid-match) is probably right.

**Prerequisite for all of it:** reconcile the eleven drifted defaults in §1.1
and §1.2. Otherwise Stage 1 validates a fit against numbers nothing runs.

---

## 5. Suggested order, independent of the derivation work

Cheap and self-contained, in rising order of risk:

1. Reconcile code defaults with shipped asset values (§1). Mechanical.
2. Add `simulated` to `ThrowMessage` (§3 Tier D). One field; unblocks capture.
3. Delete or wire up `RockController.VisualSpin` (§2.4). It is either a bug —
   the clamp was meant to be applied — or dead weight. Worth deciding which.
4. Collapse the triplicated speed fractions onto one shared source (§2.2).
5. Resolve `OptimalAngle` vs `BestFaceAngleDeg` (§2.1). Highest gameplay risk of
   the five, because it changes how throws score.
