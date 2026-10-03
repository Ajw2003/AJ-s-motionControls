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

### `Assets/Scripts/Game/Physics/DiscContact.cs` — Pure model whose formulas follow the Findings doc: lift uses the squared normal velocity
<!-- ref:3781 -->

Was `Assets/Scripts/Game/Physics/DiscContact.cs:5-28`.

The published planing-disc contact model: wetted area, its centroid, and the two
forces that act on it. Pure, closed-form geometry plus one force evaluation — no
integration lives here, that is RigidDisc's job.

Every formula here is cited in docs/Findings-RealSkipPhysics.md and must not drift
from it — that document, not this file's comments, is the source of truth for the
numbers. In particular:

- Wetted area is Bocquet [B] Eq. (9) (Findings §2), algebraically identical to the
  form quoted in docs/plans/real-skip-physics.md.
- The force law is the corrected one from Findings §1, NOT the plan's first draft:
  lift goes as the SQUARED NORMAL velocity `(v·n̂)²` (the Nagahiro/Birkhoff form,
  Findings §1.2 — Bocquet's constant-coefficient `V²` form cannot produce a magic
  angle), and friction acts along `t̂`, which lies IN the disc's own plane, not
  against the flight path (Findings §1.1). There is no force at all on the dry face.

PURE. No Unity scene access, no events, no side effects, deterministic — this file
lives in the RockSkipping.Physics assembly (`"references": []`) alongside
SkipPhysics/PhoneAttitude/SkipConstants/SkipTuning specifically so it can be used
standalone by EditMode tests and by the verification harness. Do not add a
reference to anything outside System.Collections.Generic/UnityEngine.

### `Assets/Scripts/Game/Physics/DiscContact.cs` — Lift applies only when the face moves into the water
<!-- ref:a036 -->

Was `Assets/Scripts/Game/Physics/DiscContact.cs:191-207`.

── The face must be moving INTO the water to feel any pressure ─────────────
[N] assumption (ii): "For the part of the surface facing air, there is no
hydrodynamic force." Squaring normalSpeed loses its sign, so without this
guard the lift is applied along +n̂ even when the face is travelling AWAY
from the water — and still water would be pushing the stone along its own
direction of travel, doing positive work.

That is not a rounding error. Before this guard, a 12 m/s throw left its
first contact at 42.66 m/s: once the attitude tumbled far enough for n̂ to
lean into the velocity, every substep pumped in energy and the run became
nonsense. It is the same class of bug the old solver shipped with (see the
"ONE ENERGY BUDGET" remarks in SkipPhysics) — water can take energy from a
stone and redirect what is left, never add to it.

The guard is what makes that structural rather than hoped-for: with
normalSpeed < 0 required, the lift always opposes penetration, so its work
is always negative. R8 in SkipReality asserts the consequence.

### `Assets/Scripts/Game/Physics/RigidDisc.cs` — Pure like DiscContact, and it must integrate the full Euler equations because the fast-spin shortcut diverges as spin approaches zero
<!-- ref:50b4 -->

Was `Assets/Scripts/Game/Physics/RigidDisc.cs:5-22`.

A disc's mass properties and the full Euler rigid-body integration of its attitude
and angular velocity under an applied torque.

PURE. No Unity scene access, no events, no side effects — see the assembly-level
remarks in DiscContact; this file lives alongside it for the same
reason (EditMode-testable, referenced by nothing outside System.Collections.Generic
/ UnityEngine).

docs/plans/real-skip-physics.md is explicit that this must be the FULL Euler
equations, not the fast-spin gyroscopic shortcut (`ṅ̂ = (τ × n̂)/(I_a·Ω)`) that the
literature usually reaches for: that approximation divides by the spin rate and
diverges as `Ω → 0`, and an unspun throw tumbling on its own — with no invented
`WanderDeg` fudge to make it happen — is exactly the behaviour this rewrite exists
to get for free. [B] Eq. (A1) (Findings §2) is the standard Euler set this
implements.

### `Assets/Scripts/Game/Physics/SkipPhysics.cs` — Step must stay pure because the preview runs it constantly
<!-- ref:90fc -->

Was `Assets/Scripts/Game/Physics/SkipPhysics.cs:81-112`.

Custom bounce solver. Deliberately not Unity rigidbodies: skipping needs exact
per-bounce control over attitude and energy loss, and the aim preview needs to
run the same maths headlessly to draw its arc.

PURE. Step takes state in and returns state out; it mutates nothing, touches no
scene, and publishes no events.

That last part is load-bearing. The aim preview runs Step hundreds of times per
frame for a throw that has not happened. If Step published SkipOccurredEvent,
the preview would score imaginary throws and the scoreboard would climb while a
player merely aimed. RockController inspects the returned SkipStep and publishes
— and only when driving a real throw. Audio, VFX and camera shake follow the
same rule: observable effects belong to the caller, never in here.

The model: the throw drives, the rock modulates. Speed comes straight from the
phone's swing (SkipConstants.SpeedScale converts hand m/s to stone m/s); energy
loss per bounce falls out of how steeply the stone hits and how far its face is
from level, not from an authored retention constant; and each rock only scales
that geometry with three bounded multipliers. See
docs/plans/Plan-ThrowDrivenSkipModel.md for the full reasoning.

A bounce is ONE ENERGY BUDGET: it takes a loss, then splits what survives between
forward and upward motion. It never synthesises lift out of horizontal speed. That
distinction is load-bearing — the earlier version did synthesise it, and stones
gained energy every bounce and skipped for minutes on end. Because the loss is
floored at SkipConstants.MinLoss and the split preserves magnitude exactly,
outgoing speed is always strictly less than incoming speed, no matter what any
tuning field is set to. Keep it that way: any future change here should be checked
against that one invariant first. See docs/plans/Plan-SkipEnergyInvariant.md.

### `Assets/Scripts/Game/Physics/SkipPhysics.cs` — Take the loss first, then split what survives
<!-- ref:d8ee -->

Was `Assets/Scripts/Game/Physics/SkipPhysics.cs:297-306`.

── ONE ENERGY BUDGET ────────────────────────────────────────────────────────
Take the loss first, then split what SURVIVES between forward and upward.
Lift is a share of the post-loss budget, never a quantity synthesised from
horizontal speed — that was the bug this model replaces, where a stone
arriving with vy ~ 0 left with vy = 0.09 * vh and runs never ended.

The invariant, and it is structural rather than tuned: lossFraction is
floored at MinLoss > 0, and the split preserves magnitude exactly
(vy^2 + vh^2 = speedAfter^2), so outgoing speed is ALWAYS strictly less than
incoming speed — whatever quality, roll, or the rock's multipliers do.

### `Assets/Scripts/Game/RockController.cs` — Publish ThrowCompletedEvent after the sink and schedule Destroy after the publish
<!-- ref:2951 -->

Was `Assets/Scripts/Game/RockController.cs:286-301`.

Sink, hold, then let the match move on.

The ordering here is the whole fix for "a failed throw reads as a glitch".
ThrowCompletedEvent banks the score and rotates the turn synchronously
(TurnManager.OnThrowCompleted), so publishing it on the sinking step — which is
what used to happen — meant the next player was up before the stone had drawn a
single frame of its descent.

Destroy is scheduled AFTER the publish rather than alongside Finish. It used to
be set at Finish time on the reasoning that despawnDelay comfortably outlasts
the sink; that stops being true the moment someone lengthens the settle, and
the failure mode is the object dying before it publishes, which strands the match
on a turn that never ends. Publishing first makes that unrepresentable.

## Traps

**A missing asset degrades quietly.** `SkipConstants.CreateDefault()` and
`SkipTuning.CreateDefault()` exist so physics still runs if the asset is gone — but reaching them
means tuning is back to being a recompile. They warn; notice the warning.

**`mesh.vertices` is unavailable** on the rock meshes (`RocksRemeshed.fbx.meta` sets
`isReadable: 0`). Use `mesh.bounds` and `mesh.vertexCount` for anything mesh-derived.

### `Assets/Scripts/Game/Physics/ReferenceStone.cs` — C_f is zero on purpose: with squared-normal lift
<!-- ref:d69f -->

Was `Assets/Scripts/Game/Physics/ReferenceStone.cs:50-78`.

`C_f` — Bocquet's tangential friction coefficient. **Zero, deliberately, and this
is NOT the value [B] quotes.**

[B] quotes `C_f ≈ 1`, but that number belongs to [B]'s own self-consistent model
where lift AND friction both scale with the full `V²`. We use [N]'s `(v·n̂)²` lift
instead, because [B]'s `V²` form has no interior optimum in tilt and therefore
cannot produce the magic angle at all (verified — see below).

Mixing the two is not a small error. At the shallow angles skipping happens at,
`(v·n̂)² ≪ V²`, so a `V²` friction term dwarfs the lift and the stone ploughs in.
Measured with `tools/physics/verify-disc-contact.py`, sweeping tilt 5–60° at 15°
incidence:

    C_f = 0.00  ->  optimum 30 deg, v_min  4.44 m/s
    C_f = 0.10  ->  optimum 35 deg, v_min  6.52 m/s
    C_f = 0.25  ->  optimum 40 deg, v_min 16.22 m/s
    C_f = 0.50  ->  NEVER BOUNCES at any tilt or any speed
    C_f = 1.00  ->  NEVER BOUNCES at any tilt or any speed

So `C_f = 1` — the value the first draft of the findings doc specified — sinks
every throw ever made. The Birkhoff/[N] model is pressure-only by construction:
the stone still decelerates, because a tilted disc's normal force already has a
backward component. Friction does not need to be added on top, and cannot be.

**Raising this above ~0.1 will break the model, not tune it.** See
`docs/Findings-RealSkipPhysics.md` §1.3.

### `Assets/Scripts/Game/Physics/SpinTransfer.cs` — Stone spin is phone rate times grip radius over stone radius
<!-- ref:322d -->

Was `Assets/Scripts/Game/Physics/SpinTransfer.cs:5-44`.

Converts the phone's measured rotation rate into the stone's spin rate.

**The problem this solves.** The phone reports `spinRate` in deg/s about its own
screen normal — how fast the wrist twisted. The solver needs the stone's spin about
its face normal. The old code treated those as the SAME NUMBER, and that identity is
an invented assumption, not physics: a phone and a pebble are different sizes, so
equal angular rates are not equal throws.

What is actually conserved at release is the TANGENTIAL SPEED AT THE GRIP POINT. A
real skipper holds the stone with the index finger curled around its edge; the finger
pulls, the edge moves at the finger's speed, and the stone's spin follows from its own
radius:

    v_tangential = ω_phone · r_grip          (what the hand actually did)
    Ω_stone      = v_tangential / R_stone    (what the stone does with it)

so

    Ω_stone = ω_phone · (r_grip / R_stone)

A big object turning slowly has the same rim speed as a small object turning fast,
which is why the ratio is above 1 for any stone smaller than a phone is long. This is
the whole model: one ratio of two measurable lengths, no free parameter.

**What this does NOT fix, and nothing on this side can.** Measured against 124 real
throws (`docs/Findings-RealSkipPhysics.md` §6.2), the phone's best recorded twist is
3.83 rev/s. Bocquet's bounce ceiling `N_c = R·Ω²/g` wants ~14 rev/s for a world-record
run. This conversion lifts the best throw from about 3 bounces to about 7 — a real
improvement, and still far short.

The residual is not a tuning shortfall, it is a measurement one: a wrist twist is not
a finger flick, and the phone is not being asked to perform one. A real flick moves the
fingertip at roughly 4.4 m/s (= 88 rad/s × 0.05 m); the phone gesture manages roughly
1.7 m/s of rim speed. Closing the rest means changing what the CONTROLLER captures, not
what this file computes. Do not "fix" it by inflating GripRadius past a
real phone's half-length — that would turn a measured constant back into a feel knob,
which is the failure mode this whole rewrite exists to end.

### `Assets/Scripts/Game/RockController.cs` — Position is drawn between fixed steps using the accumulator, so do not shrink FixedStep for smoothness
<!-- ref:ea08 -->

Was `Assets/Scripts/Game/RockController.cs:228-242`.

Only while flying, deliberately. Once the run is over this stops writing
position at all, which is what leaves the transform free for a sink
coroutine to own without Update fighting it back every frame.

Drawn BETWEEN simulation steps, not on them. The solver advances in fixed
increments but frames do not land on those boundaries, so writing the raw
solver position pins the stone to the step rate: at FixedStep 0.02 that is
50 position updates a second against a 60-144 Hz display, which reads as
stutter however good the trajectory is. _accumulator is exactly the time
already elapsed past the last step, so carrying the stone forward by it
puts the stone where it actually is at draw time.

This is why smoothness should not be bought by shrinking FixedStep: that
costs the aim preview 4x the work (MaxPredictSteps = PredictSeconds /
FixedStep) for a problem that is not in the physics.

### `Assets/Scripts/Game/RockController.cs` — Hide the stone the instant it is under
<!-- ref:1efc -->

Was `Assets/Scripts/Game/RockController.cs:382-391`.

Gone the instant it is under, and BEFORE depth writing is handed back. The
stone is not destroyed here — the settle beat and ThrowCompletedEvent still
have to run, and they run on this object's coroutine — so it is hidden
instead and torn down later.

The order is the bug. The only thing concealing a sunk stone is the water
writing depth; switch that off with the stone still alive 1.5 m down and it
reappears, whole, hanging under the surface. It did that before too, but the
camera had already left, so nobody watched it happen. Holding the shot on
the sink put it on screen.

## Verification

`RockSkipping/Verify/Print Skip Curve` runs speed/angle/roll sweeps per rock and writes
`skip-curve.txt` at the repo root. This is the acceptance check for "is this tuning any good",
and it includes the energy invariant that every run must satisfy.

`Assets/Tests/EditMode/SkipPhysicsTests.cs` — NUnit, against the isolated physics assembly.
`Assets/Scripts/Verification/RockTelemetry.cs` records real in-play throws to `rock-throws.csv`.

## Notes moved from source comments

The author's reasoning, moved verbatim out of the code (issue #84/#85). Each note cites the code it describes; the code carries a `doc-ref` pointer to its note.

### `Assets/Scripts/Game/Physics/DiscContact.cs` — Wetted area is Bocquet's Eq. 9 rewritten in terms of d = R - s, so d = 0 is exactly half wetted
<!-- ref:32bd -->

Was `Assets/Scripts/Game/Physics/DiscContact.cs:31-47`.

The wetted area of a circular disc of radius radius, cut by a
plane whose intersection with the disc's own plane sits a signed distance
d from the disc centre, measured along the axis that points
INTO the water (see WettedAxis).

Bocquet [B] Eq. (9) / Findings §2, `S_im(s) = R²·[acos(1−s/R) − (1−s/R)·√(1−(1−s/R)²)]`
with `s = |z|/sinθ`. Substituting `d = R − s` gives exactly the form used here,
`R²·acos(d/R) − d·√(R²−d²)`, which the Findings doc confirms is algebraically
identical.

`d` is the distance from the disc centre to the waterline, along the wetted
axis: `d = −R` means the whole disc is submerged (the far side, `d = +R`, means
bone dry). At `d = 0` the waterline passes exactly through the centre and the
disc is exactly half wetted — this is what the EditMode geometry tests check.

### `Assets/Scripts/Game/Physics/DiscContact.cs` — Centroid distance is (2/3)(R^2-d^2)^1.5 over the wetted area
<!-- ref:a425 -->

Was `Assets/Scripts/Game/Physics/DiscContact.cs:61-80`.

Distance from the disc centre to the wetted segment's centroid, along the same
wetted axis d is measured on. Always ≥ 0: the centroid of the
wetted cap sits between the waterline and the far (wettest) rim, never behind
the disc centre.

Closed form for a circular segment's centroid, derived directly from
`WettedArea`'s own integral (see the derivation in the class remarks of the
EditMode test file — it is not separately published in [B], but it follows from
the same integral [B] Eq. (9) is built from):

```
x̄ = (2/3)·(R² − d²)^1.5 / A_wet
```

At `d = −R` (fully submerged) this correctly gives 0 — the centroid of a whole
circle is its own centre. At `d = 0` (half wetted) it gives the textbook
semicircle centroid, `4R/(3π) ≈ 0.424·R`, measured from the flat edge.

### `Assets/Scripts/Game/Physics/DiscContact.cs` — The wetted axis is the in-plane direction into the water
<!-- ref:6ab6 -->

Was `Assets/Scripts/Game/Physics/DiscContact.cs:92-109`.

The in-plane direction that points INTO the water, given the disc's outward
face normal normal. This is the axis WettedArea
and WettedCentroidDistance's `d` is measured along, and the
direction the wetted centroid is offset from the disc centre in world space.

Moving a distance `x` along this axis lowers height by `x·sinθ`, where `θ` is
the disc's tilt off horizontal (`sinθ = |horizontal component of normal|`) — so
`d = z / sinθ` (see SignedWaterDistance) is exactly the disc-plane
distance to the waterline that trigonometry predicts.

Degenerate when the disc is dead flat (`normal` parallel to world up): there is
no well-defined "downhill" direction on a level disc, so this falls back to
world forward projected onto the disc's plane — an arbitrary but stable choice
(a flat disc's contact is radially symmetric, so which in-plane axis is picked
does not matter physically).

### `Assets/Scripts/Game/Physics/DiscContact.cs` — Evaluate is pure, and C_l and C_f are fields because Bocquet's constant-coefficient assumption is the model's weakest
<!-- ref:df22 -->

Was `Assets/Scripts/Game/Physics/DiscContact.cs:157-169`.

Evaluate the fluid forces for one substep of contact. Pure — reads the disc's
state, returns forces and torque, touches nothing else.

<param name="heightAboveWater">Disc centre's height minus the water height.</param>
<param name="normal">Disc's outward face normal, world space, unit length.</param>
<param name="velocity">Disc centre's velocity, world space, m/s.</param>
<param name="radius">Disc radius, m.</param>
<param name="waterDensity">kg/m³ — Bocquet [B] uses 1000 (fresh water).</param>
<param name="liftCoefficient">`C_l` — Findings §3, "≈ 1", constant (Bocquet's
weakest assumption, see Findings §5 item 1 — exposed as a field, not baked in,
for exactly that reason).</param>
<param name="dragCoefficient">`C_f` — same source, "≈ 1".</param>

### `Assets/Scripts/Game/Physics/ReferenceStone.cs` — Bocquet's standard stone
<!-- ref:b5a9 -->

Was `Assets/Scripts/Game/Physics/ReferenceStone.cs:3-33`.

Bocquet's standard stone — the exact dimensions and coefficients [B]'s closed-form
results (Findings §3) were derived for. Everything R1-R7 in the verification harness
asserts is a published number for THIS stone, so measuring it here is reproducing
the papers' own apparatus, not something merely analogous to it.

Source: L. Bocquet, "The physics of stone skipping", Am. J. Phys. 71(2), 150-155
(2003), arXiv:physics/0210015 — docs/Findings-RealSkipPhysics.md §3, first row:
"M = 0.1 kg, a = 0.1 m diameter (R = 0.05 m), C_l ≈ C_f ≈ 1, ρ_w = 1000 kg/m³,
θ ≈ 10°."

**Not a SkipTuning.** docs/plans/real-skip-physics.md's Phase 1 table describes
this as "a code-built SkipTuning", but that plan's Phase 1 also lists rewriting
SkipTuning.cs itself (three multipliers → Mass/Radius/Thickness), and this pass was
scoped NOT to touch SkipTuning.cs, SkipPhysics.cs, SkipConstants.cs or any other
gameplay file — Phase 1 here is additive only, so the existing game keeps compiling
and playing unchanged while these new pieces are built and measured standalone. This
class is therefore a plain, asset-free, dependency-free data holder instead — it is
still exactly what the harness needs (SkipReality drives DiscContact/RigidDisc
directly, never through SkipTuning), and it means adopting the real dimensions as an
actual SkipTuning conversion is a clean, isolated Phase 2 decision rather than one
smuggled in here.

**Thickness is NOT a published number.** Neither [B] nor [C] state a thickness —
both treat the stone as an idealised thin disc. `Thickness` below is an engineering
assumption (a plausible thin flat stone, R/10), not a citation, and exists only so
RigidDisc.ComputeInertia has a transverse-inertia term to compute; it
is deliberately small enough that the transverse moment is dominated by the ¼MR²
term the papers DO ground, exactly as the thin-disc idealisation intends.

### `Assets/Scripts/Game/Physics/RigidDisc.cs` — The axial moment J0 is on local Y (the face normal)
<!-- ref:b1e1 -->

Was `Assets/Scripts/Game/Physics/RigidDisc.cs:25-33`.

A disc's two distinct principal moments of inertia. Bocquet's axis convention
(Findings §2 / [B] Appendix A): axis 2 is along the disc's own normal `n̂` (the
spin axis) with `I₂ ≡ J₀`; axes 1 and 3 lie in the disc's plane (one of them is
`t̂`) and share `I₁ = I₃ ≡ J₁`. In this file's local-frame convention (matching
PhoneAttitude.FaceNormal, which is `attitude * Vector3.up`) that
maps onto Unity's local axes as: local Y = `n̂` (axial, `J₀`), local X and local Z
= the two transverse axes (`J₁`).

### `Assets/Scripts/Game/Physics/RigidDisc.cs` — Integrates attitude and angular velocity in place from a world torque in N.m (rad/s)
<!-- ref:9bb2 -->

Was `Assets/Scripts/Game/Physics/RigidDisc.cs:57-69`.

Advance attitude and angular velocity by one substep under an applied
world-space torque, integrating the full Euler rigid-body equations (not the
fast-spin approximation — see the class remarks).

<param name="attitude">World-space orientation. Updated in place.</param>
<param name="angularVelocityWorld">World-space angular velocity, rad/s. Updated
in place.</param>
<param name="torqueWorld">Applied torque about the disc centre, world space,
N·m (from DiscContact.Evaluate during contact; zero in free
flight — gravity acts at the centre of mass and exerts no torque).</param>
<param name="inertia">This disc's principal moments.</param>
<param name="dt">Substep, seconds.</param>

### `Assets/Scripts/Game/Physics/SkipPhysics.cs` — Accumulated attitude drift: unstabilised bounces add to it and it is charged against pitch and roll
<!-- ref:57e0 -->

Was `Assets/Scripts/Game/Physics/SkipPhysics.cs:54-65`.

Accumulated attitude drift, degrees. A real stone skips because spin holds its
face steady through the slap; without spin it tumbles, the attack angle
randomises and the run dies in a bounce or two. This is that: every bounce adds
drift in proportion to how UNstabilised the stone is, and the total is charged
against the pitch and roll errors, so quality decays over a run at a rate spin
controls.

It is also what stops attitude being frozen at launch. Before this existed,
quality was computed once from the release pose and never changed, so nothing
about a run ever degraded.

### `Assets/Scripts/Game/Physics/SkipPhysics.cs` — Quality is the product of three gradient bands
<!-- ref:a1dd -->

Was `Assets/Scripts/Game/Physics/SkipPhysics.cs:269-277`.

Three axes, three bands, three qualities, multiplied together. Each is a
gradient, not a cliff — see BandQuality — and past the pitch band the falloff
is asymmetric: too shallow fades gradually (a survivable skim), too steep
collapses fast (an abrupt plow-in). That asymmetry is the whole reason pitch
has two falloff constants instead of one.

WanderDeg is charged against pitch and roll here: it is the tumble a stone
picks up when spin is not holding it steady, so a poorly spun run degrades
bounce by bounce instead of repeating one frozen launch pose forever.

### `Assets/Scripts/Game/Physics/SkipTuning.cs` — SkipTuning is three bounded multipliers
<!-- ref:36fa -->

Was `Assets/Scripts/Game/Physics/SkipTuning.cs:5-26`.

One rock's character: exactly three bounded multipliers plus its display name.

Used to be 25 fields, most of them fighting each other, and the throw's own power
barely moved the result next to a one-point tuning edit — see
docs/plans/Plan-ThrowDrivenSkipModel.md for the numbers. The model now derives energy
loss from bounce geometry (incidence, face-vs-velocity) rather than an authored
retention constant, so there is nothing left here to author except how strongly THIS
rock expresses that geometry: how much it spins, how high it hops, how far it carries.

Everything else — gravity, water height, the forgiveness bands, the speed scale, and
above all SkipConstants.EnergyLoss, the single dial for how many skips a
good throw gets — is global, on SkipConstants, because it answers "how
does this game feel" rather than "what is this rock," and a rock should not be able to
quietly become easier instead of stronger.

All three multipliers currently ship at 1.0 on every rock, on purpose: the physics is
being evaluated bare first, so that every difference between two throws is the throw.
Rock character gets authored once that reads well — see
docs/plans/Plan-SkipEnergyInvariant.md.

### `Assets/Scripts/Game/RockController.cs` — Visual spin is clamped, not rescaled, at the frame-rate alias limit of about 2700 deg/s, and is shared with the aim ghost
<!-- ref:8b2f -->

Was `Assets/Scripts/Game/RockController.cs:50-64`.

Ceiling on how fast the stone is drawn turning, in deg/s. Shared with the aim
ghost so the two cannot spin differently.

Not a tuning field, because it is not a taste decision — it is the frame rate.
The silhouette of a flat box repeats every half turn, so at 60fps anything past
about 2700 deg/s aliases into a wagon-wheel that appears to turn backwards, and
it looks wrong well before that. A hard wrist flick genuinely measures 1500-2500
deg/s on a phone, so this is reachable in normal play rather than theoretical.

Clamping rather than rescaling: below the ceiling the stone turns at exactly the
rate the phone measured, which is the point. Only the flicks that could not be
drawn honestly anyway are capped.

### `Assets/Scripts/Game/RockController.cs` — Speed is exposed from the solver because the transform is only written while flying
<!-- ref:eb1f -->

Was `Assets/Scripts/Game/RockController.cs:118-130`.

How fast the stone is travelling right now, in m/s. Read-only, for presentation.

Exposed rather than derived from the transform by whoever wants it: position is
only written while IsFlying, so a frame-delta reading would report a stone that
had stopped dead the instant a run ended and then report the sink descent as
speed. This is the solver's own number and stays correct in both.

A property, not an event, and that keeps it on the right side of the line this
class documents. Reading state publishes nothing and cannot fire on a throw that
never happened.

### `Assets/Scripts/Game/RockController.cs` — Spin is seeded from the solver and then left to it
<!-- ref:f40d -->

Was `Assets/Scripts/Game/RockController.cs:140-155`.

Seeded from the solver's own spin, and from here on the solver keeps it.

This used to run a full SkipPhysics.Predict of the entire throw purely to
learn the skip count, then back-solve a per-bounce decay that would land spin
on an authored 0.001 exactly as the run ended. Nothing about that was
physical: the stone's spin was not decaying BECAUSE of anything, it was being
driven to a predetermined value on a predetermined schedule. It also made the
ending look wrong in a way that depended on the skip count — a throw that
scored zero kept every degree of its spin (the divide-by-zero branch returned
retention 1.0) and knifed under beautifully, while a good throw arrived with
0.1% of its spin and sat there like a deleted object.

SkipPhysics already decays spin honestly, by SkipConstants.SpinRetention per
bounce, because spin is load-bearing there: it is the gyroscopic stability
holding the stone's attack angle. Reading that is free, correct, and means
what the player sees is the same number the bounce is judged on.

### `Assets/Scripts/Game/RockController.cs` — Sinking works by turning water depth writing on
<!-- ref:f1af -->

Was `Assets/Scripts/Game/RockController.cs:335-352`.

Drops the stone under the surface once its run is over.

The descent on its own would just slide a fully-visible stone downwards. What
makes it read as sinking is the first line: during play the water writes no
depth, so nothing can occlude the stone and it is never cut by the surface;
switching depth writing on lets the water swallow it as it goes down.

That toggle lives on the WATER rather than the stone, and not by preference —
URP's Lit shader exposes no _ZTest to turn off, and a render queue only decides
draw order, not occlusion. PlaceholderArt.SetWaterDepthWrite records both dead
ends in full.

Safe to own the transform because Update only writes position while IsFlying,
and IsFlying is already false by the time this starts. Rotation is still driven
there, so the stone keeps spinning down as it goes under.
