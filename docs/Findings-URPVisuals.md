# Findings — water and rock rendering after the URP migration

Source: `Recording 2026-08-23 161148.mp4` — 14.3s, 1806×1018, 30fps. Reviewed as 29 frames
sampled at 2fps and 1024px wide.

Two problems were reported: the rock intersecting the water badly, and ripples reading as
"poorly rendered particles". Both are real. **Neither has the cause it looks like it has**, and
three of the four root causes are configuration rather than shader work.

---

## What the video shows

| t | Frame | What's on screen |
|---|---|---|
| 0.0 | 1 | Aiming. Ghost stone at the origin, aim arc receding to the vanishing point. Lake is **vivid saturated blue**. HUD: "predicted 9 skips over 35.8 m" |
| 1.5 | 4 | Stone airborne, shadow ellipse on the water below it |
| 2.0 | 5 | First contact — a small speckled patch appears directly under the stone |
| 2.5 | 6 | Airborne again. No ripple visible at this instant |
| 3.0 | 7 | Large **speckled, granular** elliptical region. Reads as scattered dots, not a ring |
| 4.5–6.0 | 10, 13 | Stone flat on the surface; speckle spreading across a wide area |
| 8.0 | 17 | Stone visibly **cut by a hard straight line** at the waterline |
| 10.0–12.0 | 21, 25 | Stone is a shallow dome with a hard horizontal clip. Speckle covers most of the lower screen |
| 14.0 | 29 | Stone gone. Faint speckle still visible in the distance |

The aim arc looking like a vertical white spike at t=0 is **not** a bug — the camera looks along
+Z and the arc recedes toward the vanishing point, so it correctly projects as a near-vertical
line.

---

## Root causes

### 1. The project is still in Gamma colour space — URP requires Linear

```
ProjectSettings/ProjectSettings.asset:50
  m_ActiveColorSpace: 0        # 0 = Gamma, 1 = Linear
```

This is the single biggest visual error and it affects **everything** on screen, not just the
water. URP's lighting math assumes Linear; run in Gamma it produces oversaturated, wrong-valued
output. The vivid electric blue is partly this.

It's also the classic URP migration miss, and it's my omission — the migration checklist in
[`Migration-URP.md`](Migration-URP.md) never mentions colour space. That's a gap in the doc, not
something you did wrong.

**Fix:** Project Settings → Player → Other Settings → **Color Space = Linear**.

> Expect a long asset reimport, and expect everything to look *different* immediately — lighting
> values that were tuned in Gamma will read darker. That's correct; retune from there rather than
> reverting.

### 2. The water and crest colours are wrong values, and the crest property is orphaned

From `Assets/Resources/RockSkipping_WaterRipple.mat`:

| Property | Value in material | Status |
|---|---|---|
| `_BaseColor` | `(0, 0.063, 1)` | **Live** — pure saturated blue. Intended: `(0.06, 0.16, 0.28)` dark navy |
| `_CrestColor_1` | `(0.316, 0.889, 1)` | **Live** — bright cyan. Intended: `(0.55, 0.75, 0.95)` |
| `_CrestColor` | `(0.55, 0.75, 0.95)` | **Orphan** — correct value, bound to nothing |
| `_Color` | `(0.217, 0.445, 0.708)` | Orphan, Built-in leftover |
| `_Glossiness` | `0.5` | Orphan — URP uses `_Smoothness` |
| `_Metallic` | `0.5` | Orphan — the graph exposes no `_Metallic` reference |

The Shader Graph blackboard's crest property has reference `_CrestColor_1`, not `_CrestColor`.
Shader Graph appends `_1` when a reference name collides during authoring, and the suffix stuck.
So the *correct* crest colour you entered is sitting in a dead property, and the *live* one is a
much brighter cyan that was never intended.

**This matters for the ripple bug specifically.** The ring effect is a `lerp` from base to crest.
Going from saturated blue to bright cyan is a far larger colour delta than navy → pale blue, so
whatever noise exists in the wave gets amplified to maximum visibility. Cause 3 produces the
noise; this makes it scream.

**Fix:**
1. In the graph blackboard, select **Crest Color** → set Reference to `_CrestColor` (drop the `_1`).
2. On the material, set `_BaseColor` to `(0.06, 0.16, 0.28)` and `_CrestColor` to `(0.55, 0.75, 0.95)`.
3. Verify what's wired to **Metallic** and **Smoothness** on the master stack — the material's
   `_Metallic: 0.5` is orphaned, so the real values live in the graph. They should be **0** and
   **0.5**. Metallic above 0 on water kills the diffuse term and makes specular aliasing worse.

Good news from the same audit: `_RippleLife` and `_RippleSpeed` — the only two properties
[`WaterRipples.PushTuning`](../Assets/Scripts/Game/WaterRipples.cs) sets from C# — **do** match
the graph's reference names. That binding is fine.

### 3. The "particles" are shader aliasing — the rings are sub-pixel

This is the actual ripple bug, and it is not a particle system, a texture, or a precision problem.

The ring pattern is `sin((front - d) * 2π / wavelength)` with a wavelength of **2.5 world units**.
The lake is 2000 units across and the camera looks along it at a grazing angle, so a pixel near
the horizon spans many world units. Once one pixel is wider than half a wavelength, the sine is
being point-sampled far below Nyquist — consecutive pixels land on unrelated points of the wave.

That is not a ring any more. It is white noise shaped like a ring, which is exactly what
"poorly rendered particles" describes.

Consistent with the video: the speckle is worst at distance and in the wide flat region toward the
horizon, and the ripple at t=2.0 (frame 5, close to camera, small radius) looks comparatively
tighter than the one at t=3.0+ once it has expanded.

**Fixed — this one I've already applied**, in
[`Assets/Resources/WaterRipple.hlsl`](../Assets/Resources/WaterRipple.hlsl):

- Compute the world-space size of a screen pixel via `ddx`/`ddy` of `worldPos.xz`.
- Fade the ripple contribution out via `smoothstep` as the wavelength approaches twice that
  footprint, so rings dissolve smoothly into flat water instead of degenerating into noise.
- Early-out when nothing is resolvable, so distant pixels skip the loop entirely.

**Your Shader Graph needs no changes** — the function signature is unchanged, so the Custom
Function node's ports still bind. Unity will recompile on focus.

One implementation detail worth knowing, because getting it wrong is subtle: the derivatives are
taken **once, before the loop**. Screen-space derivatives are only defined under uniform control
flow, and the loop has two `continue` statements in it. Taking `ddx`/`ddy` inside would be
undefined behaviour that happens to work on some GPUs and returns garbage on others.

### 4. The stone is half-submerged by construction — URP revealed it, it didn't cause it

[`SkipPhysics.Step`](../Assets/Scripts/Game/SkipPhysics.cs#L168) does:

```csharp
next.Position.y = tuning.WaterY;
```

That clamps the stone's **centre** to the water plane. The stone is
[`StoneScale = (0.35, 0.25, 0.35)`](../Assets/Scripts/Game/RockLauncher.cs#L32), so its lower half
is below the surface at every bounce — always has been.

Under Built-in this was invisible: everything ran `ZWrite Off` with hand-pinned render queues, so
the stone simply drew over the water regardless of geometry. URP/Lit is opaque with depth writes
on, so the water now correctly occludes the submerged half — producing the hard straight cut
visible from frame 17 onward.

**So this is a pre-existing geometry bug that the migration exposed, not a regression the
migration introduced.** Worth saying plainly: the renderer is now telling the truth about
something that was always wrong.

---

## Fix plan

Ordered by payoff per effort. Steps 1–3 are config and should be done together, then re-recorded
before touching anything else — they may change how bad step 4 even looks.

### Step 1 — Colour space → Linear *(2 min, affects everything)*

Project Settings → Player → Other Settings → Color Space = **Linear**. Wait out the reimport.

**PASS:** The scene looks materially different — likely darker and less saturated. Don't judge
the water yet; step 2 hasn't run.

### Step 2 — Fix the colour properties *(10 min)*

1. Graph blackboard: **Crest Color** reference `_CrestColor_1` → `_CrestColor`.
2. Material: `_BaseColor = (0.06, 0.16, 0.28)`, `_CrestColor = (0.55, 0.75, 0.95)`.
3. Graph master stack: confirm **Metallic = 0**, **Smoothness = 0.5**.

**PASS:** The lake is deep navy, not electric blue. Ripple rings are pale blue against it, not
cyan against blue.

### Step 3 — Verify the aliasing fix *(already in the HLSL, needs a look)*

Focus Unity so the shader recompiles, then throw a rock.

**PASS:** Rings read as smooth expanding bands near the camera and fade out cleanly with distance.
No salt-and-pepper speckle anywhere.

**If speckle persists**, the next lever is MSAA on the URP asset (4×) — but note MSAA only
anti-aliases geometry edges, not shading inside a triangle, so it will help less here than it
sounds. The `ddx`/`ddy` fade is the correct tool and should be sufficient.

### Step 4 — Float the stone on the surface *(~30 min)*

Keep [`SkipPhysics`](../Assets/Scripts/Game/SkipPhysics.cs) pure — the solver's contract is that
it owns physics and publishes nothing observable, and the half-height offset is a presentation
concern. Apply it in [`RockController`](../Assets/Scripts/Game/RockController.cs#L127):

```csharp
// The solver clamps the stone's CENTRE to the water plane, which puts its lower half
// under the surface at every bounce. Built-in hid that behind ZWrite Off; URP writes
// real depth, so the water now correctly occludes it and it reads as a hard cut.
// Lifting by half the stone's height draws it resting ON the surface, which is what a
// skipping stone does, without touching the maths that decides where it goes.
transform.position = _state.Position + Vector3.up * visualFloatOffset;
```

with `visualFloatOffset` a serialized field defaulting to `StoneScale.y * 0.5f` (0.125).

> Read the actual offset off the mesh bounds rather than hardcoding if `Rock.fbx` isn't
> centred on its origin — worth checking, since it's an imported mesh rather than a primitive.

### Step 5 — Make the sink an animation, not a freeze *(~1 hr)*

Right now [`Finish()`](../Assets/Scripts/Game/RockController.cs#L145) sets `IsFlying = false` and
the stone sits frozen at the waterline for `despawnDelay` (2.5s) before being destroyed. That's
the shallow dome sitting there in frames 21–25.

It should sink: translate down over ~0.6s with a slight tumble, letting the water occlude it
progressively. **URP's depth writes make this work for free** — the effect that's currently
causing the problem becomes the effect that sells it.

This is the natural home for the `RockSunkEvent` proposed in
[`Plan-SfxVfx.md` §4](Plan-SfxVfx.md), so it's worth doing as part of that work rather than twice.

### Step 6 — Contact transition *(with the SFX/VFX work)*

Even a correctly-floated stone looks pasted on without a waterline transition. A splash burst at
contact plus a foam term keyed to young, strong ripples is
[`Plan-SfxVfx.md` §6.5 and §6.7](Plan-SfxVfx.md) — no new event needed, `WaterRipples` already
feeds the shader everything required.

---

## Two things I noticed that you didn't ask about

**The horizon is a hard clipped line.** [`AimPreview.BuildPlaceholderScene`](../Assets/Scripts/Game/AimPreview.cs#L303)
scales the water plane to 2000 units but sets `camera.farClipPlane = 1000f`, so the lake is cut by
the far plane against a solid background — visible as a hard edge across the top of every frame.
Fix with fog fading to the background colour, a skybox, or simply matching the clear colour to the
lake's horizon tint. Cheap, and it's the first thing that reads as "unfinished" in the video.

**`_QueueControl: 0` on the water material.** Worth confirming this is left on automatic. If the
queue got forced into the transparent range the material would stop writing depth, which would
undo the fix in step 4 and reintroduce the original draw-order bug in a form that's very hard to
attribute. [`PlaceholderArt`](../Assets/Scripts/Game/PlaceholderArt.cs) no longer pins queues
under URP, so nothing in code should be doing this — but the material is authored, not generated.

---

## Status — all applied

Everything in steps 1, 2, 4, 5 and the horizon item is now in the working tree, on top of commit
`b365e88`. Step 6 (contact splash / foam) belongs to the SFX/VFX work and is not started.

| File | Change |
|---|---|
| `ProjectSettings/ProjectSettings.asset` | `m_ActiveColorSpace: 0` → `1` (Linear) |
| `Assets/Resources/WaterRipple.hlsl` | Analytic anti-aliasing via `ddx`/`ddy`. Signature unchanged, so the graph needed no rewiring |
| `Assets/Resources/WaterRippleURP.shadergraph` | Crest property reference `_CrestColor_1` → `_CrestColor` |
| `Assets/Resources/RockSkipping_WaterRipple.mat` | `_BaseColor` → `(0.06, 0.16, 0.28)`; dropped the orphaned `_Color` and `_CrestColor_1`. `_CrestColor` was already the right value and is now live |
| `Assets/Scripts/Game/RockController.cs` | `visualFloatOffset`; sink descent + tumble; position written once at the end of `Update` |
| `Assets/Scripts/Game/AimPreview.cs` | Linear fog to the background colour, 120→600 |

**Resolved by inspection, no change needed:** the graph's master stack already has
**Metallic = 0.0** and **Smoothness = 0.5**, exactly as intended. The material's `_Metallic: 0.5`
and `_Glossiness: 0.5` are dead Built-in leftovers bound to nothing — harmless, and Unity strips
them on the next material save. Left in place rather than hand-edited out.

### Verify on the next run

1. **Lake is deep navy**, not electric blue, and rings are pale blue against it.
2. **No speckle** — rings read as smooth bands near camera, fading cleanly with distance.
3. **Stone rests on the surface**, no hard cut line, and visibly sinks when the run ends.
4. **No hard horizon line** — the lake fades into the background.
5. **Fog reaches the water.** URP Lit Shader Graph should apply fog automatically, but this was
   not verified against this URP version. If the stone fogs and the water doesn't, the graph needs
   fog enabled explicitly.

Expect everything to look **darker** than the video — that's the Linear switch being correct, not
a regression. Retune light intensity from there rather than reverting.

---

## Related

- [`Migration-URP.md`](Migration-URP.md) — the migration this follows. **Needs a colour-space step
  adding at position 2**; that omission is cause #1 above.
- [`Plan-SfxVfx.md`](Plan-SfxVfx.md) — steps 5 and 6 belong to this work.
