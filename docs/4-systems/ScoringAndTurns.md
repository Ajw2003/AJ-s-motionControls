# Scoring and turns

**Source:** `Assets/Scripts/Managers/TurnManager.cs`, `ScoreManager.cs`, `Bootstrap.cs`,
`MatchSettings.cs`; `Assets/Scripts/EventTypes/MatchEvents/`

## What they own

`TurnManager` owns the player roster, whose turn it is, and where the match is in its lifecycle.
It is the only thing that tells a phone to show its throw screen.

`ScoreManager` owns **every point value in the game**, and nothing else.

The split is deliberate: scoring can be retuned without touching turn logic, and turn logic cannot
accidentally invent points.

## Scoring is push-based

Gameplay code publishes well-named events describing what happened. It never computes points.

```
RockController  ──▶  SkipOccurredEvent
RingGate        ──▶  RingPassedEvent          ──▶  ScoreManager  ──▶  ScoreAwardedEvent
TurnManager     ──▶  ThrowCompletedEvent
```

`ScoreManager` is the only subscriber that knows what a skip is worth.

**Skips bank before they score.** They arrive mid-flight, before anyone knows the throw completed,
so they accumulate in a bucket and commit to the player on `ThrowCompletedEvent`. That bucket is
**global, not per-player**: a throw that never completes — its player disconnected mid-flight —
would otherwise leave banked points to be silently credited to whoever throws next.

`ScoreAwardedEvent` is published *after* the dictionary is written, never before, so a subscriber
woken by it finds the total already banked and matching what every other reader will see. And it
sits below the empty-id guard, so a throw with no player attached scores nothing and announces
nothing, rather than announcing points that went nowhere.

## Match phases

| Phase | Meaning |
|---|---|
| `Lobby` | Players are joining, or a finished match is about to restart. Nobody may throw |
| `Choosing` | A pause for picking stones. Distinct from Lobby, and mid-match |
| `Playing` | Turns rotate. Exactly one player may throw at a time |
| `Results` | Final standings. Throws are refused until the next match |

`Phase` has exactly one assignment site — a setter that announces real changes and swallows
no-ops. A setter rather than three assignments each with a `Publish` beside it, because the
equality check is not tidiness either: `ReturnToLobby` is reachable from Lobby, and re-announcing
would republish a change that did not happen. The event is published *after* the assignment, so a
subscriber reading `Phase` sees the new value.

`TurnManager.IsChoosingPhase(phase)` is the single definition of "this phase looks like the lobby
to a rock picker". `RockSelection` and `ShoreRocks` both gate on it rather than on `Lobby`
directly, so the two stay in agreement by construction.

`Advance()` is split by phase rather than driven by one `CanStartMatch` check, because
`CanStartMatch` is also true on Results and Choosing — both are "not Playing" — and a single check
would enter Playing with the previous match's rock picks still in place. `Playing` is deliberately
absent from the switch.

## Bootstrap creation order is load-bearing

Not a preference. `Bootstrap.cs` creates every persistent manager, and its comments say why each
line cannot move. The two orderings that matter most:

**`AudioManager` first, before every other real manager.** `SingletonBase.Instance` auto-creates a
bare instance for whoever asks first. Anything touching `AudioManager.Instance` before that line
gets a manager with no bank, no mixer and no voice pool — and the real one then destroys itself in
`Awake` as the duplicate.

**`ScoreManager` before `TurnManager`.** Both subscribe to `ThrowCompletedEvent`, and
`EventManager` delivers in subscription order. `ScoreManager` must bank the final throw's points
before `TurnManager` sees the same event, ends the match, and reads the standings. Created the
other way round, **the winning throw is missing from the results screen.**

The rest of the chain, in order and each for a stated reason: `LocalServices` → `RelayClient` →
`HotspotService` → `RockSelection` → `PlayerColours` → `PlayerNames` → `TurnManager` →
`MatchSettings` → subscribers that must hear a throw ending *after* points are banked and the
phase has settled.

## Persistent versus scene-local

Created once in `Bootstrap` and never scene-local: `EventManager`, `RelayClient`, `LocalServices`,
`HotspotService`, `ScoreManager`, `TurnManager`, `RockSelection`, `PlayerColours`, `PlayerNames`,
`AudioManager`, `MatchSettings`, `JoinScreen`.

Dropping `RelayClient` on a scene transition disconnects every phone and looks like a networking
bug rather than a lifecycle one. This has bitten the project before.

## Invariants

- **Only `ScoreManager` knows point values.** If gameplay code computes a score, that is the bug.
- **`Phase` is assigned in exactly one place.**
- **The skip bucket is global and is cleared on every throw completion,** not carried.
- **Every scene-local subscriber unsubscribes in `OnDestroy`.**
- **`RoundsPerMatch` is clamped on set,** because `MatchSettings` drives it from a host panel.

## Traps

**A confirmed rock pick needs a broadcast to become visible.** Without one, it sits invisible
until some unrelated event happens to trigger the next `SPECTATE` — a lobby where everyone has
already joined could confirm rocks all match and nobody's phone would ever show it.
`TurnManager` publishes deliberately to avoid this.

**A reconnecting player who already has a pick keeps it.** `RockSelection` fires again on
reconnect; leaving the existing pick is intentional.

**`DEVICE_FUNNEL` counts devices, `TurnManager.Players` counts people.** The funnel is keyed by IP
and can only ever be a count. `TurnManager.Players` is authoritative for the roster; never derive
one from the other.

## Notes moved from source comments

The author's reasoning, moved verbatim out of the code (issue #84/#85). Each note cites the code it describes; the code carries a `doc-ref` pointer to its note.

### `Assets/Scripts/Managers/MatchSettings.cs` — Host-editable match settings, browsable as a generic list so a new setting is one entry rather than a scree...
<!-- ref:52b4 -->

Was `Assets/Scripts/Managers/MatchSettings.cs:7-19`.

Host-editable match settings, browsable as a generic list so a new setting is one entry
rather than a screen edit. Owns no gameplay state itself — each gameplay
<see cref="Setting"/> reads/writes a property that already lives on
<see cref="RockSelection"/> or <see cref="TurnManager"/>, per the deviation note on
<see cref="RockSelection"/> saying this class should read/write those properties rather
than duplicate them (only share internet, a hotspot option, is held here).


Created in Bootstrap after both RockSelection and TurnManager — Awake needs both
instances to already exist so the values loaded from <see cref="MatchSettingsStore"/>
have somewhere to land.

### `Assets/Scripts/Managers/MatchSettingsStore.cs` — Reads and writes the host's match settings as a plain JSON file beside the executable (repo root in the Edi...
<!-- ref:b541 -->

Was `Assets/Scripts/Managers/MatchSettingsStore.cs:7-23`.

Reads and writes the host's match settings as a plain JSON file beside the executable
(repo root in the Editor, since <see cref="Application.dataPath"/> resolves to
<c>Assets/</c> either way).


Deliberately not PlayerPrefs: on Windows PlayerPrefs writes into the registry, which is
invisible, isn't cleared by deleting the game folder, and isn't something a host can
inspect or edit by hand. This follows the identical file-beside-the-exe convention
already used for <c>relay-url.txt</c> and <c>relay-env.txt</c>.

<c>tuningSet</c> used to live in this file — the Authored/Derived comparison it picked
between is gone (docs/plans/Plan-ThrowDrivenSkipModel.md). SettingsData no longer
declares that field, and JsonUtility silently ignores unknown JSON keys on parse, so an
existing <c>match-settings.json</c> with a stale <c>tuningSet</c> entry loads fine
rather than erroring — the key is just never read again, and the next Save drops it.

### `Assets/Scripts/Managers/PlayerColours.cs` — Owns what colour every player is.
<!-- ref:0d02 -->

Was `Assets/Scripts/Managers/PlayerColours.cs:11-40`.

Owns what colour every player is. The only source of truth for "which dot is
theirs" — everything downstream (the shore pointer today, a stone or a scoreboard
row later) reads through here rather than deriving a colour of its own.


Modelled on <see cref="RockSelection"/>, which is the same shape of problem: a
per-player choice a phone makes, held in a dictionary keyed by player id, seeded
with a default the moment they join, and broadcast to every phone by TurnManager.

<b>Keyed by identity, and nothing else.</b> This replaces the expression that used
to live in ShoreRocks.CreateMarker:
<code>PointerPalette[Mathf.Abs(playerId.GetHashCode() + ordinal) % Length]</code>
where <c>ordinal</c> was how many pointers existed when this one was created. Every
phase change destroys the pointers and clears that collection, so a player's colour
depended on how many other people happened to have aimed before them that round —
it moved between rounds, and <c>hash + ordinal</c> collided freely inside eight
slots. Both complaints in notes.txt were that one expression.

<b>A colour outlives a disconnect.</b> Unlike RockSelection, which drops a picked
rock when a player leaves, a colour is held until the roster empties: a phone that
drops and rejoins within the relay's grace window is the same player to everyone
watching, and handing it a new colour would look exactly like the bug this
replaces.

<b>The host decides, not the phone.</b> A PICK_COLOUR is a request. See
<see cref="Pick"/> for the two things it is checked against — legibility against
the lake, and distance from the other players — both authored on
<see cref="PlayerColourPalette"/>.

### `Assets/Scripts/Managers/PlayerColours.cs` — A stone in this player's colour: their colour blended over the rock's own tint by <see cref="PlayerColourPa...
<!-- ref:ba92 -->

Was `Assets/Scripts/Managers/PlayerColours.cs:121-135`.

A stone in this player's colour: their colour blended over the rock's own tint by
<see cref="PlayerColourPalette.ConfirmedBlend"/>. Partway rather than flat, so the
stone still looks like stone while answering "whose is that" across a room.


The one place that calculation lives. The shore stone a player claims, the stone
they throw and the ghost they aim with all call this, so the three cannot drift
apart — the rock you picked has to look like the rock you threw, and a second copy
of the blend is how that would quietly stop being true.

Returns <paramref name="baseColour"/> untouched when there is no palette, so a
build without the asset degrades to the rock's authored tint rather than to
nothing.

### `Assets/Scripts/Managers/PlayerColours.cs` — Records a colour a phone asked for, after making it usable.
<!-- ref:77fa -->

Was `Assets/Scripts/Managers/PlayerColours.cs:142-160`.

Records a colour a phone asked for, after making it usable.


Accepted in any phase, unlike <see cref="RockSelection.Pick"/>. A rock pick
arriving mid-throw is stale and has to be refused; a colour change never is —
it is cosmetic, it cannot affect a throw in flight, and a player who wants a
different colour while watching somebody else throw should simply get one.

Two adjustments, in order, both authored on the palette asset:
<list type="number">
<item>raise value/saturation to the floors, so the dot reads against dark water;</item>
<item>rotate hue away from any connected player within MinSeparation, so no two
dots on the TV are confusable.</item>
</list>
Nudged rather than refused, because a refusal the phone cannot see just looks
broken — the settled colour goes back out in the next SPECTATE, so the picker
shows what the player actually got.

### `Assets/Scripts/Managers/PlayerNames.cs` — Owns what display name every player shows as.
<!-- ref:2c28 -->

Was `Assets/Scripts/Managers/PlayerNames.cs:10-27`.

Owns what display name every player shows as. The only source of truth for "whose
name is that" — everything downstream (the roster line, the scoreboard, the
active-player label) reads through here rather than printing a player id directly.


Mirrors <see cref="PlayerColours"/>, which is the same shape of problem: a
per-player value held in a dictionary keyed by player id, seeded with a fallback
the moment they join, updated by a message from their phone, and held across a
disconnect until the roster empties.

Exists because of docs/plans/seamless-rejoin.md Workstream C: <c>playerId</c> is now
the phone's stable device identity (see identity.js), not the typed name, so the
name a player chose has nowhere else to live — it arrives separately as SET_NAME and
this class is where it lands. Renaming mid-match therefore changes only what
<see cref="DisplayNameFor"/> returns; it can never look like a new player joining,
because the key it's stored under (the device id) never changes.

### `Assets/Scripts/Managers/TurnManager.cs` — Owns the player roster, whose turn it is, and where the match is in its lifecycle.
<!-- ref:428c -->

Was `Assets/Scripts/Managers/TurnManager.cs:33-44`.

Owns the player roster, whose turn it is, and where the match is in its lifecycle.
The only thing that tells a phone to show its throw screen.


Publishes match events; deliberately computes no score — that lives solely in
ScoreManager, so scoring can be retuned without touching turn logic.

The match runs a fixed number of rounds, each giving every player one throw, and
then ends. Before this existed turns rotated forever and the round counter was
incremented but never read, so a session had no way to finish.

### `Assets/Scripts/Managers/TurnManager.cs` — The one place Phase is ever assigned.
<!-- ref:7ae7 -->

Was `Assets/Scripts/Managers/TurnManager.cs:90-108`.

The one place Phase is ever assigned. Announces real changes and swallows
redundant ones.


A setter rather than three assignments with a Publish next to each, because the
duplicate that eventually gets missed is the bug: a phase change that moves the
field without announcing it leaves music and screens on the previous phase with
nothing in the log to say why.

The equality check is not tidiness either. ReturnToLobby is reachable from the
lobby — OnPlayerLeft calls it whenever the roster empties, and the guard there is
on Phase != Lobby, but StartMatch with no players and other paths can still land
here with nothing to change. Announcing that would restart lobby music every time
the last player disconnects.

Published after the assignment, so a subscriber reading Phase sees the new value
rather than the one it is being told is over.

### `Assets/Scripts/Managers/TurnManager.cs` — Move the match on from wherever it is: start from the lobby, resume from a re-pick, hand a finished match b...
<!-- ref:0201 -->

Was `Assets/Scripts/Managers/TurnManager.cs:185-199`.

Move the match on from wherever it is: start from the lobby, resume from a
re-pick, hand a finished match back to the lobby. The host's Space key and a
phone's ADVANCE both land here and nowhere else.


Split by phase rather than one CanStartMatch check driving StartMatch
unconditionally: that used to let a press from Results skip straight into
Playing with the previous match's rock picks still in place, because
CanStartMatch is also true on Results and Choosing (both are "not Playing").

Playing is deliberately absent from the switch. A phone that has not yet been
told the match started could otherwise send a stale ADVANCE into a live turn,
and there is nothing sensible for it to mean there.

### `Assets/Scripts/Managers/TurnManager.cs` — Pauses the match for a re-pick without touching scores or the round count — that is the entire difference b...
<!-- ref:e811 -->

Was `Assets/Scripts/Managers/TurnManager.cs:355-366`.

Pauses the match for a re-pick without touching scores or the round count —
that is the entire difference between this and ReturnToLobby, and why Choosing
is a separate phase rather than a reuse of Lobby.

<param name="advanceOnResume">
True from the round-boundary caller in NextTurn, where _current is still the
player whose throw just ended the round and resuming must move past them. False
from the manual forceChooseKey, which can fire mid-round with a player still
waiting to throw — advancing there would skip them entirely rather than pause
and return them to their own turn.
</param>
