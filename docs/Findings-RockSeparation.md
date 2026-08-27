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

## What to decide

These are separate calls, in rough dependency order:

1. **Fix the verdict threshold/message mismatch** (§4). Cheap, and until it is fixed every
   distance failure near 250-260 m is unreadable.
2. **Decide whether the opening-hop check is right** (§3). It fails universally, which usually
   means the check encodes an expectation the tuning never met rather than a regression. Either
   the baseline's opening hop needs to come down, or the check needs to say what it actually
   wants.
3. **Hand-tune the six Authored archetypes** (§1), or drop the Authored set and stop pretending
   the comparison exists. `Seed Authored Tunings` will not overwrite them, so editing the six
   `Assets/Rocks/Tuning/Authored/*.asset` files by hand is safe.
4. **Decide whether Derived's middle cluster matters** (§2). Two clearly distinct rocks and
   four near-identical ones may be acceptable for a party game, or the derivation may need a
   fourth input beyond flatness/size/elongation.

None of this blocks the in-play half of Part 6: `rock-throws.csv` records whatever set is live,
and a corpus gathered on today's numbers is still the evidence for §2 and §3.

## Reproducing

Run `RockSkipping/Verify/Print Skip Curve` in the Editor, then:

```bash
f=/c/Users/aj/Desktop/GameDev/RockSkipping/skip-curve.txt; if [ -f "$f" ]; then cat "$f"; else echo "skip-curve.txt not generated yet - run RockSkipping/Verify/Print Skip Curve in the Unity Editor"; fi
```
