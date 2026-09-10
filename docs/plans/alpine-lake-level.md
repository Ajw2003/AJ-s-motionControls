# Alpine Lake level — low-poly take on the Lake Louise reference

## Context

RockSkipping has only one "level", and it isn't a scene. `AimPreview.BuildPlaceholderScene()`
(`RockSkipping/Assets/Scripts/Game/AimPreview.cs:680`) builds everything in code at startup: a water plane,
a sky quad drawn behind the camera, a sand bank with dunes, fog and the sun. `Bootstrap.unity` holds only a
camera, a light and the `Bootstrap` object.

The goal is a separate, better-looking level. It starts as a copy of `Bootstrap.unity`, is styled after the
reference photo (turquoise lake, mirror reflections, forested V-valley, glacier at the far end, rocky
foreground, sun starburst), stays flat-shaded low-poly, and costs little to render.

The approach, as chosen:
- **Reflections:** mirrored copies of the scenery under a see-through version of the current water.
- **Scene assembly:** Unity MCP, driving the open Editor live.
- **Geometry:** headless Blender Python scripts.
- **Reaching the level:** open the scene and press Play. No level select.

**Installed on this machine (checked):**
- Unity 6000.3.15f1
- Blender 4.5 at `C:\Program Files\Blender Foundation\Blender 4.5`
- `uv`/`uvx` in `C:\Users\aj\.local\bin`
- Python 3.13
- git

`environment.md` does not exist. Recording these facts in it is step 0.

## How the reference breaks down into cheap pieces

| Reference element | Low-poly technique | Cost |
|---|---|---|
| Mountains and glacier | Blender script: seeded noise-displaced grids shaped into a V-valley, decimated, flat-shaded (split normals baked in). Vertex colours by height and slope: forest green, grey rock, white snow. | ≤12k tris, no textures |
| Dense pine forest | Same script scatters 10-tri pines (two stacked 5-sided cones) on faces below the treeline with gentle slopes. They are merged into a few chunk meshes: left, right and far. | ≤25k tris, 3 draws |
| Foreground boulders | Same script: about 50 displaced icospheres, varied grey/tan/rust vertex colours, some half under the waterline, merged into one mesh. | ≤4k tris, 1 draw |
| Mirror reflections | The same mesh prefabs placed again with Y scale −1 about `WaterY`. They use a darker, bluer material at queue 1890. The water becomes alpha-blended so they show through. Left side reads blue and right side turquoise-green, as in the photo. | Redraws only the backdrop meshes |
| Turquoise water | New material on the existing `WaterRippleURP.shadergraph`, which already has material override enabled. Surface is Transparent with turquoise `_BaseColor` at alpha ≈0.5 and custom queue 2000. Ripples, `WaterRipples.Bind` and the sink depth toggle all keep working. | None extra |
| Sky gradient and wisps | Existing `SkyBackdrop.shader`, with new colours (`_SkyTop` deep blue, `_SkyHorizon` pale) and low `_CloudAmount`. | None extra |
| Sun starburst | URP Lens Flare (SRP) component using the flare from URP's "Lens Flare Samples". It sits on a separate far-away `SunFlare` object at the upper left, with occlusion off so no depth texture is needed. The real directional light stays high: `AngleTheSun`'s notes record that a low sun makes the stones look flat. | One screen pass |
| Clarity and haze | A global Volume with Bloom (threshold ~1), Neutral tonemapping, +saturation and contrast, and a light vignette. Linear fog tinted to the horizon. The valley walls hide the far edge of the water plane. | Normal URP post-processing |

Render-order rules from the existing code stay in force:
- Backdrop meshes use queue ≤1900, and their reflections 1890.
- The water stays at queue 2000 with ZWrite off. It still depth-tests, so mountain bases correctly hide it.
- Backdrop meshes cast no shadows, are marked batching-static and share one material, which keeps them SRP Batcher-friendly.

## Steps

### 0. Tooling
0. Copy this plan into the project as `RockSkipping/docs/plans/alpine-lake-level.md`. Plan mode blocked
   writing it there directly.
1. Write `rules/environment.md` in the active house-rules install. Confirm which install is active with
   `claude plugin list`. Record Blender, uv, Unity and Python versions and paths.
2. **Unity MCP.** Use the `unity:unity-cli` skill to add `com.unity.ai.assistant` to
   `RockSkipping/Packages/manifest.json`, start the Unity Bridge and register it with Claude Code.
   - You may need to sign in to Unity or accept Unity AI terms. You do that part; I don't accept terms.
   - Claude Code needs a new session afterwards so the MCP tools load.
   - **Fallback if it's blocked:** editor menu commands, checked with `tools/capture-scene.ps1`. That
     means adding a scene-path argument to `Assets/Editor/SceneCaptureRunner.cs`, which currently has
     `Bootstrap.unity` hard-coded.

### 1. Geometry (Blender, headless)
- `RockSkipping/tools/art/alpine_lake.py`
  - Run as `blender.exe --background --python tools/art/alpine_lake.py`.
  - Builds, from one seed and a parameter block at the top of the file:
    - mountains: `Mountains_Left`, `Mountains_Right`, `Peaks_Far`
    - forest: `Forest_Left`, `Forest_Right`, `Forest_Far`
    - `Shore_Near`
  - Prints the triangle count per object and fails if any is over budget.
  - Exports `RockSkipping/Assets/Environments/AlpineLake/Meshes/AlpineLake.fbx` with Unity axes (Y-up, −Z
    forward, metres, transforms applied).
- `RockSkipping/tools/art/preview.py`
  - Renders a Workbench vertex-colour PNG to `tools/art/out/` (gitignored).
  - Its camera matches the game's: position (0, 2, −3), the same FOV, and the pitch from
    `AimPreview.PitchForHorizonFraction`.
  - I compare that PNG with the reference photo and adjust the parameters before touching Unity.
- **Placement limits:**
  - Far peaks within z ≈ 850, since the camera's far clip is 1000.
  - Valley walls start around x = ±120 so throws (~50 m) are never blocked.
  - The foreground rock band sits at z ≈ −0.7…1.5, the strip visible at the bottom of the frame. It stays
    clear of the six selectable stones at `ShoreRocks` `ShoreRadius`.

### 2. Code: a small environment preset (the first slice of D12 in `docs/plans/presentation-and-ui.md`)
- **New `RockSkipping/Assets/Scripts/Game/EnvironmentPreset.cs`**, a ScriptableObject with:
  - sky top and horizon colours, cloud amount
  - fog start and end
  - sun colour, intensity and rotation
  - water material
  - `buildPlaceholderShore`
- **New `RockSkipping/Assets/Scripts/Game/SceneEnvironment.cs`**, a MonoBehaviour placed in the scene that
  holds a preset reference. The reference is set in the Inspector, not through `Resources/`, which keeps
  the "Resources stays short" rule.
- **`AimPreview.BuildPlaceholderScene`:**
  - Calls `FindFirstObjectByType<SceneEnvironment>()`.
  - **No preset:** uses today's hard-coded values unchanged, so the `Bootstrap` look can't regress.
  - **Preset found:** takes the water material, horizon, sky, fog and sun values from it, and skips
    `BuildFarShore`.
  - `BuildSkyBackdrop` and `AngleTheSun` take their values as parameters instead of constants.
  - The water plane, `WaterRipples` and the `WaterPlaneScale` constants stay as they are, so
    `SkipCurve.cs` still passes.
- **`SceneNames.cs`:** add `AlpineLake`.
- **`Assets/Editor/ProjectSetup.cs`:** add the scene after Results. Bootstrap stays at index 0.
- Follow `csharp-unity-standards.md` and the project's heavy why-comment style (`Assets/CLAUDE.md`).

### 3. Scene assembly (via Unity MCP)
1. Copy `Assets/Scenes/Bootstrap.unity` to `Assets/Scenes/AlpineLake.unity`. The `Bootstrap` object keeps
   its Inspector values.
2. Create these assets under `Assets/Environments/AlpineLake/`:
   - A `VertexColourLit` URP Shader Graph in `Assets/Resources/`, following the project's shader rule. It
     feeds Vertex Color into Base Color with smoothness 0 and fog on, and has `_Tint` for the reflection
     variant.
   - Materials `Backdrop.mat` (queue 1900), `BackdropReflection.mat` (queue 1890, darker and bluer) and
     `LakeWater.mat` (transparent, queue 2000).
   - `AlpineLake.asset`, the preset.
3. FBX import settings: materials remapped to `Backdrop.mat`, no colliders, Read/Write off.
4. Place in the scene:
   - the backdrop prefab
   - a mirrored copy under a `Reflections` parent with Y scale −1, using `BackdropReflection.mat`
   - a global Volume with the profile `AlpineLake_Volume.asset`
   - a `SunFlare` object with a Lens Flare (SRP) component (import URP's Lens Flare Samples first)
   - `SceneEnvironment`
   - Turn Post Processing on for the Main Camera.
5. Tune colours, fog and flare live against the reference, taking Game-view screenshots through MCP.

### 4. Docs
- `docs/ProjectState.md`: a new Alpine Lake scene now exists.
- `docs/plans/presentation-and-ui.md` D12: note that the preset is delivered without a catalog.
- A short `tools/art/README.md` on regenerating the meshes. Re-exporting the FBX updates the scene
  automatically.

## Budget (acceptance)
- Scenery adds at most ~80k tris including reflections, and at most 20 SRP batches.
- No new textures.
- No depth or opaque texture turned on.
- Frame time in the Profiler is within ~1 ms of Bootstrap on this PC. Record both numbers.

## Verification
1. The Blender script prints per-object tri counts under budget. Preview PNG reviewed against the
   reference.
2. Unity Console shows no errors or magenta materials after import and compile.
3. **Regression:** Play `Bootstrap.unity`. An MCP screenshot matches today's look (sand bank, blue water).
4. Play `AlpineLake.unity`. Take an MCP Game-view screenshot and compare it side by side with the
   reference.
5. Throw a rock using the desktop two-tab flow from `README.md`. Check that:
   - ripples render on the turquoise water
   - the stone isn't cut in half while it sinks (the depth toggle still works on the transparent material)
   - the six selectable stones are visible and not hidden by `Shore_Near`
   - reflections stay under the water
6. `RockSkipping/Verify/Print Skip Curve` still passes its energy invariant. EditMode tests pass in Test
   Runner.
7. Frame Debugger and Stats: record tris, batches and frame time for both scenes against the budget.
