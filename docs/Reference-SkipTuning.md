# Reference — what every SkipTuning / SkipConstants value actually does

**2026-08-29.** Rewritten for the throw-driven skip model
(`docs/plans/Plan-ThrowDrivenSkipModel.md`). The previous version of this doc documented the
authored-retention model that plan replaced — `MinRetention`/`MaxRetention`, `BestFaceAngleDeg`,
`ToleranceDeg`, `OptimalAngle`, `SkipExitAngleDeg`, `OpeningHopAngleDeg`/`OpeningHopSkips`,
`Restitution`, `MinSkipSpeed`, `MinSpeed`/`MaxSpeed` are all gone — see the plan's Deprecation
section for the full list and why each one left. This is a reference for hand-tuning by feel: it
answers "if I move this, what happens to the stone", not "what should the value be". Written by
reading `Assets/Scripts/Game/SkipPhysics.cs` line by line; treat that file as the source of truth
if the two ever disagree.

## Two files, two kinds of number

**`SkipTuning`** (one per rock, `Assets/Rocks/Tuning/*.asset`) — exactly three bounded
multipliers (`0.5..1.5`) plus a display name. Nothing else lives here; there is nothing left to
author per rock beyond how strongly it expresses the shared geometry below.

- `SpinMultiplier` — scales the wrist spin the phone measured before it is stored on the throw.
  Purely visual, plus it widens the forgiveness bands (spin buys tolerance — see below).
- `VerticalMultiplier` — scales the lift a bounce produces (hop height).
- `HorizontalMultiplier` — scales the fraction of horizontal speed a bounce keeps.

**`SkipConstants`** (one asset, `Resources/SkipConstants.asset`) — everything that is not a
property of one rock: gravity, water height, the one raw-to-sim speed conversion, how lossy water
is, the three forgiveness bands, and the two presentation constants.

## The two moments that matter

A throw only has two places where tuning is read.

**At launch, once** (`SkipPhysics.CreateLaunch`).

```
speed    = swingSpeed * SpeedScale                      # SpeedScale: SkipConstants
heading  = PhoneAttitude.HeadingDirection(attitude)      # where the stone points
pitchDeg = PhoneAttitude.FaceAngleDeg(attitude)           # the phone's own tilt — no offset
velocity = heading * speed*cos(pitchDeg) + up * speed*sin(pitchDeg)

state.FaceAngleDeg = pitchDeg
state.RollDeg      = PhoneAttitude.RollDeg(attitude)
state.FaceNormal   = PhoneAttitude.FaceNormal(attitude)   # the rotated face normal — see below
state.HeadingDir   = heading
state.Spin         = spinRate * clamp(SpinMultiplier, 0.5, 1.5)
```

There is no authored offset anywhere in this — `swingSpeed` is the raw m/s the phone measured,
and `pitchDeg` is the phone's own tilt. A phone held exactly as it was at the tap throws flat
(0 degrees) and will not skip well; the player has to find the ~20 degree optimum for real.

**At every bounce** (`SkipPhysics.Step`). Runs once per water contact, and everything below is
computed fresh from the state each time — none of it is locked in at launch the way `speedKept`
used to be.

```
incidenceDeg = angle(velocity, horizontal)                # steepness of impact

pitchError  = FaceAngleDeg - 20                            # OptimalAttackDeg, a physics constant
pitchBand   = EffectiveBand(ForgivenessPitchDeg, spin)
pitchFalloff = steep 22 deg (pitchError >= 0) : shallow 55 deg (pitchError < 0)  # asymmetric
pitchQuality = BandQuality(pitchError, pitchBand, pitchFalloff)

rollBand    = EffectiveBand(ForgivenessRollDeg, spin)
rollQuality = BandQuality(RollDeg, rollBand, 35 deg falloff)

yawErrorDeg = angle(horizontalVelocity, HeadingDir)         # sideslip — 0 at launch, grows as roll curves the path
yawBand     = EffectiveBand(ForgivenessYawDeg, spin)
yawQuality  = BandQuality(yawErrorDeg, yawBand, 40 deg falloff)

quality = pitchQuality * rollQuality * yawQuality

incidenceFactor = clamp01(incidenceDeg / 45)                # MaxIncidenceDeg, a physics constant
lossFraction    = WaterLossCoefficient * lerp(incidenceFactor, 1, 1 - quality)
speedKept       = clamp01(1 - lossFraction) * clamp(HorizontalMultiplier, 0.5, 1.5)

vx, vz *= speedKept

rollLiftScale = cos(RollDeg)                                # lift falls off with bank
liftY = horizontalSpeedAfterLoss * tan(5 deg) * quality * rollLiftScale * clamp(VerticalMultiplier, 0.5, 1.5)
rise  = max(-vy * 0.25, liftY)                               # MinimalRestitution, a physics constant

lateralKick = liftY * sin(RollDeg)                           # roll's lift, deflected sideways — curves the throw
velocity += lateralKick * (up cross HeadingDir)

sink if impactSpeed < 2.5 (MinSkipSpeed) or incidenceDeg > 45 (MaxIncidenceDeg) or |RollDeg| > 80 (MaxRollDeg)
sink if rise < 0.3 (MinExitRiseSpeed)
```

`EffectiveBand(band, spin) = band <= 0 ? 0 : band + |spin| * SpinToleranceScale` — a band
authored at 0 stays off regardless of spin. `BandQuality(error, band, falloff) = clamp01(1 -
max(0, |error| - band) / falloff)` — a gradient, not a cliff: error inside the band costs
nothing, and quality fades linearly to 0 over `falloff` degrees past it.

## Roll: one rotated vector, not two decomposed scalars

`FaceNormal` is captured once at launch (`PhoneAttitude.FaceNormal`, the stone's up vector,
world space) and both of roll's effects fall out of the same number: `cos(RollDeg)` scales lift
down as the stone banks, and `sin(RollDeg)` deflects what lift remains sideways, curving the
path. `Step` never decomposes and recomposes pitch/roll scalars — that decompose/recompose
pattern is exactly what produced the old "three-angle bug" PhoneAttitude's own remarks describe.

## Why speed dominates and a multiplier cannot

`SpeedScale` and the multipliers enter differently. Speed sets the launch velocity directly and
then compounds through every bounce's geometry (`incidenceDeg`, the lift term, the sideslip
check) — a harder throw changes several terms at once, each bounce. A multiplier is a single
bounded scalar (`0.5..1.5`) applied once per bounce to one term. `RockSkipping/Verify/Print Skip
Curve` runs this comparison explicitly ("speed vs multiplier" in the report): doubling swing
speed against maxing every multiplier on a rock at once. Doubling speed should win — that is the
acceptance criterion the whole rewrite exists to satisfy.

## Reading the report

`RockSkipping/Verify/Print Skip Curve` sweeps swing speed, pitch and roll independently per rock
and re-runs the checks above through the real solver — see
`Assets/Scripts/Verification/SkipCurve.cs`. Read `skip-curve.txt` against: distance and skips
rising with speed; a throw at the physical optimum (20 deg pitch, 0 roll) skipping well; a badly
pitched or badly rolled throw dying fast; and a full-power throw staying on the drawn water.
