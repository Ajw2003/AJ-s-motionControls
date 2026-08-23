# SFX / VFX Plan

Scope: rock trails, the six requested sound events, music (menu / lobby / gameplay / results),
plus a ranked list of additional polish. Written against the code as it stands on `Sfx-Vfx`.

Nothing here is implemented yet. This is the design and the build order, plus the traps this
codebase specifically will spring on an audio/VFX layer.

---

## 1. Constraints that shape every decision below

Four facts about this project change what is and isn't possible. Read these before the design.

### 1.1 Built-in Render Pipeline — not URP

`ProjectSettings/GraphicsSettings.asset` has `m_CustomRenderPipeline: {fileID: 0}`, and
`Packages/manifest.json` has no `com.unity.render-pipelines.*`. This is the stock Built-in
pipeline.

Consequences:

| Want | Available? |
|---|---|
| Particle System (Shuriken) | Yes — `com.unity.modules.particlesystem` is in the manifest |
| `TrailRenderer` / `LineRenderer` | Yes, built-in |
| VFX Graph | **No** — URP/HDRP only |
| Shader Graph | **No** — URP/HDRP only |
| URP `Volume` post-processing (bloom, vignette) | **No** |
| Post Processing Stack v2 (`com.unity.postprocessing`) | Yes, if added — this is the Built-in route to bloom |

**Do not migrate to URP for this work.** [`WaterRipple.shader`](../Assets/Resources/WaterRipple.shader#L73)
is a *surface shader* (`#pragma surface surf Standard fullforwardshadows addshadow`). Surface
shaders do not compile under URP. Migrating means rewriting the water — which is the one piece of
real art the project has — as a side effect of adding a trail. If bloom turns out to be wanted for
sun-glint on the water, add PPv2 as a separate, later decision.

### 1.2 Render queues are pinned by hand, and new renderers will break if they aren't

[`PlaceholderArt`](../Assets/Scripts/Game/PlaceholderArt.cs) carries a long comment about a bug
where the 200-unit water plane painted over the stone and the aim arc. The cause: everything ran
`ZWrite Off`, so the depth buffer stayed empty and Unity fell back to sorting transparent
renderers by distance to each renderer's *bounds centre*. The water's centre is at z = 60; the
camera trails the stone by a few units; the moment the stone passed z = 60 the water sorted nearer
and drew last.

The fix was explicit `renderQueue` per role:

```
WaterQueue = 2000    StoneQueue = 3100    ArcQueue = 3200
```

The water still writes no depth ([`WaterRipple.shader:56`](../Assets/Resources/WaterRipple.shader#L56)
`ZWrite Off`).

**Every new renderer this plan adds — trail, splash particles, score popups, launch spray — must
pin a queue in `PlaceholderArt` or it will vanish behind the water at the exact moment the rock is
furthest out, which is the moment anyone is looking.** This is the single most likely way the VFX
work "mysteriously stops working halfway down the lake."

Proposed additions to `PlaceholderArt`:

```csharp
public const int WaterQueue      = 2000;
public const int TrailQueue      = 3050;  // behind the stone, in front of the water
public const int SplashQueue     = 3060;  // with the trail; particles at contact
public const int StoneQueue      = 3100;
public const int ArcQueue        = 3200;
public const int WorldUiQueue    = 3300;  // score popups — above everything
```

Also: because the water writes no depth, **soft particles will not work** (they need depth to fade
against). Use hard-edged particles and rely on the queue ordering.

### 1.3 Serialized fields are always default on anything Bootstrap creates

[`Bootstrap`](../Assets/Scripts/Managers/Bootstrap.cs) builds its managers with `new GameObject()`
+ `AddComponent`. An `AddComponent`'d component has never been through the inspector, so every
`[SerializeField]` is at its C# field initializer.

This has already bitten the project once — see the comment in
[`AimPreview.Awake`](../Assets/Scripts/Game/AimPreview.cs#L100): the `tuning` field was never
assigned, so "the whole game ran on C# field initializers — the numbers were only reachable by
recompiling." The fix was `Resources.Load<SkipTuning>("SkipTuning")`.

**An `AudioManager` that expects inspector-assigned `AudioSource`s and `AudioClip`s cannot be
created the way every other manager here is created.** Two working routes:

- **(a) Prefab in `Resources/`.** `Resources.Load<GameObject>("AudioManager")` + `Instantiate`.
  Inspector wiring survives. Precedent already exists: `RockObject.prefab` is loaded exactly this
  way by [`RockLauncher`](../Assets/Scripts/Game/RockLauncher.cs#L151).
- **(b) ScriptableObject data + code-built sources.** Clips live in a `Resources/AudioBank.asset`;
  the manager creates its `AudioSource`s in `Awake()`. Precedent: `SkipTuning.asset`.

**Recommendation: both.** A prefab for the manager (so the mixer groups and source settings are
authorable in the inspector), holding a reference to an `AudioBank` ScriptableObject (so clips are
data, and gameplay code never needs a clip reference — see §3).

**Trap:** `SingletonBase.Instance` auto-creates a bare instance if none is found
([`Singleton.cs:36`](../Assets/Scripts/Utils/Singleton.cs#L36)). If *anything* touches
`AudioManager.Instance` before Bootstrap instantiates the prefab, you get a clipless manager, and
the prefab one then destroys itself in `Awake`. Bootstrap must instantiate the prefab **before**
`RelayClient`, `TurnManager`, and the screens.

### 1.4 The game currently runs entirely inside the Bootstrap scene

`autoLoadFirstScene` defaults to **false**, and the comment says why: "jumping straight into an
empty scene just looks like the game closed." The lake, camera, water and aim rig are all built at
runtime by [`AimPreview.BuildPlaceholderScene`](../Assets/Scripts/Game/AimPreview.cs#L303).
`Scenes/MainMenu`, `Lobby`, `Game`, `Results` exist as files but aren't being driven.

**So music must not be driven by scene loads.** Drive it off `TurnManager.Phase`
(`Lobby` / `Playing` / `Results`), which is the thing that actually changes. Scene loads can be a
secondary trigger later, once the scenes carry content.

---

## 2. The AudioManager port — what needs fixing before it can be used

The FactoryTM [`AudioManager.cs`](../../Projects/FactoryTM/Assets/Scripts/Managers/AudioManager.cs)
is a reasonable skeleton, but it has defects that range from "does nothing" to "will actively
sound broken in this game." Listing them because several are invisible until you're debugging
audio at 1am.

| # | Line | Problem | Consequence here | Fix |
|---|---|---|---|---|
| 1 | `Start()`: `persistBetweenScenes = PersistBetweenScenes;` | `SingletonBase.Awake()` reads `persistBetweenScenes` to decide `DontDestroyOnLoad`. `Start()` runs *after* `Awake()`. | The assignment does nothing. Manager dies on the first scene load. | Set it in an `Awake()` override before `base.Awake()`, as [`ScoreManager`](../Assets/Scripts/Managers/ScoreManager.cs#L30) and [`TurnManager`](../Assets/Scripts/Managers/TurnManager.cs#L81) both do. |
| 2 | `Start()`: `sfxSource = GetComponent<AudioSource>();` | Overwrites the inspector-assigned source with whatever is on the same GameObject — often nothing. | Silently clobbers correct wiring; `sfxSource` becomes null and every SFX logs "Source or clip is null". | Delete the line. |
| 3 | `var stopClipRoutine = StopClipRoutine(channel, duration);` | Creates the iterator, never calls `StartCoroutine`. | The `duration` parameter is inert. Nothing ever stops early. | `StartCoroutine(...)`, or drop `duration` entirely — see §3, nothing in this game needs it. |
| 4 | `targetSource.clip = clip;` followed by `PlayOneShot(clip)` | Setting `.clip` is pointless with `PlayOneShot`, and wrong for music: `PlayOneShot` cannot loop and cannot be stopped individually. | Music will not loop. `StopClip(Music)` will behave unpredictably. | Split the paths: one-shots use `PlayOneShot(clip, volume)`; music/ambient use `.clip` + `.loop = true` + `.Play()`. |
| 5 | `if (volume > 0.05) targetSource.volume = volume;` | Volume is a *persistent* property of the shared source. | One quiet clip permanently ducks every later sound on that channel. And a deliberate volume of 0 is ignored. | `PlayOneShot(clip, volume)` — the volume arg is per-shot and does not stick. |
| 6 | `pitchVariation` | Declared, never read. | The field promises the thing that stops a repeated sound from machine-gunning, and doesn't do it. | Wire it — but see §3.2, it needs a source pool to work correctly. |
| 7 | `_mixer` | Declared, never assigned or used. | No mixer routing, no exposed volume params, no ducking. | Either wire an `AudioMixer` properly (§3.3) or delete the field. |
| 8 | No `OnDestroy` | Doesn't unsubscribe from `EventManager`. | Survivable — this project's `EventManager` holds subscribers weakly and prunes dead ones. But it breaks the house convention. | Add `OnDestroy` → `UnsubscribeFromAllEvents(this)` + `base.OnDestroy()`, matching every other manager here. |
| 9 | `AudioClipEvent`, `AudioChannel` | Live in FactoryTM's `EventTypes` namespace. Not in this project. | Won't compile until ported. | Port, but see §3.1 — the *shape* of `AudioClipEvent` is wrong for this codebase. |

Also strip the FactoryTM-only `using` directives (`Placeables`, `Ui`, `Weapons`, `Nodes`,
`Managers`) and the `// GEMINI_MODIFICATION_TEST` marker — same cleanup already done when
[`EventManager`](../Assets/Scripts/EventSystems/EventManager.cs#L22) and `SingletonBase` were
ported, and both files document it.

Note `EventSystems` here vs `Code.Scripts.EventSystems` in FactoryTM — the namespace differs.

---

## 3. Architecture

### 3.1 Presentation listens; gameplay publishes nothing new for audio

The codebase already has a strong rule and states it twice.
[`SkipPhysics`](../Assets/Scripts/Game/SkipPhysics.cs#L64) says observable effects belong to the
caller, never the solver — because the aim preview runs `Step` hundreds of times a frame for a
throw that never happened, and a solver that published would score and *sound* imaginary throws.
[`WaterRipples`](../Assets/Scripts/Game/WaterRipples.cs#L11) then says its safety against that is
"inherited, not re-implemented."

**Audio and VFX follow `WaterRipples` exactly: standalone components that subscribe to existing
events and add zero lines to gameplay code.** That inherits the same protection for free.

This rules out FactoryTM's `AudioClipEvent(clip, channel, volume, duration)` as the primary API.
Publishing it requires the caller to hold an `AudioClip` reference — meaning a `[SerializeField]
AudioClip` on `RockController` or `ScoreManager`, which per §1.3 would be **null forever** on
anything Bootstrap builds, and which puts asset references inside gameplay classes.

Instead:

```csharp
public enum SoundId
{
    Skip, SkipMilestone, Sink, ThrowRelease,
    ScoreTick, ThrowScored, ThrowFailed,
    TurnStart, MatchStart, MatchWin, PlayerJoined, UiConfirm,
}

/// <summary>Play a sound by identity. Callers never hold clips — the bank resolves them.</summary>
public class PlaySoundEvent : IEvent
{
    public SoundId Id { get; }
    public Vector3? Position { get; }   // null = 2D/UI
    public float Intensity { get; }     // 0..1, drives volume + brightness
    public int Step { get; }            // combo index, drives the pitch ladder
}
```

Keep `AudioClipEvent` as a secondary escape hatch for one-off clips, but everything in this plan
goes through `PlaySoundEvent` and an `AudioBank`:

```csharp
[CreateAssetMenu(menuName = "RockSkipping/Audio Bank", fileName = "AudioBank")]
public class AudioBank : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public SoundId Id;
        public AudioClip[] Variants;        // round-robin, never the same one twice running
        [Range(0f,1f)] public float Volume = 1f;
        public float BasePitch = 1f;
        [Range(0f,0.5f)] public float PitchJitter = 0.03f;
        public AudioChannel Channel = AudioChannel.Sfx;
        public bool Spatial = true;
        public float MinDistance = 5f, MaxDistance = 150f;
    }
    public Entry[] Entries;
}
```

Lives at `Resources/AudioBank.asset`, loaded the same way `SkipTuning` is. Retuning any sound is
then an inspector edit with no recompile — the explicit goal stated in `SkipTuning`'s remarks.

### 3.2 SFX needs a pooled source, not one shared `AudioSource`

Two independent reasons the single `sfxSource` won't survive this game:

**Pitch.** `AudioSource.pitch` is a property of the *source*, not the shot. A pitch ladder across a
skip run (§5.2) sets pitch, then plays. But skips land ~150–400ms apart and the previous one is
still ringing — so setting pitch for skip N audibly retunes the tail of skip N−1. It sounds like a
tape warble. Same problem defeats `pitchVariation` generally.

**Position.** Skips happen from 0m to well past 100m from the camera. A single 2D source plays
every skip at the same volume in the same place, and a run reads as flat. 3D-positioned sources
with rolloff sell the distance for free — and the rock's contact point is already carried on
`SkipOccurredEvent.Position`.

Fix: a small pool (8–12) of `AudioSource` components, round-robined, each configured 3D. Grab one,
set `transform.position` / `pitch` / `clip`, `Play()`, return it when it finishes. `MaxRipples` in
`WaterRipples` is 16 and a long run recycles rings oldest-first — the audio pool should recycle the
same way rather than dropping new sounds.

### 3.3 AudioMixer — set it up now, not later

Create `Resources/GameMixer.mixer` with groups: **Master → Music, SFX, Ambient, UI**, and expose
`MusicVolume` / `SfxVolume` / `AmbientVolume` / `MasterVolume` as parameters.

Do it before authoring sources, not after. Retrofitting mixer groups means touching every source
that was ever created, including pooled ones built in code. It also buys:

- A settings screen with working sliders (`mixer.SetFloat("MusicVolume", Mathf.Log10(v) * 20f)` —
  note the log conversion; a linear slider straight into a mixer sounds wrong).
- Snapshot transitions for ducking music under a big combo, and for the results-screen mood shift.

### 3.4 Two music sources, crossfaded

"Several tracks" plus phase changes means one `musicSource` isn't enough — a hard cut between
lobby and gameplay is the cheapest-sounding thing in a party game. Use two sources ping-ponged:
fade A down while B comes up over ~1.5s. A `MusicDirector` component owns:

- the playlist per phase (shuffled, no immediate repeat),
- crossfade on phase change,
- crossfade on track end within a phase.

---

## 4. Events that need to be added

All small, all additive. None change existing behaviour.

| Event | Published from | Carries | Needed for |
|---|---|---|---|
| `RockSunkEvent` | [`RockController.Update`](../Assets/Scripts/Game/RockController.cs#L120), on `step.Sank`, **before** `Finish()` | `Vector3 Position`, `float ImpactSpeed`, `int SkipCount` | Sinking SFX, sink splash VFX |
| `RockLaunchedEvent` | [`RockController.Launch`](../Assets/Scripts/Game/RockController.cs#L66) | `Vector3 Origin`, `float Power`, `int PredictedSkips` | Throw-release SFX, launch spray, camera punch |
| `ScoreAwardedEvent` | [`ScoreManager.OnThrowCompleted`](../Assets/Scripts/Managers/ScoreManager.cs#L42) | `string PlayerId`, `int Points`, `int NewTotal` | Scoring SFX, score tally popup |
| `MatchPhaseChangedEvent` | `TurnManager` — `StartMatch`, `EndMatch`, `ReturnToLobby` | `MatchPhase From`, `MatchPhase To` | Music transitions |
| `TurnStartedEvent` | [`TurnManager.StartTurn`](../Assets/Scripts/Managers/TurnManager.cs#L232) | `string PlayerId`, `int Round`, `int TotalRounds` | "Your turn" sting, phone haptic |
| `MatchEndedEvent` | [`TurnManager.EndMatch`](../Assets/Scripts/Managers/TurnManager.cs#L199) | `string WinnerId` (null on tie), standings | Winning SFX, fanfare, results music |

`EndMatch` already computes `winner` and handles the tie case explicitly — pass it through rather
than recomputing, so a tie stays a tie and the fanfare doesn't crown someone.

**Note on `ImpactSpeed` at sink:** [`SkipPhysics.Step`](../Assets/Scripts/Game/SkipPhysics.cs#L171)
sets `result.ImpactSpeed = speed` at line 171, *before* either sink branch (line 179 and line 236).
So `ImpactSpeed` is valid on a sank step. The splash can scale off it.

**Note on the pure-solver rule:** `RockSunkEvent` and `RockLaunchedEvent` go in `RockController`,
never in `SkipPhysics`. `RockController` is already documented as "the only place skip events are
published." Keep it that way.

### 4.1 Subscription order matters, and Bootstrap already encodes that

[`EventManager.Publish`](../Assets/Scripts/EventSystems/EventManager.cs#L109) delivers in
**subscription order**, and there's a long comment about a bug where reversing it made the match
end before the final throw was counted. Bootstrap creates `ScoreManager` before `TurnManager`
deliberately for this reason.

The audio/VFX layer should be created **after** `ScoreManager` and `TurnManager`, so by the time it
hears `ThrowCompletedEvent` the score is banked and the phase is settled — meaning it can read
final state to decide *which* sound to play.

---

## 5. The requested list

### 5.1 Configurable rock trails

`TrailRenderer` on the rock, driven by a `TrailTuning` ScriptableObject at `Resources/TrailTuning`
— the same pattern as `SkipTuning`, for the same reason (inspector-tunable, no recompile, survives
`AddComponent`).

```csharp
[CreateAssetMenu(menuName = "RockSkipping/Trail Tuning", fileName = "TrailTuning")]
public class TrailTuning : ScriptableObject
{
    public enum TrailStyle { Ribbon, Dashed, Wake, None }

    public TrailStyle Style = TrailStyle.Ribbon;

    [Tooltip("Seconds of trail behind the stone.")]
    public float Time = 0.55f;

    [Tooltip("Width at the stone, tapering to zero at the tail.")]
    public AnimationCurve WidthCurve;
    public float WidthMultiplier = 0.18f;

    [Tooltip("Colour head-to-tail. Alpha does the fading.")]
    public Gradient Colour;

    [Tooltip("Distance the stone must travel before a new trail vertex. Higher = cheaper, " +
             "chunkier. At 40 m/s a value of 0.1 is ~400 verts/sec.")]
    public float MinVertexDistance = 0.15f;

    [Tooltip("Tint the trail by the stone's remaining speed, so a dying run visibly dims.")]
    public bool ColourBySpeed = true;
    public Gradient SpeedColour;

    [Tooltip("Stop emitting the moment the stone sinks, so the trail fades in place " +
             "instead of following it underwater.")]
    public bool StopEmittingOnSink = true;
}
```

Implementation notes, all of them things this codebase will otherwise punish:

- **Pin the queue.** `TrailQueue = 3050`, via `PlaceholderArt.Create`. See §1.2. Without it the
  trail disappears behind the water past z = 60.
- **Material.** `PlaceholderArt.Create` currently resolves `Standard` → `Unlit/Color` →
  `Sprites/Default`. Its own remarks warn that `Shader.Find` only resolves shaders in
  **Always Included Shaders**, and only `Sprites/Default` is listed — so anything else "would look
  right in the editor and come out magenta in a player build." For a `Dashed` style needing a
  tiled texture (`TrailRenderer.textureMode = Tile`), either add the shader to Always Included or
  put it under `Resources/` and `Resources.Load` it, the way `CreateWater` handles `WaterRipple`.
- **Attach it where the rock is built.** [`RockLauncher.CreateRockObject`](../Assets/Scripts/Game/RockLauncher.cs#L149)
  is the single place a rock exists. Add the trail there, and to the aim ghost only if you want a
  preview of the style — probably not, the ghost doesn't move.
- **Sink behaviour.** On `RockSunkEvent`, set `emitting = false`. The rock is destroyed 2.5s later
  (`despawnDelay`), which is longer than any sane trail `Time`, so the fade completes.
- **Perf.** At `MaxSpeed = 40` and `MinVertexDistance = 0.15`, that's ~270 verts/sec, and a long
  throw runs ~10s. Cap `Time` rather than letting the trail run the full flight.

Style variants worth having, given `SkipTuning.DisplayName` implies multiple rock types later:
`Ribbon` (default), `Dashed` (tiled texture, reads as speed), `Wake` (wide + low alpha, only
visible near the water), `None`.

### 5.2 Skipping SFX — the pitch ladder is the whole thing

Subscribe to `SkipOccurredEvent`. It already carries `SkipIndex`, `Speed`, and `Position` — every
input needed.

```
pitch  = basePitch * pow(2f, Mathf.Min(skipIndex, ladderCap) * semitonesPerSkip / 12f)
volume = Mathf.InverseLerp(minImpactSpeed, maxImpactSpeed, speed)
```

- `semitonesPerSkip` ≈ 1.0, `ladderCap` ≈ 14. **Cap it.** A 20-skip run without a cap becomes a
  dog whistle. Once capped, hold the top note and let volume/variant carry the rest.
- Derive `minImpactSpeed` / `maxImpactSpeed` as *fractions of `SkipTuning.MaxSpeed`*, not
  absolutes. [`WaterRipples`](../Assets/Scripts/Game/WaterRipples.cs#L29) has a comment about
  exactly this: the values were hardcoded at 4 and 25 against a `MaxSpeed` of 26, so raising launch
  speed clamped every impact to full strength and the ripple stopped responding to anything. Same
  bug will hit skip volume. Copy the `minImpactSpeedFraction` / `maxImpactSpeedFraction` approach.
- 3–4 clip variants, round-robin, never the same one twice running.
- 3D-positioned via the pool (§3.2).

**Milestone accents.** At skips 5, 10, 15, layer a second brighter clip on top. Cheap, and it turns
a long run into an event people react to from the couch.

### 5.3 Sinking SFX

On `RockSunkEvent`. Two distinct sounds, chosen by `SkipCount`:

- **`Sink`** — a run that ended naturally. Low "gloop", volume by `ImpactSpeed`.
- **`ThrowFailed`** — `SkipCount == 0`. The rock never skipped at all. This is the "fail" sound
  from your list, and it's the same moment as the sink, not a separate event. A flat, dead plunk;
  no pitch ladder, no reward tone.

### 5.4 Scoring SFX

Two layers, because [`ScoreManager`](../Assets/Scripts/Managers/ScoreManager.cs#L23) banks points
per-skip and commits on throw completion:

- **Per skip** — a soft tick riding on top of the skip sound, following the same pitch ladder.
  Blends into the skip sound rather than being a second event.
- **On `ScoreAwardedEvent`** — the tally. A rising ka-ching whose length scales with points
  awarded, landing on a resolved note.

### 5.5 Failing SFX

See §5.3 — `SkipCount == 0` at sink. Optionally also on a turn timeout, if one ever exists.

### 5.6 Winning SFX

On `MatchEndedEvent`. `EndMatch` already refuses to name a winner on a tie
([`TurnManager.cs:208`](../Assets/Scripts/Managers/TurnManager.cs#L208)) — respect it:

- `WinnerId != null` → full fanfare.
- `WinnerId == null` → a shorter, unresolved "tied" sting. Playing a victory fanfare on a tie is a
  small lie the results screen then has to contradict.

### 5.7 Music

Driven by `MatchPhaseChangedEvent`, **not scene loads** (see §1.4).

| Phase | Track set | Feel |
|---|---|---|
| Menu (pre-lobby) | 1 track | Calm, loopable, low stakes |
| `Lobby` | 1–2 tracks | Light, anticipatory; can loop indefinitely while people join |
| `Playing` | 3–4 tracks, shuffled | The bulk of the music. Rotate per round or per track end |
| `Results` | 1 track + a fanfare stinger | Resolves the session |

- Crossfade ~1.5s on phase change (§3.4).
- Within `Playing`, crossfade at track end and shuffle without immediate repeat — a 5-round match
  with 2–8 players runs long enough that one loop gets old.
- **Duck the music** under the sink + score + turn-change pileup (§7.1) with a mixer snapshot.

**Ambient** is a separate channel and mostly free polish: a water-lapping loop, distant birds, wind
that rises with throw power. The `ambientSource` field already exists in the AudioManager.

---

## 6. Additional polish ideas, ranked by payoff per hour

Roughly ordered. The top few are where the gamefeel actually is.

### 6.1 Aim arc colour by predicted quality — *highest payoff, ~30 min*

[`AimPreview`](../Assets/Scripts/Game/AimPreview.cs#L229) already computes `PredictedSkips` and
`PredictedDistance` every frame and does nothing visual with them. Tint the `LineRenderer`
red → amber → green by predicted skips.

This isn't decoration — it closes a real readability gap. Right now the only feedback on a bad
angle is arc *length*, and the angle-quality curve in
[`SkipPhysics`](../Assets/Scripts/Game/SkipPhysics.cs#L202) is a gradient with no cliff, so a
mediocre throw looks broadly like a good one until it's in the water. Colour makes the gradient
visible while there's still time to correct.

### 6.2 Ghost rock charge glow — *~30 min*

`AimPreview` smooths `_curPower` every frame ([line 141](../Assets/Scripts/Game/AimPreview.cs#L141))
and only feeds it to arc length. Scale/emissive-pulse the ghost stone with it. Winding up should
*look* like winding up.

### 6.3 Camera punch on impact — *~1 hr*

[`RockLauncher.FollowActiveRock`](../Assets/Scripts/Game/RockLauncher.cs#L107) owns the camera and
already `SmoothDamp`s it. Add a decaying positional offset on `SkipOccurredEvent`, scaled by
`ImpactSpeed`, biggest on the opening hop.

**Keep it small.** There's a comment at [line 116](../Assets/Scripts/Game/RockLauncher.cs#L116)
about locking camera Y specifically to stop motion sickness, and another offering to lock look
pitch for the same reason. Someone has already been made queasy by this camera. Budget ~0.15
units, decaying over ~0.2s, and apply it as an offset *after* the `SmoothDamp` so it doesn't fight
the follow.

### 6.4 Skip counter popups — *~2 hrs*

World-space text at each contact point: `+10`, then `×3`, `×5`, `NICE`, escalating in size and
colour with `SkipIndex`. Pooled, `WorldUiQueue = 3300`. This is the classic party-game read from
across a room — the thing that makes a spectating player go "ohhh."

Pair with the pitch ladder (§5.2) so the eye and the ear escalate together.

### 6.5 Skip splash particles — *~2 hrs*

A small Shuriken burst at each `SkipOccurredEvent.Position`, count and size by `Speed`. Pooled —
a long run is 20 bursts in 3 seconds and `Instantiate`-per-skip is real garbage. Bigger, wetter
burst on `RockSunkEvent`.

Hard-edged particles only — soft particles need a depth buffer and the water writes none (§1.2).

### 6.6 Per-skip phone haptics — *~1 hr, and specific to this game*

The controller already vibrates on release
([`controller/js/app.js:109`](../controller/js/app.js#L109), Android-only with a documented note
that iOS `navigator.vibrate` is unreliable), and `app.js` has a clean `switch` on message type
with cases for `TURN`, `SPECTATE`, `JOINED` and others. Adding a `HAPTIC` case is small.

Buzz the throwing player's phone on each skip, escalating. The phone *is* the rock in this game —
feeling the skips in your hand is the single most on-premise idea available here.

**Caveat, and it's a real one:** [`TurnManager.BroadcastSpectate`](../Assets/Scripts/Managers/TurnManager.cs#L299)
carries an explicit warning about a previous per-frame broadcast being "the most expensive traffic
in the game." Per-skip is not per-frame — max ~20 messages over ~3 seconds, to **one** phone
(the thrower, not a broadcast). That's fine. But send it as a targeted message, and do not
piggyback it on the spectate broadcast.

### 6.7 Water sparkle / foam on impact — *~2 hrs*

[`WaterRipple.shader`](../Assets/Resources/WaterRipple.shader) already takes a `_Ripples` array of
`(x, z, age, strength)` and computes expanding rings. Adding a foam/whitening term keyed to young,
strong ripples is a contained shader edit — no new C# and no new event, since
[`WaterRipples`](../Assets/Scripts/Game/WaterRipples.cs) already feeds it everything needed.

Boost `strength` for the sink ripple specifically, so the last ring is visibly the biggest.

### 6.8 Launch spray at the throw origin — *~1 hr*

Small burst on `RockLaunchedEvent`, scaled by `Power`. Sells the effort of the throw at the moment
the arc disappears.

### 6.9 Turn-change presentation — *~2 hrs*

On `TurnStartedEvent`: a short sting, the player's name sweeping onto the TV, and a haptic to the
phone that's now up. With 2–8 players this fires constantly, so keep it under ~1.2s or it becomes
the dominant experience of the game.

### 6.10 Hit-stop on the opening hop — *consider, then probably skip*

A 40–60ms `Time.timeScale` dip on the first bounce. It works in single-player action games, but
here the simulation is a fixed-step accumulator driven by `Time.deltaTime`
([`RockController.Update`](../Assets/Scripts/Game/RockController.cs#L99)) while the network is
realtime, so a timescale dip desynchronises the felt timing between phone and TV. If tried, keep it
under 60ms and test with a phone in hand. Listed for completeness; I'd leave it out of the first
pass.

### 6.11 Bloom / sun glint — *deferred*

Needs Post Processing Stack v2 (§1.1). Nice on water, but it's a package addition and a global
look change. Revisit once the rest lands.

---

## 7. Traps specific to this codebase

Collected from the code comments, which document several of these as bugs already paid for once.

### 7.1 Three sounds land in the same frame at every throw's end

When a rock sinks, this all happens synchronously in one frame:

```
RockController.Finish()
  └─ ThrowCompletedEvent
       ├─ ScoreManager.OnThrowCompleted   → points banked → ScoreAwardedEvent
       └─ TurnManager.OnThrowCompleted    → NextTurn() → StartTurn() → TurnStartedEvent
                                                      (or EndMatch() → MatchEndedEvent)
```

So the sink splash, the score tally, and the turn-change sting (or the victory fanfare) all fire
together. Played simultaneously that's mush, and the score sound — the rewarding one — gets buried.

**The presentation layer should schedule, not just react.** Something like:

```
t+0.00   sink SFX + splash VFX
t+0.35   score tally (rising ka-ching)
t+1.20   turn sting / fanfare
```

Requires the audio layer to hold a short queue rather than playing on receipt. Worth building in
from the start — retrofitting scheduling into a fire-on-event design means touching every handler.

### 7.2 Do not put anything observable in `SkipPhysics`

Stated twice in the code, in [`SkipPhysics`](../Assets/Scripts/Game/SkipPhysics.cs#L64) and
[`RockController`](../Assets/Scripts/Game/RockController.cs#L11). The aim preview runs `Step`
hundreds of times per frame for a throw that never happened. A sound in the solver means the aim
preview machine-guns skip sounds while a player is merely aiming.

### 7.3 Anything derived from `SkipTuning.MaxSpeed` must be a fraction, not an absolute

See §5.2 and the `WaterRipples` comment. Volume ranges, splash sizes, trail widths, camera punch
magnitude — all of them. Hardcode an absolute and the effect silently stops responding the first
time `MaxSpeed` is retuned.

### 7.4 Pin every new render queue

§1.2. The failure mode is delayed and confusing: everything looks right near the camera and
vanishes past z = 60.

### 7.5 Don't let `AudioManager.Instance` be touched before its prefab loads

§1.3. `SingletonBase` will happily auto-create a clipless one.

### 7.6 Unsubscribe in `OnDestroy`

Every manager here does it, and `WaterRipples` calls it "mandatory, not tidiness."

---

## 8. Build order

Each step is independently testable and leaves the game in a working state.

**M1 — Foundation.** *No sound yet; this is the part that makes the rest cheap.*
1. Port `AudioManager` with the §2 fixes. `AudioChannel`, `AudioClipEvent` into `EventTypes/AudioEvents/`.
2. `SoundId`, `PlaySoundEvent`, `AudioBank` SO at `Resources/AudioBank.asset`.
3. `GameMixer.mixer` with Master/Music/SFX/Ambient/UI + exposed params.
4. `AudioManager.prefab` in `Resources/`, instantiated **first** in `Bootstrap.Awake`.
5. Pooled 3D SFX sources (§3.2).
6. Verify: one placeholder beep, triggered from a debug key, plays through the mixer.

**M2 — Events.** Add the six events from §4. No listeners yet. Verify with
[`EventProbe`](../Assets/Scripts/Verification/EventProbe.cs) that each fires exactly once at the
right moment. Turn on `Bootstrap.eventDebugLogging` for this.

**M3 — Core SFX.** Skip (with pitch ladder), sink, fail, score, win. Placeholder clips are fine —
the ladder and the impact-speed mapping are what's being tuned, not the samples.

**M4 — Trails.** `TrailTuning` SO, `TrailQueue` in `PlaceholderArt`, `TrailRenderer` in
`RockLauncher.CreateRockObject`, `emitting = false` on sink.

**M5 — Music.** `MusicDirector`, two crossfading sources, phase-driven playlists.

**M6 — Scheduling.** The §7.1 queue. Do this before adding more sounds, not after.

**M7 — Polish, in §6 order.** Arc colour → ghost glow → camera punch → popups → splash particles →
haptics.

---

## 9. Open questions

1. **Where are the audio assets coming from?** There are currently **zero** `.wav`/`.mp3`/`.ogg`
   files in `Assets/`. Licensed pack, commissioned, or generated? This gates M3 onward — though M1,
   M2 and M4 (trails) can all proceed without a single clip.
2. **`AudioClipEvent` — keep or drop?** §3.1 argues `PlaySoundEvent` should be the primary API. Is
   there a reason to keep the FactoryTM shape around, e.g. other ported code that expects it?
3. **Menu music vs lobby music — one phase or two?** `MatchPhase` has no `Menu` value; the
   pre-lobby menu isn't a phase the code models. Add a fourth phase, or treat menu and lobby as the
   same musical state?
4. **Is the `Results` scene going to be entered for real?** §1.4 — everything currently runs in
   Bootstrap. If the scenes are going to start being used, music should key off both phase *and*
   scene load; if not, phase alone is correct and simpler.
5. **Trail on the aim ghost?** The ghost doesn't move, so a trail would be invisible — unless you
   want a style preview during aiming.
6. **Post Processing Stack v2 — in or out?** §6.11. Deferring is the recommendation, but it's a
   look decision worth making deliberately rather than by omission.

---

## Related

- [`docs/PLAN.md`](PLAN.md) — overall project plan and architecture.
- [`docs/history/rigidbody_physics_analysis.md`](history/rigidbody_physics_analysis.md) — why the
  custom solver exists instead of Unity rigidbodies.
- `~/.claude/CLAUDE.md` and [`CLAUDE.md`](../CLAUDE.md) — working rules.
