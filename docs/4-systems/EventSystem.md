# Event system

**Source:** `Assets/Scripts/EventSystems/EventManager.cs`, `Assets/Scripts/EventTypes/`,
`Assets/Scripts/Utils/Singleton.cs`

## What it owns

Decoupled communication between systems that must not know about each other. `RockController`
announces a skip; it has no idea `ScoreManager` and `SfxDirector` are both listening, and it does
not learn if one of them goes away.

It owns **delivery**. It does not own ordering policy, and it does not own what any event means.

## How it works

Publish/subscribe keyed on the event's type. Subscribers are held in a `WeakReference`, so a
listener that is garbage or destroyed is dropped on the next publish even if it never
unsubscribed — a safety net, not a licence to skip unsubscribing.

```csharp
EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, e => Handle(e.SkipNumber));
...
void OnDestroy() => EventManager.Instance?.UnsubscribeFromAllEvents(this);
```

Events are plain classes implementing the marker interface `IEvent`, living under
`Scripts/EventTypes/<Category>Events/` and namespaced `EventTypes.<Category>Events`. They are
immutable: get-only properties, set once in the constructor. Copy an existing file in that folder
rather than writing one from scratch — the shape is consistent on purpose.

Three categories exist today: `MatchEvents` (roster, turns, phase, score, standings),
`SkipEvents` (launched, skipped, sunk, throw completed) and `AudioEvents`.

### It was ported, and the port is the interesting part

`EventManager` and `SingletonBase` came from another project (FactoryTM). Four changes were
required before the port worked here, all of them forced by this project's four-scene structure
(`EventManager.cs:13-22`):

1. `persistBetweenScenes` is true, so the manager survives a scene load
2. Destroyed-subscriber detection accounts for Unity's fake-null semantics
3. `OnDestroy` calls `base.OnDestroy()`, so the singleton static is actually cleared
4. `UnsubscribeFromAllEvents` dropped reflection and now removes *every* matching subscription
   rather than only the first

Change 2 is the one worth understanding. `WeakReference.Target` is typed `object`, so a plain
`== null` is reference equality and **bypasses Unity's overloaded `==`**, which is what reports a
destroyed `UnityEngine.Object` as null. Without the explicit `UnityEngine.Object` branch,
destroyed MonoBehaviours keep getting invoked and throw `MissingReferenceException` on every
publish — which, with multiple scenes, means on every transition.

### `Assets/Scripts/EventSystems/EventManager.cs`

#### Central publish/subscribe hub.
<!-- ref:35fd -->

Was `Assets/Scripts/EventSystems/EventManager.cs:8-23`.

Central publish/subscribe hub. Subscribers are held weakly, so a destroyed
listener is dropped on the next publish even if it forgot to unsubscribe.

Ported from FactoryTM with four changes, all required by this project's
multi-scene structure (Bootstrap -> Lobby -> Game -> Results):
<list type="number">
<item>persistBetweenScenes is now true, so the manager survives scene loads.</item>
<item>Destroyed-subscriber detection accounts for Unity's fake-null semantics.</item>
<item>OnDestroy calls base.OnDestroy so the singleton static is cleared.</item>
<item>UnsubscribeFromAllEvents no longer uses reflection, and removes every
matching subscription rather than only the first.</item>
</list>
The unused FactoryTM-only using directives were also stripped.

### `Assets/Scripts/EventTypes/MatchEvents/MatchEndedEvent.cs`

#### Published once, when the last round is done and the match is over.
<!-- ref:978c -->

Was `Assets/Scripts/EventTypes/MatchEvents/MatchEndedEvent.cs:5-18`.

Published once, when the last round is done and the match is over.

This class existed before and was deleted for having no subscribers — PlayerStanding
still carries the note, because it had to be moved out to survive the deletion. It is
back deliberately, for the victory sting in Plan-SfxVfx.md section 5.6, and it is
back in a smaller shape than it left in.

What is gone is the standings snapshot it used to carry. That was the part nobody
read: ResultsScreen reads standings straight off TurnManager.RebuildStandings, so the
copy on the event was a second version of the same list, free to drift and answering
no question the first one could not. A sting needs to know whether somebody won, not
who came fourth. Anything that later needs the full table should call
RebuildStandings rather than growing this back.

### `Assets/Scripts/EventTypes/MatchEvents/PlayerColourChangedEvent.cs`

#### Published whenever a player's colour changes: the default assigned as they join, or a pick arriving from th...
<!-- ref:648c -->

Was `Assets/Scripts/EventTypes/MatchEvents/PlayerColourChangedEvent.cs:6-19`.

Published whenever a player's colour changes: the default assigned as they join, or
a pick arriving from their phone.

Exists so anything already drawing a player — the shore pointer today, a thrown
stone or a scoreboard row later — can re-tint in place rather than polling
<see cref="Managers.PlayerColours.For"/> every frame. Mirrors RockPickedEvent,
which does the same job for a rock pick.

<see cref="Colour"/> is the colour the host actually settled on, which is not
necessarily the one the phone asked for — see PlayerColours.Pick, which nudges a
pick that is unreadable against the lake or too close to another player's.

### `Assets/Scripts/EventTypes/MatchEvents/PlayerNameChangedEvent.cs`

#### Published whenever a player's display name changes: the fallback label assigned as they join, or a SET_NAME...
<!-- ref:9825 -->

Was `Assets/Scripts/EventTypes/MatchEvents/PlayerNameChangedEvent.cs:5-20`.

Published whenever a player's display name changes: the fallback label assigned as
they join, or a SET_NAME arriving from their phone (first join, a silent rejoin, or
a rename mid-match).

Mirrors <see cref="PlayerColourChangedEvent"/>, which does the same job for colour —
exists so anything already drawing a player's name (the roster line, the scoreboard,
the active-player label) can re-render in place rather than polling
<see cref="Managers.PlayerNames.DisplayNameFor"/> every frame.

See docs/plans/seamless-rejoin.md Workstream C: <c>PlayerId</c> is the device's
stable identity now, not the typed name, which is exactly what makes a rename safe —
it changes what this event carries without changing who the player is to
ScoreManager, TurnManager, or PlayerColours.

### `Assets/Scripts/EventTypes/MatchEvents/ScoreAwardedEvent.cs`

#### Published when a completed throw's points are committed to a player.
<!-- ref:26a4 -->

Was `Assets/Scripts/EventTypes/MatchEvents/ScoreAwardedEvent.cs:5-14`.

Published when a completed throw's points are committed to a player.

From ScoreManager, which is documented as the only place that decides what anything
is worth. Presentation reads the numbers off this rather than recomputing them, so a
score popup and the scoreboard cannot disagree after a retune.

Fires once per throw, not once per skip. ScoreManager banks points as skips arrive
and commits the total on ThrowCompletedEvent — this is the commit. A per-skip tick
should ride on SkipOccurredEvent instead.

### `Assets/Scripts/EventTypes/MatchEvents/TurnStartedEvent.cs`

#### Published when the turn passes to a player.
<!-- ref:16dd -->

Was `Assets/Scripts/EventTypes/MatchEvents/TurnStartedEvent.cs:5-14`.

Published when the turn passes to a player.

Fires on every turn handover, which with two to eight players is constantly — so
anything hung off it has to stay short. Plan-SfxVfx.md section 6.9 puts the ceiling
at about 1.2 seconds, past which the turn-change presentation becomes the dominant
experience of the game.

Also fires when the active player leaves mid-turn and the turn is handed to whoever
takes their index, which is a genuine turn start and not a duplicate.

### `Assets/Scripts/EventTypes/SkipEvents/RockLaunchedEvent.cs`

#### Raw swing speed, m/s — recovered from the launch velocity's magnitude divided by SkipConstants.SpeedScale,...
<!-- ref:ca68 -->

Was `Assets/Scripts/EventTypes/SkipEvents/RockLaunchedEvent.cs:19-27`.

Raw swing speed, m/s — recovered from the launch velocity's magnitude divided
by SkipConstants.SpeedScale, which is exact: CreateLaunch builds the velocity
from a unit horizontal heading times (swingSpeed * SpeedScale), so inverting
that recovers the original number with no error. Worth preferring over an
extra parameter because it would otherwise have to be carried through Launch
purely to be re-published — a second copy of a value that is already
recoverable, and a second copy can disagree.

### `Assets/Scripts/EventTypes/SkipEvents/RockSunkEvent.cs`

#### Published the moment a rock stops skipping and goes under.
<!-- ref:0d39 -->

Was `Assets/Scripts/EventTypes/SkipEvents/RockSunkEvent.cs:6-18`.

Published the moment a rock stops skipping and goes under.

Distinct from ThrowCompletedEvent on purpose, and the difference is not cosmetic.
ThrowCompletedEvent is about the TURN — who threw, how well, what the match should
do next — and every subscriber to it changes match state. This one is about the
STONE, at a position, at a speed, and exists so presentation can react to a splash
without subscribing to something that also ends turns.

They fire in the same frame, this one first: RockController publishes it on the
sinking step, before calling Finish. So a splash is already underway by the time the
score is banked and the turn rotates. Plan-SfxVfx.md section 7.1 has the pile-up
this creates and argues the presentation layer should schedule rather than react.

## Invariants

- **Every scene-local subscriber unsubscribes in `OnDestroy`.** Mandatory, not advisory: each
  scene transition destroys every subscriber in the outgoing scene.
- **Publish and subscribe through `EventManager.Instance?.`** — always the null-conditional. This
  is a documented deviation from the usual "don't defend against impossible nulls" rule:
  `SingletonBase.Instance`'s getter already uses Unity's overloaded `==` internally, so the
  fake-null window is narrow, but the `_isQuitting` teardown path can still hand back a destroyed
  instance.
- **Events are immutable.** A subscriber must not be able to change what a later subscriber sees.
- **Delivery is in subscription order,** and some code depends on that. See below.

### `Assets/Scripts/EventSystems/EventManager.cs`

#### Forward, in subscription order.
<!-- ref:6323 -->

Was `Assets/Scripts/EventSystems/EventManager.cs:109-120`.

Forward, in subscription order. Bootstrap depends on this and says so:
ScoreManager is created before TurnManager precisely so a throw's points
are banked before TurnManager handles the same event, ends the match and
reads the standings. This loop used to run backwards, which inverted that
silently — the match ended and broadcast scores to the phones before the
final throw was counted, so every phone showed a total one throw short of
the host's while agreeing with each other.

Count is snapshotted so a handler that subscribes mid-dispatch does not
receive the event it is currently handling. Removals during a publish are
deferred (see RemoveMatching), so the list only grows here and every index
below the snapshot stays valid.

### `Assets/Scripts/Utils/SceneNames.cs`

#### Scene names, kept in one place so transitions can't be broken by a typo.
<!-- ref:43d0 -->

Was `Assets/Scripts/Utils/SceneNames.cs:3-14`.

Scene names, kept in one place so transitions can't be broken by a typo.

There is deliberately no MainMenu. The game boots straight to Bootstrap, which loads
Lobby and puts a QR on the TV — a menu somebody has to walk over and click is the exact
friction milestone 7 exists to remove. The scene and this constant were deleted together
(docs/plans/presentation-and-ui.md, D14); the constant was never referenced by anything.

Bootstrap must stay index 0 in the build settings, because it creates every persistent
manager. MainMenu had drifted in AHEAD of it, so a built player would have loaded an
orphaned scene first — the invariant was already broken and this is what restored it.
Assets/Editor/ProjectSetup.cs holds the authoritative order.

## Traps

**Subscription order is load-bearing, and it is set by `Bootstrap` creation order.** Events are
delivered in the order subscribers registered. `ScoreManager` and `TurnManager` both handle
`ThrowCompletedEvent`; `ScoreManager` must be created first so the final throw's points are
banked before `TurnManager` reads the standings to end the match. Created the other way round,
the winning throw is missing from the results screen. `Bootstrap.cs:69-73` says this in place —
do not reorder that file without reading the comments, which exist for exactly this reason.

**Publish failures are swallowed by default.** Set `EventManager.DebugLoggingEnabled` (exposed on
`Bootstrap`) while wiring new events, or a handler that throws will look like an event that never
fired.

**The weak reference is a safety net, not the design.** Relying on it means subscriptions live
until the next publish happens to prune them, and `GetSubscriptionCount<T>()` includes records not
yet pruned — which is why that method is documented as diagnostics-only.

### `Assets/Scripts/EventSystems/EventManager.cs`

#### True when a subscriber can no longer receive events.
<!-- ref:643d -->

Was `Assets/Scripts/EventSystems/EventManager.cs:153-163`.

True when a subscriber can no longer receive events.

The UnityEngine.Object branch is load-bearing. WeakReference.Target is typed
object, so a plain <c>== null</c> is reference equality and bypasses Unity's
overloaded <c>==</c> that reports destroyed objects as null. Without it,
destroyed MonoBehaviours keep getting invoked and throw
MissingReferenceException into the catch above on every single publish —
which with multiple scenes happens on every transition.

### `Assets/Scripts/EventTypes/MatchEvents/MatchPhaseChangedEvent.cs`

#### Published when the match moves between lobby, play and results.
<!-- ref:0e64 -->

Was `Assets/Scripts/EventTypes/MatchEvents/MatchPhaseChangedEvent.cs:6-18`.

Published when the match moves between lobby, play and results.

This is what music should key off, and not scene loads. Bootstrap's
autoLoadFirstScene defaults to false — "jumping straight into an empty scene just
looks like the game closed" — so the whole game currently runs inside the Bootstrap
scene and the Lobby, Game and Results scenes exist as files that are never entered.
A music director hung off SceneManager would therefore never hear anything, while
the phase genuinely changes throughout a session.

Only published on a real change. TurnManager routes every phase assignment through
one setter that compares first, so a redundant assignment is silent rather than
restarting a track that is already playing.

## Verification

`RockSkipping/Verify/Run Event System Verification` proves publish/subscribe survives a scene load
and that destroyed subscribers are dropped. It writes `verification-result.txt` at the repo root.
It enters Play mode, so the headless invocation deliberately has no `-quit` — see the root
[`README.md`](../../README.md).
