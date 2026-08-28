# Reference — what every SkipTuning value actually does

**2026-08-28.** Written by reading `Assets/Scripts/Game/SkipPhysics.cs` line by line and then
checking the maths below against `skip-curve.txt`. Every formula here reproduces the report's
own numbers to within rounding — the validation table at the end shows all seven checks.

This is a reference for hand-tuning by feel. It answers "if I move this, what happens to the
stone", not "what should the value be".

## The two moments that matter

A throw only has two places where tuning is read. Almost every surprise comes from not knowing
which of the two a field belongs to.

**At launch, once** (`CreateLaunch`). This fixes the throw's speed and — importantly — locks in
one number, `speedKept`, that never changes again for the whole run.

```
v0        = lerp(MinSpeed, MaxSpeed, power)
velocity  = heading * v0*cos(LaunchPitchDeg) + up * v0*sin(LaunchPitchDeg)

angleFactor = 1 - clamp01(|faceAngle - OptimalAngle| / ToleranceDeg)
powerFactor = v0 / MaxSpeed
speedKept   = MinRetention + (MaxRetention - MinRetention) * (0.6*angleFactor + 0.4*powerFactor)
```

**At every bounce** (`Step`). Runs once per water contact.

```
quality   = 1 - clamp01(|faceAngle - BestFaceAngleDeg| / ToleranceDeg)

sink if  impactSpeed < MinSkipSpeed  or  incidence > 45 deg      # 45 is a hard constant

vx, vz  *= speedKept                                             # speed loss FIRST
settle   = clamp01(SkipCount / OpeningHopSkips)
exitDeg  = lerp(OpeningHopAngleDeg, SkipExitAngleDeg, settle)
liftY    = horizontalSpeed * tan(exitDeg) * quality
rise     = max(-vy * Restitution, liftY)

sink if  rise < 0.3                                              # MinExitRiseSpeed, hard constant
vy = rise
```

Two things worth pinning down straight away, because they explain most of the "why did that do
nothing" moments:

- **`speedKept` is computed once and never recomputed.** Retention is not a per-bounce reaction
  to how the stone is flying. It is a single constant chosen at the moment of release, and the
  entire run is a geometric decay at that rate.
- **Angle is read twice, from two different fields.** `OptimalAngle` feeds retention at launch;
  `BestFaceAngleDeg` feeds lift at each bounce. See "Two knobs for one idea" below — this is a
  trap.

## The closed forms

Because `speedKept` is constant, the whole run has an exact solution. These are the formulas to
tune against.

**Horizontal speed after n bounces**

```
u(n) = v0 * cos(LaunchPitchDeg) * speedKept^n
```

**Length of one hop** — a plain projectile arc, since lift sets the vertical and gravity takes it
back:

```
hopLength(n) = 2 * u(n+1)^2 * tan(exitDeg(n)) * quality / Gravity
hopHeight(n) =    (u(n+1)   * tan(exitDeg(n)) * quality)^2 / (2 * Gravity)
```

**Hop-to-hop shrink, once the opening pop has faded** (exitDeg constant):

```
hopLength(n+1) / hopLength(n) = speedKept^2
```

**Total distance** — a geometric series. Once settled:

```
remainingDistance ~= hopLength / (1 - speedKept^2)
```

**Number of skips** — the run ends when the stone's speed falls under whichever limit binds
first:

```
vEnd  = max(MinSkipSpeed, 0.3 / (tan(SkipExitAngleDeg) * quality))
skips = ln(vEnd / v0) / ln(speedKept)
```

That `vEnd` is the subtle one. `MinSkipSpeed` is the limit you set, but `MinExitRiseSpeed` (a
hard-coded 0.3 m/s) takes over whenever lift is weak — which means **on an off-angle throw the
run is cut short by a constant you cannot edit, not by `MinSkipSpeed`.** On the current baseline
that crossover happens at about `quality < 0.7`, i.e. more than ~6 degrees of face error.

## Field by field

`Gravity`, `WaterY`, `MinSpeed`, `MaxSpeed`, `FixedStep` must stay identical across all six
rocks — `RockCatalogBuilder` enforces it, because `WaterRipples` and `SfxDirector` normalise
against the global `MaxSpeed` read once at startup. Per-rock character has to come from the
others.

### Speed and power

| field | where | what it does | what you feel |
|---|---|---|---|
| `MinSpeed` | launch | speed at power 0 | How much a feeble throw still does. Also raises the floor of `powerFactor`, so a high `MinSpeed` makes weak throws retain speed better and flattens the power curve. |
| `MaxSpeed` | launch | speed at power 1 | Everything. Distance goes as **speed squared** per hop, and skip count only as `ln(v0)`. Raising 45 to 60 does not add a third to the distance — it roughly doubles it. |
| `LaunchPitchDeg` | launch | upward angle of the toss itself | How much of a lob the throw opens with, before the water is involved. At 0 the stone starts on the surface and the first bounce happens immediately. See "Pitch 0 breaks three columns" below. |

### Angle and forgiveness

| field | where | what it does | what you feel |
|---|---|---|---|
| `BestFaceAngleDeg` | bounce | centre of the lift window | The wrist angle that skips best. Tune negative if players' wrists pitch over before the gesture fires. Invisible — the stone is always drawn at the real angle. |
| `OptimalAngle` | launch | centre of the retention window | Nominally the same idea, different field. **Keep it equal to `BestFaceAngleDeg`.** |
| `ToleranceDeg` | both | width of both windows | The single biggest "how forgiving is this game" knob. It divides both `quality` and `angleFactor`, so widening it softens the punishment for bad angle twice over. Narrow = skill; wide = party. |

Angle is a gradient, not a cliff, and it is punished twice: once through `quality` (less lift, so
shorter hops) and once through `speedKept` (faster decay, so fewer of them). That double hit is
why the face sweep falls off so hard — in your current data, 34 skips at 0 degrees, 23 at 5, 14
at 10, 7 at 15, 0 at 20.

### Run length

| field | where | what it does | what you feel |
|---|---|---|---|
| `MinRetention` | launch | `speedKept` floor (worst angle, weakest throw) | How bad a bad throw is allowed to be. |
| `MaxRetention` | launch | `speedKept` ceiling (perfect angle, full power) | **The most powerful number in the file.** Skips go as `1/ln(speedKept)` and distance as `1/(1-speedKept^2)`, both of which blow up near 1. 0.95 to 0.96 is not a 1% change; it is roughly +25% skips. Treat the last two decimal places as significant. |
| `MinSkipSpeed` | bounce | speed below which the stone sinks | Where a run stops. Raise it to cut long tails of tiny skips; lower it for grinding finishes. Stops mattering entirely once `quality` drops enough for the 0.3 m/s lift floor to bind first. |

### Bounce shape

| field | where | what it does | what you feel |
|---|---|---|---|
| `SkipExitAngleDeg` | bounce | settled exit angle | Sets hop **length** and **height** through `tan`. Real stones exit at 2-6 degrees. Big values read as mortar fire, not skipping — and because hop length carries `tan(angle)`, going from 5 to 9 degrees makes every settled hop ~80% longer without changing the skip count at all. |
| `OpeningHopAngleDeg` | bounce | exit angle of the very first bounce | The visible pop at the start of a throw. Costs nothing — the speed loss already happened, so this only redirects what is left. Pure showmanship. |
| `OpeningHopSkips` | bounce | how many skips the pop fades over | How long the throw looks dramatic before settling into its long run. |
| `Restitution` | bounce | energy kept by a bounce with **no** lift | Only ever applies to a badly angled stone (`rise = max(-vy*Restitution, liftY)` — on a good throw lift always wins). This is the "slap" of a bad throw. Raising it does **not** make good throws better; if it seems to, lift is broken. |

### Simulation

| field | what it does |
|---|---|
| `FixedStep` | Integration step. 0.005 is fine. Larger values overshoot the water crossing and quietly shorten hops. |
| `PredictSeconds` | How far ahead the aim preview simulates. Must exceed your longest throw's flight time or the arc truncates on exactly the throws players try hardest for. At 60 s you have headroom. |

## Tuning recipes

Working backwards from feel to field:

| I want... | turn | because |
|---|---|---|
| More skips, same distance | `MaxRetention` up **and** `SkipExitAngleDeg` down | Retention adds bounces; a shallower exit shortens each one. |
| Longer, more dramatic hops | `SkipExitAngleDeg` up | `tan` on hop length. Skip count is untouched. |
| A punchier start | `OpeningHopAngleDeg` up, `OpeningHopSkips` up | Free — costs no energy. |
| Rocks that feel different from each other | `MaxRetention`, `MinSkipSpeed`, `SkipExitAngleDeg` | The three with the most leverage that are not catalog-locked. Retention separates skip counts; exit angle separates the shape. |
| Bad throws punished harder | `ToleranceDeg` down | Hits lift and retention simultaneously. |
| Bad throws punished less | `ToleranceDeg` up, then `Restitution` up | Tolerance first; restitution only changes what a failed throw looks like. |
| Distance without more skips | `MaxSpeed` up | Distance is `speed^2` per hop; skips only `ln(speed)`. |
| The run to stop sooner | `MinSkipSpeed` up | Cheapest way to trim a long tail. |

Two habits worth having:

- **Change one field at a time and re-run the sweep.** `speedKept` mixes angle and power in a
  60/40 blend, so two simultaneous edits can cancel and look like nothing happened.
- **Watch `MaxRetention` in the third decimal.** It is the only field where a change you would
  call trivial is not.

## Two things to be aware of

### Two knobs for one idea

`OptimalAngle` (retention, at launch) and `BestFaceAngleDeg` (lift, at each bounce) describe the
same physical thing: the wrist angle that skips best. Nothing keeps them in step.

Your Authored set has both at 0, so they agree. Your Derived set does not —
`Assets/Rocks/Tuning/Derived/rock_01.asset` has `BestFaceAngleDeg: -4.74` against
`OptimalAngle: 0.26`, five degrees apart on a 13 degree tolerance. The throw that gets the best
lift is therefore **not** the throw that gets the best retention, and neither is reachable at the
same time.

This is the same shape as the bug `SkipPhysics` documents at length in its own comments ("this
line used to compare against a separate `OptimalAngleDeg` of 20 while `FaceAngleDeg` read 0"),
fixed on the lift half and still live on the retention half. Keep the two equal by hand, or
collapse them into one field.

### Pitch 0 breaks three columns of the report

Your Authored set now has `LaunchPitchDeg: 0`. That is legitimate as a *feel* choice, but it
silently corrupts the measurement, because `SkipCurve.Measure` only counts a water contact where
the previous sampled point was **above** the water:

```csharp
var touched = path[i].y <= tuning.WaterY + 0.0001f &&
              path[i - 1].y >  tuning.WaterY + 0.0001f;
```

At pitch 0 the stone launches from y = 0 and never rises before its first contact, so that first
contact is never recorded. Everything shifts by one bounce:

- **"launch arc"** is really hop 1, not the toss. Reported 81.5 m; the actual toss is ~0 m.
- **"1st hop"** is really hop 2. Reported 66.3 m.
- **the opening-hop ratio** is therefore hop2/hop1, which is just `speedKept^2` times the
  tan-ratio and is always below 1. Reported 0.81x — matching the prediction
  `0.9025 * 0.904 = 0.816` exactly. **That check is currently defanged and passes for the wrong
  reason.**
- **"a badly angled throw is punished"** fails at `0.2 m off a 0.0 m toss`, because it divides
  against a launch arc of zero — not a tuning problem at all.

Nothing about the *game* is broken by this; the stone flies correctly. It is the report that has
gone unreliable, on three of the columns you would most want while hand-tuning. The fix is a
one-line change to seed the contact detector with the launch point, but it is a code change, so
it is listed as a decision rather than made.

## Validation

Every skip count predicted by `skips = ln(vEnd/v0) / ln(speedKept)` against the current Authored
baseline, versus what the solver actually printed:

| power | face | predicted | reported |
|---|---|---|---|
| 1.00 | 0 | 48.4 | 49 |
| 0.80 | 0 | 33.3 | 34 |
| 0.80 | 5 | 22.5 | 23 |
| 0.80 | 10 | 14.5 | 14 |
| 0.80 | 15 | 7.4 | 7 |
| 0.50 | 0 | 19.3 | 20 |
| 0.20 | 0 | 7.8 | 8 |

And hop length: `2 * (60*0.95)^2 * tan(7 deg) / 9.81 = 81.3 m` against a reported 81.5 m.

The formulas are the solver, not an approximation of it.

## Also outstanding

The report currently opens with a catalog-consistency banner: all six Derived rocks disagree with
Authored on `MinSpeed` (5 vs 1) and `MaxSpeed` (45 vs 60). Until that is reconciled the two sets
are not measured on the same footing, and any Authored-vs-Derived comparison is reading a speed
difference as a tuning difference.

See [Findings-RockSeparation.md](Findings-RockSeparation.md) for the open decisions this feeds.
