# Findings — the `[PeerLink] DIRECT` flapping

**Date:** 2026-09-06
**Question asked:** the Unity console shows `[PeerLink] <id>: DIRECT — input is now
peer-to-peer` interleaved with `[TurnManager] <id> joined (1 players)` about twice a second,
continuously, mid-match, for one device — why is the direct transport flapping?

**Answer, in one line:** it is not a flapping data channel. It is one phone re-joining the room
roughly once a second, all match long, and the peer link faithfully renegotiating from scratch
every time it sees a fresh join.

---

## 1. Why this reads as a transport flap but isn't

`DIRECT` is only ever logged when a **brand new** `RTCPeerConnection` finishes negotiating
(`PeerLink.cs:269`), and `AcceptOffer` is only reached when the phone sends a fresh `RTC_OFFER`,
which `app.js` only sends on a `JOINED` (`app.js:485`). A `DIRECT` line therefore does not mean
"the data channel dropped and came back" — it means "a phone just joined this room, from
scratch, as far as the host can tell." Interleaved `[TurnManager] <id> joined` lines are the
same event seen from the room side. The WebRTC layer is the symptom being logged loudly; the
cause is one level up, in the join/reconnect logic.

## 2. Mechanism one — a join ping-pong with no exit

`relay/server.js`'s `handleJoinRoom` kicks whatever socket currently holds a `playerId` when
that id joins again:

```js
if (isReconnect && existing !== ws) {
  if (existing.meta) existing.meta.roomCode = null;
  existing.close(1000, 'Replaced by a new connection');
}
```

Close code 1000 is an ordinary, unremarkable close. The displaced client's `onclose` handler in
`controller/js/net.js` cannot tell that apart from the host itself vanishing, so it does exactly
what it is designed to do for that case: it opens the rejoin window and reconnects, which
re-sends `JOIN_ROOM` — which kicks the socket that just displaced it. Two sockets sharing one
`playerId` evict each other forever. `backoff` resets to `BACKOFF_START_MS` on every successful
`onopen`, so the loop never backs off; the observed sub-second cadence is exactly this reset
happening every round trip.

## 3. Mechanism two — orphaned sockets with live handlers

Independently of the relay's close code, `RelayConnection.connect()` assigned `this.socket`
without detaching or closing whatever the previous `this.socket` was. Three separate call sites
can invoke `connect()`: `onclose`'s `setTimeout`, `_scheduleRejoin()`'s `else this.connect()`,
and `ensureConnection()`. Every one of them could fire while an earlier socket's handlers were
still live and still closed over the same `this` — so an orphaned socket that happened to open
would send its own `JOIN_ROOM`, and an orphaned socket that happened to close would schedule
*another* `connect()`. One overlap (an `ERROR` or `ROOM_CLOSED` arriving while a reconnect is
already pending) was enough to enter this state permanently, and it self-sustains without a
second physical device: two browser tabs sharing one `deviceId` (`identity.js`'s persisted id)
reach it just as reliably as a flaky Wi-Fi radio would.

Both mechanisms compound: mechanism one supplies a legitimate-looking reason to reconnect, and
mechanism two multiplies each reconnect attempt into extra live sockets that keep the cycle
going even after the first socket pair would otherwise have settled.

## 4. Why the direct transport was deleted rather than just fixed

`PeerLink` exists, by its own docstring, to avoid "round-tripping through the hosted relay."
There is no hosted relay any more — the Fly.io deployment was torn down (`Bootstrap.cs`),
`LocalServices` runs the relay on the host PC itself, and in Hotspot mode the phone is
associated with that same PC's own soft AP. The relay path and the direct WebRTC path are now
the identical single wireless hop to the identical machine; there is no second hop left to
avoid. The one theoretical residual advantage — dodging TCP head-of-line blocking under packet
loss — was already given up deliberately by choosing `{ ordered: true }` on a reliable
`RTCDataChannel` (`peer.js`), so the relay and the direct channel behave identically on that
axis too. What remained was ~510 lines across two languages, a native `com.unity.webrtc`
dependency, and, in this bug, a fresh `RTCPeerConnection` constructed and disposed roughly once
a second for the length of a match — all for a hop that was already free.

## 5. What the removal covered

- **The actual bug** (both mechanisms above) was fixed independently of the deletion: the relay
  now closes a displaced player/host socket with a distinct application close code (4001) rather
  than the generic 1000, and `net.js` gained a one-socket-at-a-time invariant (detach a socket's
  handlers before replacing it, a single reconnect timer, and an early return in `onclose` for a
  displacement close). This is the fix that matters even on a pure-relay transport — it would
  still be evicting players before the direct transport ever entered the picture.
- **The direct transport itself** — `Assets/Scripts/Net/PeerLink.cs`, `controller/js/peer.js`,
  the `com.unity.webrtc` package, and every call site that constructed, signalled, or tore down
  either side of it — was deleted outright rather than patched, per the argument in section 4.
- **LAN mode**, already unreachable dead code (`LocalServices.cs`'s hard stop), was stripped in
  the same pass since it shared no logic with the fix and was already flagged for removal.

Anyone reading `[PeerLink] DIRECT` in an old log after this point should read this file rather
than re-diagnosing a "flapping data channel" — the class that logged it is gone, and the actual
bug it was surfacing is fixed independently of it.
