# Ground the skip solver in real stone-skipping physics

## Context

`SkipPhysics.Step` currently models a bounce as an **instantaneous impulse gated by invented
quality bands**. It works, it terminates, and its energy invariant holds — but nothing in it can
be checked against reality, because almost every number in it was chosen by feel:

- `OptimalAttackDeg = 20f` is *asserted* as a constant, citing Clanet 2004. In the real model 20°
  is not an input at all — it is a **result** that falls out of integrating the contact, and a
  solver that reproduces it has demonstrated it is right about everything upstream of it.
- Energy loss is `EnergyLoss × WorstQualityLossScale × incidence lerp × rock multiplier`, clamped.
  Four made-up shaping factors standing in for one dissipation integral.
- Lift is `LiftShare × quality × cos(roll) × multiplier`, clamped to `MaxLiftShare` — a share of a
  budget, with a comment explaining that a previous version synthesised energy here.
- Tumbling is `WanderDeg`, an accumulator of invented degrees, decayed by `SpinRetention` and
  compared against `SpinForStability`.
- Failure is three `BandQuality` gradients over `Forgiveness{Pitch,Roll,Yaw}Deg`, with five
  falloff constants and two hard cliffs.

That is roughly **23 knobs that answer to nothing**. The consequence is exactly what prompted this:
there is no way to ask "is this how a skipped stone actually behaves", so muscle memory — the only
oracle anyone has — has nothing to be compared against, and tuning is guessing in 23 dimensions
where the knobs interact.

**The intended outcome:** a solver whose contact phase is the published planing-disc model, whose
inputs are measurable SI quantities (kg, m, m/s, rad/s, kg/m³), and which is validated against
published experimental results *before* a single number is tuned for feel. Then "how punishing is
raw physics" becomes a measurement you read off a report, and every later tuning decision is a
deliberate, named departure from a known-correct baseline rather than a nudge in the dark.

The world is already real-metre scale (throws ~50 m, water plane and shore authored in metres), so
SI drops in 1:1. No scale factor anywhere.

### Decisions already taken

| Decision | Choice |
|---|---|
| Rollout | Build the solver + harness as **pure additions**, measure, write findings, *then* flip gameplay |
| Forgiveness bands | **Deleted entirely.** Raw physics baseline, no assist layer built yet |
| Rock character | Authored **SI fields** (`Mass`, `Radius`, `Thickness`), seeded once from mesh bounds |
| Reference stone | **A seventh rock ships by default: the theoretical perfect stone** (see below) |
| Scope | Solver + launch mapping + live attitude through contact + verification harness. **Not** `RockController`'s stepping loop |

### Sources

The model is Bocquet, *Am. J. Phys.* **71**, 150 (2003); Clanet, Hersen & Bocquet, *Nature* **427**,
29 (2004); Rosellini, Hersen, Clanet & Bocquet, *J. Fluid Mech.* **543**, 137 (2005).

> **Step 0 is DONE.** Both primary papers were fetched and read in full; every published value,
> formula and constant now lives in **[`../Findings-RealSkipPhysics.md`](../Findings-RealSkipPhysics.md)**
> with a citation. **That document, not this plan and not recall, is the source for every number the
> harness asserts.** It also records two corrections to this plan's force law (§1) and three
> consequences for the game (§4) that were not visible before reading the sources.
>
> Bocquet 2003 is open-access: [arXiv:physics/0210015](https://arxiv.org/abs/physics/0210015).

---

## The model

### State

`SkipState` becomes real rigid-body state in SI units:

| Field | Meaning |
|---|---|
| `Position`, `Velocity` | m, m/s (unchanged) |
| `Attitude` | quaternion — now **integrated**, not frozen at launch |
| `AngularVelocity` | rad/s, **world-space vector** — replaces the scalar `Spin` |
| `SkipCount`, `HasSunk` | unchanged |

Dropped: `FaceNormal`, `HeadingDir`, `FaceAngleDeg`, `RollDeg`, `WanderDeg`, scalar `Spin`. The
first four are derivable from `Attitude` on demand via existing `PhoneAttitude` helpers; `WanderDeg`
has no analogue because tumbling is now literally the attitude tumbling.

`RockController.Launch` reads `FaceAngleDeg`/`RollDeg`/`Spin` off the launch state for
`RockLaunchedEvent` — those become `PhoneAttitude.FaceAngleDeg(state.Attitude)`,
`PhoneAttitude.RollDeg(state.Attitude)`, and the axial component of `AngularVelocity`. Same event,
same fields, computed rather than stored.

### Two phases

**Free flight** — gravity only, at `FixedStep`. Air drag omitted (negligible over ~2 s at these
speeds; Bocquet omits it). This is what the current code already does and it is already correct.

**Contact** — entered when the disc's lowest rim point crosses `WaterY`, integrated at a fine
internal `ContactStep` (~20–50 µs) until the rim clears the surface again. Per substep:

1. **Wetted area.** The stone is a disc of radius `R`, normal `n̂`. Where the water plane cuts the
   disc's own plane, the submerged part is a circular segment. With `d` the signed in-plane
   distance from disc centre to that cut line (`d = y_centre / √(1 − n̂ᵧ²)`):

   ```
   A_wet = R² · acos(d/R) − d · √(R² − d²)        for |d| < R
   ```

   Exact closed form, cheap, no approximation. This single expression is what the current code's
   whole `incidenceFactor` / `quality` apparatus was standing in for.

2. **Forces.** **Corrected by step 0 — see `docs/Findings-RealSkipPhysics.md` §1.** Two errors in
   the original draft of this plan: friction acts **along the stone's own plane**, not against the
   flight path, and the normal force goes as the **normal** velocity, not the total speed.

   ```
   F_lift     = ½ · ρ_w · C_l · (v·n̂)² · A_wet · n̂      (Nagahiro/Birkhoff form)
   F_friction = ½ · ρ_w · C_f · V²      · A_wet · t̂      (t̂ lies IN the stone's plane)
   ```

   No force at all on the dry face. `ρ_w = 1000 kg/m³`; `C_l ≈ C_f ≈ 1` (Bocquet 2003 Eq. 1).

   > The `(v·n̂)²` form is not optional. Bocquet's constant-coefficient `V²` version **cannot
   > produce a magic angle** — tilt enters it only monotonically, with no interior optimum — and
   > reproducing 20° is check R1, the whole justification for this rewrite.

3. **Torque and attitude.** The centre of pressure is the **centroid of the wetted segment**, which
   sits forward of the disc centre — so the force exerts a torque `τ = r_cp × F` that tries to flip
   the stone. Integrate the **full Euler rigid-body equations** for a disc
   (`I_axial = ½MR²`, `I_transverse = ¼MR² + ¹⁄₁₂Mh²`).

   > Use the full equations, **not** the fast-spin gyroscopic approximation `ṅ̂ = (τ × n̂)/(I_a Ω)`.
   > That approximation is standard in the literature but diverges as `Ω → 0`, and the zero-spin
   > tumbling case is precisely the behaviour we need to be correct — it is what makes an unspun
   > throw fail on its own without a `WanderDeg` fudge.

4. **Exit or sink.** Contact ends when the rim clears the surface — that is a skip. It sinks when
   the centre passes below `−R` (fully submerged) or contact exceeds a substep budget. Both are
   physical outcomes, neither is a tuned threshold.

### What this deletes

Every one of these disappears, replaced by geometry and dissipation:

`OptimalAttackDeg`, `WorstQualityLossScale`, `ShallowIncidenceLossScale`, `SteepIncidenceLossScale`,
`MaxLiftShare`, `RollSteerRad`, `MinSkipSpeed`, `MinExitRiseSpeed` (all `SkipPhysics` consts) and
`EnergyLoss`, `LiftShare`, `MinLoss`, `SpinForStability`, `WanderPerBounce`, `SpinRetention`,
`RollFalloffDeg`, `MaxRollDeg`, `PitchSteepFalloffDeg`, `PitchShallowFalloffDeg`, `YawFalloffDeg`,
`MaxIncidenceDeg`, `ForgivenessPitchDeg`, `ForgivenessRollDeg`, `ForgivenessYawDeg`,
`SpinToleranceScale` (all `SkipConstants`).

`SkipConstants` is left with: `Gravity`, `WaterY`, `WaterDensity`, `LiftCoefficient`,
`DragCoefficient`, `SpeedScale`, `FixedStep`, `ContactStep`, `MaxContactSubsteps`, `PredictSeconds`,
`ReferenceSpeed`. Four of those are physical constants of water and one is presentation-only.

### The theoretical perfect stone

A **seventh rock ships in the catalog by default**, and it is the baseline every other rock is read
against. Its dimensions are not a design choice — they are **the experimental disc from the papers**,
so R1 and R2 are measuring the actual published apparatus rather than something merely similar.
When the solver reproduces the magic angle *for this stone*, that is the literature reproduced, not
an analogy to it.

- **Mesh:** a procedurally-built disc primitive (`PlaceholderArt`, the project's single material
  factory), not an FBX mesh. It genuinely is a perfect disc — a remeshed naturalistic rock would
  quietly misrepresent it. This also means it survives `RockSkipping/Rocks/Build Catalog from FBX`,
  which rebuilds the six from `RocksRemeshed.fbx`.
- **Selectable by players**, not verification-only. It is the honest control: a player who cannot
  out-skip the perfect stone knows the gap is the throw, not the rock.
- **Distinctly named and visually obvious** on the scoreboard and in the shore lineup. It must never
  read as a seventh naturalistic pebble.
- **Tinted by the player's colour like every other rock** — the standing rule that colour is player
  identity, never rock identity, is not suspended for this one.

Three existing constraints it has to satisfy, each already documented and each easy to break here:

1. `RockSkipping/Rocks/Validate Catalog` flags any rock with no mesh, no tuning, or a duplicate id —
   the reference stone needs a real mesh reference, a real `SkipTuning` asset, and its own id.
2. `Build Catalog from FBX` is idempotent and preserves hand-edits. It must **not** delete a rock it
   cannot find in the FBX. Verify this explicitly; it is the most likely way the stone silently
   vanishes on a later rebuild.
3. `Resources/` is a curated list — the stone's `RockDefinition` lives under `Assets/Rocks/` like the
   other six and is reached through the catalog, not added to `Resources/`.

### Emergent behaviours to verify, not implement

Sideways deflection under bank, decreasing skip spacing toward the end of a run ("pitter-patter"),
the 20° optimum, spin-dependent survival, and steeper-arrival-costs-more all fall out of the above.
**If any of them has to be added by hand, the model is wrong** — that is the diagnostic.

---

## Files

### Phase 1 — additive, gameplay untouched

| File | Change |
|---|---|
| `docs/plans/real-skip-physics.md` | **First action:** copy this plan into the repo (per your standing rule that plans live in `docs/`) |
| `Scripts/Game/Physics/DiscContact.cs` | **New.** Pure, static: wetted area, segment centroid, forces, torque for one substep. The one file that is entirely closed-form geometry, so it is the one file that is exhaustively unit-testable |
| `Scripts/Game/Physics/RigidDisc.cs` | **New.** Pure: disc inertia tensor, Euler rigid-body integration of `Attitude` + `AngularVelocity` |
| `Scripts/Game/Physics/SkipPhysics.cs` | Rewritten `Step` — flight phase unchanged, contact phase sub-steps through `DiscContact`/`RigidDisc`. **`Step`, `Predict`, `CreateLaunch` signatures preserved**, as is the `SkipStep { Bounced, ImpactSpeed, HasSunk }` contract |
| `Scripts/Game/Physics/SkipConstants.cs` | Strip the invented sections, add the physical ones. Keep `LoadOrWarn`/`CreateDefault` |
| `Scripts/Game/Physics/SkipTuning.cs` | Three multipliers → `Mass` (kg), `Radius` (m), `Thickness` (m) |
| `Scripts/Game/Physics/ReferenceStone.cs` | **New.** The theoretical perfect stone's SI dimensions as a `SkipTuning`, built in code with the paper cited in the file. Pure and asset-free, so the harness can use it with no catalog dependency — the same trick `SkipCurve.cs:258` already uses with `SkipTuning.CreateDefault()` |
| `Scripts/Verification/SkipReality.cs` | **New.** The reality-check harness (below), run against `ReferenceStone`. Writes `skip-reality.txt` at repo root, same pattern as `SkipCurve.Run()` |
| `Editor/ProjectSetup.cs` | Add `RockSkipping/Verify/Print Reality Check` beside the existing `Print Skip Curve` (`ProjectSetup.cs:132`) |
| `Tests/EditMode/DiscContactTests.cs` | **New.** Closed-form geometry tests — area and centroid against analytic values at known depths |
| `Tests/EditMode/SkipPhysicsTests.cs` | Keep all four existing tests (purity, non-mutation, termination + per-bounce energy loss, reference throw skips). They are exactly the right invariants and must survive the rewrite unchanged in intent |
| `docs/Findings-RealSkipPhysics.md` | **The deliverable.** Published values with citations, measured results, and the verdict on how punishing raw physics is |

`PhoneAttitude.cs` is **not** changed — it is already quaternion-based and already the single
conversion point. `RockSkipping.Physics.asmdef` is unchanged; the new files go in the same folder so
`Tests/EditMode/` reaches them and the purity boundary (`"references": []`) still holds.

### Phase 2 — flip gameplay over, only after you have read the findings

| File | Change |
|---|---|
| `Scripts/Game/RockController.cs:133` | `Launch` computes `FaceAngleDeg`/`RollDeg`/spin from `Attitude` + `AngularVelocity` for `RockLaunchedEvent`. Stepping loop (`:190-226`) untouched |
| `Scripts/Game/RockLauncher.cs:147` | `CreateLaunch` call — signature preserved, so likely no change beyond the spin argument's units |
| `Scripts/Game/AimPreview.cs:488` | Same; confirm predict cost per frame is still acceptable |
| `Scripts/Editor/RockCatalogBuilder.cs` | Seed `Mass`/`Radius`/`Thickness` from `mesh.bounds` on rebuild, preserving hand-edits — the pattern it already uses for display names and tints. `mesh.vertices` is unavailable (`isReadable: 0`), bounds only |
| `Assets/Rocks/Tuning/*.asset` | Six tuning assets take real dimensions |
| `Assets/Rocks/Rock_Reference.asset` + tuning | **New.** The perfect stone as a shipped, selectable catalog rock — its dimensions read from `ReferenceStone`, so the played stone and the measured stone cannot drift apart |
| `Scripts/Game/PlaceholderArt.cs`, `ShoreRocks.cs` | Procedural disc mesh for the reference stone, and its distinct presentation in the shore lineup |
| `Scripts/Game/RockCatalog.cs` | `Validate Catalog` must accept a rock whose mesh is procedural rather than from the FBX |
| `Scripts/Verification/SkipCurve.cs` | Rewrite the verdict bands (`:380-445`) against real release speeds. `ReferenceSwingSpeed` drops from 30 m/s to a real release speed, and `OptimalAttackDeg` stops being imported from `SkipPhysics` |
| `docs/4-systems/SkipPhysics.md`, `RockCatalogAndTuning.md`, `Assets/CLAUDE.md` | The architecture sections describing the energy-budget model and the three-multiplier rock split are then describing nothing that exists |

Nothing in `SfxDirector`, `WaterRipples`, `RockTrail` or `ScoreManager` changes — they read
`ReferenceSpeed` and the `SkipOccurredEvent`/`RockSunkEvent` payloads, all preserved.

---

## Verification

### Reality checks — `RockSkipping/Verify/Print Reality Check`

Each asserts a published result, with its citation printed beside it. **The published values are
in [`../Findings-RealSkipPhysics.md`](../Findings-RealSkipPhysics.md) §3 — take them from there.**

| # | Check | Expectation |
|---|---|---|
| R1 | **The magic angle emerges.** Sweep attack angle 0–45°, find the one minimising critical speed | ≈ 20°, and shallow — Clanet 2004's central result |
| R2 | Critical speed at the optimum for their experimental disc | Matches the published curve |
| R3 | Contact duration for a typical bounce | Order milliseconds |
| R4 | Energy lost per bounce | **Constant in absolute terms** (`W ≈ −μMgε`), NOT a fixed fraction of KE. This is what produces R6 |
| R5 | Skip count vs release speed | `N_c = V²/(2gμε)`: 6 at 5 m/s, 17 at 8 m/s, 38 at 12 m/s (world record) |
| R6 | **Skip spacing decreases toward the end of a run** | Geometric decay — the pitter-patter, the thing muscle memory knows best |
| R7 | Spin threshold, and the spin ceiling | `Ω ≫ √(g/R)` ≈ 14 s⁻¹; and `N_c^spin = RΩ²/g` — 5 bounces at 5 rev/s, 38 at 14 rev/s |
| R8 | Energy invariant | Exit KE < entry KE on **every** bounce, every case (guards the explicit integrator adding energy) |
| R9 | Integrator convergence | Halving `ContactStep` changes outcomes by < 1% — proves results are physics, not step size |
| R10 | **The six naturalistic rocks measured against the perfect stone** | Each rock's skips and distance as a fraction of the reference stone's, at identical throws. No pass/fail — this is the readout that says how much of the gap is the rock and how much is the throw |

### Two structural requirements the sources impose

Both come out of `Findings-RealSkipPhysics.md` and neither was visible when this plan was drafted.
Getting either wrong makes every check above meaningless even if it passes.

1. **A run ends at `min(N_slowdown, N_spin)`.** Speed-limited bounces grow as `V²` without bound;
   spin-limited bounces do not depend on speed *at all*. Both ceilings must exist, and the smaller
   one wins (Bocquet §VII). If only the energy ceiling is modelled, throwing harder buys unlimited
   skips — which is the failure this whole rewrite is meant to end, reappearing in a new costume.
2. **Per-bounce energy loss is a constant absolute quantity, not a fraction of kinetic energy.**
   Constant loss gives `V² ∝ (N_c − N)`, and that is *precisely* what makes skip spacing collapse as
   `√(1 − N/N_c)` at the end of a run. A fractional loss decays geometrically and can never produce
   the pitter-patter. Do not reintroduce a fractional loss under any name.

R1–R7 all run against the **theoretical perfect stone**, so they are reproducing the papers'
apparatus directly. R9 has no analogue in the current code and is the one that makes the rest
trustworthy. R10 is the number that makes rock tuning a decision rather than a guess.

### Existing tests

`Tests/EditMode/SkipPhysicsTests.cs` and `PhoneAttitudeTests.cs` must pass unchanged in intent —
purity, non-mutation, termination, and per-bounce energy loss are all still exactly the right
invariants and the rewrite must not weaken them.

### Running it

The Unity Editor holds an exclusive project lock, so there is no out-of-band compile check — verify
via the open Editor's Console, per `Assets/CLAUDE.md`. Both the reality check and the skip curve are
pure and need no Play mode or scene.

**Step cards for every command will be produced at implementation time, run first, and handed over
in the standard format.** The relay tests (`cd relay && npm test`) are untouched by this work.

---

## Risks

1. **Raw physics may be too punishing for a party game.** Expected, and the entire point of measuring
   before flipping — the findings doc answers it with numbers, and an assist layer becomes an
   informed, separate decision.
2. **Real skip counts may not match the current game's feel.** The curve tool currently targets a
   band at an unreal 30 m/s reference. Real release speeds are much lower; skip counts will move.
3. **`AimPreview` cost.** ~100 substeps/bounce × ~20 bounces ≈ 2 000 extra substeps per predict, per
   frame. Cheap in principle, but there is a recent commit tuning aim preview performance
   (`8402f11`, `57a3a20`), so measure rather than assume. `MaxContactSubsteps` caps the worst case.
4. **Explicit integration can add energy.** Mitigated by R8 and R9 and by a small `ContactStep`.
5. **Recalled physics constants.** Mitigated by step 0 — fetch the papers, cite every number.
6. **The perfect stone may simply be the best rock**, making the other six pointless to pick. That is
   a real possibility and R10 measures it. It is a game-design consequence to decide on once the
   numbers exist, not something to pre-empt by handicapping the reference stone — a handicapped
   reference stone would stop being a reference.
7. **The reference stone silently disappearing on the next `Build Catalog from FBX`.** It is the one
   catalog entry with no FBX counterpart. Explicitly re-run the rebuild after adding it and confirm
   it survives.

## Out of scope

Air drag, water waves and surface disturbance from prior skips, buoyancy during the sink,
non-circular stone footprints, an assist/forgiveness layer, and any change to the phone-side motion
pipeline or the relay.
