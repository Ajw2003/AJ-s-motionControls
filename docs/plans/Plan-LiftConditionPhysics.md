# Plan — retool skip physics onto the Lift Condition

> **SUPERSEDED, 2026-09-09, by [`real-skip-physics.md`](real-skip-physics.md).** That plan replaced
> this one because it quotes the primary sources directly (Bocquet 2003, Clanet 2004, Nagahiro
> 2004) rather than reasoning from the Lift Condition alone — and in doing so it found that the
> constant-coefficient `V²` form assumed below **cannot produce a magic angle at all**. See
> [`../Findings-RealSkipPhysics.md`](../Findings-RealSkipPhysics.md) §1.2.
>
> **Kept, not deleted, because two things here are still wanted and appear in no other plan:**
>
> 1. **Phase 1, "give the stone a body"** — deriving mass, planform area, centroid and the full
>    inertia tensor from the *real mesh geometry* by signed-tetrahedron integration. The active
>    plan takes `Mass`/`Radius`/`Thickness` from `mesh.bounds` instead, which is cruder.
> 2. **The `isReadable` correction** — the FBX genuinely carries its geometry, and the
>    "`mesh.vertices` is unavailable" bullet in `Assets/CLAUDE.md` is wrong and propagated the
>    misreading.
>
> This file was rescued from the never-pushed branch `claude/stone-skipping-physics-34903d`. See
> [`../archive/Findings-HistoryUntangle.md`](../archive/Findings-HistoryUntangle.md) §4.

**Status:** superseded. Originally approved 2026-09-02. Supersedes
`Plan-ThrowDrivenSkipModel.md` (deleted in 0b7c500; recover with `git show 0b7c500^:docs/plans/Plan-ThrowDrivenSkipModel.md`),
`Plan-SkipEnergyInvariant.md` (deleted in 0b7c500; recover with `git show 0b7c500^:docs/plans/Plan-SkipEnergyInvariant.md`),
`../Guide-TuningFailure.md` (deleted in 0b7c500; recover with `git show 0b7c500^:docs/Guide-TuningFailure.md`) and the physics half of
`../Reference-SkipTuning.md` (deleted in 0b7c500; recover with `git show 0b7c500^:docs/Reference-SkipTuning.md`). Those three describe the heuristic
quality/energy-budget bounce that this plan deletes outright.

The threshold this is built on, the **Lift Condition**:

```
½ · ρ_w · A · C_L(α) · V²  ≥  M · g
```

with the two supporting conditions — an attack angle α and a trajectory angle β both near 20°, and
a gyroscopic spin satisfying `Ω > √(2·M·g / I)`. The whole point of the retool is that none of
those three numbers is typed into the solver: they are properties the model must *reproduce*, and
the verification harness asserts it does.

## Context

The current solver (`Assets/Scripts/Game/SkipPhysics.cs:219` `Step`) does not model skipping. It
models a *feel*: it scores the throw on three forgiveness bands, multiplies them into a scalar
`quality`, and uses that to scale an authored `EnergyLoss` and an authored `LiftShare` that splits
one energy budget between forward and upward. Every number in that chain — `WorstQualityLossScale`,
`ShallowIncidenceLossScale`, `MaxLiftShare`, `RollSteerRad`, `MinSkipSpeed`, `MinExitRiseSpeed`,
`WanderPerBounce`, `SpinRetention` — was guessed and then re-guessed against how it played. The
project has already burned two rewrites on it (`docs/plans/Plan-ThrowDrivenSkipModel.md`,
`docs/plans/Plan-SkipEnergyInvariant.md`) and a whole tuning guide
(`docs/Guide-TuningFailure.md`), and the shipped `Assets/Resources/SkipConstants.asset` has since
drifted so far from the code defaults that all three forgiveness bands are zeroed and
`SpinRetention` sits at 0.5 — the value the field's own tooltip calls the failure mode where
`EnergyLoss` "silently stops being the dial."

The stone also has no physical existence. `RockDefinition` carries a mesh, a scale and a tint;
`SkipTuning` carries three unitless multipliers. There is no mass, no area, no radius, no moment of
inertia anywhere in the project. So the solver cannot be checked against anything — there is no
external fact it is supposed to reproduce.

**The change:** replace the heuristic bounce with the real fluid-dynamic contact. Give the stone
mass, radius, thickness, planform area and moment of inertia derived from its actual mesh. Integrate
lift, drag, buoyancy and weight through the water-contact phase, so skip-versus-sink *emerges* from
the Lift Condition rather than being tested as a branch, and energy loss *is* drag rather than an
authored fraction. Anchor the whole thing to a reference stone matching a published experiment, so
the model has a right answer to be checked against. Then, and only then, back-solve the
player-facing feel — the three forgiveness bands survive, applied last, as an assist on the
*inputs*, never as a correction to the physics.

Decisions already taken (all four confirmed with the user):

1. **Sub-stepped contact integration**, not a threshold branch. The Lift Condition
   `½ρ_w·A·C_L(α)·V² ≥ Mg` becomes a *derived property the harness asserts*, not an `if`.
2. **20° emerges**, it is not typed in. `C_L(α)` and `C_D(α)` are monotonic plate functions; the
   verification sweep must *find* the optimum near 20°. The hardcoded `OptimalAttackDeg = 20f`
   const is deleted.
3. **Stone properties derive from the real mesh geometry × density.** No hand-authored physics on a
   rock — `Assets/CLAUDE.md` forbids it and this keeps that true.
4. **Forgiveness is an input assist**, applied before the solver runs, pulling the measured attack
   angle / roll / yaw toward optimal by up to the band width. The solver never learns forgiveness
   exists.

---

## Phase 0 — commit the plan into the repo ✅

This file, plus a "Superseded" callout at the top of `docs/plans/Plan-ThrowDrivenSkipModel.md`,
`docs/plans/Plan-SkipEnergyInvariant.md`, `docs/Guide-TuningFailure.md` and
`docs/Reference-SkipTuning.md`. Per this repo's own convention (`Assets/CLAUDE.md`, "Docs drift
fast and say so") the old text stays and gets marked, rather than being deleted.

---

## Phase 1 — give the stone a body

### New: `Assets/Scripts/Game/StoneBody.cs`

A plain readonly struct, no `ScriptableObject`, computed once per throw and passed into the solver
alongside `SkipConstants`. Nothing about it is authored on a rock.

```
Volume        V    m³      exact, signed-tetrahedron sum over the triangles
Mass          M    kg      StoneDensity · V
Centroid      c    m       exact, volume-weighted
PlanformArea  A    m²      true area of the mesh projected onto the face plane
Radius        R    m       √(A/π) — the equivalent-disc radius, used for Ω_min and A_wet
Thickness     t    m       extent along the face normal
Inertia       I    kg·m²   exact tensor about c, component about the face normal
```

**The mesh is readable, and the docs are misleading on this.** `isReadable: 0` is set
(`Assets/Resources/Fbx/RocksRemeshed.fbx.meta:33`), but that is a Unity *import* setting, not a
property of the file: the binary FBX carries all six `Geometry` nodes with their `Vertices` and
`PolygonVertexIndex` arrays intact. The flag only drops the CPU-side copy after GPU upload, so it
restricts `mesh.vertices` **at runtime in a player build** — and this derivation never runs at
runtime. `RockCatalogBuilder` is an editor script and the body is computed at import time.

Set `isReadable: 1` on the FBX importer as part of this phase anyway, so the derivation does not
depend on the Editor's habit of retaining imported mesh data. The cost is a CPU copy of six meshes
out of a 172 KB file. Then correct the "**`mesh.vertices` is unavailable**" bullet in
`Assets/CLAUDE.md`, which is what propagated the misreading.

Derivation, `StoneBody.FromMesh(Mesh mesh, Vector3 localScale, Quaternion meshRotation, float stoneDensity)`:

- Transform vertices by `LocalScale` and `RockDefinition.MeshRotation`, so the flat face lies in the
  face plane — the same correction `RockTrail` and the solver already depend on.
- `V` and `c` by the standard signed-tetrahedron sum over triangles against the origin; `I` by the
  standard per-tetrahedron inertia accumulation, then parallel-axis-shifted to `c`.
- `A` by summing the projected triangle areas onto the face plane, halved (each closed surface
  projects twice), so a genuinely oval stone reports its real planform rather than a bounding disc.
- `M = StoneDensity · V`, with `StoneDensity` (~2500 kg/m³, granite) a global on `SkipConstants`.

**`StoneDensity` is now the only authored physical constant on the stone**, and it is a real
material property with a right answer, not a shape fudge. In particular there is no `FillFraction` —
the earlier plan needed one only because it was approximating the stone as a cylinder from bounds.
It is dropped from `SkipConstants` below.

A non-manifold or inverted mesh yields `V ≤ 0`; the catalog validator must catch that (below) rather
than let a negative mass reach the water.

A `StoneBody.Reference` static giving the Clanet/Hersen/Bocquet (Nature, 2004) experimental disc —
**R = 0.025 m, t = 0.00275 m, M = 0.0089 kg, aluminium** — is the "mathematically perfect stone" the
base implementation is built and validated against. It is a hard external anchor: the published
critical throwing speed for that disc at 20° is a number the sim either reproduces or does not.

### New: `Assets/Rocks/Reference/Stone_Ideal.asset`

A `RockDefinition` with no mesh, flagged reference-only, whose body is `StoneBody.Reference`. Used
by the verification harness and by the editor sweep; never selectable in-game.

### Edited: `Assets/Scripts/Editor/RockCatalogBuilder.cs`

Compute and log each rock's derived body during the existing idempotent rebuild, so the six stones'
real masses, planform areas and radii are visible in the console. Because the vertex walk is not
free (six meshes, a few thousand triangles each), `RockLauncher` computes the `StoneBody` once when
a rock is selected and hands it to `CreateLaunch` — `AimPreview` must never recompute it per frame.
Do **not** serialise the body onto `RockDefinition` —
they are derived, and a serialised copy is a copy that can go stale.

### Edited: `Assets/Scripts/Editor/` catalog validator

Extend the existing `RockSkipping/Rocks/Validate Catalog` menu item to fail a rock whose derived
volume, mass, planform area, radius, thickness or inertia is non-finite or ≤ 0. A non-manifold or
inside-out mesh produces a negative signed volume, and that is now the one way a bad import can
reach the water — so it gets caught at the catalog, not at a bounce.

---

## Phase 2 — the new solver

### New: `Assets/Scripts/Game/HydroCoefficients.cs`

Flat-plate impact theory, pure static functions, no Unity types:

```
C_N(α) = 2 sin²α                       normal-force coefficient
C_L(α) = C_N(α) · cos α                lift, ⊥ to velocity
C_D(α) = SkinDrag + C_N(α) · sin α     drag, along −V̂
```

This is the standard Newtonian plate decomposition and it is the whole reason 20° is allowed to
emerge: lift grows as sin²α·cos α while drag grows as sin³α, so shallow angles do not generate
enough lift to reverse `v_y` inside the contact and steep angles pay too much drag doing it. The
optimum falls out of that trade. `SkinDrag` is a global on `SkipConstants`.

### New: `Assets/Scripts/Game/WaterContact.cs`

The contact-phase integrator. Pure, no Unity scene access, same rule as `SkipPhysics` today.

Entered when `Position.y < WaterY`. Runs at `ContactStep` (~0.5 ms, a global — the ballistic
`FixedStep` of 20 ms is far too coarse for a contact that lasts a few milliseconds). Per sub-step:

1. **Immersed area** `A_wet` — real circular-segment geometry, not a lerp. The water plane cuts the
   tilted disc along a chord; the chord's signed distance from the disc centre is
   `s = −depth / sin(θ_face)` clamped to `[−R, R]`, and `A_wet` is the standard circular-segment
   area for that chord. Exact for a disc, cheap, and it has no free parameter.
2. **Angle of attack** `α = 90° − angle(FaceNormal, V̂)`, signed nose-up-positive. This is the
   *real* α, the angle between the stone's plane and its velocity — **not** the phone's tilt.
   Correcting this is a substantive fix: today `SkipPhysics.CreateLaunch` deliberately conflates the
   toss angle and the attack angle into one `FaceAngleDeg`, which is why the trajectory angle β and
   the attack angle α cannot be tuned against each other at all.
3. **Forces**, summed and divided by `M`:
   - Lift `½ ρ_w A_wet C_L(α) |V|²`, directed ⊥ to `V̂` in the plane spanned by `V̂` and
     `FaceNormal`. Bank now deflects lift sideways *automatically*, because a rolled face normal
     tilts that plane — which retires `RollSteerRad` and makes the curve of a banked throw an
     emergent result.
   - Drag `½ ρ_w A_wet C_D(α) |V|²` along `−V̂`.
   - Buoyancy `ρ_w · V_displaced · g` up. Free, real, and it matters for the sink descent.
   - Weight `−M g ŷ`.
4. **Gyroscopic drift.** The contact forces act at the centre of pressure of the wetted segment, not
   the centroid, so they apply a pitching torque `τ`. A spinning disc responds by precessing at
   `τ / (I Ω)`, so drift per sub-step is `(τ / (I Ω)) · dt`, accumulated into the attitude. This is
   the third condition from the brief implemented as a mechanism rather than a test: when
   `Ω > √(2Mg/I)` the drift over a contact is negligible and the stone holds its angle; below it the
   drift is large enough to swing α out of the useful range within a bounce or two and the run dies.
   `WanderPerBounce`, `SpinForStability` and `SpinRetention` all go away — spin now bleeds because
   the water torques it, and stability is the inequality, not a lerp against an authored threshold.

**Exit:** `Position.y ≥ WaterY` with `v_y > 0` → a skip. **Sink:** the disc is fully immersed while
`v_y` is still negative → the lift never overcame the weight, which is precisely
`½ρ_w·A·C_L(α)·V² < M·g` having held throughout the contact.

`ρ_w = 1000` and the `ContactStep`, `SkinDrag`, `StoneDensity`, `FillFraction` constants all live on
`SkipConstants`.

### Rewritten: `Assets/Scripts/Game/SkipPhysics.cs`

`Step` keeps its exact signature shape and its purity contract — the class remarks at
`SkipPhysics.cs:86-112` about `AimPreview` running it speculatively still bind, and `SkipStep`'s
`Bounced` / `Sank` / `ImpactSpeed` fields stay so `RockController.cs:200-225` and `SkipCurve` do not
change shape. Above water it is unchanged ballistic integration. Below water it delegates to
`WaterContact`. `SkipState` gains the stone's angular rate `Ω` in rad/s and loses `WanderDeg`.

`CreateLaunch` keeps the throw driving the stone, but with two corrections:

- **The attack angle stops being the toss angle.** The phone's tilt still sets the stone's attitude
  verbatim (`PhoneAttitude` is untouched — it is correct and hard-won); what changes is that α is
  now computed against velocity at each contact instead of being frozen at `FaceAngleDeg`.
- **Spin needs a scale factor, and this is load-bearing.** `Ω_min = √(2Mg/I)`, with the real `I`
  from the mesh rather than the disc idealisation — for a disc that reduces to `√(4g/R)`, which is
  the useful sanity check on the measured tensor. For a 3.5 cm
  stone that is ≈ 33 rad/s ≈ **1900 deg/s**, and real skipping stones do spin at 1800–5000 deg/s.
  But the logged phone throws in `rock-throws.csv` sit at 200–900 deg/s at the *wrist*. Without a
  conversion **every throw fails the stability condition and every stone tumbles out immediately.**
  Add `SpinScale` to `SkipConstants` beside the existing `SpeedScale`, same justification, same
  tooltip pattern: wrist deg/s → stone deg/s.

**Deleted outright** from `SkipPhysics`: `OptimalAttackDeg`, `WorstQualityLossScale`,
`ShallowIncidenceLossScale`, `SteepIncidenceLossScale`, `MaxLiftShare`, `RollSteerRad`,
`MinSkipSpeed`, `MinExitRiseSpeed`, `EffectiveBand`, `BandQuality`, and the entire quality / one-
energy-budget block at `SkipPhysics.cs:264-336`.

### Rewritten: `Assets/Scripts/Game/SkipConstants.cs`

| Keep | Delete | Add |
|---|---|---|
| `Gravity`, `WaterY` | `EnergyLoss` | `WaterDensity` (1000) |
| `SpeedScale`, `ReferenceSpeed` | `LiftShare`, `MinLoss` | `SpinScale` |
| `FixedStep`, `PredictSeconds` | `SpinForStability` | `ContactStep` |
| `Forgiveness{Pitch,Roll,Yaw}Deg` *(phase 5)* | `WanderPerBounce`, `SpinRetention` | `StoneDensity` |
| `SpinToleranceScale` *(phase 5)* | `RollFalloffDeg`, `MaxRollDeg` | `SkinDrag` |
| | `Pitch{Steep,Shallow}FalloffDeg`, `YawFalloffDeg`, `MaxIncidenceDeg` | |

`Assets/Resources/SkipConstants.asset` must be re-authored to match; the deleted fields will
otherwise linger as orphaned YAML keys.

### `Assets/Scripts/Game/SkipTuning.cs` — the three multipliers

`SpinMultiplier`, `VerticalMultiplier` and `HorizontalMultiplier` all scale things that no longer
exist (`EnergyLoss`, `LiftShare`, and an authored spin decay). All six assets ship at 1.0 today, so
nothing is lost by **deleting all three now** and letting rock character come from the derived body —
a thin wide stone and a thick narrow one genuinely skip differently once mass, area and inertia are
real, which is what `RockDefinition`'s own remarks say the feature was always meant to be. Keep the
`SkipTuning` asset and its `DisplayName`; the type stays so `RockDefinition.Tuning` and the six
assets do not need rebuilding. Revisit in phase 4 if the six stones read as too similar.

### Callers — mechanical follow-through

`RockController.cs` (drop the `WanderDeg`/`SpinRetention` reads at `:202-206`, take spin from the
new `Ω`), `RockLauncher.cs`, `AimPreview.cs:392-395`, `RockTelemetry.cs`. While in
`RockTelemetry.cs:109`, fix the hardcoded `"0.0"` yaw column — sideslip is judged at every contact
and has never once been recorded, so there is currently zero measured data on that axis.

---

## Phase 3 — verification, before any tuning

### Rewritten: `Assets/Scripts/Verification/SkipCurve.cs`

Keep the harness and its `RockSkipping/Verify/Print Skip Curve` menu item; replace what it asserts.
`skip-curve.txt` at the repo root is stale (it predates the current solver) and is not a baseline —
the first run of the new harness is.

New assertions, in order of what they prove:

1. **The optimum is 20°, and we did not tell it so.** Sweep α from 0° to 60° at fixed speed against
   `StoneBody.Reference`; report the α maximising skip count. **PASS if it lands in 15–25°.** If it
   does not, the coefficient model is wrong and gets fixed — not the game feel.
2. **The Lift Condition predicts the skip/sink boundary.** For each swept (α, V), evaluate
   `½ρ_w·A·C_L(α)·V²` against `M·g` analytically and compare to what the integrator actually did.
   The two should agree along the boundary. Disagreement means the contact geometry is wrong.
3. **The spin threshold predicts the tumble boundary.** Sweep Ω across `√(2Mg/I)` and confirm runs
   collapse below it and survive above it.
4. **Energy strictly decreases across every contact.** This replaces the old `MinLoss` clamp: lift
   does no work (it is ⊥ to velocity), drag always removes energy, so the invariant should hold
   *structurally* rather than be enforced. Assert it and delete the clamp. Same invariant the class
   remarks at `SkipPhysics.cs:298-301` describe, now checked instead of imposed.
5. **Runs terminate**, within the existing `TerminationBudgetSeconds = 120`.

The existing "throw speed must move skip count more than any per-rock multiplier" check
(`SkipCurve.Verdict`, `SpeedVsMultiplierCheck`) is obsolete once the multipliers are gone; replace
it with "throw speed must move skip count more than rock choice does."

The 15–30-skip band (`MinGoodSkips`/`MaxGoodSkips`) is a *feel* target and must be **reported but
not asserted** in phase 3. Bare physics is allowed to disagree with it. That is what phase 4 is for.

There is no Unity test runner in this project (`Assets/CLAUDE.md` states this outright) and this plan
does not add one — the editor menu item is the harness.

---

## Phase 4 — back-solve for feel

Only after phase 3 passes. The dials are now physical quantities with real units, and there are
only three:

- **`SpeedScale`** — wrist m/s → stone m/s. Sets how far a throw carries.
- **`SpinScale`** — wrist deg/s → stone deg/s. Sets how much of the player's spin buys stability
  against `√(2Mg/I)`. Expect this to be the sensitive one.
- **Stone size** (via `LocalScale` in the catalog builder, or `StoneDensity`). A
  bigger stone has more area for lift but more mass; `Ω_min = √(4g/R)` falls with radius, so bigger
  stones are easier to stabilise.

Target: a firm 30 m/s wrist throw at a natural angle lands in 15–30 skips, and hard throws beat soft
ones by more than stone choice does. Record what was changed and why in
`docs/Findings-PhysicsBacksolve.md` — measured numbers from the harness, not impressions.

---

## Phase 5 — the three bands of forgiveness, applied last

New: `Assets/Scripts/Game/ThrowAssist.cs`. One pure function, called by `CreateLaunch` (and
therefore equally by `AimPreview`, so the drawn arc never lies about what the throw will do):

```
AssistedAttitude(measured, constants) →
    for each of pitch / roll / yaw:
        error = measured − optimal
        assisted = error inside band ? optimal : optimal + (error − sign(error)·band)
```

Inside the band the player throws the physics-optimal stone; outside, only the *excess* reaches the
solver. `SpinToleranceScale` keeps its current job of widening each band with measured spin, and
keeps its current rule that a band authored at 0 stays off.

Two things this buys that outcome-softening would not: `SkipPhysics` stays pure real physics and has
no knowledge of assistance, and the assist is explainable to the player ("you were 8° off, we threw
it as 0°"). Two things to log: `RockTelemetry` should record both raw and assisted values per axis,
so how much the assist is actually doing is measurable rather than assumed.

`ForgivenessPitchDeg`, `ForgivenessRollDeg` and `ForgivenessYawDeg` all currently ship at **0** in
`Assets/Resources/SkipConstants.asset` (commit `d81f9e3` zeroed them). Re-author them from the new
harness's measured α/roll/yaw sensitivity, not from the numbers in `docs/Guide-TuningFailure.md`,
which describe a solver that will no longer exist.

---

## Files

**New:** `Assets/Scripts/Game/StoneBody.cs`, `WaterContact.cs`, `HydroCoefficients.cs`,
`ThrowAssist.cs`; `Assets/Rocks/Reference/Stone_Ideal.asset`;
`docs/plans/Plan-LiftConditionPhysics.md`, `docs/Reference-SkipPhysicsModel.md`,
`docs/Findings-PhysicsBacksolve.md`.

**Rewritten:** `Assets/Scripts/Game/SkipPhysics.cs`, `SkipConstants.cs`;
`Assets/Scripts/Verification/SkipCurve.cs`; `Assets/Resources/SkipConstants.asset`.

**Edited:** `Assets/Resources/Fbx/RocksRemeshed.fbx.meta` (`isReadable: 0` → `1`);
`Assets/Scripts/Game/SkipTuning.cs`, `RockController.cs`, `RockLauncher.cs`,
`AimPreview.cs`; `Assets/Scripts/Verification/RockTelemetry.cs`;
`Assets/Scripts/Editor/RockCatalogBuilder.cs` and the catalog validator; `Assets/CLAUDE.md`
(the "Skip physics" and "Rock catalog and tuning" sections both describe the model being replaced).

**Untouched, deliberately:** `PhoneAttitude.cs` (the phone→Unity conversion is correct and the file
documents why nothing should be negated at a call site), the event types, `ScoreManager`,
`TurnManager`, the relay and controller.

---

## Verification

No git actions at any point — status checks only, commits are the user's.

**1. Compile.** There is no out-of-band C# compile check in this project — Unity holds an exclusive
lock, and a prior attempt at one (`tools/check-compile.ps1`) was built and deleted for that reason.
Verify by reading the open Editor's Console after each phase.

**2. Physics harness** — the primary gate, run after phase 2 and again after every tuning change.
In Unity: `RockSkipping/Verify/Print Skip Curve`. Then, in **PowerShell 5.1**, working directory
`C:\Users\aj\Desktop\GameDev\RockSkipping`:

```bash
Get-Content .\skip-curve.txt
```

Expect the five phase-3 assertions, each PASS/FAIL with its measured number, and the emergent
optimum α printed explicitly.

**3. Catalog** — `RockSkipping/Rocks/Build Catalog from FBX` then
`RockSkipping/Rocks/Validate Catalog`. Console should log six stones with plausible real masses
(a 5–8 cm skipping stone is roughly 50–250 g). A stone coming out at 3 kg or 2 g means the volume
integral or `LocalScale` is wrong, and that is worth catching before any of it reaches the water.
Cross-check each stone's measured `I` against the disc idealisation `½MR²` for its derived `R` — a
flattish stone should land within roughly a factor of two, and a wild disagreement means the
tetrahedron accumulation or the face-normal axis is wrong.

**4. Play it.** From the repo root, PowerShell 5.1: `pwsh ./deploy/local-https.ps1`, then Play on
`Bootstrap`, join a phone, throw. The aim preview arc must match where the stone actually goes —
they run the same code, so a divergence means the assist or the contact step is being applied on
only one path.

**5. Telemetry** — `rock-throws.csv` at the repo root, after a handful of real throws. The yaw
column should now carry real numbers, and raw-vs-assisted per axis should show how much phase 5 is
actually doing.

## Risks

- **Preview cost.** `AimPreview` runs `Predict` every frame — 1100 ballistic steps today. Contact
  sub-stepping at 0.5 ms adds roughly 30–80 sub-steps per bounce across ~25 bounces. Measure it
  before optimising; if it bites, the fix is an adaptive contact step or a coarser preview cadence,
  not a shrunken `FixedStep` (the remarks at `RockController.cs:240-242` explain why).
- **20° might not emerge on the first try.** That is the model doing its job — it means the
  coefficient functions or the wetted-area geometry need correcting. It is not licence to hardcode
  the answer back in.
- **The game may play very differently after phase 2 and before phase 4.** Expected, and the point:
  phase 3 explicitly does not assert the feel target.
