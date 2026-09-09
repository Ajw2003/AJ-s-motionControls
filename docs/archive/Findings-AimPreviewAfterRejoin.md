# The aim preview stays hidden after a player rejoins

Found 2026-09-06, reproduced from a playtest report: leaving, closing the tab and rejoining
gets the player back in correctly, but the ghost stone and aim arc are never drawn again.

## Root cause

`AimPreview.OnAim` drops any frame whose sequence number is not strictly newer than the last
one it accepted ([AimPreview.cs:345](../../Assets/Scripts/Game/AimPreview.cs:345)):

```csharp
if (aim.seq <= _lastSeq) return;
_lastSeq = aim.seq;
```

`ThrowController` starts `this.seq = 0` in its constructor
([throw.js:379](../../controller/js/throw.js:379)), so a page reload restarts the phone's sequence
at 1 while the host still holds the pre-disconnect value — after a few minutes of 20 Hz
streaming that is in the thousands.

Every frame from the rejoined phone therefore fails the guard and returns early. The return
happens *before* `_lastAimTime = Time.time` on line 359, so the timestamp never updates,
`HasSignal` goes false in `Update`, and both `_line.enabled` and the ghost's `SetActive` are set
false. The preview is not broken — it is being starved of accepted frames and correctly hiding
itself.

## Why it never resets

`_lastSeq` is reset in exactly one place ([AimPreview.cs:330](../../Assets/Scripts/Game/AimPreview.cs:330)):

```csharp
if (aim.playerId != _lastAimPlayer) { _lastAimPlayer = aim.playerId; _lastSeq = -1; ... }
```

That fires only when the *aiming player changes*. A rejoining player has the same id they had
before, so it never fires for them. `OnTurnStarted` does not reset it either — it only swaps the
mesh, scale and tint.

## Reproduction and self-healing

- **Solo:** rejoin, and the preview is hidden for the rest of the session. Nothing recovers it.
- **Two or more players:** it repairs itself as soon as somebody else aims, because that changes
  `_lastAimPlayer` and resets `_lastSeq`; the rejoined player's next turn works normally.

That difference is worth knowing — it explains why this survived earlier multi-player testing.

## Is this a regression?

**No — it is pre-existing**, and the comment above the guard shows the author had reconnects in
mind ("a reconnect can interleave"). Before the seamless-rejoin work the player id was the typed
name, and a player who rejoined typed the same name, so the id matched then too and the reset
would not have fired either.

What changed is exposure. Rejoining used to mean retyping a name on a form; now a reload silently
puts the player straight back in their seat, so the path this bug lives on is the normal one
rather than a rare one.

## Options

1. **Reset on join (recommended, and implemented).** `AimPreview` already subscribes to events in
   `Awake`. Subscribe to `PlayerJoinedEvent` too and, when `PlayerId` matches `_lastAimPlayer`,
   set `_lastSeq = -1`. Event-driven, matches the push-based style the rest of the project uses.

   **Correction to an earlier draft of this document:** it proposed gating the reset on
   `PlayerJoinedEvent.Reconnected`. That would not have fixed the reported case. The relay
   deletes a player from the room when their socket closes (`relay/server.js`, the `close`
   handler), so closing a tab and rejoining arrives as a fresh join with `Reconnected` **false**.
   The flag distinguishes a socket that flapped from one that closed, which is not the
   distinction that matters here — any join at all means a new `ThrowController` with its
   sequence back at zero, and that is the fact the reset depends on.
2. **Treat a large backwards jump as a restart.** Reset when `aim.seq` is far below `_lastSeq`.
   No new subscription, but it is a heuristic with a threshold to tune, and it silently guesses
   at something option 1 is actually told.
3. **Have the phone persist its sequence across reloads.** Wrong direction — it makes the phone
   carry host-side state, and `localStorage` writes at 20 Hz are not acceptable.

`PeerLink` is not implicated: AIM prefers a direct WebRTC channel and falls back to the relay,
but both arrive through the same `RelayClient.AimReceived` path and the same guard.

## Verifying a fix

1. Solo, mid-match: throw once so the sequence climbs, close the tab, reopen the join URL, rejoin.
   The ghost and arc must be drawn on the very next aim, with no other player needing to take a
   turn first.
2. Confirm the guard still does its job: it exists to stop a stale frame overwriting a newer one,
   so ordinary play must show no stutter or backwards jump in the ghost.
3. Two players: rejoin one mid-match and confirm the other player's preview is unaffected.
