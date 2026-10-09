# Presentation and UI: art direction, HUD, and the IMGUI retirement

**Status:** planned, not started. Decided 2026-09-08 in a design interrogation; no source
touched. **Tier 5** (today's plan) against [`../Roadmap.md`](../archive/rock-skipping/Roadmap.md) milestone 9.

## Context

The project is ~75% by [`../ProjectState.md`](../archive/rock-skipping/ProjectState.md), and everything that is
missing is presentation. Read plainly, the current state is:

**The entire art content of the project** is `Resources/Fbx/Rock.fbx`,
`Resources/Fbx/RocksRemeshed.fbx`, `Resources/WaterRipple.shader`, and two materials. No
textures, no skybox, no fonts, no VFX assets, no shoreline. [`Scenes/Game.unity`](../../Assets/Scenes/Game.unity)
holds exactly two GameObjects — `Main Camera` and `Directional Light`. The lake is a
`GameObject.CreatePrimitive(PrimitiveType.Plane)`
([AimPreview.cs:589](../../Assets/Scripts/Game/AimPreview.cs:589)). The material factory is
named [`PlaceholderArt.cs`](../../Assets/Scripts/Game/PlaceholderArt.cs), accurately.

**Every TV screen is legacy IMGUI.** [`JoinScreen.cs`](../../Assets/Scripts/UI/JoinScreen.cs) is
609 lines of `OnGUI` styled off `GUI.skin.label` — Unity's built-in Arial, at fixed pixel sizes,
on a television, at couch distance. No Canvas, no TextMeshPro, no prefab, no animation, no
transition anywhere in the project.

**There is no in-play HUD at all.** During a throw the TV shows no scoreboard, no turn
indicator, no skip counter, no distance. The number the game is *about* exists only as
`Debug.Log($"[Rock] {_playerId}: {_state.SkipCount} skips…")`
([RockController.cs:281](../../Assets/Scripts/Game/RockController.cs:281)) — visible in the
Editor console and nowhere else. This is a gameplay hole wearing a UI costume, and it is the
single highest-value item in this document.

### Facts established while planning

Recorded because each one changed a decision, and re-deriving them costs a session.

| Fact | Where | Consequence |
|---|---|---|
| No `com.unity.ugui` package; `com.unity.modules.uielements` **is** present | `Packages/manifest.json` | UI Toolkit costs zero new packages. uGUI+TMP costs a package add. |
| No font asset exists anywhere in the repo | filesystem | A typeface must be sourced and licence-recorded — same class of blocker as the milestone 9 SFX decision. |
| `SkipPhysics.Predict` returns the **whole** trajectory before launch | [AimPreview.cs:422](../../Assets/Scripts/Game/AimPreview.cs:422) | Any throw-timed presentation beat can be scheduled deterministically, not detected reactively. |
| `SPECTATE` already carries a per-player score map, and the phone already renders it | [Messages.cs:304](../../Assets/Scripts/Net/Messages.cs:304), `controller/js/app.js` → `#scores` | Half the waiting-phone work is already built. Extend the existing broadcast; do not add a message type for it. |
| Chase camera locks Y to home height and `LookRotation`s at the stone | [RockLauncher.cs:132-148](../../Assets/Scripts/Game/RockLauncher.cs:132) | The stone settles at frame centre; the band above it is sky. Top-centre HUD is safe, provided the counter never grows down into centre at its punch peak. |
| Unity 6 ships UI Builder and live USS hot-reload in Play mode | Unity 6 | Editing `.uss` while running already updates the HUD for free. A bespoke preview window would duplicate a shipped tool. |
| The rock validator catches nulls, missing tuning, and duplicate ids | [RockCatalogBuilder.cs](../../Assets/Scripts/Editor/RockCatalogBuilder.cs) | It exists to catch *silent* failures. An environment validator earns a menu item only on the same terms. |

## Decisions

Every one of these was put as a question and answered. Where a decision reverses an earlier
one, that is noted — the reversal is the record.

### D1 — This is a demo/grant build first

Art targets **legibility in a screenshot**, not a shippable art bar. Nothing here assumes an
artist.

### D2 — The placeholder look becomes the art direction

Stylised-flat, deliberately: flat-shaded low-poly, strong silhouette, gradient sky, fog to the
horizon, low golden light, bloom and a warm tonemap via the URP Volume already installed. It
reads as a choice rather than as unfinished, and it is reachable in code with no assets.

### D3 — IMGUI is replaced with UI Toolkit

Animation is worth having, and since the HUD does not exist yet there is no reason to build it
in a framework that cannot move. UI Toolkit specifically: no package cost, code-authored, and
consistent with this repo's build-in-code-not-prefabs convention (`CLAUDE.md`, the
`[SerializeField]`/`AddComponent` note).

**Exception:** `ShoreRocks`' world-anchored labels stay as they are for now. World-space text is
the one thing UI Toolkit is genuinely worse at than the current `WorldToScreenPoint` approach.

### D4 — The in-play HUD, and how it moves

Four elements, priority order:

1. **Live skip counter**, top-centre, punch-scaling on each `SkipOccurredEvent`, **changing
   colour at the bounce that beats the current leader**. When the throw ends it shrinks and
   flies into the scoreboard strip — the flight is what ties the number to the score.
2. **Player name + colour banner** for whose turn it is.
3. **Persistent slim scoreboard strip.**
4. **"Best so far" ghost line** on the water.

Constraint from the camera fact above: top-centre, and the counter must never grow downward far
enough to reach frame centre, where the stone is.

### D5 — Rocks stay mechanically identical; they differentiate visually only

The six rocks keep their distinct meshes and are **named on the scoreboard**. No per-rock tint:
the **player's colour always wins**, because identity in a five-phone party game outranks a
cosmetic choice, and a player's blue rock rendered in the rock's own grey reads as a bug.

**Milestone 8 is to be rewritten, not left failing.** Its current acceptance criterion — "each
plays measurably differently under `Print Skip Curve`" — is unmeetable by design under this
decision. Restate it as *"each rock is visually distinct on the TV and named on the
scoreboard"*, and record the mechanical differentiation as deliberately deferred with a pointer
to `SkipTuning.cs:22`, which already explains why the physics is being evaluated bare first.

### D6 — Strict turn-taking stays; the waiting phones get filled

Simultaneous throwing is rejected — it would wreck the camera and the spectacle, which is the
point of the couch format. Instead the waiting phones show the live scoreboard (already built)
plus the **current thrower's climbing skip count**, carried as one new field on the existing
`SPECTATE` broadcast rather than a new message type.

### D7 — No scripted final-bounce beat

**This reverses an earlier decision in the same session.** A cinematic slow-motion beat on the
last bounce was agreed and then dropped. It stays dropped.

Recorded because the reasoning matters if anyone revives it: the tempting implementation is
`Time.timeScale`, and that would be a bug. `RockController` steps the solver at
`_constants.FixedStep` inside `Update()`
([RockController.cs:197](../../Assets/Scripts/Game/RockController.cs:197)), so scaling time
silently changes throw outcomes. Any future beat must slow the **camera and audio only**.

### D8 — Golden hour by default, other times of day authorable

### D9 — The shore gets geometry

A dark ground plane and a scatter of low-poly boulders, in the existing procedural style.
Without it the six selectable shore rocks float on nothing, which is the most obviously
unfinished thing in any screenshot.

### D10 — Font: a heavy-weight SIL-OFL face, committed

Tall x-height, genuinely heavy weight — the TV failure mode is thin strokes vanishing at three
metres. Committed to `Assets/Fonts/` with its `OFL.txt` alongside. **Never fetched at runtime**:
the entire product is built to work with no internet.

### D11 — Editor tooling: state driver, then runtime tuning panel

- **Build:** a **state driver** that fakes game states (five players joined, throw in flight at
  seven skips, results screen) so any screen can be seen without five phones in the room. This
  is the one that saves real time.
- **Build:** a **runtime tuning panel** exposing HUD numbers — punch scale, settle time, counter
  size, safe-area margins — as live values during Play, following the existing `*Tuning`
  ScriptableObject convention, so motion is tuned by feel rather than by recompile.
- **Do not build:** a HUD preview window. UI Builder plus live USS reload already does it.

### D12 — Environment presets mirror the rock-catalog pattern exactly

An `EnvironmentPreset` ScriptableObject — sky top and horizon colours, fog colour and density,
sun angle/colour/intensity, water tint, bloom and tonemap settings — with assets in
`Assets/Environments/` and a catalog in `Resources/`. Same shape as `RockCatalog` →
`Assets/Rocks/`, honouring the `CLAUDE.md` rule that `Resources/` stays a short curated list
with per-asset data outside it, pulled in by reference.

**Ship two presets, not one:** golden hour and a contrasting cool blue-hour. A preset system
with a single entry has never been proven to work.

### D13 — `RockSkipping/Environments/Validate` checks four things

Scoped so it catches failures that are otherwise silent — the standard the rock validator sets.

1. **Fog hides the water plane's far edge.** The real one. The lake is a finite 200-unit plane;
   if fog density and horizon colour do not occlude its far edge at the camera's framing, a seam
   crosses the lake. Computable from the preset's fog params and the plane extent.
2. **Sun elevation is above the horizon.** A negative pitch yields a black scene that reads as a
   broken build.
3. **HUD text contrast against the sky gradient.** The counter sits top-centre against sky; a
   warm preset with warm HUD text is unreadable, and it only fails in a room you are not in. A
   WCAG-style luminance ratio between sky-top colour and HUD text colour is a hard number.
4. **Every preset is reachable from the catalog** — the orphan case.

### D14 — `Scenes/MainMenu.unity` is deleted

It is not in the `Bootstrap → Lobby → Game → Results` build order, and `StartScreen.cs` is IMGUI
like the rest. A party game that boots straight to a QR on the TV is better than one that boots
to a menu somebody must walk over and click — milestone 7 is entirely about removing that
friction. An orphaned scene a port might half-revive is a trap.

## Sequencing

**Art first, then UI** — deliberately, not by convenience. The environment work needs no font
decision, no framework learning, and no new packages, and it changes every screenshot. Building
the HUD first would mean choosing its colours against a default skybox that is about to be
deleted; D13's contrast check has nothing to check until the sky exists.

| Phase | Work | Notes |
|---|---|---|
| 1 | Sky gradient, fog, golden-hour light, bloom + warm tonemap, shore geometry | D2, D8, D9 |
| 2 | `EnvironmentPreset` + catalog + `Environments/Validate`; golden hour and blue hour | D12, D13 |
| 3 | Font sourced and committed; state driver and runtime tuning panel | D10, D11 |
| 4 | **In-play HUD in UI Toolkit** — new code, no regression surface | D4 |
| 5 | Port Results, then Start/MatchSettings | D3 |
| 6 | Delete `MainMenu.unity`; rewrite milestone 8's criterion | D14, D5 |
| 7 | **Port JoinScreen last, on its own, with a hardware test session attached** | D3 |
| 8 | Waiting-phone live skip count via `SPECTATE` | D6 |
| 9 | Reactions — **specified below, deliberately not built** | D15 |

IMGUI and UI Toolkit render simultaneously with no conflict, so phases 4–7 coexist rather than
requiring a big-bang port. `JoinScreen` goes last because it is 609 lines closed after ten
rounds of hardware testing, and it is the only screen where "it looked fine in the Editor" is
not evidence.

### D15 — Reactions: deferred, specified now

Ranked last by the developer and explicitly not a priority. Specified here anyway so the design
does not have to be re-derived — which is the exact failure `ProjectState.md` records happening
to the captive portal and the tuning audit.

**The rule:** reactions that do not appear on the TV are pointless, because the TV is where the
room is looking. If the TV side is not built, cut the feature rather than ship a button nobody
can see react.

Shape, per the `CLAUDE.md` four-part convention for a new message (type + DTO in `Messages.cs`,
a `RelayClient` event, a `Dispatch` case, and the `app.js` handler):

- **`REACT`**, phone → host, carrying the reacting player's id and one reaction from a small
  fixed set.
- **TV side:** a burst of the reaction rendered in that player's colour.

Unlike the live skip count in D6, this genuinely needs a new message type — it travels
phone-to-host, where `SPECTATE` is host-to-everyone.

## What this plan does not decide

- Concrete colour values for the two environment presets.
- The HUD's numeric tuning defaults — they are seeds, since D11's panel makes them live.
- The specific reaction set.

All three are authoring details, settled by looking at the result on a TV rather than by
argument.
