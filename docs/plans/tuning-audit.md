# Tuning audit — catalogue, redundancy, and hand-tuned → derived migration

## Context

The project has accumulated tuning values in five separate places: `SkipTuning`
(physics), `AimFeedbackTuning`, `TrailTuning`, loose `[SerializeField]` floats on
`AimPreview` / `RockController` / `WaterRipples` / `SfxDirector`, and the JS
`ThrowTuning` literal in `controller/js/throw.js`. Several encode the same concept
under different names, several are dead, and the numbers that matter most —
the ones mapping a real human swing onto a 0..1 power — are global constants
guessed by hand. `throw.js:69-71` already carries a comment saying
"calibration replaces them per player"; that calibration was never written.

The exploration also found that C# defaults have drifted badly from the shipped
`.asset` values (`MaxSpeed` 40→45, `ToleranceDeg` 30→20, `FixedStep` 0.02→0.005,
`PredictSeconds` 22→60), so reading the source gives the wrong numbers.

**Deliverable of this plan is a document, not a code change.** Per the standing
rule, findings go to `.md` and wait for a decision before any source is edited.

## Output

Single new file: `docs/Findings-TuningAudit.md` (the repo's existing findings
home, alongside `docs/Findings-WiFiDirect.md`).

### Section 1 — Catalogue

One table per owning asset/class: field, file:line, code default, **shipped asset
value**, and every read site traced. Sources already gathered:

- `Assets/Scripts/Game/SkipTuning.cs` (19 fields) → consumed by
  `SkipPhysics.CreateLaunch` / `.Step` / `.Predict`, `RockController.Launch`,
  `AimPreview`, `WaterRipples.Bind`, `SfxDirector.Awake`, `RockTrail.Attach`.
- `Assets/Scripts/Game/AimFeedbackTuning.cs` (14 fields) → `AimPreview` only.
- `Assets/Scripts/Game/TrailTuning.cs` (15 fields) → `RockTrail` only.
- Loose serialized fields on `AimPreview`, `RockController`, `RockLauncher`,
  `ScoreManager`, `TurnManager`, `AudioManager`, `WaterRipples`, `SfxDirector`.
- `controller/js/throw.js` `ThrowTuning` (11 fields) + `SimulatedThrow` (7) +
  `controller/js/sensors.js` constants (`GRAVITY_MS2`, `SAMPLE_HZ`,
  `BUFFER_SECONDS`, `GravityFilter.tauMs`, `gravityK`).

Flag the code-default vs asset-value drift inline in the table — that column is
the point.

### Section 2 — Redundant / dead

Findings to write up, each with the evidence trace:

1. **`OptimalAngle` vs `BestFaceAngleDeg`** (`SkipTuning.cs:59,78`) — two "best
   angle" fields divided by the same `ToleranceDeg`; one drives launch
   retention (`SkipPhysics.cs:124`), one per-bounce quality (`:202`). Shipped
   asset has `0` and `-5`, so no throw can be optimal on both.
   `RockTuningGenerator.WarnOnAngleSplit:347` already warns and declines to fix.
2. **Triplicated `0.15 / 0.96` speed fractions** — `WaterRipples.cs:33-34`,
   `SfxDirector.cs:29-30`, `TrailTuning.cs:263-264`. One concept, three fields;
   retuning one desyncs the others silently.
3. **`SkipTuning.DisplayName` vs `RockDefinition.DisplayName`** — same string,
   two assets; generator copies one into the other.
4. **Whole rock-selection layer is unwired at runtime** — `RockCatalog`,
   `RockDefinition`, `TuningSet` and all 12 `Assets/Rocks/Tuning/{Authored,
   Derived}/*.asset` are referenced only from `Scripts/Editor/`. `RockLauncher`
   uses the single `Resources/SkipTuning`. No rock id exists on the wire
   (`Messages.cs`). Note this as blocking, not as cleanup — see §4.
5. **`RockController.VisualSpin` / `MaxVisualSpinDegPerSec:48-54` dead** —
   nothing calls it; `Update:188` applies `_spinRate` unclamped.
6. **`ThrowTuning.minPeakAccel` / `maxPeakAccel` inert** while
   `powerMetric === 'swingSpeed'` (`throw.js:184`).
7. **`AimPreview.attitudeSmoothing:51` does double duty** — smooths ghost
   rotation (`:186`) and arc power (`:189`) from one field.
8. **Stale invariant**: `SkipTuning`'s header claims power is the only driver of
   distance; `DynamicSpeedRetention` (`SkipPhysics.cs:126`) blends
   `angleFactor*0.6 + powerFactor*0.4`, so both angle and power drive distance
   twice.
9. **Cost note**: `PredictSeconds 60 / FixedStep 0.005` = 12 000 solver steps,
   run every aim frame in `AimPreview.UpdateArc:276` and once more per throw in
   `RockController.Launch:100`.

### Section 3 — Migration candidates (the core answer)

Rank each candidate by *is the phone data actually sufficient to derive this*.
Recommended verdicts:

**Tier A — derive, high confidence.** These are direct properties of the
player's motion; hand-tuning them is guessing at a number the sensor measures.

| Setting | File | Derive from |
|---|---|---|
| `minSwingSpeed` / `maxSwingSpeed` | `throw.js:72-73` | per-player percentiles of `peakSwingSpeed` over their own throws; population percentiles offline |
| `swingAccel` / `stopAccel` | `throw.js:50,53` | fractions of the player's observed peak `|linear|`, not absolute m/s² |
| `minValidPeakAccel` | `throw.js:39` | population low percentile of real (non-fumble) throws |
| `maxSwingMs` | `throw.js:59` | high percentile of measured swing durations |
| `SkipTuning.MinSpeed` / `MaxSpeed` | `SkipTuning.cs:44,46` | should be a *game-feel* range, but the mapping into it is fixed once power is calibrated — recheck after Tier A lands |

**Tier B — derive offline only.** Population-level, no per-player meaning:
`GravityFilter.tauMs` / `gravityK` (`sensors.js:215`, `app.js:39`) — fit against
recorded hardware-vs-filter gravity error; `spinEnvelopeTauMs` (`throw.js:83`).

**Tier C — keep hand-tuned.** These are authored feel, not measurements:
`Restitution`, `OpeningHopAngleDeg`, `SkipExitAngleDeg`, `LaunchPitchDeg`,
everything in `AimFeedbackTuning` and `TrailTuning`, `ScoreManager` points.
Say so explicitly — the value of the doc is as much in what *not* to migrate.

**Tier D — blocked, needs data we do not send.** `LaunchPitchDeg` is currently
fixed at launch while the phone's actual tilt is reduced to the scalar
`FaceAngleDeg`; deriving elevation from tilt needs a decision, not a fit. Note
that `peakAccel`, `swingSpeed`, `sampleCount` already reach Unity and are only
displayed on `RelayDebugHud.cs:193-201`, and `simulated` is dropped entirely
(no field in `ThrowMessage`).

### Section 4 — Staged plan (user chose "both, staged")

Write this as a proposal, not as work to start now.

- **Stage 0 — capture.** No fitting is possible without a corpus. Log every
  `THROW` descriptor plus the raw ring buffer to a JSONL file. The relay
  (`relay/server.js:502` default branch) is the natural tap point since it
  already sees every message and stamps `playerId`. Deliverable: a real file in
  the repo the user can re-run, per the standing rules.
- **Stage 1 — offline fit.** A script over the corpus emitting the Tier A/B
  population defaults, written back into `ThrowTuning` and the `.asset` values.
  Reviewable in git.
- **Stage 2 — runtime per-player.** Implement the calibration `throw.js:69-71`
  already promises, on top of the Stage 1 defaults. Reuse the existing
  `MotionSource.calibrate()` (`sensors.js:443`) hook, already called from
  `ThrowController.arm()` (`throw.js:428`) — it currently zeroes attitude only,
  and is the right place to also seed power range.
- **Prerequisite for any of it**: reconcile the code-default drift (§1) first, or
  a fit will be validated against numbers nothing runs.

## Critical files

Read-only for this task; listed because the doc cites them:
`Assets/Scripts/Game/SkipTuning.cs`, `SkipPhysics.cs`, `AimPreview.cs`,
`RockController.cs`, `AimFeedbackTuning.cs`, `TrailTuning.cs`,
`Assets/Resources/SkipTuning.asset`, `controller/js/throw.js`,
`controller/js/sensors.js`, `relay/server.js`, `Assets/Scripts/Net/Messages.cs`.

Files written: `docs/Findings-TuningAudit.md`, plus a copy of this plan at
`docs/plans/tuning-audit.md` so it is tracked in the repo.

## Verification

- Spot-check the catalogue against source: every file:line cited resolves and
  the asset-value column matches `Assets/Resources/*.asset` on disk.
- Confirm each "dead" claim with a repo-wide grep for the symbol (e.g.
  `VisualSpin`, `RockCatalog`, `MaxVisualSpinDegPerSec`) and record the hit
  count in the doc so the claim is falsifiable later.
- No code runs and no behaviour changes at this stage, so there is nothing to
  test beyond the citations being correct.
