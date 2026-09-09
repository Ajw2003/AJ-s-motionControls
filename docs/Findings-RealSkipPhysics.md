# Findings — the real physics of stone skipping

Step 0 of [`plans/real-skip-physics.md`](plans/real-skip-physics.md): the published values the new
solver is built from and validated against. **Every number here is quoted from a paper, not
recalled.** Anything the harness asserts must cite a row in this document.

Both primary sources are open-access on arXiv and were read in full:

| Ref | Paper | Source |
|---|---|---|
| **[B]** | L. Bocquet, "The physics of stone skipping", *Am. J. Phys.* **71**(2), 150–155 (2003) | [arXiv:physics/0210015](https://arxiv.org/abs/physics/0210015) |
| **[C]** | C. Clanet, F. Hersen, L. Bocquet, "Secrets of successful stone-skipping", *Nature* **427**, 29 (2004) | [doi:10.1038/427029a](https://www.nature.com/articles/427029a) |
| **[N]** | S. Nagahiro, Y. Hayakawa, "A theoretical and numerical approach to 'magic angle' of stone skipping" (2004) | [arXiv:physics/0411125](https://arxiv.org/abs/physics/0411125) |

[C] is paywalled; its experimental parameters and results are quoted here via [N], which reproduces
them by SPH simulation and compares against the published domains directly.

---

## 1. Corrections to the plan as written

### 1.1 The friction force acts along the stone, not against the velocity

The plan specified `F_drag = ½·ρ·C_D·v²·A_wet·(−v̂)`. **That is wrong.** [B] Eq. (1):

```
F = ½·C_l·ρ_w·V²·S_im·n̂  +  ½·C_f·ρ_w·V²·S_im·t̂
```

> "The force can be adequately decomposed into a component along the direction of the stone (that
> is, along **t**) and a component perpendicular to it (that is, along **n**)." — [B] §II

`n̂` is the stone's face normal and `t̂` lies **in the stone's own plane**. Water skims along the
face; it does not resist along the flight path. At the shallow angles skipping happens at, the two
directions are close — which is exactly why the error would have been invisible in play while
quietly poisoning every calibration. `C_l` and `C_f` are the lift and friction coefficients, and
[B] takes both **constant and independent of tilt and incidence**.

### 1.2 The normal force goes as the normal velocity, not the total speed

[B] uses `V²`. [N]'s ODE model — the one **shown to reproduce the magic angle** — instead assumes:

> "(i) Hydrodynamic pressure p acting from water is proportional to **(v · n)²**, where v is the
> speed of the body and n is the unit vector to the surface of the disk. (ii) For the part of the
> surface facing air, there is no hydrodynamic force." — [N]

This matters more than it looks. **[B]'s constant-coefficient `V²` form cannot produce a magic
angle** — tilt enters it only through `C = C_l·cosθ − C_f·sinθ`, which is monotonic in θ and has no
interior optimum. The 20° result needs the `(v·n)²` dependence together with the real wetted-area
geometry. Since reproducing 20° is check R1, and R1 is the whole justification for the rewrite:

> **Use the [N] / Birkhoff form: normal pressure ∝ (v·n)², no force on the dry face.** [B] remains
> the source for the geometry, the dissipation argument, the stability criterion and every closed-
> form estimate below — those are what R2–R7 assert.

### 1.3 The two corrections are not independent — combining them naively sinks every throw

§1.1 and §1.2 were written as separate fixes. **They are not compatible as stated, and the
combination — `(v·n̂)²` lift plus [B]'s `V²` friction with `C_f ≈ 1` — sinks the stone at every
tilt and every speed.** This was caught before it reached the Editor by re-implementing the
contact model outside Unity: **`tools/physics/verify-disc-contact.py`**, which is checked in and
re-runnable.

The reason is dimensional. At the shallow angles skipping happens at, `(v·n̂)² ≪ V²`, so a `V²`
friction term dwarfs a `(v·n̂)²` lift term and the stone ploughs in. Measured, sweeping tilt
5–60° at 15° incidence:

| `C_f` | Result |
|---|---|
| **0.00** | optimum **30°**, `v_min` **4.44 m/s** |
| 0.05 | optimum 35°, `v_min` 5.36 m/s |
| 0.10 | optimum 35°, `v_min` 6.52 m/s |
| 0.25 | optimum 40°, `v_min` 16.22 m/s |
| 0.50 | **never bounces at any tilt or speed** |
| **1.00** | **never bounces at any tilt or speed** ← the value §1.1 originally specified |

**Resolution: the model is pressure-only.** `C_l` from the `(v·n̂)²` law, and **`C_f = 0`**. This is
Birkhoff's model as [N] uses it, and it is self-consistent. The stone still decelerates — a tilted
disc's normal force already has a backward component — so nothing is lost by dropping the separate
friction term. `C_f` is retained in the code as a parameter, documented as **breaking the model
above ~0.1 rather than tuning it**.

`§1.1's` claim that friction acts along `t̂` rather than `−v̂` remains correct as a statement about
[B]. It is simply moot here, because the coefficient is zero.

### 1.4 Verified: only the `(v·n̂)²` law has an interior optimum

The claim in §1.2 — that [B]'s `V²` form cannot produce a magic angle — was an analytical
assertion when written. It has now been tested, and it holds:

| Force law | `v_min` vs tilt | Magic angle? |
|---|---|---|
| [B], both ∝ `V²` | **monotonic**: 2.12 → 2.94 → 3.88 → 5.19 → 7.51 → 13.86 m/s across 5–30° | **No** — no interior minimum |
| [N], pressure-only `(v·n̂)²` | **U-shaped**, minimum at 25–30° | **Yes** |

The `(v·n̂)²` law also reproduces [N] Fig. 4's qualitative result that the optimum *falls* as
incidence rises — 30° at β = 10° and 15°, 25° at β = 20°.

**The optimum lands at 25–30°, not the published 20°.** Do not treat that gap as settled. The
verification script is 2-D, holds attitude fixed through the collision, ignores spin and thickness,
and uses a coarse full-immersion sink criterion; [N] additionally fits an inflection parameter
`θ̂ = 2.6` to land on 20°. The structure is right and the magnitude is close; **R1's tolerance
should be set to accept 20–30° on the first pass**, and narrowing it is a later, separate piece of
work once live attitude and spin are in.

---

## 2. The model

### Wetted area — [B] Eq. (9)

For a circular stone of radius `R`, immersed to depth `|z|` at tilt `θ`, with `s = |z|/sinθ`:

```
S_im(s) = R² · [ arccos(1 − s/R) − (1 − s/R)·√(1 − (1 − s/R)²) ]
```

This is algebraically identical to the form in the plan (`R²·acos(d/R) − d·√(R²−d²)` with `d = R−s`).
**The plan's geometry was correct and needs no change.**

### Characteristic frequency and contact time — [B] Eqs. (4), (6)

The collision is a harmonic oscillator in the immersed depth `z`:

```
ω₀² = C·ρ_w·V_x0²·a / (2·M·sinθ)        where C = C_l·cosθ − C_f·sinθ
z(t) = −g/ω₀² + (g/ω₀²)·cos(ω₀t) + (V_z0/ω₀)·sin(ω₀t)
t_coll ≈ π/ω₀
```

### Angular dynamics — [B] Eq. (20) and Appendix A

Full Euler equations, with axis `1` along `y` (perpendicular to `n̂` and `t̂`), `2` along `n̂`, `3`
along `t̂`, `I₁ = I₃ ≡ J₁`, `I₂ ≡ J₀`, torque from the lift component only:

```
θ̈ + Ω²(θ − θ₀) = M_y / J₁        with  Ω = [(J₀ − J₁)/J₁]·ω_spin
```

The torque `M_y = OP × F_lift`, where **P is the centroid of the immersed area** — confirming the
plan's centre-of-pressure treatment. [B] Eq. (A1) is the standard Euler set, so the plan's
instruction to integrate the full equations rather than the fast-spin approximation is consistent
with the source and strictly safer at low spin.

---

## 3. Published numbers — what the harness asserts

[B]'s standard stone throughout: **M = 0.1 kg, a = 0.1 m diameter (R = 0.05 m), ρ_w = 1000 kg/m³,
θ ≈ 10°**. [B] quotes `C_l ≈ C_f ≈ 1`; we use `C_l = 1`, **`C_f = 0`** — see §1.3, this is not
optional.

| # | Quantity | Published value | Source |
|---|---|---|---|
| R1 | **Magic angle** — tilt minimising the speed required to bounce | **≈ 20°** | [C], title result |
| R1b | Its sensitivity to incidence angle β | falls as β rises, but only **~15% for β up to 40°** — "remains around 20° for the ordinal incident angle" | [N] Fig. 4 |
| R2 | Critical speed, **lift** criterion (weight vs lift) | `V_c = √(16Mg/(Cρ_w a²)) / √(1 − 8M·tan²β/(a³Cρ_w sinθ))` ≈ **0.71 m/s** | [B] Eq. (14) |
| R2b | Critical speed, **dissipation** criterion — **the binding one** | `V_c = √(2μgε)` ≈ **2 m/s** | [B] Eq. (19) |
| R2c | Clanet's experimental disc | specific gravity **2.7**, aspect ratio **9.1**, spin **65 rev/s**, test speed **3.5 m/s** | [N] |
| R3 | Contact time | `t_coll ≈ π/ω₀` | [B] Eq. (6) |
| R4 | Energy lost per collision | `W ≈ −μ·M·g·ε`, **independent of the incoming speed** | [B] Eq. (17) |
| R4b | Its two constants, standard stone | `μ = C̃/C ≈ **1.4**`, `ε = π√(2M·sinθ/(C·ρ_w·a)) ≈ **13 cm**` | [B] Eq. (18) |
| R5 | Bounces from slowdown | `N_c = V_x0²/(2gμε)` → **6** at 5 m/s, **17** at 8 m/s, **38** at 12 m/s | [B] Eq. (23) |
| R6 | **Skip spacing** | `X[N] = X₀·√(1 − N/N_c)`, `X₀ = 2V_x0·|V_z0|/g` ≈ **2.25 m** at 8 m/s | [B] Eq. (24) |
| R7 | Minimum spin for stability | `Ω₀ ≫ √(g/R)` ≈ **14 s⁻¹** for a 10 cm stone — "larger than a few revolutions per second (≈ 2 Hz)" | [B] Eq. (21) |
| R7b | Bounces from angular destabilisation | `N_c = R·Ω₀²/g` → **5** at 5 rev/s, **38** at 14 rev/s | [B] Eq. (25) |

### The structural result — R5 and R7b are both ceilings, and the answer is the smaller

> "The maximum number of bounces is therefore given by the **minimum of the two previous
> estimates**, in Eqs. (23) and (25)." — [B] §VII

Speed-limited bounces grow as `V²` without bound. Spin-limited bounces **do not depend on speed at
all**. So past a certain point throwing harder buys nothing, and only spin does:

> "even if the initial velocity of the stone is very large … the stone will be stopped by angular
> destabilization after N_c^spin bounces. In other words, the initial 'kick' that puts the stone in
> rotational motion is a key factor for a good throw." — [B] §VII

`X[N] = X₀√(1 − N/N_c)` is the pitter-patter, and [B] names it: spacing falls slowly at first, then
collapses as `N → N_c` because of the square root. [B] calls it **"pitty-pat"** and cites
stone-skipping competitions. R6 is therefore not a nice-to-have — it is a published, named,
observable signature, and the one item on this list that muscle memory knows directly.

---

## 4. What this means for the game

### 4.1 The game's reference speed is roughly 3× reality

`SkipCurve.ReferenceSwingSpeed` is **30 m/s**. In [B], **12 m/s is the world record** (38 bounces,
J. Coleman-McGhee). The paper's worked throws run **5–12 m/s**. The current solver's "25 skips at
30 m/s" is a well-tuned answer to a question reality does not ask.

`SkipConstants.SpeedScale` is the one honest place to absorb this: the phone measures wrist speed,
and the mapping to a release speed must land a hard human throw near **10–12 m/s**, not 30.

### 4.2 Skip count becomes spin-governed, and that is the point

At 12 m/s the slowdown ceiling is already 38. At 30 m/s it is ~250 — so under real physics **the
speed ceiling stops binding almost immediately and spin becomes the only thing limiting a run**.
`N_c^spin = R·Ω₀²/g` needs ~14 rev/s to reach 38 bounces.

This inverts the current game. Today `EnergyLoss` is documented as "THE one dial for how many skips
a good throw gets". Under real physics **the wrist flick is the dial**, and throwing harder mostly
buys distance rather than count. That is also what every stone skipper's muscle memory already says
— which is the whole reason for doing this.

### 4.3 Per-bounce energy loss does not scale with speed

[B] Eq. (17): `W ≈ −μMgε`, independent of `V_x0`. The current solver takes a **fractional** loss
(`EnergyLoss = 0.20` of kinetic energy per bounce). These are qualitatively different laws. A
constant absolute loss gives `V² ∝ (N_c − N)`, which is precisely what produces the `√` spacing
collapse. A fractional loss decays geometrically and **can never** produce pitty-pat.

This single distinction is most of why the current game cannot feel like skipping a stone no matter
how its 23 knobs are set.

---

## 5. Open items for implementation

1. `C_l` and `C_f` are "≈ 1" and assumed constant — [B] flags this as the model's weakest assumption
   (note 6: `C_l ∼ h/a` for a thin ellipsoid, with a large prefactor). Expose both as
   `SkipConstants` fields defaulting to 1.0, documented as **the published estimate, not a feel knob**.
2. [N] fits an inflection parameter `θ̂ = 2.6` to its own SPH runs. If R1 does not land at 20°,
   suspect the force law before suspecting the geometry.
3. [B] assumes tilt `θ` is constant through a collision and derives stability separately. We
   integrate attitude live instead, so R1–R7 test a *stronger* model than the one that produced these
   closed forms — expect agreement in order of magnitude and in trend, not to three digits. Set the
   tolerances accordingly, and record in the harness what they were set to.

---

## 6. Measured: what the phone actually produces

Answering "is 30 m/s the right reference?" from 124 real recorded throws in `rock-throws.csv`,
which `Verification/RockTelemetry` had already been collecting. Re-runnable at any time:

```
python "C:/Users/aj/Desktop/GameDev/RockSkipping/tools/physics/analyse-throws.py"
```

### 6.1 Speed — the 30 m/s reference is the median, not a hard throw

| | swingSpeed (raw m/s off the phone) |
|---|---|
| median | **26.1** |
| p90 | 46.5 |
| p95 | **53.0** |
| max | 74.8 |

`SkipCurve`'s 30 m/s reference sits at roughly the **52nd percentile** — it has been characterising
a *median* throw as "a good hard throw" all along. A hard throw is ~53 m/s.

Taking p95 as "hard" (the max is noisy), the calibration is:

| Target release speed | `SpeedScale` | Meaning |
|---|---|---|
| 12 m/s | **0.227** | world record, 38 skips |
| 10 m/s | **0.189** | very strong throw |
| 8 m/s | **0.151** | good adult throw, 17 skips |

`SpeedScale` is currently **1.0**, so it is wrong by roughly a factor of five. This part is
straightforward: pick a target and the number follows.

### 6.2 Spin — this is the real problem, and no Unity-side tuning can fix it

Bocquet Eq. (25) gives a bounce ceiling that **does not depend on throw speed at all**:
`N_c = R·Ω²/g`. Measured against recorded spin:

| Source | rev/s | Max bounces |
|---|---|---|
| published: world record | 14.00 | 39.4 |
| published: decent spin | 5.00 | 5.0 |
| published: minimum for stability | 2.00 | 0.8 |
| **MEASURED: best of 124 throws** | **3.83** | **3.0** |
| MEASURED: p90 | 2.78 | 1.6 |
| MEASURED: median | 1.45 | 0.4 |

*(The formula reproduces Bocquet's own worked numbers — 14 rev/s → 39.4 against his stated 38,
5 rev/s → 5.0 against his stated 5 — so the measured rows can be trusted.)*

**Under raw physics, the best throw ever recorded on this game would skip 3 times, and a median
throw would not skip at all.**

This is a **units and semantics gap, not a tuning gap**. `spinRate` measures how fast the phone
rotates about its own screen normal — a wrist twist. A real skipper spins the stone with a finger
flick off the index finger, and the *stone* leaves at ~14 rev/s while the *wrist* never approaches
that. The phone is measuring the wrong body.

No value of any Unity-side constant can recover information the phone never captured. The options
are a documented physical transfer model (finger tangential speed ÷ stone radius), a controller-side
gesture change that actually captures a flick, or accepting a low spin ceiling as the honest raw
baseline. **That decision is deliberately not made here** — it is recorded so it is made on the
numbers.

### 6.3 The current solver is running away

Recorded distances: median 55.4 m, **max 783.3 m**. `SkipCurve`'s own verdict asserts "full power
stays on the drawn water", and the drawn water is nowhere near 783 m. The existing energy-budget
model is producing runaway throws in real play, which is independent corroboration for the rewrite.

### 6.4 Spin transfer — decided, implemented, measured

Decision: **the physical transfer model** (`Assets/Scripts/Game/Physics/SpinTransfer.cs`).

The old code treated phone deg/s as stone deg/s **1:1**. That identity is invented, not measured:
a phone and a pebble are different sizes, so equal angular rates are not equal throws. What is
conserved at release is the **tangential speed at the grip point** — the stone leaves the finger,
and its spin follows from its own radius:

```
Ω_stone = ω_phone · (r_grip / R_stone)      r_grip = 0.075 m (half a phone), R_stone = 0.05 m
```

One ratio of two measurable lengths. No free parameter. Measured effect on the same 124 throws:

| Throw | raw rev/s | stone rev/s | Max bounces |
|---|---|---|---|
| best of the set | 3.83 | 5.75 | **6.7** |
| p90 | 2.78 | 4.17 | 3.5 |
| median | 1.45 | 2.18 | 1.0 |

So the best recorded throw goes from **3 bounces to about 7**, and a median throw crosses from
"does not skip" to "skips once". Real, and still far from the 38 a world-record throw gets.

**The residual is quantified rather than hidden.** Grip tangential speed on the best throw is
**1.81 m/s** against a real finger flick's **4.4 m/s** — the gesture captures **41%** of a real
flick. That remaining 59% is a controller-side measurement problem: a wrist twist is not a finger
flick, and the phone is not currently asked to perform one. Inflating `GripRadius` past a real
phone's half-length would turn a measured constant back into a feel knob, which is the exact
failure this rewrite exists to end.

### 6.5 Still open: `SpeedScale`

Deferred deliberately until R1–R10 produce numbers from the new solver, then chosen so skip counts
land where they should. The candidates are in §6.1; **`SpeedScale` remains 1.0 until then**, which
is known-wrong by roughly 5× and must not be left that way.

---

## 7. Headless calibration — one bug fixed, one blocker found

Built `tools/physics/HeadlessSolver/`: a dotnet console app that **links the real
`DiscContact.cs` / `RigidDisc.cs` / `SpinTransfer.cs` / `ReferenceStone.cs`** and runs them
outside Unity. The Editor's project lock no longer gates physics iteration — a full parameter
sweep takes seconds instead of one menu click per data point.

UnityEngine.CoreModule.dll cannot be used for this: `Quaternion.Inverse`, `FromToRotation` and
friends are native ECalls that throw outside the player. `UnityShim.cs` supplies managed
equivalents, and `Quaternion.SelfTest()` pins them to Unity's conventions (rotation sense, cross
product handedness, composition order) before any number is trusted.

### 7.1 FIXED — the water was creating energy

`DiscContact.Evaluate` computed lift as `½·ρ·C_l·(v·n̂)²·A` along `+n̂`. Squaring loses the
sign, so lift was applied **even when the face was moving away from the water**. Once the
attitude tumbled far enough for `n̂` to lean into the velocity, every substep did positive work.

Measured before the fix: a **12 m/s throw left its first contact at 42.66 m/s**.

The fix is [N] assumption (ii) — no hydrodynamic force on the face turned away from the water —
implemented as an explicit `normalSpeed < 0` guard. Lift now always opposes penetration, so its
work is always negative and the energy invariant is structural. Verified: 12 m/s in, 10.74 m/s out.

This is the same class of bug the previous solver shipped with, and it is exactly what R8 exists
to catch.

### 7.2 Calibrated — `C_l` is ~12–32, not 1

Bocquet's `C_l ≈ 1` belongs to his `V²` model. With the `(v·n̂)²` law, `C_l = 1` gives far too
little lift: contact lasts 20–60 ms against his own `t_coll = π/ω₀ ≈ 7 ms`, and the stone wallows
in rather than planing off. [B] flags `C_l` as its weakest assumption and [N] fits an equivalent
parameter, so this is the one honest place to absorb the difference.

Raising it also moves the **magic angle toward the published 20°**, which is an independent check
it is not just a fudge:

| `C_l` | magic angle |
|---|---|
| 1 | 27.5° |
| 4 | 25.0° |
| 16 | 22.5° |
| 32 | 22.5° |
| 64 | 5.0° (collapses) |

**`C_l` ≈ 16–32.** Above ~48 the magic angle collapses and the calibration is invalid.

### 7.3 BLOCKER — the contact torque over-destabilises the stone

With the full model, skip count is **erratic and non-monotonic in throw speed**: 47 skips at one
setting, 2 at its neighbour, and a p95 throw frequently out-skipping the hardest throw. Throwing
harder doing *worse* is both unphysical and unacceptable in a game.

Isolated by zeroing the contact torque. The model immediately became smooth and monotonic:

| SpeedScale | weakest | median | hard | strongest |
|---|---|---|---|---|
| 0.23 | 0 | 3 | 8 | 13 |
| 0.35 | 0 | 5 | 14 | 22 |
| **0.50** | **0** | 9 | 21 | **32** |
| 0.75 | 0 | 15 | 37 | 22 (inverts) |

So the torque — not energy, not lift, not the spin transfer — is what ends runs early and
randomly. **Zeroing it is a diagnostic, not a fix**: it deletes the gyroscopic physics that makes
spin meaningful, which is half of what the rewrite is for.

**Leading hypothesis, not yet tested.** Contact lasts 20–60 ms against a published ~7 ms. The
torque is therefore acting for 3–8× too long, which is plenty to flip a stone that would really
have left the water already. The over-long contact and the tumbling may be one fault, not two —
and if so, shortening the contact fixes both. Worth testing before adding any damping term, which
would be a new invented mechanism.

### 7.4 Status against the target

Target: strongest possible throw = 38 skips, weakest = outright sink.

- **Weakest sinks: ACHIEVED**, robustly, across every configuration tested.
- **Strongest = 38: NOT ACHIEVED.** Best with the full model is erratic; best with torque
  disabled is 32 at `C_l = 12`, `SpeedScale = 0.50`, `R = 5 cm`.

The gap is §7.3, and it is a physics question rather than a tuning one. Tuning around it would
mean picking numbers that flatter a model known to be wrong — which is what this rewrite exists
to stop.

---

## 8. Diagnosis complete — the model rings, and the skip counter lies

Ran the headless harness through hypothesis tests rather than more sweeping. Two hypotheses
killed, one confirmed.

### 8.1 KILLED — it is not integrator divergence (R9 passes)

Angular accelerations in contact reach ~1e6 rad/s², so a 20 µs step changes ω by ~20 rad/s
against an 88 rad/s spin. That looked like guaranteed divergence. It is not — across a **200×
range** of contact step:

| step µs | 20 | 10 | 5 | 2 | 1 | 0.5 | 0.25 | 0.1 |
|---|---|---|---|---|---|---|---|---|
| skips | 19 | 19 | 19 | 18 | 18 | 17 | 23 | 23 |
| distance m | 7.3 | 7.3 | 7.3 | 7.1 | 7.2 | 7.2 | 7.2 | 7.2 |

Distance is rock steady. **R9 passes**; the results are physics, not step size. `ContactStep`
can stay at 20 µs, which matters for the aim preview's per-frame cost.

### 8.2 KILLED — it is not the contact being too long

The torque *impulse* is `r_cp × Δp`: it depends on the momentum change and the lever arm, not on
how long contact lasts. Shortening the contact cannot reduce it. §7.3's hypothesis was wrong.

### 8.3 CONFIRMED — the stone flips, and the "skips" are surface chatter

Per-bounce trace, 12 m/s, 14 rev/s, `C_l = 16`:

```
skip  gap m   speed  contact ms  tilt deg
   1   0.50   11.21       43.23      45.8
   2   0.02    8.55        0.96      31.0
   3   0.02    8.55        1.30     147.6   <- inverted
   4   0.02    8.47        0.92      32.1
   5   0.02    8.47        1.18     146.7
```

After one real bounce the tilt oscillates **32° ↔ 147°** — the stone is flipping over and back —
and the gaps collapse to 2 cm. 19 "skips" over 7.3 m is 0.38 m per hop against Bocquet's
`X₀ = 2v_x|v_z|/g ≈ 5.9 m`. It is vibrating on the surface, not skipping.

**Two consequences.**

1. **The skip counter is wrong.** "Left the water going up" counts surface chatter as skips. Every
   full-model skip count in §7 is inflated and should not be trusted. A skip needs a minimum hop —
   Bocquet's `X₀` gives a principled floor.
2. **The torque is over-driven, not mis-signed.** The wetted region is the *trailing* edge, so lift
   there pitches the nose down — correctly restoring. Overshooting past level into inversion is
   the signature of a restoring torque with **no damping**, not of a destabilising one.

### 8.4 The bind, stated plainly

- The `(v·n̂)²` law is required for the magic angle (§1.4) but gives ~4× too little lift at
  `C_l = 1`, so the stone sinks instead of planing.
- Fixing that needs `C_l ≈ 16`, which also multiplies the **torque** by 16.
- The model has **no rotational damping**, so the now-16× restoring torque rings and inverts the
  stone after one bounce.

A real plate rotating in water is heavily damped by added mass; this model is frictionless in
rotation. That is a genuine missing physical term, not a tuning knob — but adding it is a
modelling decision, and it is where the work stops pending a call.

### 8.5 Honest status

**Not achieved.** Weakest-throw-sinks holds robustly. Strongest-throw-equals-38 does not, and the
numbers that looked closest (§7.3, torque disabled) were both physically hollow — torque removed —
and inflated by the miscounting in §8.3.
