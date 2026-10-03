# Skip physics

**Source:** `Assets/Scripts/Game/Physics/SkipPhysics.cs`, `SkipConstants.cs`, `SkipTuning.cs`;
callers `RockController`, `RockLauncher`, `AimPreview`

## What it owns

The stepping function that decides where a thrown stone goes, whether it bounces, and when it
sinks. It owns **the simulation**. It owns none of the consequences.

## Not PhysX, on purpose

Skipping needs exact per-bounce control over attitude and energy, and the aim preview needs to run
the same maths headlessly, many frames ahead, to draw its arc. Rigidbody collision gives neither.

`SkipPhysics`, `PhoneAttitude`, `SkipTuning` and `SkipConstants` form a pure, dependency-free core
— they import only `System.Collections.Generic` and `UnityEngine` — and live in their own
`RockSkipping.Physics` assembly specifically so `Assets/Tests/EditMode/` can test them in
isolation. That asmdef is `autoReferenced: true`, so the rest of the project keeps compiling
against them with no explicit reference.

## The purity rule — the single most important thing here

> **`Step` takes state in and returns state out. It mutates nothing, touches no scene, and
> publishes no events.**

This is load-bearing, not hygiene. The aim preview runs `Step` hundreds of times per frame for a
throw that has not happened. If `Step` published `SkipOccurredEvent`, the preview would score
imaginary throws and the scoreboard would climb while a player merely aimed.

`RockController` inspects the returned `SkipStep` and publishes — and only when driving a real
throw. **Audio, VFX, haptics, camera shake and score all follow the same rule:** observable
effects belong to the caller, never inside `Step`.

## The model

> The throw drives, the rock modulates.

- **Speed** comes straight from the phone's swing. `SkipConstants.SpeedScale` converts hand m/s
  to stone m/s, and it is the only unit conversion the model cannot do without.
- **Energy loss per bounce** falls out of bounce geometry — how steeply the stone arrives, and how
  far its face sits from level — not from an authored retention constant.
- **A rock** only scales that geometry, with three bounded multipliers.

### A bounce is one energy budget

It takes a loss, then splits what survives between forward and upward motion. **It never
synthesises lift out of horizontal speed.**

That distinction is the invariant. An earlier version did synthesise lift, and stones gained
energy every bounce and skipped for minutes on end. Because the loss is floored at
`SkipConstants.MinLoss` and the split preserves magnitude exactly, outgoing speed is always
strictly less than incoming speed — regardless of what any tuning field is set to.

**Any change here gets checked against that invariant first.**

### Spin is the stabiliser

A real stone skips because spin holds its face steady through the slap. Without spin it tumbles,
the attack angle randomises, and the run dies in a bounce or two.

`SkipState.WanderDeg` is that: every bounce adds drift in proportion to how *un*stabilised the
stone is, and the total is charged against the pitch and roll errors. Quality therefore decays
over a run, at a rate spin controls. Before `WanderDeg` existed, quality was computed once from
the release pose and never changed, so nothing about a run ever degraded.

Spin also widens every forgiveness band (`SpinToleranceScale`) — but a band authored at 0 stays
off regardless of spin. **Spin buys tolerance; it does not turn tolerance on where none was
authored.**

## The rock/global split

**A rock owns three numbers and nothing else:** `SpinMultiplier`, `VerticalMultiplier`,
`HorizontalMultiplier`, all narrowly bounded.

Everything that is not a property of one rock lives on the single `Resources/SkipConstants.asset`:
gravity, water height, the speed scale, `EnergyLoss`, the forgiveness bands, `ReferenceSpeed`, the
fixed step.

**A global belongs in `SkipConstants`, never on a rock.** This is enforced structurally rather
than by a validator: a rock has no field to disagree with. `SkipTuning` used to hold 25 fields,
most of them fighting each other, and the throw's own power barely moved the result next to a
one-point tuning edit.

See [`RockCatalogAndTuning.md`](RockCatalogAndTuning.md) for the asset side.

## The one dial

`SkipConstants.EnergyLoss` is *the* control for how many skips a good throw gets. Approximately:

```
skips ≈ ln(MinSkipSpeed / launchSpeed) / (0.5 · ln(1 - EnergyLoss))
```

Measured against a 30 m/s swing at the optimal angle: `0.17` ≈ 29 skips, **`0.20` ≈ 25 (the
default)**, `0.23` ≈ 21, `0.26` ≈ 18. That is an upper bound — a wobbly, low-spin throw ends
sooner, because tumble ends a run before the energy budget does.

**Watch `SpinRetention`.** Below about `0.98`, spin bleeding out — not `EnergyLoss` — becomes what
ends every run: tumble caps the skip count and throwing twice as hard stops buying more skips.
Keep it high enough that the energy budget runs out first, or `EnergyLoss` silently stops being
the dial.

## Constants that are facts, not knobs

`OptimalAttackDeg = 20°` is a private const and stays one. It is the empirically optimal attack
angle for a skipping stone (Clanet, Hersen & Bocquet, 2004) — a statement about what skipping *is*.
Nobody ever needed to dial it, only how forgiving missing it is, which is
`SkipConstants.ForgivenessPitchDeg`.

The falloff and hard-limit thresholds moved the other way, from consts to `SkipConstants` fields:
`RollFalloffDeg`, `MaxRollDeg`, `PitchSteepFalloffDeg`, `PitchShallowFalloffDeg`, `YawFalloffDeg`,
`MaxIncidenceDeg`. They decide what counts as a flat-out failed throw, which is a feel question
that has to be answerable without a recompile.

## Invariants

1. **`Step` is pure.** No events, no scene, no observable side effects.
2. **Outgoing speed < incoming speed, always,** for every possible tuning value.
3. **Lift is never synthesised** from horizontal speed — a bounce splits, it does not create.
4. **A global belongs in `SkipConstants`, never on a rock.**
5. **Presentation normalizes against `SkipConstants.ReferenceSpeed`.** `WaterRipples`,
   `SfxDirector` and `RockTrail` all do, and that is the one speed every such system must agree
   on.

## Traps

**A missing asset degrades quietly.** `SkipConstants.CreateDefault()` and
`SkipTuning.CreateDefault()` exist so physics still runs if the asset is gone — but reaching them
means tuning is back to being a recompile. They warn; notice the warning.

**`mesh.vertices` is unavailable** on the rock meshes (`RocksRemeshed.fbx.meta` sets
`isReadable: 0`). Use `mesh.bounds` and `mesh.vertexCount` for anything mesh-derived.

## Verification

`RockSkipping/Verify/Print Skip Curve` runs speed/angle/roll sweeps per rock and writes
`skip-curve.txt` at the repo root. This is the acceptance check for "is this tuning any good",
and it includes the energy invariant that every run must satisfy.

`Assets/Tests/EditMode/SkipPhysicsTests.cs` — NUnit, against the isolated physics assembly.
`Assets/Scripts/Verification/RockTelemetry.cs` records real in-play throws to `rock-throws.csv`.
