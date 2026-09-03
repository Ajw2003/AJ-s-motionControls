# Standards Audit & Remediation — RockSkipping

## Status

| Phase | State |
|---|---|
| Phase 0 — re-runnable audit | **Done** (commit `0ab198d`) |
| Phase 1 — tooling + documented deviations | **Done** (commit `0ab198d`) |
| Phase 2 — mechanical fixes | **Done** (commit `0ab198d`) |
| Phase 3 — tests | **Done** — EditMode tests verified green in the Editor (commit `0300acc`) |
| Phase 4 — structural | Not started (gated on explicit go-ahead) |

Phase 3 step 12 (Node test runner) converted `relay/test-relay.js` and `relay/test-dns.js` to
`node:test` — verified, 19/19 pass. `controller/test-sensors.js` was deliberately left as-is:
1139 lines of working ESM holding the iOS guard PLAN.md calls the standing regression check,
already exiting correctly. Restructuring it for uniformity alone was not worth the risk.

Audit went from **11 failing checks to 0** — `node tools/audit/check-standards.js` now exits 0.

The last failure closed was `cs.linq-in-per-frame-methods`: `RelayDebugHud.Spread` took an
`IEnumerable<float>` and called `.ToList()`/`.Average()`/`.Max()`/`.Min()`, fed by
`_throws.Select(...)` at two call sites inside `OnGUI`. It now takes the list plus a selector
and computes min/max/mean in a single pass, with the two selectors cached as static readonly
delegates so no closure is allocated per frame. `using System.Linq` is gone from the file.

Worth recording honestly: this HUD is key-toggled and built on IMGUI, which allocates heavily
regardless, so the change satisfies the standard and closes the audit but is not a meaningful
performance win.

Also fixed during Phase 2, beyond the written steps:
- `ShoreRocks` camera caching was first written as a one-shot `Awake` lookup. `Camera.main` can
  be null at Awake, which would have left the shore labels permanently blank with no error. It
  now uses the same lazy retry as `RockLauncher`.
- `ChildProcessGroup.cs:91` had a genuinely empty `catch (EntryPointNotFoundException) {}` —
  now carries the same degradation reason as the sibling block above it. This contradicted the
  plan's original "no silent exception swallowing" finding.
- Four defects in the audit script itself: it flagged its own TODO/HACK/FIXME search pattern,
  counted nested private structs as multi-type files (18 reported vs 7 real), could not silence
  the noise-word check (it uses a `Set`, so `.size` not `.length`), and flagged a comment that
  merely mentioned `Camera.main`.

Drift found since the plan was first measured: 5 C# files now exceed 500 lines (`JoinScreen`
grew to 515), and `Debug.Log` is at 110, not 104.

### Phase 3 — how the asmdef blocker was resolved

Unity Test Framework tests must live in an `.asmdef` assembly, and an `.asmdef` assembly
**cannot reference `Assembly-CSharp`** — the predefined assembly that currently holds all 70
of this project's scripts. There are 0 asmdefs in the project today.

A full folder-per-assembly split turned out to be impossible without either large file moves
or real refactoring: `Game` and `Managers` reference each other (3 files each way), and
`Managers/Bootstrap.cs` constructs `Ui.JoinScreen`/`Ui.ResultsScreen`/`Ui.MatchSettingsScreen`
by fully-qualified name, so `Managers` and `Ui` are circular too. Asmdefs forbid cycles;
`Assembly-CSharp` allowed them. Asmdefs also cover folder *trees*, so merging those sibling
folders into one assembly would mean physically relocating ~40 files.

The way out: `SkipPhysics.cs` and `PhoneAttitude.cs` — the actual test targets — import only
`System.Collections.Generic` and `UnityEngine`. The tangle is entirely in `AimPreview`,
`RockLauncher` and `ShoreRocks`, none of which are under test. So the four pure files
(`SkipPhysics`, `PhoneAttitude`, `SkipConstants`, `SkipTuning`) moved to
`Assets/Scripts/Game/Physics/` under a `RockSkipping.Physics` asmdef with
`autoReferenced: true`, which keeps `Assembly-CSharp` compiling against them unchanged. Two
cycles avoided rather than untangled; no refactoring needed.

Tests live in `Assets/Tests/EditMode/` under an Editor-only test asmdef, and build their
inputs with `ScriptableObject.CreateInstance` so they depend on no project assets.


## Context

Three coding-standards documents now load into every session in this repo:
`coding-philosophy.md` (all languages), `csharp-unity-standards.md` (the Unity project under
`Assets/`), and `web-js-ts-node-standards.md` (`relay/`, `controller/`, `tools/`). They are
binding for code written here, but the codebase predates them — nobody has ever checked it
against them.

This plan does two things: establishes a **re-runnable audit** so compliance is a measurable
number rather than a one-off opinion, then fixes the gaps in an order that avoids churn
(tooling before mass edits, decisions before code, tests last).

A survey has already been done — the numbers below are measured, not estimated. The headline
is that **the codebase is in better shape than expected**: the real gaps are missing *tooling*
and missing *tests*, not bad code.

### Audit findings (measured)

**C# — `Assets/` — 70 files, ~12.9k lines**

Already compliant, and worth not disturbing:
- Allman braces everywhere (0 files contain a K&R `) {`).
- Plain private fields are `_camelCase` — 139 sites across 26 files, no exceptions found.
- No silent exception swallowing — every `catch` inspected logs or returns a described error.
- Zero `TODO`/`HACK`/`FIXME` comments.
- `[SerializeField] private` used over public fields; `ScriptableObject`s used for shared
  config (`SkipConstants`, `SkipTuning`, `RockCatalog`); events used to decouple systems.
- LINQ confined to 4 files, and only editor/setup code (`AudioBankBuilder`,
  `RockCatalogBuilder`) plus two runtime files to re-check.

Gaps:
| # | Gap | Size |
|---|---|---|
| C1 | No `.editorconfig` at the repo root — the standard explicitly asks for one | 1 file |
| C2 | No tests at all; no Unity Test Framework, no NUnit reference, no `Tests/` folder | — |
| C3 | `Camera.main` called inside `ShoreRocks.OnGUI` (per-frame) | `Scripts/Game/ShoreRocks.cs:411` |
| C4 | No `.asmdef` files; 70 files is past "a handful of scripts" | 0 → 3–4 |
| C5 | 104 `Debug.Log` calls with no level discipline | 104 sites |
| C6 | No `#nullable enable` anywhere (standard says opt in file-by-file, so: low priority) | — |
| C7 | Four files over 500 lines: `AimPreview` 617, `LocalServices` 585, `RelayClient` 562, `TurnManager` 533 | 4 files |
| C8 | Multi-type files: `Messages.cs` (12 types), `Setting.cs` (4), `SkipPhysics.cs` (3) | 3 files |

Not gaps — **decided as documented deviations** (see Decisions below): `[SerializeField]`
field naming, and `EventManager.Instance?.`.

**Global philosophy — `coding-philosophy.md` — both stacks**

This is the doc that governs comments, naming and dead code, and it is where the codebase scores
*best*. Measured:

- **Comments: 30% of C# lines (3,873 / 12,937) and 25% of JS lines (1,348 / 5,387).** Every single
  file carries doc comments — 0 of 70 C# files lack `///`, 0 of 13 JS files lack a leading block.
  2,970 XML doc lines cover 567 public members.
- **Comment *quality* is the standard's ideal, not a gap.** Sampled comments explain *why*, not
  *what*, and document exactly the non-obvious gotchas the doc asks for — e.g. `AimPreview.cs`
  explaining that a scene-root object was destroyed by the first scene load because `Awake` only
  runs once, and `LocalServices.cs:370` noting that `UnityWebRequest.Dispose()` does not dispose a
  custom `certificateHandler`. **No action needed here; the risk is a future pass "tidying" these
  away.** Worth protecting rather than trimming.
- **Dead code: effectively zero.** Three regex hits, all false positives — prose comments that
  happen to begin with a word like `return`. No commented-out code to delete.
- **Abbreviations: one** (`_cts` for `CancellationTokenSource`). Fine.

Genuine philosophy gaps:

| # | Gap | Size |
|---|---|---|
| P1 | ~20 booleans not phrased as a yes/no question — `visible`, `bound`, `Sunk`, `Sank`, `Terminated`, `Spatial`, `Usable` | ~20 sites |
| P2 | Noise-word type names, which the doc names explicitly: four `*Manager` classes (`AudioManager`, `EventManager`, `ScoreManager`, `TurnManager`), plus `SettingsData` and `ProcessUtil` | 6 types |

**Both P1 and P2 are partly blocked, and this is the important finding of the philosophy pass.**

- Four of the offending booleans — `ok`, `resumed`, `reconnected` — are **wire-format field names**,
  matched by string between `Assets/Scripts/Net/Messages.cs:54,64` and `relay/server.js:178,206,262,426`.
  Renaming the C# field to `isResumed` silently breaks JSON deserialization unless `relay/server.js`
  and the controller change in the same commit. `randomRocks` is likewise persisted in
  `match-settings.json` via `MatchSettingsStore`, so renaming it breaks saved settings.
- The four `*Manager` classes are referenced by name throughout `CLAUDE.md`, `docs/PLAN.md` and the
  `Bootstrap` ordering comments. Renaming them is a docs-wide churn for a naming-taste win, and
  `EventManager`/`ScoreManager` are Unity-idiomatic besides.

Recommendation: **rename only the safe, purely-internal booleans** (P1 minus the wire/persistence
ones) and **leave the `*Manager` names alone as a recorded deviation** (P2). Details in Phase 2.

**Web/Node — `relay/`, `controller/`, `tools/` — ~5.6k lines**

Already compliant: correct module system per package (CJS in `relay/`, ESM in `controller/`),
`relay/package-lock.json` committed, no hardcoded secrets or remote URLs, `console.log` used
once in `server.js` (the rest goes through a `log()` helper), zero TODOs, `try`/`catch` present
around the `await` sites.

Gaps:
| # | Gap | Size |
|---|---|---|
| W1 | **No Prettier and no ESLint anywhere** — the standard's two headline tools | 0 → configured |
| W2 | No `tsconfig.json`; no type checking of any kind | 1 file |
| W3 | No `.env.example`, though `relay/server.js` reads `BIND_HOST` and `PORT` from env | 1 file |
| W4 | 5 `.then()` chains where the standard prefers `async`/`await` | 5 sites |
| W5 | Three hand-rolled test scripts (`test-relay.js`, `test-dns.js`, `test-sensors.js`) with no framework and no runner beyond `npm test` | 3 files |

## Decisions already made

These three were put to the user and answered; they are settled, and the plan reflects them.

1. **`[SerializeField]` fields keep bare camelCase.** The codebase has a consistent internal
   split — plain privates get `_`, serialized ones do not. Renaming 70 fields risks silently
   dropping Inspector-set values and buys nothing. Record it as a deliberate deviation instead.
2. **Stay on JavaScript**; add a `tsconfig.json` with `allowJs`/`checkJs` and type via JSDoc.
   No file renames, no build step, no compile stage added to the hosting path.
3. **Keep `EventManager.Instance?.`** (69 sites). `SingletonBase.Instance`'s getter already
   uses Unity's overloaded `==` internally, so the fake-null window is narrow — only the
   `_isQuitting` teardown path can hand back a destroyed instance. Record it as a known,
   bounded deviation rather than churning most gameplay files.

## Approach

### Phase 0 — Make the audit re-runnable *(do this first)*

Build `tools/audit/check-standards.js` (plain Node, no dependencies — `node` is on PATH at
`C:\Program Files\nodejs\node.EXE`). It walks the repo and prints one named check per line with
a pass/fail and a count, exiting non-zero on failure.

Structure it as a flat list of named checks so a human can read, edit, and extend it without an
agent present:

- C#: brace style, plain-private underscore naming, `Camera.main`/`GetComponent`/`Find` inside
  `Update`/`FixedUpdate`/`LateUpdate`/`OnGUI`, LINQ in per-frame methods, empty `catch` blocks,
  `Debug.Log` count, files over 500 lines, multi-type files.
- Web: presence of Prettier/ESLint/tsconfig config, `.then()` chains, `console.log` count,
  committed lockfiles, `.env` never committed.
- Philosophy (both stacks): commented-out code, booleans not phrased as a yes/no question,
  noise-word type names, files carrying no doc comment at all. Comment *density* should be
  reported as a number but **never failed on** — high density is this codebase's strength, and a
  check that punishes it would drive exactly the wrong edit.
- It must **exclude** `Assets/.claude/worktrees/`, `Library/`, `Temp/`, `obj/`, and
  `node_modules/` — these duplicate the real tree and will double every count.
- It must handle **CRLF line endings** — every file in `Assets/` is CRLF, which silently breaks
  naive `$`-anchored regexes (this bit the initial survey).

Wire it up as `npm run audit` from a small root `package.json`, or as a documented
`node tools/audit/check-standards.js` invocation. This is the artifact that makes every later
phase verifiable, so it comes before any fix.

Also copy this plan to `docs/plans/Plan-StandardsAudit.md` so it lives in the repo.

### Phase 1 — Tooling and documented deviations *(no code changes)*

Cheap, mechanical, and it establishes the baseline that later phases are measured against.

1. **`.editorconfig` at the repo root** (C1) — Allman braces, 4-space C# indent, `_camelCase`
   private naming rule, CRLF, UTF-8. Matches what the code already does, so it should produce
   a zero-diff reformat; if it doesn't, the difference is a finding.
2. **Prettier + ESLint** (W1) — devDependencies and config in `relay/` and `controller/`, with
   `eslint:recommended` as the base. Add `npm run lint` / `npm run format` scripts to both.
   **Do not run the formatter yet** — that is Phase 2, kept as its own commit.
3. **`tsconfig.json`** (W2) with `allowJs`, `checkJs`, `strict`, `noEmit`, per decision 2. Expect
   an initial wall of errors; the goal of this step is only to *see* them, not fix them.
4. **`.env.example`** (W3) listing `BIND_HOST` and `PORT` with placeholder values.
5. **Record the deviations** in `Assets/CLAUDE.md` — decisions 1 and 3 above, plus the two from
   the philosophy pass, each with its one-line reason. This is what stops a future session
   "fixing" them back:
   - `[SerializeField]` fields keep bare camelCase (decision 1).
   - `EventManager.Instance?.` is kept deliberately (decision 3).
   - The `*Manager` class names stay (P2) — referenced by name across `CLAUDE.md`, `docs/PLAN.md`
     and the `Bootstrap` ordering comments.
   - `ok`/`resumed`/`reconnected`/`randomRocks` keep their non-question names because they are
     wire-format and save-file field names, not internal identifiers (P1).
   - **The heavy comment density is intentional and is not to be trimmed.** Worth stating
     outright, since a future pass reading "comment why, not what" could otherwise read 30%
     density as bloat and delete the gotcha notes that are the codebase's best asset.

Files: `.editorconfig`, `relay/.prettierrc`, `relay/eslint.config.js`, `controller/.prettierrc`,
`controller/eslint.config.js`, `tsconfig.json`, `.env.example`, `Assets/CLAUDE.md`.

### Phase 2 — Mechanical fixes *(one commit per category)*

6. **Run Prettier** across `relay/`, `controller/`, `tools/`. Large diff, zero semantic change —
   keep it in its own commit so it never obscures a real change in review.
7. **Fix `Camera.main` in `ShoreRocks.OnGUI`** (C3) — cache it in a `_camera` field resolved in
   `Awake`, exactly as `RockLauncher.cs:63` already does. Reuse that pattern rather than
   inventing a second one.
8. **Clear the ESLint findings** (W1) — fix them, or disable a rule inline with a stated reason.
   The standard forbids leaving them unaddressed and unexplained.
9. **Convert the 5 `.then()` chains** to `async`/`await` (W4).
10. **Rename the safe booleans** (P1) — `visible` → `isVisible`, `bound` → `isBound`,
    `Usable` → `IsUsable` (note `IsUsable` already exists elsewhere in the codebase, so this is
    following an established local precedent), `Terminated` → `HasTerminated`, `Sunk`/`Sank` →
    `HasSunk`. Rider's rename refactor handles these safely.

    **Explicitly excluded from this step:** `ok`, `resumed`, `reconnected` (wire format, matched
    by string against `relay/server.js`) and `randomRocks` (persisted in `match-settings.json`).
    Renaming any of those is a coordinated cross-stack change with a save-file migration, which is
    far more than a naming cleanup is worth — leave them and record why.

### Phase 3 — Tests *(the largest real gap)*

11. **Add Unity Test Framework + an EditMode test assembly.** This is C2 and it is the single
    highest-value item in the plan.

    Start with **`SkipPhysics.Step`** — `Scripts/Game/SkipPhysics.cs` is explicitly documented
    as pure and side-effect-free, which makes it the ideal first EditMode target: no scene, no
    MonoBehaviour, deterministic. The project already has an acceptance check for it
    (`RockSkipping/Verify/Print Skip Curve`, which writes `skip-curve.txt` and asserts an energy
    invariant) — port that invariant into a real assertion rather than writing new criteria.

    Then `PhoneAttitude.HeadingDirection`/`HeadingDeg` — also pure, and named in `CLAUDE.md` as
    the one conversion reused everywhere, so a regression there is expensive.

12. **Give the three Node test scripts a runner** (W5) — Node's built-in `node:test` needs no
    dependency and is already available on Node ≥20, which both `package.json`s require.

### Phase 4 — Structural *(ask before starting; largest blast radius)*

Do **not** start these without a separate go-ahead — each touches more than three files and the
philosophy doc requires asking first.

13. **Assembly definitions** (C4) — a plausible split following the existing folder boundaries:
    `RockSkipping.Net`, `RockSkipping.Game`, `RockSkipping.Editor` (editor-only), with
    `EventSystems`/`EventTypes` as a shared core. An Editor-only asmdef is also a prerequisite
    for the EditMode test assembly in Phase 3, so scope that one in early if Phase 3 needs it.
14. **Split the four 500+ line files** (C7) and reconsider the multi-type files (C8).
    `Messages.cs` holding 12 DTOs is defensible as a single wire-format file — the standard's
    one-class-per-file rule is aimed at unrelated types, and `CLAUDE.md` treats `Messages.cs` as
    one unit deliberately. `Setting.cs` and `SkipPhysics.cs` are the real candidates.
15. **`Debug.Log` discipline** (C5) — a pass over 104 sites promoting real problems to
    `LogWarning`/`LogError` and deleting spam. Best done incrementally, per file, as those files
    are touched for other reasons.
16. **`#nullable enable`** (C6) — file-by-file, per the standard's own explicit instruction not
    to big-bang it. Lowest priority; fold into whatever file is being edited anyway.

**Deliberately not in scope:** the `Assets/_Project/` folder layout from the standard. This
project's `CLAUDE.md` documents its own `Assets/Scripts/<Category>/` structure, `Resources/` is
described as intentionally curated, and moving every script would break every `.meta` GUID
reference for no functional gain. Flagging it as an accepted deviation is the right call — but
it is the user's call, so raise it rather than assuming.

## Verification

After Phase 0, the audit script is the check for everything else:

```powershell
UNTESTED: node tools\audit\check-standards.js
```

PowerShell 5.1, from `C:\Users\aj\Desktop\GameDev\RockSkipping`. Marked UNTESTED because the
script does not exist yet — it is built in Phase 0, and the command gets run and its real output
pasted back before this plan's instructions are handed over as final.

Expected: one line per named check with a count, and a non-zero exit while gaps remain. Re-run
it at the end of each phase; the failing-check count should fall monotonically and never rise.

Per-phase verification beyond the script:

- **Phase 1** — `.editorconfig` should produce a zero-diff reformat in Rider. `npx tsc --noEmit`
  should run and report its (expected, large) initial error count.
- **Phase 2** — `cd relay && npm test` and `cd controller && npm test` must still pass after
  Prettier and the `.then()` conversions. For `ShoreRocks`, press Play on `Bootstrap` and confirm
  the shore rock labels still draw — `OnGUI` has no test coverage, so this one is eyes-on.
- **Phase 3** — the new EditMode tests run green via the Unity Test Runner window, and
  `RockSkipping/Verify/Print Skip Curve` still produces a `skip-curve.txt` satisfying the energy
  invariant.
- **Phase 4** — the Unity Console must be clean after the asmdef split; there is no out-of-band
  compile check in this project (`CLAUDE.md` explains why, and that a previous attempt at one was
  deleted), so the open Editor's Console is the only verification available.

Note that all Unity verification is manual and in-editor by design — this project has no CLI test
runner, and `Unity -batchmode` cannot compile while the Editor holds the project lock.
