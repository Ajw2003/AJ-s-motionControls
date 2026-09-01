# Plan — the bounce must never return more energy than it received

**Status:** in progress, 2026-08-29. Follows
[`Plan-ThrowDrivenSkipModel.md`](Plan-ThrowDrivenSkipModel.md), which built the throw-driven model
this fixes. That plan's model is kept; only its bounce arithmetic is replaced.

## Context

The throw-driven model inverted the game as designed, but its bounce **creates energy**. In
playtesting, rocks gained upward and forward speed every bounce and skipped essentially forever —
even with all three per-rock multipliers set to `1, 1, 1`.

Reproduced numerically by replicating `SkipPhysics.Step` exactly outside Unity, using the shipped
`Resources/SkipConstants.asset`, all multipliers `1.0`:

| swing | pitch | skips | time | distance |
|---|---|---|---|---|
| 20 m/s | 10° | 110 | 18.9 s | 252 m |
| 30 m/s | 10° | **132** | **29.8 s** | 573 m |

With the multipliers the six rocks actually shipped, three of them **never sink at all**:

| `HorizontalMultiplier` | outcome |
|---|---|
| 1.05 (`Slab`) | still flying at a 600 s cap, 3.1e5 m, speed past 600 m/s |
| 1.15 (`Boulder`) | still flying at 600 s, 1.2e6 m |
| 1.20 (`Shard`) | still flying at 600 s, 1.7e6 m, speed **1989 m/s** by skip 25 |

Corroborated on real hardware — five throws captured in `rock-throws.csv` during the session that
found this:

| swing | spin | pitch | skips | distance |
|---|---|---|---|---|
| 9.9 m/s | −339 °/s | 4.2° | 33 | 42 m |
| 39.6 m/s | −205 °/s | −9.9° | 36 | 190 m |

A **4× harder throw bought three more skips.** That is the logarithmic compression the whole
throw-driven rewrite exists to remove, still present because skip count is being set by a runaway
bounce rather than by the throw.

Intended outcome: a good throw is **15–30 skips**, tunable from **one** documented variable, and
runaway is made *structurally impossible* rather than tuned away.

## Root cause — four defects

**1. Lift is synthesized from horizontal speed, unbounded by incoming vertical momentum.**

```csharp
var liftY = exitHorizontal * Mathf.Tan(ExitAngleDeg * Mathf.Deg2Rad) * quality * rollLiftScale * VerticalMultiplier;
var rise  = Mathf.Max(-velocity.y * MinimalRestitution, liftY);
```

Nothing ties `rise` to `velocity.y`. A stone arriving with `vy ≈ 0` leaves with `vy = 0.0875·vh` —
vertical kinetic energy from nothing. Measured on a flat throw, bounce 3 takes in `vy = −0.62` and
returns `vy = 0.73`.

**2. The fixed exit angle is a self-sustaining low-loss fixed point.** Ballistic flight returns the
stone at the speed it left with, so incidence locks to exactly `ExitAngleDeg` (5°). Loss is scaled
by incidence (`incidenceFactor = incidence / 45 → 0.11`), giving `0.18 × 0.11 ≈ 2%` loss per bounce
— forever. This is why runs reach 130 skips even with neutral multipliers.

**3. `HorizontalMultiplier > 1` pushes `speedKept` above 1.0** —
`Clamp01(1 - loss) * Clamp(HorizontalMultiplier, 0.5f, 1.5f)`. At the fixed point that is
`0.962 × 1.2 = 1.154`: 15% compounding speed growth, unbounded.

**4. The lateral roll kick adds horizontal speed after loss is applied** — a second injection
whenever roll ≠ 0.

**A fifth, latent:** `FaceAngleDeg`/`RollDeg` are frozen at launch, so `quality` is constant for an
entire run. Nothing degrades, so nothing ends a run except the (tiny) speed decay.

### Why the harness missed it

`SkipCurve.Measure` loops only `constants.MaxPredictSteps` (22 s) and reports whatever count it
reached — it never checks whether the run *terminated*. The sole acceptance check
(`SpeedVsMultiplierCheck`) is purely **relative**: 2× speed vs maxed multipliers, no absolute skip
bound. A rock that never sinks passes it.

## The fix — one energy budget per bounce

Replace "compute loss, then synthesize lift" with "**take a loss, then split what survives**".
Gain becomes unrepresentable rather than merely unlikely.

```
speedIn                                       // incoming speed
lossFrac   = clamp(EnergyLoss × shape, MinLoss, 0.95)
speedAfter = speedIn × sqrt(1 - lossFrac)     // strictly < speedIn, always
liftFrac   = clamp(LiftShare × quality × cos(roll) × VerticalMultiplier, 0, 0.5)
vy = speedAfter × sqrt(liftFrac)              // vertical and horizontal are a split
vh = speedAfter × sqrt(1 - liftFrac)          // of one budget: vy² + vh² = speedAfter²
```

with `shape = lerp(1, 2.5, 1-quality) × lerp(0.8, 1.6, incidenceFactor) × HorizontalMultiplier`.

Because `lossFrac ≥ MinLoss > 0` and the split preserves magnitude exactly, outgoing speed is
**always strictly less than incoming speed** — regardless of quality, roll, or what anyone types
into a multiplier field. Roll's curve becomes a *rotation* of the horizontal direction rather than
an added vector, so it steers without injecting energy.

### Spin becomes real gyroscopic stability

This also fixes defect 5. Real stones skip because spin holds the attack angle through each
impact; without it they tumble and die in a bounce or two.

```
stability  = clamp01(spin / SpinForStability)   // SpinForStability = 400 °/s
wanderDeg += (1 - stability) × WanderPerBounce  // attitude drift, accumulates in SkipState
spin      *= SpinRetention                      // spin bleeds off, so runs always end
```

`wanderDeg` is added to the pitch and roll errors, so quality decays over a run at a rate spin
controls. Spin now does genuine work beyond widening forgiveness bands.

## Validation (run outside Unity, before implementing)

Strict per-bounce check across **360 configurations / 6,527 bounces**: **zero** violations of
`speedOut < speedIn`. Pathological inputs (`VerticalMultiplier = 999`, `HorizontalMultiplier =
−999`) terminate in 30 skips without growing.

Spin drives skip count at swing 30 / pitch 20:

| spin | 0 | 100 | 200 | 400 | 800 °/s |
|---|---|---|---|---|---|
| skips | 11 | 13 | 15 | 23 | 25 |

`EnergyLoss` calibration — **0.17 is the default**:

| `EnergyLoss` | swing 10 | 20 | 30 | 40 |
|---|---|---|---|---|
| 0.14 | 22 | 26 | 27 | 28 |
| **0.17** | **18** | **24** | **25** | **26** |
| 0.20 | 15 | 21 | 23 | 25 |
| 0.25 | 12 | 17 | 19 | 21 |

Acceptance criterion holds: base 21 skips, 2× speed 25, maxed rock 23 — **speed wins**.

## Multiplier ranges must narrow

Skip count goes as `1 / ln(speed factor)` — hyperbolic, the same trap the throw-driven plan was
written to kill. At the current `[0.5, 1.5]` clamp a rock beats doubling your throw speed. New
clamps: **Horizontal `[0.9, 1.1]`, Vertical `[0.7, 1.3]`, Spin `[0.8, 1.2]`**.

## Rocks ship neutral — no authored character yet

All six rocks get **identical placeholder tuning, every multiplier `1.0`**. The point of this pass
is to see the physics in its bare, untuned state: with the rocks neutral, every difference between
two throws is the throw. Authoring rock character comes after this is confirmed good, and the
`Reference-SkipTuning.md` rewrite documents the knobs so it can be done deliberately rather than
guessed at.

## Files to change

| File | Change |
|---|---|
| `Assets/Scripts/Game/SkipConstants.cs` | `WaterLossCoefficient` → **`EnergyLoss`** (default `0.17`); add `LiftShare`, `SpinForStability`, `WanderPerBounce`, `SpinRetention`, `MinLoss`. Full tooltips. |
| `Assets/Resources/SkipConstants.asset` | Rename the serialized key, set new defaults. |
| `Assets/Scripts/Game/SkipPhysics.cs` | The energy-budget bounce; roll as rotation; wander/stability. `SkipState` gains `WanderDeg` and a decaying `Spin`. Delete `ExitAngleDeg`, `MinimalRestitution`. **`Step` stays pure.** |
| `Assets/Scripts/Game/SkipTuning.cs` | Tooltips on all three multipliers; narrowed `[Range]`s. |
| `Assets/Rocks/Tuning/Rock_0*.asset` | All six to `1.0 / 1.0 / 1.0`. |
| `Assets/Scripts/Verification/SkipCurve.cs` | Termination check + absolute skip band; re-baseline. |
| `docs/Reference-SkipTuning.md` | Rewrite the bounce derivation against this model. |

### Tooltips

`EnergyLoss` carries the formula, since it is the one dial:

> Fraction of kinetic energy the water takes on every bounce — the one dial for how many skips a
> good throw gets. The bounce takes this loss first, then splits what survives between forward and
> upward motion, so outgoing speed is always less than incoming speed and a run can never run away.
> Approximate skip count for a clean throw:
> `skips ≈ ln(MinSkipSpeed / launchSpeed) / (0.5 × ln(1 − EnergyLoss))`
> — an upper bound; a wobbly (low-spin) throw ends sooner. At the default 0.17 a 30 m/s swing gives
> ~25 skips. 0.10 ≈ 30 skips, 0.25 ≈ 19.

The three per-rock multipliers get matching tooltips for parity: `SpinMultiplier` (how well this
rock turns wrist spin into gyroscopic stability), `VerticalMultiplier` (share of surviving energy
sent upward — higher hops, fewer and longer skips, same budget), `HorizontalMultiplier` (scales
this rock's energy loss per bounce; deliberately narrow because skip count is hyperbolic in loss).

## Execution order

1. `SkipConstants` — fields, defaults, tooltips, `.asset`.
2. `SkipPhysics` — the energy-budget bounce, roll-as-rotation, wander/stability.
3. `SkipTuning` tooltips + narrowed ranges; six rocks to neutral `1/1/1`.
4. `SkipCurve` — termination check, absolute skip band, re-baseline.
5. `docs/Reference-SkipTuning.md` rewrite.

## Verification

- **Compile** — the open Editor's Console. No batch-mode check exists; Unity holds the project lock.
- **`RockSkipping/Verify/Print Skip Curve`** — every throw must terminate, a reference throw must
  land in 15–30 skips, and speed must still beat maxed multipliers.
- **`RockSkipping/Rocks/Validate Catalog`** — still passes with the narrowed ranges.
- **Relay/controller suites** — `cd relay; npm test` and `node controller/test-sensors.js`. Nothing
  here touches the wire, so both must stay green.
- **Play it** — `pwsh ./deploy/local-https.ps1`, then Play on `Bootstrap`. The felt test: a great
  throw is a satisfying ~20 skips that visibly *ends*, and spinning the phone matters.
