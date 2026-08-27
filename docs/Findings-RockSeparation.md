# Findings — the first catalog-wide skip curve

**2026-08-26.** First run of the Part 6 sweep (`RockSkipping/Verify/Print Skip Curve`, now
looping catalog × both tuning sets). Source data: `skip-curve.txt` at the repo root, 531 lines.

Nothing here has been changed in response. This is the diagnosis; the fixes are decisions.

## 1. Authored is six copies of one rock

At power 0.8, each rock thrown at its own best angle:

| rock | authored skips | authored dist | derived skips | derived dist | delta |
|---|---|---|---|---|---|
| Skimmer | 17 | 144.4 m | 12 | 127.5 m | -5 |
| Slab | 17 | 144.4 m | 26 | 156.2 m | +9 |
| Pebble | 17 | 144.4 m | 17 | 150.1 m | 0 |
| Nugget | 17 | 144.4 m | 14 | 137.7 m | -3 |
| Shard | 17 | 144.4 m | 16 | 148.9 m | -1 |
| Boulder | 17 | 144.4 m | 16 | 149.1 m | -1 |

Skip spread across Authored is **0**. Every authored tuning also prints the same header line
(`kept 0.75..0.95/bounce`, `exit 7deg -> 5deg over 3 skips`, `best face -5deg +/- 20deg`).

**Cause, not yet confirmed by reading the assets:** `RockSkipping/Rocks/Seed Authored Tunings`
copies `Resources/SkipTuning` and, by design, only writes files that do not already exist. It
seeded six identical copies and nobody hand-edited them afterwards. The plan called Authored
"six hand-tuned archetypes… guaranteed to separate, because separating them is what a person
sat down and did" — that sitting-down never happened.

So the Authored/Derived comparison currently has nothing on one side of it. Any conclusion
about which approach is better is, today, a conclusion about Derived versus the baseline.

## 2. Derived separates, but three rocks collapse together

Spread 14 skips (12..26) — passes the ≥ 4 check. The pair check fails three times:

- Shard and Boulder — **0 skips and 0.2 m apart**
- Pebble and Boulder — 1 skip and 0.9 m apart
- Pebble and Shard — 1 skip and 1.1 m apart

Slab (26) and Skimmer (12) are the whole spread; the middle four are a cluster with Nugget
slightly outside it. This is the "are mesh bounds enough" question answering *partly*: the
extremes come through, the middle does not. The derivation keys off flatness, size and
elongation, and four of the six meshes are evidently similar on all three.

## 3. Every rock fails the same two baseline checks

`the opening hop does not out-throw the launch arc` fails for **all twelve** rock × set
combinations — authored at 42.5 m hop vs 35.6 m arc (identical across all six, per §1),
derived from 36.7 m to 57.0 m against the same 35.6 m arc.

That arc length is the same in every row because `MaxSpeed`, `MinSpeed` and `Gravity` are
catalog-wide by design, so the launch arc at full power is a property of the baseline, not of
any rock. This check is therefore failing against the *baseline tuning*, not against anything
Part 6 introduced — and it would have been failing before the catalog existed, on the single
`Resources/SkipTuning` curve, if it were run today.

**Opening hop resolved 2026-08-27.** The check was the thing that was wrong, not the tuning.
It demanded `FirstHopLength <= LaunchArcLength`, which no tuning this project has ever had
could meet: the stone arrives at `LaunchPitchDeg` (5 deg) and leaves at `OpeningHopAngleDeg`
(7 deg) with most of its speed intact, so it exits steeper than it came in and the first hop
beats the arc by construction. A check that fails everything reports nothing.

It is now a ratio with a ceiling of 1.35x, renamed `the opening hop does not out-lob the throw`
and printing the ratio it measured. The ceiling is calibrated against this corpus rather than
derived from physics, and the corpus argues for it: the two largest opening hops (Skimmer at
1.60x, Nugget at 1.51x) belong to the two worst skippers in their set, 12 and 14 skips against
16-26 for everything under 1.30x. A stone leaving the first bounce that much hotter than it
arrived is lobbing, and the skip count shows it paying for that. The fence sits in the gap the
readings leave between 1.27x and 1.51x, so ten of twelve rows pass and the two that fail are
the two with a demonstrable problem. If a retune moves the whole population, the fence moves
with it deliberately.

`full power stays on the water` fails for four Derived rocks (257.9 m, 262.5 m, 267.3 m and
292.8 m — Shard, Boulder, Pebble and Slab) and for none of the Authored ones, which all sit
under the threshold at the same baseline distance.

## 4. A pre-existing bug in the verdict text

`SkipCurve.Verdict` checks `hard.Distance < 250f` but prints "against roughly 260 m of lake".
Shard's Derived run at **257.9 m is reported as a failure against a number it is under**. One
of the two is wrong — either the lake is 250 m and the message is stale, or the lake is 260 m
and the threshold is. This predates Part 6 (the check and its message are unchanged from the
original single-curve version); it only became visible because twelve runs now straddle the
gap.

**Resolved 2026-08-27.** Kept the threshold at 250 m and made the message read the same value,
rather than raising the threshold to 260 — raising it would have silently reclassified Shard's
Derived run (257.9 m) as a pass, which is a tuning decision dressed up as a typo fix. Both now
read `SkipCurve.PlayableLakeLength`, a single named constant. The lake is still placeholder art
(a 10-unit Plane scaled by 200, per `AimPreview`), so 250 m remains a design target and that
constant is the one place to reconcile it with a real lake later. Pass/fail on every existing
row is unchanged; the reported number is now the one being tested against.

## 5. The 250 m lake does not exist in the scene

**2026-08-27.** Asked what the delta is between the verdict's lake and the water the game
actually has. There is no relationship between the two.

One water source in the project — `AimPreview.CreatePlaceholderWater`, `Scripts/Game/AimPreview.cs:561`:

```csharp
GameObject.CreatePrimitive(PrimitiveType.Plane)   // Unity's Plane is 10 x 10 units
position   = (0, WaterY = 0, 60)
localScale = (200, 1, 200)                        // -> 2000 x 2000 units = 2000 x 2000 m
```

The throw origin is `(0, 0, 0)` (`AimPreview.cs:487`), so the plane spans z = -940 to z = +1060.

| | value |
|---|---|
| `SkipCurve.PlayableLakeLength` | 250 m |
| Water ahead of the throw origin | **1060 m** |
| Delta | **810 m unused** — the check is 4.2x tighter than the geometry |
| Longest throw in the sweep (Slab, Derived) | 292.8 m = 27.6% of the water |
| Authored at full power (all six) | 249.8 m = 23.6% of the water |

**Nothing else binds before 1060 m either.** The two candidates both fail to:

- **Fog** fades to the background between 120 m and 600 m, but it is measured from the camera,
  and `RockLauncher.FollowActiveRock` chases the rock at `(x - 2, z - 3)` — about 3.6 m behind
  it. The rock never fogs out; the shore behind it does.
- **The 1000 m far clip** travels with the camera for the same reason.

So the real failure mode at distance is not "leaves the lake", it is **"flies off the mesh at
1060 m"**. `SkipPhysics` bounces against `WaterY`, a plane equation, not against the mesh — past
z = 1060 a rock keeps skipping perfectly on water that is no longer drawn. That is the condition
worth asserting, and no rock is remotely near it.

Which makes the four Derived FAILs in §3 failures against a number nothing in the build
enforces. Whether that is wrong depends on whether 250 m is meant as a *design* budget for how
big the lake should eventually feel, which is a gameplay call, not something readable off the
scene.

**Resolved 2026-08-27.** The check now asserts the condition that is actually real, and reads
the geometry rather than restating it. `AimPreview` gained named constants for the plane it
builds — `WaterPlaneBaseSize`, `WaterPlaneScale`, `WaterCentreZ` — and a computed
`WaterReachFromOrigin` (1060 m). `SkipCurve.DrawnWaterLength` reads that property, so the check
and the mesh cannot disagree, and the check follows the water on its own if the placeholder is
ever resized or replaced. The hardcoded 250 is gone.

Renamed to `full power stays on the drawn water`, since that is what it tests: past 1060 m a
throw does not stop or slow, because `SkipPhysics` bounces against `WaterY` rather than against
the mesh — it carries on skipping over water nobody can see. All twelve rows pass today, which
is the correct answer, not a weakened one. The four failures this replaces were against a limit
nothing in the build enforced.

## 6. Authored passes the water check by 0.2 m

**2026-08-27**, from the re-run after the §4 fix. All six Authored rocks report:

```
PASS  full power stays on the water — 249.8 m against 250 m of lake
```

That is a margin of **0.2 m on 250, or 0.08%**. It is a pass in name only: the throw is sitting
on the threshold, not clearing it.

Two things make it worse than a single tight pass would be. First, per §1 the six Authored
tunings are byte-identical apart from `m_Name` and `DisplayName`, so they do not fail
independently — any change that adds a metre to the full-power distance flips **all six at
once**, and the report goes from six PASS to six FAIL with no intermediate state to read as a
warning. Second, per §5 the 250 m it is 0.2 m under is not a measurement of anything in the
scene, so the precision here is spurious: the check is tight against a number that is itself
arbitrary.

Not a bug, and not urgent — but the Authored column of the water check currently carries no
information. It will read PASS until it reads FAIL six times.

## 7. The separation summary prints FAIL next to its passing half

Same family as §4 — a verdict and its message drawn from different things.
`Scripts/Verification/SkipCurve.cs:180` prints, for the Derived set:

```
FAIL  Derived separates the rocks — 14 skips between the weakest and strongest rock
```

The FAIL is correct. `Separation` returns `spreadOk && pairsOk` (`SkipCurve.cs:325`), and while
the spread check passes at 14, the three pairwise checks in §2 fail. But the detail string comes
from `DescribeSpread` (`SkipCurve.cs:401`), which reports **only the spread** — the half that
passed. So the line states a failure and then justifies it with its own evidence for success.

Read in isolation it is simply wrong; read against the per-set block a few lines above, where
the pair failures are printed in full, it is merely redundant. The fix is either to fold the
pair result into the detail, or to drop the summary line as duplicating the block above it.

## What to decide

These are separate calls, in rough dependency order. Resolved items are dropped from this list
once done; the resolution note lives in the section it belongs to.

1. **Hand-tune the six Authored archetypes** (§1), or drop the Authored set and stop pretending
   the comparison exists. `Seed Authored Tunings` will not overwrite them, so editing the six
   `Assets/Rocks/Tuning/Authored/*.asset` files by hand is safe. The only item here that changes
   how the game plays rather than what the report says.
2. **Make the separation summary line say what it means** (§7). Cheap and self-contained; same
   class of bug as the one resolved in §4.
3. **Decide whether Derived's middle cluster matters** (§2). Two clearly distinct rocks and
   four near-identical ones may be acceptable for a party game, or the derivation may need a
   fourth input beyond flatness/size/elongation.
4. **Decide whether the Authored water margin matters** (§6). Reduced by the §5 resolution — the
   threshold it was 0.2 m under is gone, and Authored now clears 1060 m by 810 m — but the
   underlying point stands: six identical tunings cannot fail independently, so that column
   still carries no information until §1 is dealt with.

**Done:** §4 (verdict threshold/message mismatch), §5 (what 250 m is for), §3's opening-hop
check.

None of this blocks the in-play half of Part 6: `rock-throws.csv` records whatever set is live,
and a corpus gathered on today's numbers is still the evidence for §2 and §3.

## Reproducing

Run `RockSkipping/Verify/Print Skip Curve` in the Editor, then:

```bash
f=/c/Users/aj/Desktop/GameDev/RockSkipping/skip-curve.txt; if [ -f "$f" ]; then cat "$f"; else echo "skip-curve.txt not generated yet - run RockSkipping/Verify/Print Skip Curve in the Unity Editor"; fi
```
