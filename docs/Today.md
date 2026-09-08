# Today — 2026-09-08 (second session)

**Tier 5.** What is being worked on today and why that rather than something else. Rewritten each
working session. If this file is more than a few days old, it is not today's plan — it is a record
of a day that has passed.

The earlier session today was the documentation audit and the five-tier rebuild; that record is in
[`Findings-DocumentationAudit.md`](Findings-DocumentationAudit.md). This session replaced the plan
that came out of it, so this file is rewritten rather than appended to.

---

## Today was a design session, and it changed the plan

**Done:**

- Interrogated the UI, the art and the gameplay against the actual code rather than against the
  docs describing it. Twenty-one questions over five rounds.
- Wrote [`plans/presentation-and-ui.md`](plans/presentation-and-ui.md) — fifteen numbered
  decisions, a nine-phase sequence, and a table of the environment facts that drove them.
- Brought tiers 1–4 into line with it: this file, [`README.md`](README.md),
  [`Roadmap.md`](Roadmap.md), [`ProjectState.md`](ProjectState.md),
  [`systems/RockCatalogAndTuning.md`](systems/RockCatalogAndTuning.md), and `Assets/CLAUDE.md`.

**Not done, and deliberately not done:** nothing in `Assets/`, `relay/` or `controller/` was
changed except the `CLAUDE.md` paragraph describing the rock decision. No scene was deleted, no
source edited, no git command run. Plan phase 6 deletes `Scenes/MainMenu.unity`; that is an
action, not a documentation update, and it is still pending.

---

## What the interrogation surfaced

These came out of reading the code, not the docs. Each one changed a decision.

### 1. There is no in-play HUD, and that is a gameplay hole

No scoreboard, no turn indicator, no skip counter during a throw. The skip count exists only as a
`Debug.Log` ([RockController.cs:281](../Assets/Scripts/Game/RockController.cs:281)). This is now
the highest-value single item in the project and it is blocked on nothing.

### 2. The project contains no font asset at all

Every screen renders in Unity's built-in Arial, on a TV, at couch distance. This is a sourcing and
licensing decision of the same class as the audio-clips blocker, and it had never been written
down as one. It now sits in [`Roadmap.md`](Roadmap.md) milestone 9 alongside the clips.

### 3. UI Toolkit is free; uGUI + TextMeshPro is not

`Packages/manifest.json` has `com.unity.modules.uielements` but no `com.unity.ugui`. That, plus
this repo's build-in-code-not-prefabs convention, settled the framework question on facts rather
than taste.

### 4. Half the "waiting phones are bored" problem is already built

`SPECTATE` already carries a per-player score map and the controller already renders it. Only the
live skip count is missing, and it is one field on an existing broadcast rather than a new message
type.

### 5. A `Time.timeScale` slow-motion beat would have been a bug

`RockController` steps the solver at `_constants.FixedStep` inside `Update()`, so scaling time
would silently change throw outcomes. The cinematic final-bounce beat was agreed and then dropped
in the same session; the reasoning is preserved in the plan as D7 so it is not rediscovered the
hard way.

---

## What to do next, in order

Ranked by what unblocks what. **This ordering replaces the previous one**, which led with
authoring the six rocks — a task the plan's D5 has since removed.

### Next — phase 1 of the plan: the art pass

Sky gradient, fog to the horizon, golden-hour light, bloom and a warm tonemap on the URP Volume
already installed, and shore geometry so the six selectable rocks stop floating on nothing.

It is first because it needs no font decision, no framework learning and no new packages, it is
roughly a day, and it changes every screenshot. It also has to precede the HUD: the HUD's colours
are chosen against the sky, and building it first means choosing them against a default skybox
that is about to be replaced.

### Then — phase 2: environment presets and their validator

`EnvironmentPreset` + catalog, mirroring the `RockCatalog` → `Assets/Rocks/` pattern exactly.
Golden hour and one contrasting blue-hour, because a preset system with one entry has never been
proven to work. The validator earns its menu item on four checks — fog actually hides the water
plane's far edge, the sun is above the horizon, HUD text clears a contrast ratio against the sky,
and no preset is orphaned from the catalog.

### Then — phases 3 and 4: the font, the editor tooling, and the HUD

Answer the typeface question, commit the face and its `OFL.txt`, build the state driver and the
runtime tuning panel, then build the in-play HUD in UI Toolkit. The HUD is new code with no
regression surface, which makes it the right screen to learn the framework on.

### Then — phases 5 to 7: the IMGUI port, `JoinScreen` last

Results, then Start and MatchSettings, then `JoinScreen` on its own with a hardware test session
attached. It is 609 lines closed after ten rounds of hardware testing and it is the only screen
where "it looked fine in the Editor" is not evidence.

### Still open, unchanged by today

- **Fill in [`DeviceMatrix.md`](DeviceMatrix.md).** Every row is `TODO` and it is cited as
  evidence in `grant/02-innovation-claims.md`. Do not invent rows.
- **Decide where audio clips come from.** Open long enough that milestone 9's audio half has gone
  as far as it can without an answer.
- **Milestone 10, blocker 3 first** — `relay/` and `controller/` are not in the build, and a
  downloaded build fails there before reaching the others.
- **Fix the seven source comments citing deleted documents**, and the mis-citation at
  `StartScreen.cs:21`. A five-minute pass — and note that `StartScreen.cs` is scheduled for a
  UI Toolkit port in phase 5, so doing both at once is cheaper.

---

## Keeping this file honest

- Rewrite it at the start of a working session, not the end.
- A day whose plan is "routine audit, check for documentation drift" is a legitimate plan and
  should be written down as one.
- If today's work does not map to a milestone in [`Roadmap.md`](Roadmap.md), say so explicitly and
  say why. Today's work maps to milestone 9, and to the rewriting of milestone 8's acceptance
  criterion — which is a tier-2 change, and tier 2 changing is rare enough to be worth noticing.
