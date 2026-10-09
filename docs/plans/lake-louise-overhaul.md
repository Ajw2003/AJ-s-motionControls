# Lake Louise graphics overhaul — SVG → Blender → shaders

**Status:** stages 1–3 built and checked in Blender renders, 2026-09-27; **Unity side written, not yet run in the Editor**. **Tier 5** against [`../Roadmap.md`](../archive/rock-skipping/Roadmap.md)
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
| Concept + data SVGs | `tools/art/lake-louise/svg/` (shape from `photo-silhouettes.svg` since revision 2) | Rasterised and compared to the photo; skyline checked with `skyline_overlay.py` |
| Blender build, FBX | `tools/art/lake-louise/build_lake_louise.py` → `Assets/Environments/LakeLouise/LakeLouise.fbx` | Runs; 48,304 tris of a 55,000 budget; FBX read back with all five objects, vertex colours and upward terrain normals; two exports geometry-identical |
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

## Revision 2 (2026-09-27): shape from the photo, crisp clouds, visible boathouse

**Shape.** `svg/photo-silhouettes.svg` holds the reference photo's three silhouettes (left
massif, glacier, right ridge), traced by hand in photo pixels. The build turns each traced point
into a direction from the camera and sets every crest sample's height so it lands on that line.
It replaces `ridge-profiles.svg` (deleted), whose heights were fitted to the concept drawing.
Both side crests now curl in to the lake's end, as the photo's ridges do at the V.

- **The one compromise:** heights are scaled by 0.47 (`data-elevation-scale`). The photo's left
  summit is ~31° above the water; the game frame reaches ~19° at top centre and ~14° in its
  corners. Left-to-right positions match the photo; slopes are about half as steep. Getting
  closer means showing more sky, i.e. moving the horizon lower than 1/3 in
  `AimPreview.HorizonScreenFraction` - a gameplay framing call, not made here.
- **Checked by** `skyline_overlay.py`, which draws the traced lines through the game camera onto
  a render. Measured model skyline vs photo on the right ridge: within 0.3° from 14° to 38°.
- Colour zones now follow which ridge shapes the ground (glacier wall vs side ridges), not a
  z cut-off; a cut-off either whitened the side cliffs or forested the glacier's scree.
- Ground no camera position can see is culled before decimation, so the triangle budget goes
  to visible crests; grid spacing 7 m → 4.5 m.

**Boathouse.** Moved to the photo's position (left shore, 31° left of centre); at 53° it had been
outside the game's 45.7° half-frame. Scaled 2× in place (`data-scale`), no pines within 40 m
(`data-clearing`), canoes turned across the view. **Bug fixed:** the wall block was built
inside-out - invisible in Blender (both sides drawn) but it would have vanished under Unity's
back-face culling. Now checked by ray parity: 0 of 51 faces inward.

**Clouds.** Narrow edge (`cloud_edge` 0.03 vs 0.22), a finer noise octave (`cloud_detail`), and
self-shadowing: the noise is read again toward the sun and each puff is shaded from
`cloud_shadow_colour` to `cloud_colour`. `SkyBackdrop.shader` gained `_CloudEdge`,
`_CloudDetail`, `_CloudShadowColour`, `_CloudShade`, `_CloudShadeOffset`; defaults reproduce the
old clouds, so Bootstrap's sky is unchanged.

**Light.** Sun swung to the left (the photo's left cliff is in full shadow, the right ridge lit),
haze reduced and started later, sky gradient eased. Sampled against the photo: low sky #7ea9ee
vs #80b2fd, right rock #8c8987 vs #8d8480; the left cliff (#414b5a vs #263756) and glacier
(#cacdd5 vs #eeeff0) are still lighter and dimmer than the photo respectively.

Still unverified in Unity, as before: the shaders and C# have not been compiled.

## Revision 3 (2026-09-28): first Unity test - orientation, scale, water lines

First run in Unity (by the user): everything worked except these three.

- **Valley behind the camera and mirrored.** The export used `axis_forward='Z'`, chosen by
  reasoning; Unity imported the whole valley turned 180° about y. That observation pins Unity's
  importer rule down: it reads a file's (x, y, z) as (x, y, -z). `export_fbx` now uses Blender's
  standard `-Z` forward, whose file holds (X, Z, -Y), so Unity gets (X, Z, Y). Checked on the new
  file's raw coordinates under that rule: Forest_Left x = -210, valley z = +488; the same rule
  applied to the old file predicts x = +210, z = -364, which is what the test showed.
  `LakeLouiseEnvironment` now also corrects a 180° turn itself, with a warning.
- **Too small - throws reached the mountains.** `scenery_scale` (2.5) in `LakeLouiseLook.json`
  scales the scenery about the camera at the waterline, so the picture from the throw position is
  unchanged and everything is 2.5× farther. The water plane, the camera's far clip
  (1000 → 2700) and the haze distances scale with it. Blender preview renders with the same
  scale: visually identical to the unscaled render.
- **Dark lines on the far water.** The wind-streak noise aliases into hard lines once its cells
  shrink below a pixel. Streaks now fade out by `water_streak_fade_distance` (120 m), in the
  Unity shader and the preview; the banding visible near the horizon in earlier previews is gone.
  That this was the cause of the lines seen in Unity is the likely explanation, not yet
  confirmed there.

### Open: dark horizontal lines on the far water (2026-09-28)

Still seen in Unity after revision 3. What is known, from the user's screenshots and renders here:

- **Before revision 3** the lines were a green-only oscillation in the far water. Green is the
  channel that differs between the water body and what shows through it, so that was the water's
  opacity flickering: the unfaded wind streaks. Fixed by the streak fade.
- **After revision 3** what remains are thin dark rows in all channels, only inside the
  reflected forest and mountains: a problem in the mirrored scenery, not the water surface.
- **Ruled out here:** (1) back-face culling of the mirrored copy - a Blender render that skips
  back faces on reflection rays shows no lines; (2) a full Unity-style reconstruction (mirrored
  copies clipped at the waterline, culled, under alpha-blended water) shows no lines;
  (3) colour banding from URP's R11G11B10 HDR buffer - quantising a float render to it changes
  52 blue levels to 48 and adds no lines.
- **Isolated in the Editor (user, 2026-09-28):** the lines come from BOTH the water and the
  mirrored copy; only with both disabled is the (mirrored-sky) water clean.
  - Water share: the wind streaks, still partly on 50-120 m out, where a streak cell is a pixel
    or two tall at the game's grazing angle. `water_streak_strength` is now 0.
  - Mirror share: the mirrored copy was drawn with back faces culled; Blender's mirror (the
    clean previews) never culls. The mirrored copy is now double-sided (`_Cull` Off on the
    reflection material only). **Unverified** - Blender cannot show the difference; needs a
    Unity run.
