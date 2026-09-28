# Rock rework — 2D references, then 3D models

**Tier 5 plan.** Status, 2026-09-28: **Phase 2 built.** `rock_shape.py --check` passes, including
the thickness rule (every rock is under 0.25), and `make_sheets.py` writes the sheets. Nugget and
Boulder were raised on the owner's call so Boulder is the thickest: Boulder 19.8 cm (0.22), Pebble
15.8, Nugget 14.7 (0.24). The Slab outline was also adjusted on the owner's call to be less
square (`n` 3.5 -> 2.6, `p` 4 -> 3, 2026-09-28) — already reflected in the values table below.

`build_rocks.py` built and exported `Assets/Resources/Fbx/Rocks.fbx` (run twice, byte-identical
output both times — 49644 bytes), `preview.py` rendered `tools/art/rocks/preview.png`, and the
`RockCatalogBuilder.cs` change (`FbxPath` -> `Rocks.fbx`, `LocalScale = Vector3.one`,
`NormalisedScale` removed) is in place. All of that is verified from this machine — see the
per-rock Blender console output and the preview-vs-sheet comparison in this session's report.

**Still the owner's to run, in the Unity Editor (this machine's Unity holds an exclusive lock
that a headless session cannot open):**
- `RockSkipping/Rocks/Build Catalog from FBX`, then `RockSkipping/Rocks/Validate Catalog` —
  confirm the Console shows six rocks with sensible world sizes next to the 0.75 m reference
  footprint, and the catalog validates clean.
- `RockSkipping/Verify/Print Skip Curve` — confirm all six rocks still come out identical (D5);
  mesh changes should not affect the physics, but this is the check that proves it.
- Play `Bootstrap` and look at the six rocks on the shore and in flight — the actual visual
  gate this whole rework has been building toward.
- Watch for shore-rock overlap now that Boulder and Shard are longer than the old 0.75 m
  stone — see this session's `ShoreRocks` spacing finding below; the numbers say there is
  comfortable margin, but only Play confirms it.

## Why

Two problems with the six rocks from `Assets/Resources/Fbx/RocksRemeshed.fbx`:

1. **The shapes do not match the names.** The meshes import as `NurbsCurve.00N`, and the mapping
   to Skimmer/Slab/Pebble/Nugget/Shard/Boulder was arbitrary on first build
   (`Assets/Scripts/Editor/RockCatalogBuilder.cs:54`). A "Boulder" is not visibly a boulder.
2. **They are too similar on a TV.** At couch distance six irregular grey blobs read as one.

[`presentation-and-ui.md`](presentation-and-ui.md) D5 says rocks differ *visually only*, so the
mesh is the whole of a rock's identity. D2 sets the style: flat-shaded low-poly, strong
silhouette. The player's colour always tints the rock (D5), so **shape is the only channel** —
outline, proportions and height.

## Archetypes

One silhouette rule each, chosen so no two share an outline family:

| # | Name | Top outline | Side profile | Reads as |
|---|---|---|---|---|
| 01 | Skimmer | near-circle coin | thin, even lens | a coin-thin disc |
| 02 | Slab | rounded rectangle | flat faces, rounded edge | a tile |
| 03 | Pebble | classic oval | smooth lens | the default stone |
| 04 | Nugget | rounded triangle | chunky, flattish base | a knuckle of rock |
| 05 | Shard | slate flake, one end broader | thin, slightly wedged | a flake / arrowhead |
| 06 | Boulder | big egg / D-shape | flat bottom, domed top | a heavy lump, still skippable |

Shore order and ids are unchanged (`rock_01`…`rock_06`), so telemetry, tunings and names carry
over.

**Realism rule (owner, 2026-09-28):** rocks must look like they could realistically skip — flat,
smooth, water-worn edges, proportions only slightly embellished. Hard rule: **thickness ÷ length
< 0.25 for every rock**, measured on the final mesh as `bbox_y / max(bbox_x, bbox_z)`. `--check`
asserts this and prints the ratio per rock.

## Single source of truth: one shape function, two consumers

The 2D sheets and the 3D models are generated from **the same parameters by the same code**, so
the approved drawing and the shipped mesh cannot drift:

```
tools/art/rocks/rocks.json      six parameter sets (below)
tools/art/rocks/rock_shape.py   pure Python, no deps: params -> (vertices, faces)
   ├─ tools/art/rocks/make_sheets.py   plain python  -> tools/art/rocks/svg/*.svg + index.html
   └─ tools/art/rocks/build_rocks.py   Blender 4.5   -> Assets/Resources/Fbx/Rocks.fbx
```

Follows the Lake Louise precedent (`tools/art/lake-louise/`). `bpy` is **not** importable from
the system Python on this machine; Blender scripts run as
`"C:\Program Files\Blender Foundation\Blender 4.5\blender.exe" --background --python <script>`.

### Parameters (`rocks.json`), metres, world size

Units are world metres at `LocalScale = (1,1,1)`. The current stone's footprint is 0.75 m
(`RockObject.prefab`, read by `ReadReferenceStone`), so Pebble is 0.75 m long — the size every
player has seen until now.

| Field | Meaning |
|---|---|
| `a`, `b` | top-outline half-length (x, long axis) and half-width (z) |
| `n` | superellipse exponent of the top outline: 2 = ellipse, 4+ = rounded rectangle, <2 = pinched |
| `tri` | 0..1, radial scale of (x, z) by `1 + tri*cos(3θ)` — rounded-triangle corner at +x. Nugget only |
| `egg` | broadens +x / narrows -x: `z *= 1 + egg*(x/a)`, clamped ≥ 0.2 — D/teardrop shape. Shard, Boulder |
| `taper` | 0..1, narrows z toward +x: `z *= 1 - taper * max(0, x/a)`. Currently unused (0 for all six) |
| `wedge` | thickens toward +x: `y *= max(0.2, 1 + wedge*(x/a))`, apexes unaffected. Shard only |
| `h` | total thickness |
| `p` | superellipse exponent of the side profile: <2 = sharp rim, 2 = lens, 5+ = vertical sides |
| `bottomFlat` | 0..1, squashes the lower half's y toward the equator |
| `seg` | vertices per ring (fewer = bigger facets) |
| `rings` | rings per half, between equator and apex |
| `jitter` | seeded vertex noise amplitude, metres: radial ±jitter, vertical ±jitter/2 |
| `seed` | per-rock RNG seed |

Current values (tuned 2026-09-28 for the realism/0.25-ratio rule):

| | a | b | n | tri | egg | taper | wedge | h | p | bottomFlat | seg | rings | jitter |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Skimmer | .35 | .33 | 2.0 | 0 | 0 | 0 | 0 | .08 | 2.2 | 0 | 16 | 2 | .004 |
| Slab | .42 | .28 | 2.6 | 0 | 0 | 0 | 0 | .13 | 3.0 | 0 | 16 | 2 | .004 |
| Pebble | .37 | .27 | 2.0 | 0 | 0 | 0 | 0 | .16 | 2.0 | 0 | 14 | 3 | .004 |
| Nugget | .31 | .29 | 2.0 | .12 | 0 | 0 | 0 | .17 | 2.5 | .3 | 12 | 3 | .008 |
| Shard | .43 | .20 | 1.8 | 0 | .35 | 0 | .3 | .07 | 1.8 | 0 | 9 | 2 | .015 |
| Boulder | .45 | .36 | 2.2 | 0 | .2 | 0 | 0 | .27 | 2.3 | .5 | 14 | 3 | .008 |

### Geometry (`rock_shape.py`)

Coordinates: x right (long axis), y up, z forward — Unity's frame. Origin at the bounding-box
centre.

- **Ring outline**, vertex `i` of `seg`, angle `θ = 2πi/seg`:
  `x = a·sgn(cosθ)|cosθ|^(2/n)`, `z = b·sgn(sinθ)|sinθ|^(2/n)`, then in order: `tri` (radial
  scale by `1 + tri*cos(3θ)`, a rounded-triangle corner at +x), `egg` (`z *= 1 + egg*(x/a)`,
  clamped ≥ 0.2, broader at +x / narrower at -x), then `taper`.
- **Rings** at `t_k = k/(rings+1)` for `k = -rings..rings`; ring y = `t·h/2`, ring scale
  `s(t) = (1 - |t|^p)^(1/p)`. For `t < 0`, y is multiplied by `(1 - bottomFlat)`. Then `wedge`:
  `y *= max(0.2, 1 + wedge*(x/a))` using the ring vertex's pre-jitter x; apexes (x=0) unaffected.
- **Apexes**: one top and one bottom vertex at `t = ±1`, centre.
- **Jitter**: seeded, applied after, to every vertex.
- **Faces**: quads between adjacent rings, triangle fans to the apexes, outward winding.
- Re-centre on the bounding box after jitter.

Pure function, deterministic for a given parameter set. It returns plain lists, so both plain
Python and Blender can use it.

## Phase 1 — 2D reference sheets

`make_sheets.py` (system Python 3.13, stdlib only) writes:

- **`svg/Rock_0N_<Name>.svg`**, one per rock: top, side (x–y) and front (z–y) orthographic views
  of the actual mesh. Faces are drawn back to front, filled by a flat Lambert term off the face
  normal, in the flat-shaded grey of D2. Each view has overall dimensions in centimetres. A small
  solid-black silhouette of the 3/4 view sits in the corner.
- **`svg/index.html`**, the comparison page: all six sheets side by side on a shared scale, plus
  a **TV-distance strip** — the six silhouettes at roughly the pixel size a shore rock occupies
  on a 1080p TV (about 40 px wide), in a 3/4 view. That strip is the acceptance check for
  problem 2.

**Gate:** the owner looks at `index.html` and approves or asks for changes to `rocks.json`.
Phase 2 does not start until then.

## Phase 2 — 3D models

- `build_rocks.py` (Blender, headless) builds six meshes from `rock_shape.py`, named
  `Rock_01_Skimmer` … `Rock_06_Boulder` so `RockCatalogBuilder`'s ordinal name sort matches shore
  order. It triangulates, flat-shades, and exports `Assets/Resources/Fbx/Rocks.fbx` with the Lake
  Louise export settings (`build_lake_louise.py` `export_fbx`: `-Z` forward, `Y` up, transform
  baked).
- `preview.py` renders the six from Blender to a PNG, as a side-by-side check against the sheet.
- `RocksRemeshed.fbx` **stays** until the new rocks have been seen in game. Removing it is the
  owner's call.

### Builder change — needs approval at the Phase 1 gate

`RockCatalogBuilder.NormalisedScale` rescales **every** mesh to the same 0.75 m footprint and
squashes y by a catalog-wide 0.52. Run as-is on the new meshes, it would erase the size
differences and halve Boulder's height — undoing half of this rework.

Proposed: point `FbxPath` at `Rocks.fbx`, and set `LocalScale = (1,1,1)` for it, because the
meshes are authored at world size. The build log keeps the per-rock world size next to the
reference footprint, so a size problem is visible in the Console. `ReadReferenceStone` stays for
that report.

### Risks to check in game

- **Shore spacing.** Boulder (0.90 m) and Shard (0.86 m) are longer than today's 0.75 m stone.
  `ShoreRocks` spacing may need widening if neighbours overlap.
- **Aim ghost and trail** use `rock.LocalScale` (`AimPreview.cs:288`,
  `RockLauncher.cs:253`), so they follow automatically. The physics does not read mesh size.
- **`Print Skip Curve`** must still show all six identical (D5). Mesh changes should not affect
  it, which is why the check is worth running.

## Verification

| Step | Who | Evidence |
|---|---|---|
| Sheets generate, six SVGs + index | Claude | script output, page opened in browser |
| Silhouettes distinct at TV size | Owner | approval at the gate |
| FBX builds, triangle counts printed | Claude | Blender console output |
| Blender renders match the sheets | Claude | preview PNG |
| `Build Catalog from FBX` + `Validate Catalog` clean | Owner (Editor lock) | Unity Console |
| Rocks on shore and in flight look right | Owner | Play `Bootstrap` |
