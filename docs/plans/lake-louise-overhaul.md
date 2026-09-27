# Lake Louise graphics overhaul — SVG → Blender → shaders

**Status:** stages 1–3 built and checked in Blender renders, 2026-09-27; **Unity side written, not yet run in the Editor**. **Tier 5** against [`../Roadmap.md`](../Roadmap.md)
milestone 9. Builds on [`alpine-lake-level.md`](alpine-lake-level.md), which already settled the
low-poly technique, render-queue rules and budget; this plan keeps all of those and changes
three things, listed under *What changes from the alpine-lake plan*.

## The goal

The game's lake should read as Lake Louise: turquoise glacial water, a forested V-valley with a
tall grey rock face on the left and a lower forested ridge on the right, a white glacier filling
the gap at the far end, deep blue sky with a few cumulus clouds, and the mountains mirrored in
the water.

## Pipeline — three stages, each checked with a picture

| Stage | Output | Checked by |
|---|---|---|
| 1. SVG | `tools/art/lake-louise/svg/` — a concept frame of the game's own view, the valley plan (top-down), the ridge height profiles, and the pine tree profile | Rasterised PNG next to the reference photo |
| 2. Blender | `tools/art/lake-louise/build_lake_louise.py` reads the SVGs and builds the terrain, forest and far glacier; exports `Assets/Environments/LakeLouise/LakeLouise.fbx` | Workbench render from the game camera next to the concept frame |
| 3. Shaders | Blender material that mirrors the Unity shaders (flat vertex-colour lit, snow/rock/forest by slope and height, turquoise water with mirrored scenery, sky gradient); Unity `.shader` files under `Assets/Resources/` | Blender render next to the reference photo |

**The SVGs are data, not just pictures.** The Blender script parses them: plan-view paths give
ridge crest lines and the shoreline, profile paths give crest heights along each ridge, and the
pine profile is lathed into the tree mesh. Changing a line in an SVG and re-running the script
changes the model.

## Camera the art is built for

Everything is judged from the game camera, not a free view: position (0, 2, −3), vertical FOV
60°, pitched 10.9° down (`AimPreview.PitchForHorizonFraction` for a horizon 1/3 down the frame),
16:9. The throw lane runs +z.

**The photo is not the game's framing.** In the photo the waterline sits ~73% down the frame; in
the game it sits 33% down, so two thirds of the screen is water. The mountains must read inside
the top third, which means they sit closer and rise to at most ~18° above the horizon. The
target is the photo's *elements and colours*, fitted to the game's frame.

## Palette (sampled from the reference photo)

| Element | Colour |
|---|---|
| Sky, top | `#3c70d5` |
| Sky, horizon | `#8cbbfc` |
| Cloud | `#dce4fb` |
| Snow / glacier | `#e6ebf3`, shadowed `#8fa3c4` |
| Rock face (left, in shade) | `#2c3d57` |
| Rock (right, lit) | `#8a8a86` |
| Forest | `#26301f` → far `#3c4d5a` (haze) |
| Water, near | `#2f9aa3` |
| Water, reflection zone | `#295459` |

## Placement limits (unchanged from the alpine-lake plan)

- Far peaks inside z ≈ 850 (far clip 1000).
- Valley walls no closer than x = ±120 at the throw end; throws cover ~50 m.
- Water stays at y = 0 (`SkipConstants.WaterY`); water plane, `WaterRipples` and
  `SkipCurve.cs` untouched.

## What changes from the alpine-lake plan

1. **An SVG stage comes first**, and the Blender script consumes it (see above).
2. **Blender runs in the cloud session too**, as the `bpy` 4.5 Python module, so geometry and
   previews can be produced and checked without the Windows machine. The same script runs under
   `blender.exe --background --python` there.
3. **Horizon framing** is the game's, not the photo's (see *Camera*).

## What this session cannot verify

Unity is not installed in the cloud container. The FBX and the Unity shader files are produced
here, but **importing, compiling and screenshotting them in Unity is untested** until run in the
Editor. The Blender previews are the verification for stages 1–3; Unity scene assembly (steps 2–3
of the alpine-lake plan) remains.

## Budget

As the alpine-lake plan: scenery ≤ ~80k tris including reflections, no textures, no depth or
opaque texture. The build script prints per-object triangle counts and fails over budget.

## Where it stands (2026-09-27)

| Piece | Where | Verified |
|---|---|---|
| Concept + data SVGs | `tools/art/lake-louise/svg/` | Rasterised and compared to the photo |
| Blender build, FBX | `tools/art/lake-louise/build_lake_louise.py` → `Assets/Environments/LakeLouise/LakeLouise.fbx` | Runs; 48,303 tris of a 55,000 budget; FBX read back with all five objects and vertex colours |
| Look numbers | `Assets/Environments/LakeLouise/LakeLouiseLook.json` | Read by both the preview and Unity |
| Blender shader stand-ins | `tools/art/lake-louise/shaders.py`, `preview.py` | Rendered from the game camera; two runs pixel-identical |
| Unity shaders | `Assets/Resources/LakeLouiseBackdrop.shader`, `LakeLouiseWater.shader`, `SkyBackdrop.shader` (new properties, old defaults) | **Not compiled** — no Unity in the cloud session |
| Scene wiring | `Assets/Scripts/Game/LakeLouiseEnvironment.cs`, `AimPreview.BuildPlaceholderScene`, `Assets/Editor/LakeLouiseSetup.cs` | **Not compiled** |

### Regenerate

```
python3 tools/art/lake-louise/build_lake_louise.py                     # FBX (or: blender --background --python ...)
python3 tools/art/lake-louise/preview.py tools/art/out/shaded.png --mode shaded
```
Needs the `bpy` 4.5 wheel (`pip install bpy==4.5.0`) plus `pillow` and `cairosvg` for `compare.py`.

### First run in Unity

1. Open the project; the FBX imports with `LakeLouiseModelImport`'s settings.
2. **RockSkipping → Environments → Create Lake Louise Scene** makes `Assets/Scenes/LakeLouise.unity`.
3. Press Play in that scene. The Console should show `[LakeLouiseEnvironment] Orientation OK`.
   An orientation error means the FBX axis mapping (reasoned, not observed) is wrong — fix
   `export_fbx` and re-export.
4. If the scenery looks washed out or too dark, toggle `_VertexColourIsSrgb` on the backdrop
   material first (vertex-colour decoding is the least certain assumption).
5. Play `Bootstrap.unity` too: it must look exactly as before (sand bank, blue water).

### Known differences between the preview and Unity

- Cloud and water-streak *shapes* differ (Blender Perlin vs the shaders' value noise); amounts match.
- Reflections: Blender uses a true mirror, Unity mirrored copies. Same image for this
  flat-lit look, except reflections are hazed slightly more in Unity.
