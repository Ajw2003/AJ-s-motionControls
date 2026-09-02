# Reference — what every SkipTuning / SkipConstants value actually does

**2026-08-29.** Rewritten for the energy-invariant bounce
(`docs/plans/Plan-SkipEnergyInvariant.md`), which replaced the bounce arithmetic of the
throw-driven model (`docs/plans/Plan-ThrowDrivenSkipModel.md`). The rest of that model — raw
swing speed, pitch straight off the phone, no authored offsets, three per-rock multipliers — is
unchanged.

What changed, and why an earlier version of this doc is wrong if you find one: the bounce used to
compute a loss and then *synthesise* lift out of horizontal speed (`liftY = horizontalSpeed *
tan(5 deg) * ...`). Nothing tied that lift to the vertical speed coming in, so a stone arriving
almost flat left with more vertical speed than it had — energy from nothing. Combined with a fixed
exit angle it settled into a 2%-loss-per-bounce fixed point and ran for 130 skips; with
`HorizontalMultiplier` above 1 it never sank at all. `ExitAngleDeg`, `MinimalRestitution` and
`WaterLossCoefficient` are gone with it.

This is a reference for hand-tuning by feel: it answers "if I move this, what happens to the
stone", not "what should the value be". Written by reading `Assets/Scripts/Game/SkipPhysics.cs`
line by line; treat that file as the source of truth if the two ever disagree.

## The one invariant

**A bounce always returns strictly less speed than it received.** This is structural, not tuned —
it holds for any quality, any roll, and any value in any tuning field, including nonsense ones:

- the energy loss is floored at `MinLoss > 0`, so the post-loss speed is always below the incoming
  speed; and
- forward and upward are a *split* of that one post-loss budget (`vy² + vh² = speedAfter²`), so
  the split cannot add anything back.

Every run therefore reaches `MinSkipSpeed` in finite bounces and ends. Verified across 4,032
configurations / 35,441 bounces: zero violations, zero runs that failed to sink.

If you change `Step`, check this first. It is the property the previous model lacked, and losing
it does not look like a bug — it looks like the game being fun for a minute and then never
stopping.

## Two files, two kinds of number

**`SkipTuning`** (one per rock, `Assets/Rocks/Tuning/*.asset`) — three bounded multipliers plus a
display name. **All six rocks currently ship at `1.0 / 1.0 / 1.0`** so the physics can be judged
bare; rock character gets authored once it reads well.

| field | range | what it does |
|---|---|---|
| `SpinMultiplier` | 0.8–1.2 | how well this rock turns wrist spin into gyroscopic stability |
| `VerticalMultiplier` | 0.7–1.3 | its share of surviving energy sent upward — arc, traded against carry |
| `HorizontalMultiplier` | 0.95–1.05 | scales its energy loss per bounce. **Below 1 is slipperier** |

The ranges are narrow on purpose, `HorizontalMultiplier` narrowest of all: skip count goes as
`1 / ln(speed kept)`, which is hyperbolic, so a wide range lets the rock outweigh how hard the
player actually threw — the exact failure the throw-driven model exists to prevent. At the old
`0.5..1.5` a rock beat doubling your swing speed.

**`SkipConstants`** (one asset, `Resources/SkipConstants.asset`) — everything that is not a
property of one rock. The one you will actually reach for:

> **`EnergyLoss` (default 0.20) is the single dial for how many skips a good throw gets.**
> `skips ≈ ln(MinSkipSpeed / launchSpeed) / (0.5 × ln(1 − EnergyLoss))`, an upper bound — a
> wobbly throw ends sooner. Measured at a 30 m/s swing, optimal angle: **0.17 ≈ 29 skips,
> 0.20 ≈ 25, 0.23 ≈ 21, 0.26 ≈ 18.**

The rest: `Gravity`, `WaterY`, `SpeedScale` (the one raw-to-sim conversion), `LiftShare` and
`MinLoss` (below), the three forgiveness bands, `SpinForStability` / `WanderPerBounce` /
`SpinRetention` (below), and `ReferenceSpeed` / `FixedStep` / `PredictSeconds`.

## At launch, once (`SkipPhysics.CreateLaunch`)

```
speed    = swingSpeed * SpeedScale                       # SpeedScale: SkipConstants
heading  = PhoneAttitude.HeadingDirection(attitude)      # where the stone points
pitchDeg = PhoneAttitude.FaceAngleDeg(attitude)          # the phone's own tilt — no offset
velocity = heading * speed*cos(pitchDeg) + up * speed*sin(pitchDeg)

state.RollDeg   = PhoneAttitude.RollDeg(attitude)
state.Spin      = spinRate * clamp(SpinMultiplier, 0.8, 1.2)
state.WanderDeg = 0
```

No authored offset anywhere: `swingSpeed` is the raw m/s the phone measured, `pitchDeg` is the
phone's own tilt. A phone held exactly as it was at the tap throws flat (0 degrees) and will not
skip well — the player has to find the ~20 degree optimum for real.

## At every bounce (`SkipPhysics.Step`)

```
incidenceDeg = angle(velocity, horizontal)               # steepness of impact

pitchError   = FaceAngleDeg - 20                         # OptimalAttackDeg, a physics constant
pitchFalloff = steep 22 deg (pitchError >= 0) : shallow 55 deg   # asymmetric: plowing in is abrupt
pitchQuality = BandQuality(|pitchError| + WanderDeg, EffectiveBand(ForgivenessPitchDeg, spin), pitchFalloff)
rollQuality  = BandQuality(|RollDeg|   + WanderDeg, EffectiveBand(ForgivenessRollDeg,  spin), 35 deg)
yawQuality   = BandQuality(sideslipDeg,             EffectiveBand(ForgivenessYawDeg,   spin), 40 deg)
quality      = pitchQuality * rollQuality * yawQuality

# --- one energy budget: take the loss, then split what survives -----------------
lossShape    = lerp(1, 2.5, 1 - quality)                 # a bad bounce costs up to 2.5x
             * lerp(0.8, 1.6, incidenceDeg / 45)         # a steeper arrival slaps harder
             * clamp(HorizontalMultiplier, 0.95, 1.05)
lossFraction = clamp(EnergyLoss * lossShape, MinLoss, 0.95)
speedAfter   = speed * sqrt(1 - lossFraction)            # ALWAYS < speed

liftShare = clamp(LiftShare * quality * cos(RollDeg) * clamp(VerticalMultiplier, 0.7, 1.3), 0, 0.5)
rise      = speedAfter * sqrt(liftShare)                 # upward
exitSpeed = speedAfter * sqrt(1 - liftShare)             # forward   (rise² + exitSpeed² = speedAfter²)

heading  = rotate(horizontalDirection, by sin(RollDeg) * 0.05 rad)   # bank steers — a ROTATION
velocity = heading * exitSpeed + up * rise

# --- stability -----------------------------------------------------------------
stability  = clamp01(|Spin| / SpinForStability)
WanderDeg += (1 - stability) * WanderPerBounce           # tumble the spin failed to prevent
Spin      *= SpinRetention

sink if impactSpeed < 2.5 (MinSkipSpeed) or incidenceDeg > 45 (MaxIncidenceDeg)
        or |RollDeg| + WanderDeg > 80 (MaxRollDeg) or rise < 0.3 (MinExitRiseSpeed)
```

`EffectiveBand(band, spin) = band <= 0 ? 0 : band + |spin| * SpinToleranceScale` — a band authored
at 0 stays off regardless of spin. `BandQuality(error, band, falloff) = clamp01(1 - max(0, |error|
- band) / falloff)` — a gradient, not a cliff.

## Spin is gyroscopic stability, and `WanderDeg` is what it prevents

A real stone skips because spin holds its attack angle through each slap; without spin it tumbles,
the angle randomises, and the run dies in a bounce or two. That is exactly what `WanderDeg` models:
every bounce adds drift in proportion to how *un*stabilised the stone is, and the total is charged
against the pitch and roll errors, so quality decays over a run at a rate spin controls. Measured
at a 30 m/s swing: **spin 0 → 11 skips, 200 → 17, 400 → 26, 800 → 29.**

`WanderDeg` is also what stops attitude being frozen at launch. Before it existed, `quality` was
computed once from the release pose and never changed, so nothing about a run ever degraded.

**`SpinRetention` has a trap.** Set it too low (below ~0.98) and spin bleeds out fast enough that
*tumble*, not energy, becomes what ends every run — skip count caps out, throwing twice as hard
stops buying skips, and `EnergyLoss` quietly stops being the dial this doc says it is. It shipped
at 0.97 and did exactly that; 0.99 is the current value. If changing `EnergyLoss` stops moving the
skip count, check this first.

## Roll: one rotated vector, and a rotation rather than an addition

`FaceNormal` is captured at launch (`PhoneAttitude.FaceNormal`) and both of roll's effects fall out
of it: `cos(RollDeg)` scales lift down as the stone banks, and the bank steers the path. `Step`
never decomposes and recomposes pitch/roll scalars — that pattern produced the old "three-angle
bug" `PhoneAttitude`'s own remarks describe.

Steering is a **rotation of the surviving horizontal velocity**, never a lateral vector added to
it. Same visible curve, but a rotation cannot change a vector's magnitude, so it cannot smuggle in
energy. The previous additive kick could, and did.

## Why speed dominates and a multiplier cannot

Speed sets the launch velocity directly and then compounds through every bounce's geometry, while
a multiplier is one bounded scalar applied once per bounce to one term.
`RockSkipping/Verify/Print Skip Curve` runs the comparison explicitly: doubling swing speed against
the best rock the ranges can express (spin 1.20 / vertical 1.30 / **horizontal 0.95** — the floor,
because that field scales *loss*). Currently: base 25 skips / 257 m, doubled 31 / 1042 m, best rock
25 / 288 m.

## Reading the report

`RockSkipping/Verify/Print Skip Curve` sweeps swing speed (3–55 m/s, the range logged throws
actually produce), pitch and roll per rock through the real solver, with a representative spin —
sweeping at spin 0 would measure a tumbling stone. Read `skip-curve.txt` against:

- **every throw sinks** — a run still flying after 120 s is a broken solver, not a long throw;
- **a reference throw lands in 15–30 skips** — an absolute band, because purely relative checks are
  how 130-skip runs passed review;
- distance and skips rising with speed;
- a badly pitched or badly rolled throw dying fast;
- a full-power throw staying on the drawn water (1060 m).
