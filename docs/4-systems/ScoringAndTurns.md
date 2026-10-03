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
