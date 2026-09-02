# Guide — tuning what counts as a failed throw

**2026-08-29.** Written because badly rolled throws survive: a stone banked 30 degrees, which
should catch an edge and sink, currently gets **15 skips**. This names the dials, says which way
to turn each one, and gives measured estimates. It does not set them — the numbers below are
starting points to throw against, not decisions.

Everything here lives on **one asset: `Assets/Resources/SkipConstants.asset`** (select it in the
Project window; every field has a tooltip). Nothing needs a recompile. The six `RollFalloffDeg` /
`MaxRollDeg` / `Pitch*FalloffDeg` / `YawFalloffDeg` / `MaxIncidenceDeg` fields used to be private
constants in `SkipPhysics.cs` — they are now on this asset precisely so this is a feel exercise
rather than a code edit.

## The problem, measured

Swing 30 m/s, pitch 20 degrees (optimal), spin 400 deg/s, neutral rock:

| bank (roll) | 0 | 10 | 15 | 20 | 25 | **30** | 35 | 40 | 45 | 50 |
|---|---|---|---|---|---|---|---|---|---|---|
| skips now | 25 | 25 | 24 | 21 | 17 | **15** | 12 | 10 | 8 | 6 |

A 45-degree bank still gets 8 skips. Nothing here reads as a failure.

## How a bank is judged

Three numbers decide it, in this order:

```
effectiveBand = ForgivenessRollDeg + |spin| * SpinToleranceScale     # free pass, no penalty
rollQuality   = 1 - (|roll| - effectiveBand) / RollFalloffDeg        # fades to 0 past the band
sink outright if |roll| > MaxRollDeg
```

With today's values and a typical 400 deg/s spin that is: **19 degrees completely free**, quality
not reaching zero until **54 degrees**, and no hard sink until **80**. All three are too generous,
which is why the failure never arrives.

## The four dials, in the order I would pull them

### 1. `MaxRollDeg` — 80 → try **40**

The biggest single lever and the bluntest. This is the hard "edge digs in, stone stops" threshold.
At 80 a stone banked 79 degrees still bounces. Real stones catch and stop far earlier.

Set this first, alone, and throw a few. It puts a hard floor under the failure without touching
how good throws feel at all — anything under the threshold is completely unaffected.

### 2. `RollFalloffDeg` — 35 → try **20**

How fast quality dies once past the free band. Smaller is harsher. This is what turns 25–35 degrees
from "somewhat worse" into "clearly going wrong", and it is the dial that controls the *feel* of
the failure rather than its cliff edge.

### 3. `ForgivenessRollDeg` — 15 → try **10**

The free pass. Everything inside this band costs nothing at all, so it sets where degradation
*starts*. Lowering it makes the player actually hold the phone level. Be careful going below ~8:
this is also the band that absorbs ordinary hand tremor, and a too-tight value reads as the game
being twitchy rather than demanding.

### 4. `SpinToleranceScale` — 0.01 → consider **0.005**, or reconsider it entirely

This widens *every* forgiveness band by `|spin| * scale`. At a 900 deg/s flick it hands out
**9 extra degrees** of free bank.

Worth a thought before you turn it down: spin physically stabilises the stone against *drifting*
mid-flight — that is `WanderPerBounce`, and it is real. It does not make a stone you deliberately
banked 30 degrees behave like a level one. Letting spin widen the *roll* band is arguably modelling
the wrong thing. Options, your call:

- leave it (spin forgives everything — simplest, currently shipping),
- halve it to 0.005 (spin still helps, less),
- or have it widen pitch and yaw but **not** roll, which is the physically honest version and a
  small change to `EffectiveBand`'s call site in `SkipPhysics.Step`. Say the word and I'll wire it.

## Measured combinations

Same throw as the table above. Format is `ForgivenessRollDeg / RollFalloffDeg / MaxRollDeg /
SpinToleranceScale`:

| setting | 0 | 10 | 15 | 20 | 25 | 30 | 35 | 40 | 45 |
|---|---|---|---|---|---|---|---|---|---|
| **current** 15 / 35 / 80 / 0.010 | 25 | 25 | 24 | 21 | 17 | 15 | 12 | 10 | 8 |
| **B** 10 / 20 / 40 / 0.005 | 25 | 23 | 18 | 13 | 10 | 6 | 0 | 0 | 0 |
| **C** 12 / 18 / 38 / 0.005 | 25 | 24 | 20 | 14 | 10 | 6 | 0 | 0 | 0 |
| **A** 8 / 15 / 35 / 0.005 | 25 | 21 | 14 | 9 | 0 | 0 | 0 | 0 | 0 |
| **D** 8 / 12 / 32 / 0 | 25 | 17 | 11 | 0 | 0 | 0 | 0 | 0 | 0 |

**B is where I would start.** A level throw is untouched at 25, 20 degrees is a noticeably worse
throw, 30 is nearly dead, and 35+ sinks outright. **A** and **D** are there if B still feels soft —
D in particular is punishing, and it is the one to try if you want bank to be genuinely scary.

Note none of these change the roll-0 result. That is deliberate: these dials only shape the
failure, so the max range and power you said felt good stays exactly as it is.

## Spin interacts with all of this

Under setting B, the same bank at different spins:

| spin | 0 | 10 | 15 | 20 | 25 | 30 |
|---|---|---|---|---|---|---|
| 0 deg/s | 9 | 7 | 5 | 4 | 2 | 0 |
| 400 deg/s | 25 | 23 | 18 | 13 | 10 | 6 |
| 900 deg/s | 25 | 25 | 23 | 17 | 12 | 8 |

A hard flick still buys real tolerance. If that gap looks too wide, `SpinToleranceScale` (dial 4)
is the one that narrows it.

## While you are in there — pitch has the same softness

You did not mention it, so I have not touched it, but the same structure applies and the numbers
look similarly generous:

| pitch | −20 | −10 | 0 | 10 | 20 | 30 | 40 | 50 |
|---|---|---|---|---|---|---|---|---|
| skips | 14 | 17 | 21 | 25 | 25 | 24 | 17 | 0 |

A stone thrown 20 degrees nose-**down** gets 14 skips. The dials are `ForgivenessPitchDeg` (12),
`PitchShallowFalloffDeg` (55, governs nose-down) and `PitchSteepFalloffDeg` (22, nose-up). Halving
`PitchShallowFalloffDeg` to ~28 is the equivalent of dial 2 for the nose-down case.

## How to check your work

After editing the asset, run **`RockSkipping/Verify/Print Skip Curve`** and read `skip-curve.txt`.
Its roll sweep is the table above, regenerated against whatever you set. Two of its checks are
guard rails for exactly this exercise:

- `a badly rolled throw catches and sinks` — wants ≤ 2 skips at 90 degrees of bank;
- `a good throw lands in the intended skip band` — wants 15–30 skips at 30 m/s, so you find out
  immediately if a failure dial has quietly hurt good throws too.

If the good-throw check starts failing, you have gone too far on dial 3 (`ForgivenessRollDeg`)
rather than too far on dials 1 or 2.
