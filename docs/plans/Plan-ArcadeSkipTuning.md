# Tune the arcade skip model: cap runaway throws, raise hop height

## Context

The working tree (branch `ArcadePhysics`, tip `15a7557`) runs the **arcade energy-budget**
model in `Assets/Scripts/Game/Physics/SkipPhysics.cs` — the fluid-dynamics rewrite from
`6d8956b` was reverted one commit later. Two feel problems on the current tuning:

1. **Extremely far, high throws.** Launch is `swingSpeed * SpeedScale` with the phone's raw
   tilt as the toss angle (`SkipPhysics.cs:186-197`), and flight is *pure ballistic* — no drag
   anywhere (`SkipPhysics.cs:230-231`). Nothing caps launch speed or launch pitch, so range is
   `v²·sin(2θ)/g` unbounded. A hard swing at a steep tilt lobs like a mortar. Neither existing
   global (`SpeedScale`, `Gravity`) can fix this without equally shrinking weak throws, so
   the fix is a new outlier-only clamp.

2. **Flat, low skips.** Hop height comes only from `rise = speedAfter · √liftShare`
   (`SkipPhysics.cs:317`), and `LiftShare` is authored at **0.03**, then scaled down further by
   `quality · cos(roll)` (`:313-315`). Apex is proportional to `liftShare`, so this is a
   direct, cheap dial.

Decisions taken with the user: **add launch clamps** rather than retune `SpeedScale`/`Gravity`
or add drag; and **raise `LiftShare` only** — the three forgiveness bands stay at their harsh
authored `0`, so the game does not get easier as a side effect of getting bouncier.

## The levers, and why these ones

| Goal | Lever | Where | Now → target |
|---|---|---|---|
| Cap far throws | `MaxLaunchSpeed` (new) | `SkipConstants` + `CreateLaunch` | — → `20` m/s |
| Cap high throws | `MaxLaunchPitchDeg` (new) | `SkipConstants` + `CreateLaunch` | — → `30`° |
| Raise hops | `LiftShare` | `Resources/SkipConstants.asset:19` | `0.03` → `0.10` |
| Stop the ceiling biting silently | `MaxLiftShare` (const → field) | `SkipPhysics.cs:143` → `SkipConstants` | `0.5` const → `0.5` field |

`LiftShare` is *already* an exposed, ranged, tooltipped field — the thing that was hidden is
the ceiling clamping it (`SkipPhysics.cs:143`, applied at `:315`). At `LiftShare 0.03` it never
binds, but it starts binding as soon as hop height is pushed, and it would do so invisibly.
Promoting it is the same argument `SkipConstants.cs:76-79` already makes for the failure
thresholds: a number that decides feel must be changeable without a recompile.

Rejected, and worth not re-deriving later: `SpeedScale` and `Gravity` are linear on every
throw, so they trade outlier control for dead-feeling soft throws. `VerticalMultiplier` is
per-rock and capped at 1.3, and all six rocks ship 1.0 by design (`Assets/CLAUDE.md`) — it is
a flavour knob, not a global height dial. `OptimalAttackDeg = 20f` (`:121`) stays a const: its
own comment argues it is a fact about skipping, and the tunable form of it is the forgiveness
bands. `MinExitRiseSpeed` and the three loss-shape scales stay const for now.

## Changes

### 1. `Assets/Scripts/Game/Physics/SkipConstants.cs` — two new fields under `[Header("Launch")]`

Add after `SpeedScale` (`:26`), following the existing `0 disables` convention already used by
`ForgivenessPitchDeg` (`:107-110`) so the code default is a no-op and only the asset opts in:

- `public float MaxLaunchSpeed = 0f;` — hard ceiling on launch speed in m/s, `0` = no cap.
- `public float MaxLaunchPitchDeg = 0f;` — ceiling on the *upward* component of the toss angle
  in degrees, `0` = no cap.

Both need `[Tooltip]` text in the style of the surrounding fields — the tooltips are explicitly
this project's most complete constants reference (`Assets/CLAUDE.md`). Say in them that these
are outlier clamps, not scales: a throw under the cap is untouched.

Also add, under `[Header("Bounce")]` next to `LiftShare`:

- `[Range(0.05f, 1f)] public float MaxLiftShare = 0.5f;`

Note the convention break, and put it in the tooltip: unlike the launch clamps, **`0` is not
"disabled" here** — it would mean no lift at all and every stone plunks. The `[Range]` floor of
`0.05` is what prevents that, matching how `EnergyLoss`/`MinLoss`/`SpinRetention` are guarded.
The tooltip must also carry across the reasoning from the XML doc comment being deleted
(`SkipPhysics.cs:141-143`): it sits well below 1 because a bounce that sent everything vertical
would stop the stone dead in the air.

### 2. `Assets/Scripts/Game/Physics/SkipPhysics.cs` — apply them in `CreateLaunch` (`:180-216`)

Clamp `speed` at `:186` and `pitch` at `:195-196`, both guarded on `> 0f`.

**The one subtle decision:** clamp only the *velocity* pitch used to build the launch vector.
Leave `SkipState.FaceAngleDeg` (`:206`) reading the **raw** `PhoneAttitude.FaceAngleDeg`, so
the bounce still judges the pose the player actually held against `OptimalAttackDeg` (`:121`,
via `:273-276`). A steeply-tilted phone then throws a tamed arc but is still scored as the bad
pose it was — capping the visual arc must not launder a bad throw into a good one.

The comment block at `:184-196` currently asserts *"Where the stone is pointing IS where it
goes"* and *"no hidden neutral-pose offset"*. A clamp is a real deviation from that; rewrite
those lines to state the clamp and the raw-`FaceAngleDeg` split above. This repo's ~30% comment
density is deliberate and load-bearing (`Assets/CLAUDE.md`) — do not leave the stale claim.

Then delete the `MaxLiftShare` const and its XML doc (`:141-143`) and read it off `constants`
at the clamp site (`:313-315`). That is the only line in `Step` that changes, and it is a
substitution, not a behaviour change.

The energy invariant documented at `:104-111` is untouched: clamps only lower the initial
state, and `LiftShare` is a split of an already-lossy budget. Worth confirming while editing
`:313-317` that this still holds — `MaxLiftShare` is now authorable up to `1`, and at exactly
`1` the horizontal term `√(1-liftShare)` goes to zero. The invariant survives (magnitude is
still preserved and `lossFraction ≥ MinLoss > 0`), but the stone would stop dead horizontally,
which is why the tooltip has to say so.

### 3. `Assets/Resources/SkipConstants.asset` — three authored values

- `LiftShare: 0.03` → `0.1` (line 19). Apex scales with `liftShare`, so this is ~3.3× hop
  height; the cost to forward carry is `√(1-liftShare)`, i.e. 0.985 → 0.949, about 4% per
  bounce. Within the field's `[Range(0.005, 0.2)]`.
- `MaxLaunchSpeed: 20` (new key).
- `MaxLaunchPitchDeg: 30` (new key).
- `MaxLiftShare: 0.5` (new key) — same value the const held, so this change alone is a no-op.
  It is here so the ceiling is visible next to the dial it constrains.

`20` and `30` are **starting points to be measured, not settled numbers.** Desktop-simulated
throws top out at 6 m/s (`controller/js/throw.js:81-101`) → 7.2 m/s launch, well under the cap,
so the cap will not show up in desktop testing at all — it must be checked against a real phone
swing, which the `EnergyLoss` tooltip (`SkipConstants.cs:38`) puts around 30 m/s.

## Verification

There is no Unity CLI test runner in this project; verification is in-editor.

0. **Prove the `MaxLiftShare` promotion is a no-op first.** Run `Print Skip Curve` and keep
   `skip-curve.txt` *before* touching anything. Make only the const→field change with the asset
   authored at `0.5`, re-run, and diff: the output should be byte-identical. Do that before
   raising `LiftShare`, so a surprise later is unambiguously the tuning and not the refactor.
1. **Energy invariant + curve sweep** — run `RockSkipping/Verify/Print Skip Curve` from the
   Unity menu bar. It writes `skip-curve.txt` at the repo root. Confirm every run still
   satisfies the energy invariant (outgoing speed strictly below incoming, every bounce) and
   compare skip counts against the file's current contents before the change.
2. **EditMode tests** — run `Assets/Tests/EditMode/` via the Unity Test Runner window; confirm
   nothing in the physics assembly regressed.
3. **Aim preview** — press Play on `Bootstrap` and aim from a phone. The preview runs the same
   `SkipPhysics.Step`, so the clamped arc and taller hops should be visible in the preview line
   before anything is thrown. Tilt the phone past 30° and confirm the arc stops climbing while
   the throw still fails at the water (the raw-`FaceAngleDeg` split doing its job).
4. **A real hard throw** — the only way `MaxLaunchSpeed: 20` gets validated. If a full-power
   swing still sails, lower it; if a normal strong throw feels capped, raise it.

### 4. `docs/4-systems/SkipPhysics.md`

Three new constants land on the one asset that documents the global/per-rock split. Add
`MaxLaunchSpeed`, `MaxLaunchPitchDeg` and `MaxLiftShare` there, and note that `MaxLiftShare`
moved out of `SkipPhysics` — a reader following that doc to find the ceiling will otherwise
still be looking for a const that no longer exists.

Also copy this plan into `docs/plans/` so it is tracked with the repo rather than living only
in the session scratch directory.

## Not in scope

Left alone deliberately, but flagged since they came up while reading the model:

- `SpinRetention: 0.9` in the asset contradicts its own tooltip (`SkipConstants.cs:68-73`),
  which warns that below ~0.98 spin decay — not `EnergyLoss` — becomes what ends every run.
  That is currently capping skip counts, but it is a run-length question, not a height or
  distance one.
- `docs/Findings-RealSkipPhysics.md` and `tools/physics/HeadlessSolver/` still describe the
  reverted fluid model; `Assets/CLAUDE.md` was reverted with the code but those were not.
