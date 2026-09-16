# Findings — untangling the skip-physics history

Written 2026-09-09, before any git action. Read-only investigation; every command run was a
status/log/diff query. **Nothing is lost.** The work is committed *and* pushed. What went wrong is
attribution and placement, not survival.

---

## 1. What actually happened

The physics session's entire output — 23 files, **6,727 insertions** — exists only as the content
of a single merge commit:

```
eee5059  Merge branch 'Artefacts' into claude/aim-preview-perf-yhwuwf
   parent1 57a3a20  Hold the aim preview off until the camera is back
   parent2 e3f824b  Merge branch 'main' into Artefacts
```

Neither parent contains any of it. Verified:

| Check | Result |
|---|---|
| `git ls-tree e3f824b` for `DiscContact.cs` / `SkipReality.cs` / `tools/physics` | 0 matches |
| `git ls-tree 57a3a20` for the same | 0 matches |
| `git ls-tree eee5059` for the same | 9 matches |
| `git diff --stat e3f824b eee5059` | 23 files, 6727 insertions |

This is an *evil merge*: work introduced as merge-resolution content rather than as its own
commits. The consequences are all about reviewability, not safety:

- `git log main..HEAD -- Assets/Scripts/Game/Physics/` shows only the merge, so per-file history
  simplification hides the change from ordinary review.
- The commit message says "Merge branch 'Artefacts'" and describes none of the physics work.
- There is no atomic history — no way to bisect, revert or review the physics work independently
  of the art pass it was folded into.

The session evidently knew it could not commit: it dumped its own diff to
**`SESSION-physics-work.patch`** (3,340 lines) at the repo root, and that safety copy then got
swept into the merge too.

### The patch file is fully redundant

`SESSION-physics-work.patch` covers **19 files**, every one of which is already applied in the
working tree at HEAD. It is a duplicate of committed content, not an unapplied change. Nothing in
it needs recovering.

### Push state

```
## claude/aim-preview-perf-yhwuwf...origin/claude/aim-preview-perf-yhwuwf
```

No ahead/behind markers — HEAD is fully pushed. The physics work is on the remote. There is no
recovery emergency and no deadline pressure on any of the cleanup below.

---

## 2. Where the work actually stands

The plan of record is **`docs/plans/real-skip-physics.md`** (on the current branch, grounded in
Bocquet 2003, Clanet 2004 and Nagahiro 2004, with measured results in
`docs/Findings-RealSkipPhysics.md`).

### Phase 1 — mostly done

| Plan item | State |
|---|---|
| `docs/plans/real-skip-physics.md` | committed |
| `Physics/DiscContact.cs` | 241 lines |
| `Physics/RigidDisc.cs` | 132 lines |
| `Physics/ReferenceStone.cs` | 96 lines |
| `Physics/SpinTransfer.cs` | 92 lines (not in the plan; added) |
| `Verification/SkipReality.cs` | 682 lines, R1–R10 harness |
| `Editor/ProjectSetup.cs` menu item | `RockSkipping/Verify/Print Reality Check` |
| `Tests/EditMode/DiscContactTests.cs` | 131 lines |
| `docs/Findings-RealSkipPhysics.md` | 495 lines |
| **`Physics/SkipPhysics.cs` — rewritten `Step`** | **NOT DONE — byte-identical to `main`** |
| **`Physics/SkipConstants.cs`** | **NOT DONE — byte-identical to `main`** |
| **`Physics/SkipTuning.cs`** | **NOT DONE — byte-identical to `main`** |

`grep` for `DiscContact|RigidDisc|ReferenceStone|SpinTransfer` in `SkipPhysics.cs` returns
nothing.

> **The new physics is a library and a test harness that the game does not call.** The match still
> runs the old heuristic quality/energy-budget solver. This is the single reason you cannot test
> the new solver in a match today.

The `RockLauncher.cs` (+55) and `AimPreview.cs` (+110) changes in the merge are the camera-settle
aim-preview work, unrelated to physics.

Phase 2 (flip gameplay over, catalog, six tuning assets) is entirely untouched.

### The harness has never been run

`skip-reality.txt` does not exist at the repo root. `skip-curve.txt` is dated Sep 2 and predates
this work. Every measured number in `Findings-RealSkipPhysics.md` came from the standalone Python
model (`tools/physics/verify-disc-contact.py`) and the headless C# solver
(`tools/physics/HeadlessSolver/`), not from the in-Editor harness.

---

## 3. The open technical blocker

`Findings-RealSkipPhysics.md` §8.4–8.5 stops at an explicit, unresolved modelling call. Quoting its
own conclusion: **"Not achieved."**

The bind:

- The `(v·n̂)²` pressure law is *required* for the magic angle to emerge (§1.4), but at `C_l = 1`
  it gives roughly **4× too little lift**, so the stone sinks instead of planing.
- Restoring the lift needs `C_l ≈ 16`, which multiplies the **torque** by 16 as well.
- The model has **no rotational damping**, so that 16× restoring torque rings and inverts the
  stone after one bounce — measured tilt oscillating 32° ↔ 147°.

Two consequences the findings record:

1. **The skip counter is wrong.** "Left the water going up" counts surface chatter as skips. A
   measured run reported 19 skips over 7.3 m — 0.38 m per hop against Bocquet's `X₀ ≈ 5.9 m`. It
   was vibrating on the surface. **Every full-model skip count in §7 is inflated.**
2. **The torque is over-driven, not mis-signed.** Overshooting past level into inversion is the
   signature of a restoring torque with no damping.

The findings name the missing term: a real plate rotating in water is heavily damped by **added
mass**; this model is frictionless in rotation. That is a genuine missing physical term, not a
tuning knob — and adding it is the decision the work stopped on.

---

## 4. The second, stranded plan

`claude/stone-skipping-physics-34903d` is a **local-only, never-pushed** branch — `main` plus one
commit, `18ff715 docs: plan the Lift Condition physics retool`. It lives in a worktree at
`Assets/.claude/worktrees/stone-skipping-physics-34903d`.

It carries `docs/plans/Plan-LiftConditionPhysics.md` (416 lines) — an earlier, differently-shaped
approach to the same retool. It was superseded by `real-skip-physics.md`, which is better grounded
(it cites and quotes the papers; this one reasons from the Lift Condition alone).

**But it is not worthless.** Its Phase 1, "give the stone a body", specifies deriving mass, planform
area, centroid and the full inertia tensor from the *actual mesh geometry* by signed-tetrahedron
integration. The active plan does none of that — it takes `Mass`/`Radius`/`Thickness` from
`mesh.bounds`. It also establishes that the FBX meshes are genuinely readable and that
`Assets/CLAUDE.md` is wrong to say otherwise. That work is worth keeping whichever solver wins.

---

## 5. Other loose ends

| Item | State |
|---|---|
| `SESSION-physics-work.patch` | Redundant (§1). 3,340 lines of duplicate at the repo root. |
| `claude/remove-skip-constants-tuning-6a6b4d` | Local-only, `b9d6a78`, already an ancestor of `main`. Nothing unique. |
| `claude/ask-matt-a9bbac`, `claude/ask-matt-d8dbdd`, `claude/html-color-variables-ui-8d8c19`, `claude/mattpocock-skills-grill-0e618b`, `claude/remove-skip-constants-failure-tuning-438d4f` | All local-only at `67dfb2e` (2026-07-31). Stale. |
| Worktrees under `Assets/.claude/worktrees/` | Four, three of them stale. Dot-prefixed so Unity ignores them. |

Both `b9d6a78` and `claude/throw-driven-skip-model` are confirmed ancestors of `main` — that work
already landed.

---

## 6. What this means for "test the new solver in a match"

Three things stand between here and a testable match, in order:

1. **Resolve the rotational-damping call** (§3) — otherwise the stone inverts after one bounce and
   there is nothing worth playing.
2. **Fix the skip counter** to require a minimum hop, so the numbers stop being inflated.
3. **Do the Phase 1 wiring that was never done** — rewrite `SkipPhysics.Step` to sub-step through
   `DiscContact`/`RigidDisc`, and reshape `SkipConstants`/`SkipTuning`, preserving the `Step` /
   `Predict` / `CreateLaunch` signatures and the `SkipStep` contract as the plan requires.

Only then does Phase 2 (catalog, six tuning assets, the reference rock) make sense.
