# Rock catalog and tuning

**Source:** `Assets/Scripts/Game/RockDefinition.cs`, `RockCatalog.cs`, `ShoreRocks.cs`;
`Assets/Scripts/Managers/RockSelection.cs`; `Assets/Scripts/Editor/RockCatalogBuilder.cs`;
`Assets/Rocks/`, `Assets/Resources/RockCatalog.asset`

## What it owns

Which stones exist, what each one is, and which one every player is throwing.

## The data model

```
Resources/RockCatalog.asset          ordered array of six RockDefinition references
   └─ Assets/Rocks/Rock_0N.asset     RockDefinition — id, name, blurb, mesh, scale,
        └─ Tuning/Rock_0N.asset      rotation, tint, and ONE SkipTuning reference
```

Six meshes live in `Resources/Fbx/RocksRemeshed.fbx` and have since it was imported.

**Only the catalog lives in `Resources/`.** The definitions and their tunings sit in
`Assets/Rocks/` and come along as asset dependencies. `Resources/` is intentionally a short,
curated list of things loaded by name at runtime; new per-rock data goes under `Assets/Rocks/` and
is pulled in as a normal reference from something that *is* in `Resources/`.

### A rock is a mesh AND a tuning

The pairing is the whole feature. A flat stone should skip like a flat stone. Neither half alone
is a rock, and `RockDefinition.IsUsable` requires both.

**One tuning, not two.** There used to be an Authored/Derived pair compared against each other,
along with `MatchSettings.TuningSet`. That split is gone. A stale `tuningSet` key in an old
`match-settings.json` parses harmlessly and is dropped on the next save.

### The catalog's order is load-bearing, twice

It is the left-to-right layout of the stones on the shore, and it is the index a phone's pick
resolves against. `RockCatalog.At(index)` **wraps rather than throwing**, so a stale index cannot
crash a turn.

## The split — three numbers and nothing else

A rock owns exactly:

| Field | Range | What it does |
|---|---|---|
| `SpinMultiplier` | bounded | How much spin this rock carries |
| `VerticalMultiplier` | bounded | How high it hops |
| `HorizontalMultiplier` | bounded | How far it carries |

Everything else is global, on `Resources/SkipConstants.asset`: gravity, water height, the speed
scale, `EnergyLoss`, the forgiveness bands, `ReferenceSpeed`, the fixed step.

**Why the line is drawn there.** `SkipTuning` used to have 25 fields, most of them fighting each
other, and the throw's own power barely moved the result next to a one-point tuning edit. The
model now derives energy loss from bounce geometry rather than an authored retention constant, so
there is nothing left to author except how strongly *this* rock expresses that geometry.

`EnergyLoss` above all is global, because it answers "how does this game feel" rather than "what
is this rock" — **and a rock must not be able to quietly become easier instead of stronger.**

The constraint is structural, not validator-enforced: a rock has no field to disagree with.
`RockCatalog.Validate()` used to enforce that every rock's tuning agreed on shared fields like
`MaxSpeed` and `Gravity`; those fields no longer exist, so it cannot drift.

## Current state — the rocks differ visually, not mechanically, and that is now the design

All six tunings ship identical values:

```
SpinMultiplier: 1
VerticalMultiplier: 1
HorizontalMultiplier: 1
```

**This is deliberate** (`SkipTuning.cs:22`): the physics is being evaluated bare first, so that
every difference between two throws is the throw.

**As of 2026-09-08 it is also the settled design, not a gap.**
[`../plans/presentation-and-ui.md`](../plans/presentation-and-ui.md) D5 keeps the rocks
mechanically identical and differentiates them by mesh and silhouette, with the chosen rock
**named on the scoreboard**. [`../Roadmap.md`](../Roadmap.md) milestone 8's acceptance criterion
was rewritten to match. This document previously said the identical tunings were "the one thing
standing between the project and milestone 8" — they are not, any more.

Two consequences for anyone working in this file's area:

- **Do not give a rock its own tint.** `RockDefinition` carries a `Tint` field and the catalog
  builder preserves hand-edits to it, but the **player's colour always wins** at render time.
  Colour is how five people tell themselves apart on a TV; a player's blue rock rendered in the
  rock's own grey reads as a bug, not as flavour.
- **Mechanical character is deferred, not cancelled.** When it is picked up, put the six rocks on
  **one** legible axis — flat-and-light skips more but travels less, heavy travels far but skips
  less. One axis fits in a sentence on the phone's choose screen; three axes are noise and a
  balance problem nobody has playtest budget for.

## Selection

`RockSelection` is the only source of truth for what any player is throwing.

- **`Pick(playerId, rockId)`** is refused outside a choosing phase — a `PICK_ROCK` arriving
  mid-turn does not change the stone in flight.
- **`For(playerId)`** returns their pick, else the catalog's first rock. It degrades gracefully:
  the instant an entry is gone it falls back to the default, exactly as before they ever picked.
- **A reconnecting player keeps an existing pick.** The join fires this again; leaving it is
  intentional.
- **Every fresh Lobby and every mid-match Choosing pause re-picks from scratch.**
  `TurnManager.IsChoosingPhase` is why the two are treated as one case.
- **Random mode** assigns everyone a rock from a logged seed. `LastRandomSeed` is exposed rather
  than only logged, so `Verification/RockTelemetry` can stamp it on every throw and a run is
  reproducible. It is **cleared** when players pointed at their own picks, because carrying the
  previous random match's seed would label those throws as reproducible from a draw that never ran.
- It iterates `TurnManager.Players` — the roster — never the connection list.

Players choose by **pointing the phone at the shore** (`ShoreRocks.cs`), which uses
`PhoneAttitude.HeadingDirection` like everything else that needs a heading. `PICK_ROCK` confirms
the highlighted stone.

## Editor tooling

| Menu item | Does |
|---|---|
| `RockSkipping/Rocks/Build Catalog from FBX` | Regenerates `Rock_0N.asset` + `RockCatalog.asset` from the FBX. **Idempotent** — preserves hand-edited display names, tints and tuning references |
| `RockSkipping/Rocks/Validate Catalog` | Names any rock with no mesh, no tuning, or an id another rock already uses |

Hand edits to generated files survive a rebuild by design; that is what makes the builder safe to
re-run.

## Invariants

- **A global belongs in `SkipConstants`, never on a rock.**
- **The catalog order is the shore order and the pick index.** Reordering changes both.
- **`RockSelection` is the only answer to "what is this player throwing".**
- **`At(index)` wraps, never throws.**
- **A missing catalog degrades to an empty catalog with a warning**, not a crash — but note that
  `CreateDefault()` returns an *empty* catalog, so reaching it means no rocks at all.

## Traps

**`mesh.vertices` is unavailable.** `RocksRemeshed.fbx.meta` sets `isReadable: 0`. Use
`mesh.bounds` and `mesh.vertexCount`.

**`[SerializeField]` defaults are dead on components built via `AddComponent`.** `RockSelectTuning`
and its siblings exist specifically because runtime code builds these objects rather than
instantiating an Inspector-configured prefab. Values must come from C# initializers.

**`[SerializeField] private` fields keep bare camelCase here** while plain privates use
`_camelCase`. A documented deviation: renaming risks silently dropping Inspector-set values across
roughly 70 fields for a naming-taste win.

**New shaders must be URP.** A Built-in surface shader renders **magenta**. Check in under
`Assets/Resources/` and load with `Resources.Load<Shader>`, not `Shader.Find` — and go through
`PlaceholderArt.cs`, the single material factory.

## Verification

`RockSkipping/Rocks/Validate Catalog`, then `RockSkipping/Verify/Print Skip Curve` — which sweeps
**per rock**, and therefore currently shows all six behaving identically. **That is now the
expected result, not a failure** (see "Current state" above): the sweep's job here is the energy
invariant every run must satisfy, not proving the rocks apart. It becomes a differentiation check
again only if mechanical character is ever authored.
