# Presentation

**Source:** `Assets/Scripts/Game/` (AimPreview, AimFeedbackTuning, LakeLouiseEnvironment, PlaceholderArt, RockTrail, TrailTuning, WaterRipples, PlayerColourPalette), `Assets/Scripts/UI/`, `Assets/Editor/LakeLouiseSetup.cs`

## What it owns

Everything the TV shows that is not the solver: the aim ghost and arc, the placeholder and Lake Louise scenery, materials and render queues, the stone's trail, water ripples, and the IMGUI screens. All of it is presentation-only and must never feed back into the simulation. The notes below are the author's reasoning moved verbatim from the source comments.

## How it works

### `Assets/Scripts/Game/AimFeedbackTuning.cs` — Arc-colour numbers live in a Resources asset because AddComponent-built components never see inspector values
<!-- ref:7c43 -->

Was `Assets/Scripts/Game/AimFeedbackTuning.cs:5-35`.

Every number behind the one thing a player reads while aiming: what colour the arc
is, from red through amber to green as the predicted throw gets better. One asset,
edited in the inspector, no recompile.

A ScriptableObject at Resources/AimFeedbackTuning for exactly the reason TrailTuning
is one. AimPreview is built by Bootstrap with AddComponent, so anything held as a
[SerializeField] on it sits at its C# field initializer forever — AimPreview.Awake
already carries the note about having been caught by that once, when "the whole game
ran on C# field initializers and the numbers were only reachable by recompiling."
These are look numbers, which is precisely the kind that get tuned twenty times in an
evening, so they are the last ones that should need a compile.

Nothing in here is read by the simulation. The arc's colour is downstream of
SkipPhysics.Predict and feeds back into nothing — the same presentation-only
guarantee RockTrail states, and it matters here for the same reason: the preview runs
the solver every frame for a throw that never happened.

The ghost used to swell, pulse and glow with wind-up (a "Ghost charge" header lived
here). That feature is gone — the ghost now simply shows the throwing player's
colour, from PlayerColours via AimPreview.TintGhost — and every field that only fed
it went with it.

Skip counts are stored as ABSOLUTE counts rather than fractions of anything, and that
is not an oversight of the fractions rule TrailTuning documents. That rule exists
because a threshold in metres-per-second silently stops meaning anything when
SkipConstants.ReferenceSpeed is retuned. A skip count has no such scale behind it —
six skips is six skips at any launch speed, and it is the number a player counts
out loud.

### `Assets/Scripts/Game/AimPreview.cs` — Ghost follows the phone with one Slerp per frame
<!-- ref:caf5 -->

Was `Assets/Scripts/Game/AimPreview.cs:10-31`.

Live trajectory preview on the TV while a player aims.

Two things on screen: a ghost stone that matches the phone's rotation exactly, and
an arc showing roughly where a throw from that angle would go.

Smoothing is not polish here, it is what makes the feature work: AIM frames arrive
at only ~20Hz over a jittery connection, so applying them raw looks choppy. One
Slerp toward the last received attitude every frame is the whole of it — it used
to be four SmoothDampAngle calls over four separate scalars, which is what a
three-angle wire format forces you into.

The arc comes from SkipPhysics.Predict — the same solver the live throw uses, so
what a player sees is what they get. It runs at a nominal power because the real
power is unknown until release; the arc teaches the angle, not an exact landing
spot. There used to be a per-player learned-power model here trying to close that
gap; it made the arc's meaning depend on throw history, which is worse than it
simply being approximate and obviously so.

This never publishes gameplay events. Prediction is speculative by definition.

### `Assets/Scripts/Game/LakeLouiseEnvironment.cs` — Lives in a scene, not Resources
<!-- ref:e918 -->

Was `Assets/Scripts/Game/LakeLouiseEnvironment.cs:6-22`.

Turns AimPreview's placeholder lake into Lake Louise: the Blender-built valley and its
mirrored reflection, turquoise see-through water, and a sky that mirrors itself below
the horizon.

Placed in a scene rather than looked up from Resources, so the ordinary Bootstrap scene
is untouched and "Resources stays short". <c>RockSkipping/Environments/Create Lake
Louise Scene</c> makes that scene and fills both fields. AimPreview finds this component
while building its placeholder scene and hands over the water and sky; everything built
here is parented to AimPreview, which Bootstrap marks DontDestroyOnLoad, so it survives
the Lobby/Game/Results loads exactly as the water does.

All look numbers come from look (LakeLouiseLook.json). The Blender preview
in tools/art/lake-louise reads the same file, so the preview and the game cannot drift.
The pipeline and the reasons behind it: docs/plans/lake-louise-overhaul.md.

### `Assets/Editor/LakeLouiseSetup.cs`

#### LakeLouiseSetup.cs creates the LakeLouise scene as a copy of Bootstrap
<!-- ref:95c3 -->

Was `Assets/Editor/LakeLouiseSetup.cs:9-18`.

Makes Assets/Scenes/LakeLouise.unity: a copy of Bootstrap with a LakeLouiseEnvironment
in it. Open that scene and press Play. Batchmode entry point: CreateScene.

A copy of Bootstrap rather than a new scene because Bootstrap is what creates the
persistent managers and the AimPreview that builds the lake; the environment only
restyles what AimPreview builds. Not added to the build settings: Bootstrap stays at
index 0 and this scene is reached by opening it (docs/plans/alpine-lake-level.md).

### `Assets/Scripts/Game/AimPreview.cs`

#### Only the ghost's mesh and scale change per turn
<!-- ref:1a25 -->

Was `Assets/Scripts/Game/AimPreview.cs:257-269`.

Resolves the new active player's rock and tuning once per turn, and swaps the
ghost's mesh and scale to match — see the class remarks on Tuning.

Only sharedMesh and localScale are touched. RockDefinition.MeshRotation is
deliberately left unapplied here — the ghost's rotation is overwritten every
frame from the phone's live attitude (see UpdateGhost), and RockController does
the same from the solver's attitude, so neither has anywhere to compose a
per-mesh correction without also changing what the solver reads as the stone's
face angle. Every mesh RockCatalogBuilder has produced so far imports flat, so
this has not yet needed solving for real.

#### The arc is drawn at live swing speed floored at a nominal throw
<!-- ref:460e -->

Was `Assets/Scripts/Game/AimPreview.cs:471-481`.

Drawn at how hard the phone is being swung right now, floored at the nominal.

The floor is what makes this usable. Real speed is genuinely unknowable
before release, so a player holding still to line a shot up reads as zero
effort — and an arc that collapses to a two-metre dribble every time they
stop moving is not an aiming guide. So the arc shows at least a nominal
throw, and winding up extends it from there.

It used to be pinned at the nominal outright, which made the HUD's
"predicted N skips over M m" mathematically incapable of responding to how
hard anyone threw. That was the 13-skip ceiling.

#### Arc colour runs red to green by predicted skips and is set on the LineRenderer's vertex colour
<!-- ref:acf8 -->

Was `Assets/Scripts/Game/AimPreview.cs:506-520`.

Red through amber to green by how many skips the solver just predicted.

This is a readability fix rather than decoration. Arc LENGTH was the only signal
a bad angle gave, and the angle-quality curve in SkipPhysics is a smooth gradient
with no cliff in it — so a mediocre throw looks broadly like a good one right up
until it is in the water. Colour makes that gradient legible while there is still
time to correct.

Tinting via startColor/endColor rather than the material, because the material is
shared with nothing but is drawn with a gradient the LineRenderer applies as
vertex colour — writing the material colour instead would multiply underneath and
leave two places that decide one hue.

#### The far bank sits at 320 m so enough of its colour survives the fog to read as sand
<!-- ref:8bec -->

Was `Assets/Scripts/Game/AimPreview.cs:659-675`.

How far down the lake the far bank sits, in metres. Well past the ~50 m a throw
covers, so it is never in the flight path, and inside the camera's 1000-unit far
clip so it is not cut off by the frustum.

Pulled in from 480 m, and the reason is the fog rather than the composition. Linear
fog runs 120 m to 600 m, so at 480 m the bank is about three-quarters faded into the
horizon colour — only a quarter of its own colour survives, which is why a sand
colour there still rendered as blue-grey. It reads as haze, and haze cannot be
recoloured by choosing a different albedo.

At 320 m roughly 58% of the material colour comes through, which is enough for sand
to look like sand while still leaving the distance haze that makes it sit back behind
the lake. Still six times further out than a throw travels, so nothing about the
flight path changes.

#### The sky clear colour is derived from the water's base colour so the horizon has no hard line and retuning the lake carries the sky
<!-- ref:5850 -->

Was `Assets/Scripts/Game/AimPreview.cs:711-722`.

One colour for the empty sky and for what the lake fades into, so the two
cannot disagree — see the fog block below, where the hard horizon is fixed.

Derived from the water rather than picked independently. The water's authored
_BaseColor is a bright mid-blue (0.06, 0.36, 0.72); the sky used to be an almost
black (0.05, 0.09, 0.15), and a headless capture shows the two meeting as a hard
line across the frame — the fog cannot hide an edge when the colour on the far
side of it is nothing like the water's own.

Lightened and desaturated from the water instead of set to a taste value, so the
relationship survives someone retuning the lake: change _BaseColor and the sky
follows it rather than drifting apart from it again.

#### Fog fades the lake into the sky colour before the far clip plane cuts it
<!-- ref:3a02 -->

Was `Assets/Scripts/Game/AimPreview.cs:761-772`.

The lake is 2000 units across (a 10-unit Plane scaled by 200) but the far
clip is 1000, so the far half is cut off by the frustum and the cut shows as
a hard line across the middle of the screen with flat background above it.

Fogging to the background colour rather than moving the clip plane out:
pushing the far plane past 2000 would double the depth range and spend real
depth precision on water nobody can make out anyway, and the plane would
still end in a hard edge — just further away. Fading it into the same colour
the sky is cleared to means the edge is gone rather than relocated.

Ends well inside the clip plane so the fade is complete before the geometry
is cut, which is the whole point.

#### The backdrop is a Background-queue quad on the camera
<!-- ref:a26e -->

Was `Assets/Scripts/Game/AimPreview.cs:787-809`.

A gradient-and-cloud backdrop, drawn behind everything as a camera-parented quad.

A quad rather than `RenderSettings.skybox`, and that is not a stylistic choice.
A skybox was tried and measured: it rendered correctly and the lake vanished
completely. URP's skybox pass runs AFTER opaques and skips pixels the depth buffer
says are covered — and the water writes no depth (WaterRipples holds its ZWrite off
except during a sink), so the sky painted straight over the lake. A Background-queue
quad draws BEFORE the water instead, so the water lands on top of it exactly as it
lands on the camera's clear colour today.

Parented to the camera so it follows the chase without ever leaving the frustum. The
gradient itself is computed from the world view direction inside the shader, not from
the quad's UVs, so it still tilts like real sky when the camera pitches to follow a
throw — a UV gradient would be welded to the screen and would not.

Failure leaves the flat clear colour, which is the previous look rather than a broken
one, and says WHICH failure it was: not found, versus found but failed to compile.
Both render identically and need entirely different fixes.

Returns the material (null on failure) so a LakeLouiseEnvironment can restyle it.

#### The bank goes at the far end
<!-- ref:094c -->

Was `Assets/Scripts/Game/AimPreview.cs:858-876`.

A low bank across the far end of the lake, so the water meets land instead of ending.

FAR, not near, and that is the correction to the obvious placement. A bank behind the
throw origin is the intuitive choice — it keeps geometry out of the flight path — and
it is invisible: the camera sits at z = -3 looking toward +z, so anything behind the
origin is behind the camera and appears in no frame. A headless capture of that
version showed nothing at all.

Across the far end it is out of the flight path anyway (a throw covers about 50 m,
this sits at ShoreDistance) and it does a job the near bank could not:
it gives the horizon something to be, so the six selectable stones stop reading as
floating on an empty plane.

Drawn at PlaceholderArt.ShoreQueue, BELOW the water's queue. That is not a detail —
at or above it, the bank paints over the entire lake, because the water writes no
depth for it to be occluded by.

#### The bank is a flattened cube wide enough to cross the frustum and sized for its distance
<!-- ref:4be6 -->

Was `Assets/Scripts/Game/AimPreview.cs:883-892`.

A flattened cube rather than a Plane: a Plane is single-sided and shows as a hole
from underneath, and the water sits at y = 0 with the camera slightly above it.

Wide enough to cross the whole frustum at this distance. A strip that ends inside
the view reads as an island, and an island nobody can reach raises a question the
game never answers.

Sized for the distance, not for the ruler. A measured capture at 480 m showed a
6-unit bank as a five-pixel hairline nobody would read as land. Standing ~10 units
above the waterline at 320 m is a low ridge rather than a wall.

#### The sand is far brighter than real sand because fog replaces about 40% of it
<!-- ref:2612 -->

Was `Assets/Scripts/Game/AimPreview.cs:900-909`.

The shore material — sand, picked for what survives the fog rather than for realism.

Deliberately far brighter and warmer than sand actually is. Linear fog runs 120 m to
600 m, so at ShoreDistance roughly 40% of this is already replaced by
the horizon's blue-grey before it reaches the screen; a realistic sand albedo arrives
as mud. Picked for the far side of the fog, not the inspector swatch — which is also
why moving ShoreDistance means re-checking this colour.

#### Raise the existing directional light high and swing it off-axis
<!-- ref:1a80 -->

Was `Assets/Scripts/Game/AimPreview.cs:974-995`.

Puts the scene's directional light at a low side angle so the stones have form.

Adjusts the light the scene already has rather than adding one — a second
directional light would double the ambient contribution and wash out the very
shading this is for.

Found by type, not by name: Game.unity's is called "Directional Light", but a
string lookup breaks silently the moment somebody renames it, leaving the scene lit
by the old values with no error to follow.

The angle is the point, and the obvious choice is the wrong one. A LOW raking light
is what gives most scenes form, and it was tried first: a headless capture at 28
degrees came back with the stones darker and flatter than Unity's default. These
stones are flat and horizontal, so their large visible face is their TOP, and a low
sun barely lands on it — it lights the thin rims instead, which is nothing.

So the elevation goes up, not down, and the direction comes from the azimuth
instead: high enough to actually reach the top faces, swung off-axis so the two
halves of each stone are lit differently and the silhouettes separate.

### `Assets/Scripts/Game/PlaceholderArt.cs`

#### The opaque shader chain tries URP first
<!-- ref:21db -->

Was `Assets/Scripts/Game/PlaceholderArt.cs:288-301`.

The best available opaque lit shader for the active pipeline.

URP first, because under URP "Standard" resolves to a shader that cannot render
and the lake comes out magenta. Standard second, so the Built-in pipeline is
unaffected by any of this. The unlit tail is a last resort only — the water
shader's own remarks record that going unlit visibly darkened the scene, because
ambient mode is Skybox and a lit shader picks up skybox SH on the diffuse plus a
grazing-angle specular that no unlit fallback reproduces.

Sprites/Default stays at the end because it is the one entry guaranteed to be in
Always Included Shaders, so the chain can never come back null in a build.

### `Assets/Scripts/Game/PlayerColourPalette.cs`

#### The palette is a Resources asset, not a component, so a human can edit it
<!-- ref:6272 -->

Was `Assets/Scripts/Game/PlayerColourPalette.cs:5-27`.

The colours players can be, and the rules the host applies to a colour a phone
picks for itself.

A ScriptableObject rather than a component, deliberately, and for the reason
SkipConstants is one: Bootstrap builds every manager with AddComponent, so a
[SerializeField] on one has never been through an inspector and sits at its C#
field initializer forever. An asset under Resources/ is the only shape of tuning
data in this project that a human can actually edit — see the same note on
AimFeedbackTuning and RockSelectTuning, which are components precisely because
they gave up on that.

The swatches used to be a private static Color[] inside ShoreRocks, indexed by
`hash(playerId) + how many pointers existed when this one was created`. That
count resets on every phase change, so a player's colour changed between rounds and
two players collided freely — both complaints in notes.txt. Colour is now owned by
Managers.PlayerColours and keyed by player identity alone.

Everything here is public on purpose: the pointer dot is the first thing to read
these, not the last. A player-coloured stone, scoreboard row or trail should pull
from this asset rather than introducing a second palette.

### `Assets/Scripts/Game/RockLauncher.cs`

#### The trail is initialised after the controller and localScale
<!-- ref:cb6f -->

Was `Assets/Scripts/Game/RockLauncher.cs:298-304`.

After the controller, because the trail reads IsFlying and Speed off it. The
same constants the throw is about to be simulated with are handed over so the
trail's speed thresholds cannot drift from the launch speed they scale
against — see TrailTuning's remarks on why they are fractions. The stone's
own tint goes to the trail, not the raw player colour, so the ribbon is the
colour of the rock it streams from. After localScale is set above, because
the trail sizes itself off the stone's world width.

### `Assets/Scripts/Game/RockTrail.cs`

#### Euler(-90,0,0) lays the ribbon flat on the stone's face plane
<!-- ref:05ec -->

Was `Assets/Scripts/Game/RockTrail.cs:44-65`.

Local rotation that lays the ribbon flat in the stone's own face plane.

LineAlignment.TransformZ builds every trail quad facing the transform's local +Z,
so making the ribbon flat WITH the stone is a question of pointing this child's
+Z along the stone's face normal — and the face normal is local +Y, not +Z.
RockController spins the stone with Quaternion.Euler(0, angle, 0) applied on the
right, which is a turn about its own local Y, and calls that "about its own face
normal." So the flat face is the local XZ plane and its normal is local up.

Euler(-90, 0, 0) is the rotation that carries +Z onto +Y, and no other component
is needed: the remaining freedom is a turn about the ribbon's own normal, which
does not change the plane it lies in.

It also cannot be shaken by the spin, which is the part worth having. The stone
turns about local Y at up to MaxVisualSpinDegPerSec; this child's +Z IS local Y,
and a rotation about an axis leaves that axis fixed — so the ribbon's plane is
invariant under the spin and the trail cannot strobe or flicker as the stone
whirls. Aligning to any other local axis would have it flipping edge-on twice per
revolution.

#### Width is measured from mesh.bounds as the mean of the two face axes
<!-- ref:ae7e -->

Was `Assets/Scripts/Game/RockTrail.cs:115-131`.

The stone's width in world metres, across its flat face.

Measured, not assumed, so each rock gets a ribbon its own width — a Boulder's wider
than a Shard's — rather than one fixed number for every stone. This is the stone's
WORLD width; Init divides the rock's scale back out before handing it to the
TrailRenderer, which multiplies its width by the transform's scale (see Init).

Mean of the two face-plane axes (local X and Z) rather than either one. The stone
spins about its face normal in flight, so which axis lies across the direction
of travel changes every few frames; the mean is the width it reads as on average.
mesh.bounds rather than vertices — the rock meshes are not CPU-readable.

Falls back to 1 m when there is no mesh (the prefab's plain box fallback), which
keeps TrailTuning.WidthMultiplier meaning metres in that one case.

#### The dash texture is generated in code so it cannot go missing, with point filtering to keep hard edges
<!-- ref:2481 -->

Was `Assets/Scripts/Game/RockTrail.cs:213-227`.

Builds the dash pattern as a generated texture and tiles it along the trail.

Generated rather than authored, so the dashed style needs no texture asset. That
is not laziness about art: PlaceholderArt.CreateWater's remarks record that a
shader or texture reached by name rather than from Resources "would look right in
the editor and come out magenta in a player build." A texture built in code
cannot go missing.

Point filtering on purpose. Bilinear on a 64-pixel strip smears each dash edge
into a gradient, which at speed reads as a blurry ribbon rather than as dashes —
and hard edges are what is wanted anyway, since soft-edged particles are off the
table while the water's depth writing flips mid-throw (Plan-SfxVfx.md 1.2).

#### The whole ribbon is dimmed by the stone's current speed
<!-- ref:16ef -->

Was `Assets/Scripts/Game/RockTrail.cs:281-295`.

Dims the whole ribbon as the stone runs out of speed.

Whole-trail rather than per-vertex, and the difference is visible if you look for
it: the tint is the stone's speed NOW, applied uniformly, so an old segment laid
down while the stone was fast dims along with everything else instead of keeping
the colour it was born with. Per-vertex colour would need the gradient rebuilt
every frame from a speed history, which is a real cost for a ribbon that lives
half a second. Revisit only if a dying run does not read.

Multiplies into the gradient rather than replacing it — the material tint and the
vertex colour are separate inputs to the shader, so the authored head-to-tail
fade survives.

### `Assets/Scripts/UI/JoinScreen.cs`

#### OnGUI scaffolding
<!-- ref:17f0 -->

Was `Assets/Scripts/UI/JoinScreen.cs:14-27`.

The screen guests actually look at: how to join, and the code to type. Shows the
launch progress first, so a slow tunnel reads as "working" rather than "broken".

Scaffolding in the same spirit as RelayDebugHud — OnGUI, no Canvas, no prefab, so
it works in whatever scene is loaded. Replace with authored UI when the real Lobby
scene exists; the QR fetch and the join-URL construction are the parts worth
keeping.

The QR matrix comes from the relay rather than being generated here. The relay is
already a required running process and already has a QR library, so this stays a
texture blit instead of a Reed-Solomon implementation.

### `Assets/Scripts/UI/MatchSettingsScreen.cs`

#### Renders MatchSettings.All in a loop and is reachable whenever CanEdit is true
<!-- ref:b8ad -->

Was `Assets/Scripts/UI/MatchSettingsScreen.cs:6-16`.

Host settings panel: Random rocks, Tuning set, Rounds per match — see
docs/plans/rock-choosing-system.md Part 5. Renders MatchSettings.All in a
loop, so a fourth setting is one entry there, not a screen edit.

Scaffolding in the same spirit as JoinScreen/ResultsScreen — OnGUI, no Canvas, no
prefab. Reachable whenever MatchSettings.CanEdit is true (Lobby, Choosing,
Results — anything but Playing), so it opens both from the main lobby before a match and
from the existing mid-match re-pick pause without any new phase logic.

### `Assets/Scripts/UI/ResultsScreen.cs`

#### Reads standings straight off TurnManager
<!-- ref:ef90 -->

Was `Assets/Scripts/UI/ResultsScreen.cs:10-23`.

Final standings at the end of a match, with a prompt to play again.

Scaffolding in the same spirit as JoinScreen — OnGUI, no Canvas, no prefab — so it
works in whatever scene is loaded. PLAN.md calls for a dedicated Results scene;
that transition is deliberately not wired yet, for the same reason Bootstrap does
not auto-load the Lobby: the scene is empty, so loading it would look like the game
had closed rather than like a results screen.

Reads standings straight off TurnManager, so there is one source of truth on
screen. There used to be a MatchEndedEvent carrying a duplicate snapshot; nothing
ever subscribed to it, so it was deleted rather than left as a second answer.

### `tools/art/lake-louise/build_lake_louise.py`

#### Crest samples land exactly on the photo skyline
<!-- ref:4a6f -->

Was `tools/art/lake-louise/build_lake_louise.py:181-191`.

Dense crest samples along one ridge, each as high as the photo's silhouette says.

A crest sample at horizontal distance d from the camera, in a direction where the photo's
(scaled) skyline stands e degrees up, gets height CAMERA_HEIGHT + d * tan(e), so it lands
exactly on that skyline. Directions past either end of a traced line take its end value,
which is how the ridges carry on beyond the photo's edges.

The crest dominates the skyline because every data-slope (0.6-1.15) is steeper than any
sight line to a crest (tan of at most ~15 degrees, 0.27): ground in front of a crest,
along the same sight line, always drops away below it.

## Invariants

### `Assets/Scripts/Game/PlaceholderArt.cs` — The shore's queue must sit below the water's
<!-- ref:355b -->

Was `Assets/Scripts/Game/PlaceholderArt.cs:38-55`.

The far shoreline — the most distant thing in the scene, drawn BEFORE the water.

Below WaterQueue, and the ordering is load-bearing rather than tidy. The water keeps
its ZWrite OFF except during a sink (see SetWaterDepthWrite), so it
leaves nothing in the depth buffer. Anything drawn AFTER it therefore has nothing to
be occluded by and paints straight over the lake — which is exactly what happened
when the shore first shipped sharing WaterQueue.

Drawn first, the relationship comes out right for free: the shore writes depth at its
own distance, then the water draws over it wherever the water is nearer, and water
fragments beyond the bank fail the depth test and stay hidden behind it.

The lesson the file's header already states, arrived at from the other direction: a
NEW ROLE GETS A NEW QUEUE. Do not reuse another role's number because the two happen
to sit near each other in the world.

### `Assets/Scripts/Game/RockTrail.cs` — The trail is presentation-only: it publishes and subscribes to nothing
<!-- ref:9800 -->

Was `Assets/Scripts/Game/RockTrail.cs:5-29`.

The ribbon behind a stone in flight. Reads TrailTuning, drives a TrailRenderer, and
stops emitting the moment the run ends.

Presentation only, and it adds nothing to gameplay. It publishes no events, is read
by nothing, and holds no state the simulation can see — so the rule SkipPhysics
states twice, that observable effects belong to the caller and never to the solver,
is not at risk here. WaterRipples calls that safety "inherited, not re-implemented,"
and this inherits it the same way: the trail exists only where a real stone does, and
RockLauncher.CreateRockObject is the single place a real stone is made. The aim
preview runs the same solver hundreds of times a frame for a throw that never
happened and never touches this file.

It also does not subscribe to anything. Stopping on sink hangs off
RockController.IsFlying, which is already false by the time the sink coroutine
starts, rather than off a RockSunkEvent — that event is M2 work and does not exist
yet, and inventing it here would have coupled the trail to a milestone it does not
need. Reading a sibling's public flag is the smaller dependency, and when
RockSunkEvent does arrive this can move onto it without changing what it does.

Lives on a CHILD of the rock, not on the rock itself. Unity permits one Renderer per
GameObject and the rock already has a MeshRenderer, so AddComponent<TrailRenderer>
on the stone is refused outright.

### `Assets/Scripts/Game/WaterRipples.cs` — Ripples hang off SkipOccurredEvent
<!-- ref:029b -->

Was `Assets/Scripts/Game/WaterRipples.cs:8-17`.

Feeds bounce positions to the water material so each skip leaves an expanding ring.

Lives on the water rather than inside SkipPhysics for the reason the solver spells
out: observable effects belong to the caller, never in the solver. Ripples hang off
SkipOccurredEvent, which only RockController publishes, so the aim preview — which
runs the same solver hundreds of times a frame for a throw that never happened —
cannot produce them. That safety is inherited, not re-implemented here.

### `Assets/Scripts/Game/AimPreview.cs`

#### Water size constants are shared with SkipCurve so its on-mesh check reads the real numbers
<!-- ref:b58d -->

Was `Assets/Scripts/Game/AimPreview.cs:632-644`.

The placeholder water's geometry, named because more than one thing needs it.
Verification.SkipCurve asserts that a full-power throw stays on the
drawn mesh, and it can only do that honestly if it is reading the same numbers the
mesh is built from — it runs in the Editor without entering Play mode, so there is
no live plane for it to measure. Duplicating the values there instead would go
stale silently the first time the water changed size, which is exactly the failure
the check exists to catch.

Unity's Plane primitive is 10 x 10 units before scaling, hence the base size — a
scale of 200 gives 2000 m across, not 200.

### `Assets/Scripts/Game/LakeLouiseEnvironment.cs`

#### The scenery is scaled about the camera so view angles are unchanged
<!-- ref:8efd -->

Was `Assets/Scripts/Game/LakeLouiseEnvironment.cs:109-120`.

Builds the valley and its reflection under parent, scaled up by
scenery_scale about the camera, and restyles the sky. skyMaterial
may be null (AimPreview logs why); the scenery is still built, but the water then
shows flat horizon colour where sky should reflect.

The scale exists because throws reached the mountains at their built size. Scaling
about the camera keeps every view angle, so the look is unchanged from the throw
position; the water plane and the camera's far clip must grow with it, or the lake
ends in a hard edge and the glacier is clipped away.

### `Assets/Scripts/UI/MatchHud.cs`

#### IMGUI by the owner's call on 2026-09-30
<!-- ref:46c4 -->

Was `Assets/Scripts/UI/MatchHud.cs:11-29`.

The in-match TV readout: the current or last throw, top-centre, and a slim leaderboard
of every player's last throw and total, top-left.

IMGUI, like every other TV screen, by the owner's call on 2026-09-30: usable now, ported
with the rest when docs/plans/presentation-and-ui.md D3 (UI Toolkit) happens. This is a
first cut of D4's elements 1 and 3 — the counter and the scoreboard strip — without the
punch-scaling and fly-in, which need a framework that animates.

Numbers only, no words: "124m   12sk   1350pts". The format lives in Separator
and the two Format methods, so changing how it reads is one place.

Visible only while Playing. The lobby and the mid-match re-pick are the shore's screen,
and Results has its own.

Reads, never computes: skips and distance come off the throw's own events and points
off ScoreManager, which the project's scoring rule says is the only owner of points.

### `Assets/Scripts/UI/StartScreen.cs`

#### Start is a scene boundary ahead of Bootstrap, so nothing touches the network until a person presses Start
<!-- ref:3bb9 -->

Was `Assets/Scripts/UI/StartScreen.cs:8-25`.

The first thing a player sees, in its own scene ahead of Bootstrap. Nothing
network-related exists yet at this point — LocalServices, RelayClient and
HotspotService are all created in the Bootstrap scene, which this screen only loads
once Start is pressed.

That scene boundary is the whole gate: no relay spawns, no certificate is fetched, and
in particular no hotspot starts — nothing happens on this PC's network — until a person
standing at it deliberately presses Start.

The LAN/Hotspot picker that used to live here is gone — see
docs/plans/magic-domain-replacement.md Phase 3 and
docs/plans/remove-direct-transport.md. Hotspot is the only mode there is now (LAN mode
was removed outright, not merely deprecated — its certificate stopped validating once
local-ip.sh's wildcard was replaced by a single-name one), so this screen shows no
choice at all.

## Traps

### `Assets/Scripts/Game/AimPreview.cs` — Bound in Start, not Awake, because Bootstrap adds the launcher after this component
<!-- ref:0fb4 -->

Was `Assets/Scripts/Game/AimPreview.cs:201-222`.

Binds the launcher and the relay client once, rather than re-testing for them
every frame.

Start rather than Awake, and that is the whole reason this can be a one-shot.
Bootstrap.Awake creates RelayClient.Instance before it calls CreateAimPreview, and
CreateAimPreview adds RockLauncher to this same object immediately after this
component — so at OUR Awake the launcher does not exist yet, but by the first
Start every Awake in that chain has run and both are guaranteed present. Bootstrap
is the only thing that ever builds an AimPreview (it guards on
FindFirstObjectByType), so there is no other construction order to survive.

The previous shape ran this same resolution from Update: a GetComponent plus two
UnityEngine.Object null comparisons plus a HasInstance probe, every frame, for the
entire session — all of them native interop calls, and all of them answering a
question that stops changing after the first frame.

HasInstance, not Instance — Instance would create a RelayClient, and a preview
that conjures its own data source hides a real failure. If it is genuinely absent
the preview is inert for the session, so say so loudly rather than sitting dark.

### `Assets/Scripts/Game/AimPreview.cs` — A join must reset the ordering guard because the new phone's sequence counter restarts at zero
<!-- ref:f6df -->

Was `Assets/Scripts/Game/AimPreview.cs:320-340`.

A phone that joins has a brand new ThrowController, whose sequence counter starts
at zero again — so the ordering guard in OnAim must forget the number
it was holding for that player, or every frame they send is older than what it
already saw and the preview starves.

This is what left the ghost and arc hidden for the rest of a session after a player
closed their tab and came back. The guard rejected each frame before
`_lastAimTime` was updated, so HasSignal went stale and Update hid both.
See docs/Findings-AimPreviewAfterRejoin.md.

Deliberately NOT gated on PlayerJoinedEvent.Reconnected. The relay
deletes a player from the room on socket close (relay/server.js, the 'close'
handler), so closing a tab and coming back arrives as a fresh join with that flag
false — which is precisely the case being fixed. Any join at all means a phone with
a sequence counter back at zero, and that is the fact this depends on.

Only the player the guard is currently tracking matters. For anyone else the reset
already happens in OnAim, the moment the aiming player changes.

### `Assets/Scripts/Game/AimPreview.cs` — Hide the preview until the stone has landed and CameraSettled
<!-- ref:0c3d -->

Was `Assets/Scripts/Game/AimPreview.cs:361-373`.

Nothing to predict while a stone is already in the air, and leaving the arc
up through the throw means the line the player was aiming with is still
drawn over the result. Both the tooltip above and the debug HUD used to
claim this happened; it did not, because _line.enabled was set true
unconditionally just before the early return below.

CameraSettled as well as the flying check, because the throw is not over when
the stone is. The camera is still downrange and swinging back for a second or
so afterwards, and the preview coming back at the top of that move drew a
ghost and an arc that slid across the frame from wherever the chase had left
them — pointing somewhere the player was not aiming, for as long as the return
took. Waiting for the shot to be the aiming shot again is what makes the
preview mean what it says. See RockLauncher.CameraSettled.

### `Assets/Scripts/Game/AimPreview.cs` — React to AIM only while Playing
<!-- ref:e8c1 -->

Was `Assets/Scripts/Game/AimPreview.cs:408-414`.

Playing only. TurnManager.ActivePlayer is null outside Playing, so the old
guard here (active-is-empty-or-matches) let every lobby AIM frame from every
phone through — latent until ShoreRocks started streaming AIM during
selection, at which point it would have yanked this ghost around a lobby it
has no business appearing in. See the plan's constraints section.
Not logged: outside Playing these frames are ShoreRocks' lobby pointing, used
legitimately there, and logging them as "rejected" read like lost input (#59).

### `Assets/Scripts/Game/AimPreview.cs` — The ghost's rotation is the phone's attitude verbatim
<!-- ref:478e -->

Was `Assets/Scripts/Game/AimPreview.cs:544-554`.

The phone IS the rock. Nothing is added on top.

There used to be an accumulated spin angle here, and it was wrong twice over.
It double-counted: _curAttitude already contains every rotation the phone
made, wrist twist included, so spinning the phone turned the ghost twice. And
it only ever grew, off an unsigned three-axis rotation magnitude, so merely
turning to aim ratcheted the ghost's yaw permanently and it never came back.
The stone appeared to increment rather than to match the phone.

The thrown stone still spins, because once it leaves the hand there is no
phone left to track — see RockController.

### `Assets/Scripts/Game/AimPreview.cs` — Do not use a skybox: URP draws it after opaques and the depth-less water leaves nothing to test against
<!-- ref:27c2 -->

Was `Assets/Scripts/Game/AimPreview.cs:736-751`.

SolidColor, and a SKYBOX IS NOT AN OPTION HERE. Tried and measured: a gradient
skybox renders correctly and the lake disappears completely.

The reason is the water's depth behaviour, not the skybox. Clearing to a solid
colour happens BEFORE any geometry, so the water draws over it. URP's skybox pass
instead runs AFTER opaques and relies on the depth buffer to skip pixels already
covered — and WaterRipples keeps the water's ZWrite OFF except for the length of a
sink (see PlaceholderArt.SetWaterDepthWrite, which explains why: with depth on,
the water swallows the stone). Writing no depth, the water leaves nothing for the
skybox pass to test against, so the sky paints straight over the lake.

So a gradient sky needs either water that writes depth — which reintroduces the
stone-cut-in-half bug that toggle exists to prevent — or a background quad drawn
at the Background queue before the water rather than RenderSettings.skybox. Do not
simply set a skybox and expect it to work; it will look fine in isolation and take
the lake with it.

### `Assets/Scripts/Game/LakeLouiseEnvironment.cs` — The valley's turn is measured on the raw instance
<!-- ref:2ccd -->

Was `Assets/Scripts/Game/LakeLouiseEnvironment.cs:262-271`.

The turn that puts the valley in front of the camera, measured on the raw instance.

The first export imported turned 180 degrees about y - behind the camera and
mirrored left-right - because its axis setting was reasoned rather than observed
(see export_fbx in tools/art/lake-louise/build_lake_louise.py, now fixed). A 180 degree
turn is corrected here with a warning, so an old or mis-set FBX still plays; anything
else is an error, since no rotation about y can fix it.

### `Assets/Scripts/Game/PlaceholderArt.cs` — Each role gets an explicit render queue so draw order never falls back to distance sorting
<!-- ref:3e8b -->

Was `Assets/Scripts/Game/PlaceholderArt.cs:6-35`.

Materials for the runtime placeholder scene. Central because the draw order
between water, stone and arc is one decision, not three.

It used to be three, and that was the bug: each caller ran its own Shader.Find
chain and landed on Sprites/Default, which is Queue=Transparent with ZWrite Off.
With every object depth-write disabled the depth buffer stays empty, so depth
testing arbitrates nothing and Unity falls back to sorting transparent renderers
by distance from the camera to each renderer's bounds centre.

The water is a 200-unit plane sorted by a single point at its middle (z = 60).
The camera trails the stone by ~8.8 units, so the moment the stone passed that
midpoint the water became the nearer object, flipped to drawing last, and painted
over everything — and at alpha 1 it obliterates rather than tints. The stone and
the aim arc did not fade out, they vanished.

Fixed by pinning an explicit queue per role. Unity honours renderQueue before any
distance sorting, so the order is now deterministic no matter where the camera is.
That pinning is the only thing in this file worth keeping — it is a bug fix, not
decoration.

The queues are pinned under URP too, and they still only buy draw ORDER. They do
NOT stop the water hiding the stone — depth testing does that, and it is settled
separately by SetWaterDepthWrite below. Assuming a later queue was enough to draw
over the water cost a debugging round, so it is spelled out here as well.

Shader selection is pipeline-aware, and that part is not optional. Under URP,
Standard is not an error — it is magenta.

### `Assets/Scripts/Game/PlaceholderArt.cs` — Emission needs both the colour and the _EMISSION keyword
<!-- ref:ca3a -->

Was `Assets/Scripts/Game/PlaceholderArt.cs:81-102`.

Turns emission on for a material and reports whether it took, so a caller can
stop driving a property that does not exist.

Two steps because emission needs both, and missing either one is silent. The
property carries the colour; the _EMISSION keyword is what compiles the shader
variant that reads it. Set the colour without the keyword and the value lands
correctly on a material that renders as though it were black — which looks like
the colour being wrong rather than the variant being absent.

The HasProperty check is not defensive padding. FindOpaqueShader's tail can
resolve Unlit/Color or Sprites/Default, and neither has _EmissionColor at all, so
on a machine that fell down that chain every emission write would be a no-op with
no error — the same failure SetWaterDepthWrite records losing a debugging round
to on the stone's _ZTest. Returning a bool lets the caller find that out once at
build time instead of writing into nothing every frame.

GI flags are cleared because this is meant to be animated per frame. Left at the
default the value is treated as a baked lighting contribution, which it cannot be
when it changes sixty times a second.

### `Assets/Scripts/Game/PlaceholderArt.cs` — Only the water can toggle depth writing: render queue and the stone's _ZTest were both dead ends
<!-- ref:8c9c -->

Was `Assets/Scripts/Game/PlaceholderArt.cs:123-152`.

Whether the water writes depth, and so whether it is able to hide anything
drawn after it.

This is the only lever available for the stone-cut-in-half problem, and finding
that out took two wrong turns worth recording.

The render queue was the first. Queues control draw ORDER only: a stone at
StoneQueue really does draw after the water, but it still runs ZTest LEqual, so
its submerged fragments fail against the depth the water already wrote and are
discarded regardless of when they are drawn. Reordering cannot fix occlusion.

Setting _ZTest on the STONE was the second, and it silently did nothing.
URP's hand-written Lit.shader has no ZTest line in its ForwardLit pass and
declares no _ZTest property, so it is hardwired to LEqual and SetInt lands on a
property that does not exist. (Shader Graph materials DO expose _ZTest, which is
what made this look plausible — the water material has one. The stone is not a
Shader Graph material.)

So the toggle has to live on the water, which IS a Shader Graph material with
material override enabled, and therefore really does emit ZWrite[_ZWrite]. With
depth writing off there is nothing in the buffer to occlude the stone and it
draws whole; turning it back on lets the water swallow the stone as it sinks.

Safe now in a way it was not before URP, because the queues above are explicit
and distinct. The original draw-order bug came from every object sharing one
Transparent queue and falling back to sorting by distance to bounds centre;
with water, stone and arc pinned to 2000/3100/3200 that cannot recur.

### `Assets/Scripts/Game/PlaceholderArt.cs` — The trail material uses Sprites/Default
<!-- ref:1058 -->

Was `Assets/Scripts/Game/PlaceholderArt.cs:204-232`.

A transparent, vertex-coloured material for a TrailRenderer.

Separate from Create because a trail needs two things an opaque lit
material does not have: alpha blending, and respect for vertex colour. A
TrailRenderer's gradient reaches the shader as vertex colour and nothing else —
on a shader that ignores it the ribbon comes out flat white and the authored fade
silently does nothing, which looks like the gradient failing to apply rather than
the material being wrong.

The tint is left white here. Colour is the gradient's job; multiplying a second
colour in underneath would mean two places to edit one look.

Sprites/Default first here, and that inverts FindOpaqueShader's URP-first rule
on purpose rather than by oversight. Three things have to be true at once for a
trail — alpha blending, vertex colour, and a sampled main texture for the dashed
style — and Sprites/Default is the only name that has all three with no setup and
is guaranteed to be in Always Included Shaders, so it cannot come back null in a
player build. It renders correctly under URP; it is simply unlit.

URP's Particles/Unlit would be the richer choice but defaults to an OPAQUE
surface, so using it means setting _Surface, _Blend, _SrcBlend, _DstBlend, ZWrite
and the _SURFACE_TYPE_TRANSPARENT keyword by hand from code. Every one of those is
a silent no-op if the property name is wrong — which is exactly the failure mode
SetWaterDepthWrite records losing a debugging round to on the stone's _ZTest.
Worth revisiting deliberately if the trail ever needs to receive light; not worth
carrying untested for a ribbon that is meant to read as spray.

### `Assets/Scripts/Game/PlaceholderArt.cs` — The arc needs vertex colour too: URP/Lit has no COLOR semantic, so the arc stayed white until 2026-08-25
<!-- ref:f8c0 -->

Was `Assets/Scripts/Game/PlaceholderArt.cs:235-259`.

A transparent, vertex-coloured material for the aim arc's LineRenderer.

The arc needs exactly what the trail needs, and for exactly the reason CreateTrail
states: LineRenderer.startColor and endColor reach a shader as VERTEX COLOUR and by
no other route, so a shader that does not read vertex colour discards them in
silence and draws the material's flat tint instead.

This is not hypothetical. The arc was built with Create — which is
URP/Lit, whose vertex Attributes struct declares positionOS, normalOS, tangentOS
and three UV sets and NO COLOR semantic at all — so it could not read them even in
principle. The arc rendered plain white from the URP migration until 2026-08-25,
including the blue it was originally authored with, and the quality tint added in
Plan-SfxVfx.md section 6.1 computed correctly every frame and went nowhere. It
presented as "the colour feature does not work" rather than as "the material is
wrong", which is why it survived a round of widening the tuning ranges.

Unlit is an improvement here rather than the compromise it is for the trail. The
arc is a red/amber/green readout of predicted quality, and a lit material would
multiply scene lighting into the verdict — the same angle would report a different
colour at a different time of day. Sprites/Default also carries ZWrite Off, which
agrees with what ArcQueue was already pinned for: the guide must never be hidden,
including by the water's depth-write window (section 1.2).

### `Assets/Scripts/Game/RockLauncher.cs` — CameraSettled is false while a throw's camera swings back
<!-- ref:3c82 -->

Was `Assets/Scripts/Game/RockLauncher.cs:55-69`.

True while the camera is framing the aiming shot — false from the moment a throw
takes it away until it has come all the way back.

AimPreview holds the ghost and the arc off until this is true. Showing them the
instant the stone despawns put a full-size ghost on screen while the camera was
still metres downrange and swinging round, so it slid across the frame from
wherever the chase had left it — an aiming guide that lies about where the player
is aiming for as long as the move takes.

True when there is no Camera.main at all, deliberately. There is then no return to
wait on, and a preview that hides itself forever over a missing camera is a worse
failure than one drawn against an unframed shot.

### `Assets/Scripts/Game/RockTrail.cs` — Divide the width by the transform's world scale: TransformZ multiplies width by it
<!-- ref:06b0 -->

Was `Assets/Scripts/Game/RockTrail.cs:161-167`.

Divided by the trail's own world scale because, with LineAlignment.TransformZ, a
TrailRenderer's width IS multiplied by its transform's scale — measured in the
Editor on 2026-09-30: widthMultiplier 1 baked 1 m wide under a parent at scale 1
and 2.47 m under one at 2.47. The ribbon hangs off the rock, which is scaled ×2.47,
so without this the world-width measurement above was scaled a second time and
every trail came out about 2.5× wider than its stone (issue #24). Uniform scale is
assumed, as everywhere else that reads rock.LocalScale.

### `Assets/Scripts/Game/RockTrail.cs` — The ribbon is aligned to the face plane by TransformZ plus FaceAlignedRotation as a pair
<!-- ref:e996 -->

Was `Assets/Scripts/Game/RockTrail.cs:179-195`.

Flat in the stone's own face plane, not billboarded at the camera. The stone
tracks the phone one-to-one and its face angle is the variable the whole game
is about, so a ribbon lying in that plane shows the angle of every bounce —
whereas LineAlignment.View turns to meet the camera and reads the same width
whatever the stone is doing, which is the one thing it should not do here.

FaceAlignedRotation on this child is the other half of this line and they only
work as a pair: TransformZ faces local +Z, and the rotation is what puts local
+Z on the stone's face normal. Changing either alone silently tilts the ribbon
off the face.

Cost, and it is real: a plane seen edge-on is a line. The camera sits near the
waterline (RockLauncher.FollowActiveRock locks its Y to the home height) and a
well-thrown stone lies nearly flat, so the face plane is close to horizontal
and the view of it is close to grazing — the trail will read thin on exactly
the good throws. Widen it if so: WidthMultiplier, or the Wake style, which is
WakeWidthScale times wider for this reason.

### `Assets/Scripts/Game/RockTrail.cs` — Stop emitting when the run ends
<!-- ref:6246 -->

Was `Assets/Scripts/Game/RockTrail.cs:263-271`.

Stop laying down new ribbon the moment the run ends. Two reasons, and the
second is the load-bearing one: the stone is about to be dragged 1.5 m
underwater by RockController.SinkRoutine, and that routine turns water depth
writing ON for its duration — so a trail still emitting into the sink is cut
by the surface rather than fading out behind it.

The ribbon already laid down is untouched and fades over TrailTuning.Time.
RockController destroys the rock after despawnDelay (2.5s), comfortably
longer than any sane trail time, so the fade always completes.

### `Assets/Scripts/Game/WaterRipples.cs` — The ripple array is set as a global because Shader Graph arrays cannot live in the SRP Batcher CBUFFER
<!-- ref:a765 -->

Was `Assets/Scripts/Game/WaterRipples.cs:168-177`.

Global rather than per-material, and that is a URP requirement rather than a
preference. Shader Graph has no array property type, so an array can never
live inside the UnityPerMaterial CBUFFER the SRP Batcher requires — which
makes Material.SetVectorArray a global set wearing a per-material costume.
Setting the global directly is the same operation without the ambiguity, and
costs nothing here: there is exactly one water plane, so global scope is not
a restriction. See the matching remarks in WaterRipple.hlsl.

The fixed-size caveat is unchanged and still load-bearing: the array length
is fixed by the first call, and a shorter one later is silently ignored.

### `Assets/Scripts/Game/AimPreview.cs`

#### Mounds are squashed spheres sunk into the bank with a seeded Random
<!-- ref:2f4a -->

Was `Assets/Scripts/Game/AimPreview.cs:913-931`.

Low mounds along the bank, so the shoreline has a silhouette instead of a straight
edge.

**Spheres, never cubes.** A previous attempt at shore detail used rotated cubes,
and a capture showed exactly what they are — a row of shipping containers along the
horizon. Hard vertical edges catch the light identically on every instance, and at
this distance that regularity is the only thing the eye picks up. A squashed sphere
has no edge to catch, so it reads as a landform at any size.

Sunk into the bank rather than resting on it. A mound sitting exactly on a flat strip
draws a contact line under itself, and thirty of those in a row read as objects placed
on a shelf rather than as terrain.

Seeded Random so the coastline is identical every run. An unseeded scatter means a
screenshot cannot be reproduced and "it looked better last time" is unanswerable —
which matters here specifically because this gets tuned by capturing it.

### `Assets/Scripts/Game/PlaceholderArt.cs`

#### The water loads its shader via Resources, not Shader.Find, which turns magenta in a player build
<!-- ref:cc0b -->

Was `Assets/Scripts/Game/PlaceholderArt.cs:174-189`.

Water, with the ripple shader if it is available and flat colour if it is not.

Resources.Load rather than Shader.Find: a project shader is not in Always
Included Shaders, but everything under a Resources folder ships with the build.
Shader.Find on one works in the editor and comes out magenta in a player build —
the worst way to find out. Falling back to flat water keeps a broken or missing
shader playable instead of leaving the lake invisible.

The URP graph is tried first and the Built-in surface shader is kept as the
fallback, so this stays playable on both pipelines for the length of the
migration. They are named differently on purpose: two assets called WaterRipple
in the same Resources folder would make Resources.Load ambiguous. Once URP is
confirmed, the old shader goes and the graph takes the plain name.

### `Assets/Scripts/Game/RockTrail.cs`

#### A factory rather than inspector wiring because rocks are built at runtime
<!-- ref:186b -->

Was `Assets/Scripts/Game/RockTrail.cs:68-83`.

Hangs a configured trail off a rock, or returns null if the style is None or the
material could not be built.

A factory rather than inspector wiring, because the rock is instantiated at
runtime from Resources and nothing here ever passes through the inspector — the
trap TrailTuning's remarks describe. The skip constants are passed in rather than
looked up so the speed thresholds come from the same asset the throw was
simulated with; two lookups could disagree after a retune.
<param name="stoneColour">
The colour the stone itself is drawn in — PlayerColours.StoneColourFor, the same
blend the shore stone and the aim ghost use — so the ribbon matches the rock it
streams from rather than being a second, different shade of the player.
</param>

#### Copy the gradient with white colour keys so only alpha fades
<!-- ref:e884 -->

Was `Assets/Scripts/Game/RockTrail.cs:308-323`.

A copy of a gradient keeping only its alpha keys, each scaled, with every colour
key forced to white.

White colour keys because the colour is the stone's, set on the material, and the
gradient multiplies into it. Left as authored, TrailTuning.Colour's keys (a dark
grey head fading to pale blue on the shipped asset) tinted every trail darker and
bluer than the rock it came from — two places deciding one colour. The gradient
now decides only the fade.

A copy, because the source gradient belongs to the shared TrailTuning ASSET.
Editing it in place would edit the asset itself — which in the editor persists to
disk, so every subsequent throw would come out fainter than the last until the
wake vanished entirely and the asset on disk was quietly wrong.

### `Assets/Scripts/Game/TrailTuning.cs`

#### Trail numbers live in a Resources asset
<!-- ref:07ed -->

Was `Assets/Scripts/Game/TrailTuning.cs:5-23`.

Every number that shapes the ribbon behind a stone. One asset, edited in the
inspector, no recompile.

A ScriptableObject at Resources/TrailTuning for the same reason SkipTuning is one,
and it is not a style preference. The rock is built at runtime by
RockLauncher.CreateRockObject, so anything holding trail numbers as [SerializeField]
on a component would sit at its C# field initializer forever — the exact trap
AimPreview.Awake documents having already been caught by once, where "the whole game
ran on C# field initializers and the numbers were only reachable by recompiling."

Speed-derived values are stored as FRACTIONS of SkipConstants.ReferenceSpeed rather
than absolutes. WaterRipples carries the note explaining why: its impact thresholds
were hardcoded at 4 and 25 against a fixed MaxSpeed of 26, so raising the launch
speed clamped every impact to full strength and the ripple stopped responding to
anything at all. A trail tinted by absolute speed fails the same way and just as
quietly.

### `tools/art/lake-louise/build_lake_louise.py`

#### The FBX axis rule was observed, not assumed
<!-- ref:0448 -->

Was `tools/art/lake-louise/build_lake_louise.py:637-647`.

FBX for Unity: Blender's standard Unity settings, -Z forward, Y up, transform baked.

The file then stores Blender (X, Y, Z) as (X, Z, -Y), and Unity's importer reads a file's
(x, y, z) as (x, y, -z), so Unity gets (X, Z, Y): x right, y up, z down the lake.

That importer rule was OBSERVED, not assumed, and at some cost: the first export used
axis_forward='Z' on paper reasoning, and in Unity the whole valley came in turned 180
degrees - behind the camera and mirrored left-right (2026-09-28). The file then held
(-X, Z, Y); Unity showing it turned 180 degrees is what pins the rule down.
LakeLouiseEnvironment.cs still checks on load and corrects a 180 degree turn with a warning.
