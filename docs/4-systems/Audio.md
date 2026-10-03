# Audio

**Source:** `Assets/Scripts/Audio/`, `Managers/AudioManager.cs`, `EventTypes/AudioEvents/`

## What it owns

Procedurally generated and bank-driven sound effects: which SoundId plays, how it is synthesised and varied, and the AudioManager that plays it. The notes below are the author's reasoning moved verbatim from the source comments.

## How it works

See the notes below; each cites the code it describes.

## Invariants

See the notes below.

## Traps

See the notes below.

## Notes moved from source comments

The author's reasoning, moved verbatim out of the code (issue #84/#85). Each note cites the code it describes; the code carries a `doc-ref` pointer to its note.

### `Assets/Scripts/Audio/AudioBank.cs` — What each SoundId actually sounds like.
<!-- ref:79d1 -->

Was `Assets/Scripts/Audio/AudioBank.cs:7-20`.

What each SoundId actually sounds like. One asset, edited in the inspector, no
recompile — the same contract SkipTuning and TrailTuning hold to.


Lives at Resources/AudioBank and is loaded the way SkipTuning is. Every number that
shapes a sound is here rather than in the code that triggers it, so retuning how a
skip run climbs is an inspector edit and not a build.

An entry with no variants is not an error. AudioManager falls back to a synthesised
placeholder for that SoundId, so the ladder and the impact-speed mapping can be tuned
against something audible before a single .wav exists — which is the state this
project is actually in. See ProceduralClips.

### `Assets/Scripts/Audio/ProceduralClips.cs` — Synthesised stand-in clips, so every sound in the game is audible before any audio asset exists.
<!-- ref:c731 -->

Was `Assets/Scripts/Audio/ProceduralClips.cs:8-30`.

Synthesised stand-in clips, so every sound in the game is audible before any audio
asset exists.


This exists because of a real gap rather than as a toy: there are currently zero
.wav/.mp3/.ogg files anywhere in Assets, and Plan-SfxVfx.md's open question 1 — where
the audio is coming from — is still open. The plan's M3 says "placeholder clips are
fine, the ladder and the impact-speed mapping are what's being tuned, not the
samples," which is true and needs placeholders to exist.

Generated rather than committed as files, for the reason PlaceholderArt.CreateWater
gives about assets reached by name: a thing built in code cannot go missing from a
build. It also cannot be mistaken for finished art, which matters more here — nobody
is going to ship a sine wave by accident.

These are placeholders and they sound like it. Every one is replaced the moment a
real clip is dropped into the matching AudioBank entry's Variants; nothing else has
to change, and this class stops being reached for that sound.

Cached per SoundId. AudioClip.Create allocates and uploads sample data, and a long
run asks for the same skip sound thirty times in three seconds.

### `Assets/Scripts/Audio/SfxDirector.cs` — Turns gameplay events into sounds.
<!-- ref:3e0f -->

Was `Assets/Scripts/Audio/SfxDirector.cs:10-24`.

Turns gameplay events into sounds. The only thing that decides WHICH sound a moment
deserves; AudioManager decides what that sound is made of.


A standalone subscriber that adds no lines to gameplay code, exactly as WaterRipples
is. That is what inherits the protection SkipPhysics states twice: the aim preview
runs the solver hundreds of times a frame for a throw that never happened, and only
RockController publishes the events this listens to, so aiming cannot machine-gun
skip sounds. The safety is inherited, not re-implemented.

Created after ScoreManager and TurnManager — see Bootstrap. EventManager delivers in
subscription order, so by the time this hears anything the score is banked and the
phase has settled, and it can read final state to decide what to play.

### `Assets/Scripts/Audio/SfxDirector.cs` — The skip sound, its pitch ladder, its score tick, and the milestone accent.
<!-- ref:2a6d -->

Was `Assets/Scripts/Audio/SfxDirector.cs:87-99`.

The skip sound, its pitch ladder, its score tick, and the milestone accent.


SkipIndex goes to Step and the ladder itself lives on the bank entry, so how many
semitones a skip is worth is tuned in the inspector rather than here.

The score tick rides on this event rather than on a scoring one, and that is not
a shortcut. ScoreManager banks points per skip and commits the total on throw
completion, so there is no per-skip scoring event to hang it off — and a tick
that arrived separately would drift against the skip it belongs to. It shares the
skip's step so the two climb together and read as one sound.

### `Assets/Scripts/Audio/SfxDirector.cs` — The score tally, held back so it is not buried by the splash.
<!-- ref:a35a -->

Was `Assets/Scripts/Audio/SfxDirector.cs:144-162`.

The score tally, held back so it is not buried by the splash.


This is the pile-up Plan-SfxVfx.md section 7.1 describes, and it is real: when a
rock sinks, RockController publishes RockSunkEvent and then Finish publishes
ThrowCompletedEvent, which banks the score and rotates the turn — all
synchronously in one frame. Played on receipt, the splash, the tally and the turn
sting land together as mush, and the tally, which is the rewarding one, loses.

So the ordering is authored rather than inherited from event delivery: splash at
t+0, tally at t+scoreDelay, sting at t+turnDelay. The delay rides on the event and
AudioManager schedules it, so when the fuller queue arrives in M6 nothing here has
to change.

A failed throw is silent here on purpose. Zero skips still awards distance points,
but a ka-ching after the dead plunk of a stone that never skipped rewards the one
outcome the game is trying to make disappointing.

### `Assets/Scripts/EventTypes/AudioEvents/PlaySoundEvent.cs` — Seconds to wait before it sounds.
<!-- ref:6215 -->

Was `Assets/Scripts/EventTypes/AudioEvents/PlaySoundEvent.cs:39-55`.

Seconds to wait before it sounds. Zero plays immediately.


Here from the start rather than added later, and that is deliberate. When a rock
sinks, RockController publishes the splash, then Finish publishes
ThrowCompletedEvent, which banks the score and rotates the turn — all
synchronously, in one frame. So the sink, the score tally and the turn sting all
want to sound at the same instant, which is mush, and the score sound (the
rewarding one) is the one that gets buried.

Plan-SfxVfx.md section 7.1 argues the presentation layer must schedule rather
than react, and warns that retrofitting scheduling into a fire-on-event design
means touching every handler. A delay on the event is the cheap version of that:
callers stagger their own sounds now, and the fuller queue in M6 can take over
without any caller changing.

### `Assets/Scripts/EventTypes/AudioEvents/SoundId.cs` — Every sound the game can ask for, by identity rather than by asset.
<!-- ref:a857 -->

Was `Assets/Scripts/EventTypes/AudioEvents/SoundId.cs:3-15`.

Every sound the game can ask for, by identity rather than by asset.

An enum rather than an AudioClip reference, and that is the whole design. FactoryTM's
AudioClipEvent carries a clip, which means the caller has to hold one — a
[SerializeField] AudioClip on RockController or ScoreManager. Every manager in this
project is built with new GameObject() + AddComponent by Bootstrap, so a serialized
field on one has never been through an inspector and sits at its C# field initializer
forever: null. AimPreview.Awake documents the project already losing time to exactly
that, with the tuning asset.

Naming a sound instead means gameplay code holds no assets at all, and swapping what
a skip sounds like is an edit to the bank asset rather than to a gameplay class.

### `Assets/Scripts/Managers/AudioManager.cs` — Owns every sound in the game.
<!-- ref:ed06 -->

Was `Assets/Scripts/Managers/AudioManager.cs:11-34`.

Owns every sound in the game. Gameplay publishes PlaySoundEvent describing what
happened; this is the only place that decides what it sounds like.


Written fresh rather than ported from FactoryTM, and that was the cheaper route.
Plan-SfxVfx.md section 2 lists nine defects in that file ranging from "does nothing"
to "will actively sound broken here" — persistBetweenScenes assigned in Start() where
SingletonBase has already read it in Awake(), an inspector-assigned source clobbered
by GetComponent, a coroutine created but never started, PlayOneShot used for music so
it cannot loop, a per-clip volume written to a shared source so one quiet sound ducks
every later one permanently, and a declared-but-unused mixer field. Fixing nine
things in a file whose shape is wrong for this game — see section 3.1 on why
AudioClipEvent cannot be the primary API here — is more work than writing the right
one. The lessons are kept; the code is not.

Sources are built in code, so there is no prefab and nothing to wire in an inspector.
Section 1.3 offers a Resources prefab or a ScriptableObject plus code-built sources
and recommends both; with the bank holding every number and the pool built here,
there is nothing left for a prefab to carry, so this is route (b) alone. The trap
that section warns about still applies and is handled in Bootstrap: this must be
created BEFORE anything can touch AudioManager.Instance, because SingletonBase
auto-creates a bare instance for whoever asks first.

### `Assets/Scripts/Managers/AudioManager.cs` — Builds the pool of 3D sources every sound plays through.
<!-- ref:5c1f -->

Was `Assets/Scripts/Managers/AudioManager.cs:124-139`.

Builds the pool of 3D sources every sound plays through.


A pool rather than the one shared source the ported version used, for two
independent reasons and either one alone would force it.

Pitch is a property of the SOURCE, not of the shot. A pitch ladder across a skip
run sets pitch and then plays — but skips land 150-400 ms apart and the previous
one is still ringing, so retuning the source audibly bends the tail of the skip
before it. It sounds like a tape warble, and it defeats per-shot pitch jitter for
the same reason.

Position is the other. One 2D source plays a skip at 5 m and a skip at 120 m
identically, and a run that should travel away from you reads flat.

### `Assets/Scripts/Editor/AudioBankBuilder.cs` — Points AudioBank at clips in Assets/Audio by filename prefix
<!-- ref:ac1f -->

Was `Assets/Scripts/Editor/AudioBankBuilder.cs:11-29`.

Points Resources/AudioBank.asset at whatever clips are sitting in Assets/Audio,
matched by filename prefix.

Exists because the alternative is dragging twenty-two clips into an inspector by
hand and doing it again every time the generator runs — which is the kind of manual
step that silently stops being done, leaving the bank pointing at a stale set while
the files on disk have moved on.

The convention is the whole contract: a clip named skip_01.wav joins the Skip entry's
Variants, skip_02.wav follows it, and the numbering decides the round-robin order.
Anything with an unrecognised prefix is reported rather than ignored, so a typo in a
filename is visible instead of just producing a sound that never plays.

Existing tuning is preserved. Rebuild only refreshes Variants, so the pitch ladder
and volumes survive a regeneration — that is the point, since those are what M3 is
for tuning. Reset Tuning is the deliberate way back to the defaults below.

### `Assets/Scripts/Editor/AudioBankBuilder.cs` — Per-prefix defaults: Skip's pitch ladder is capped at fourteen steps because semitones compound
<!-- ref:81e4 -->

Was `Assets/Scripts/Editor/AudioBankBuilder.cs:35-46`.

Filename prefix, the sound it feeds, and how that sound behaves by default.

The defaults are the interesting part, and the two that matter most are on Skip:
one semitone per step and a cap of fourteen. The cap is not a nicety — semitones
compound, so an uncapped ladder puts skip thirty more than two octaves above skip
zero, which is a dog whistle rather than a climax.

Spatial is off for anything that has no place in the world. A fanfare emitted
from wherever the last stone sank would drift around the lake between matches.

### `tools/audio/generate-sfx.js` — Why the water plop has no click
<!-- ref:67c5 -->

Was `tools/audio/generate-sfx.js:277-288`.

A stone touching water and coming off it. A soft, rounded plop.

The earlier version layered a high-passed noise click on top to sell the contact, and
that click was the harshness: it fires twenty to thirty times in three seconds, and
broadband high frequencies repeated that fast are fatiguing however quiet each one is.
Water does not click. It displaces, which is a pitched drop, not a transient.

So the contact is carried entirely by how FAST the pitch falls — 25 ms — rather than by
anything bright. The remaining noise layer is low-passed to keep only its body; it reads
as wetness rather than as spray.

### `tools/audio/generate-sfx.js` — Why the tone is struck wood, not FM bell
<!-- ref:68fb -->

Was `tools/audio/generate-sfx.js:88-105`.

A soft struck-wood tone — marimba or kalimba rather than bell.

This replaced a 2-operator FM bell, and the swap is the single biggest reason the set
stopped sounding harsh. FM with a high modulation index produces dense inharmonic
partials high in the spectrum: that is precisely what "metallic" and "clangy" mean, and
it is wrong for a game about stones and water. Additive synthesis with a few LOW
harmonics cannot produce that texture at all.

Two details do most of the warmth:

- Higher partials decay faster than the fundamental. That is what real wood does, and it
  means the sound is only briefly bright before settling to a pure note. A bell holds its
  upper partials, which is why it rings and why it cuts.
- The attack is a few milliseconds of raised cosine, not an instant jump. An instant
  attack is a step discontinuity, and a step is broadband click — audible as a hard edge
  on the front of every note.
