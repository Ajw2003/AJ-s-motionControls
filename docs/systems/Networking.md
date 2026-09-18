# Networking

**Source:** `Assets/Scripts/Net/RelayClient.cs`, `Messages.cs`, `LocalServices.cs`;
`relay/server.js`, `relay/funnel.js`, `relay/dns.js`

## What it owns

Getting input from phones to the PC and state from the PC to phones. Nothing else — it does not
interpret a throw, score it, or decide whose turn it is.

## Shape

```
phone browser ──wss──┐
phone browser ──wss──┤
phone browser ──wss──┼──▶  relay (Node, on the host PC)  ◀──wss──  Unity (RelayClient)
phone browser ──wss──┘         relay/server.js
                               serves controller/ over HTTPS
                               answers DNS on :53 (hotspot mode)
```

One socket, one hop. The relay is a child process of the game itself, started by `LocalServices`
on Play — **not** a dev convenience and not a service anyone deploys. The Fly.io deployment this
replaced is torn down and its files deleted; there is no hosted fallback.

**There is no Unity-to-Unity networking, and no direct phone-to-PC transport.** A WebRTC
DataChannel (`PeerLink`, `com.unity.webrtc`) once carried `AIM`/`THROW` alongside the relay. It
was removed: once the relay and the phone are already one hop apart on the same hotspot, a direct
channel bought nothing and cost a renegotiation storm. `Assets/Scripts/Net/PeerLink.cs` no longer
exists.

## The wire format

`Messages.cs` holds the serializable DTOs mirroring the JSON, and `relay/server.js` holds the
matching literals. The two must agree by string.

**Phone → PC:** `AIM` (~20Hz, cosmetic), `THROW` (authoritative), `PICK_ROCK`, `PICK_COLOUR`,
`SET_NAME`, `ADVANCE`
**PC → phone:** `TURN`, `SPECTATE`, `DEVICE_FUNNEL`, `ERROR`
**Room lifecycle:** `CREATE_ROOM`, `ROOM_CREATED`, `PLAYER_JOINED`, `PLAYER_LEFT`

Inbound messages are deliberately **flat**, because Unity's built-in `JsonUtility` cannot
deserialize nested collections. Outbound `SPECTATE` carries a per-player score map and is built
by hand for the same reason.

**To add a message,** extend all four points: the type constant, the DTO, the `RelayClient` event,
and the `Dispatch` case. Do not sniff new fields out of `RawMessageReceived` — that hook exists
for signalling this class deliberately does not model, and using it as a shortcut is how the wire
format stops being described in one place.

### Field names that look wrong and are not

`ok`, `resumed`, `reconnected` and `randomRocks` do not read as questions, against the usual
naming rule. They are wire-format names matched by string across `Messages.cs` and
`relay/server.js`, plus one persisted `match-settings.json` key. Renaming them is a coordinated
cross-stack change with a save-file migration, not a naming fix.

## Relay behaviour worth knowing

| Constant | Value | Why |
|---|---|---|
| `MAX_PLAYERS_PER_ROOM` | 8 | Couch game |
| `MAX_MESSAGE_BYTES` | 4096 | |
| `RATE_MAX_MESSAGES` | 60/s | `AIM` streams at 20Hz; leaves burst headroom |
| `HOST_GRACE_MS` | 60s | A host socket drops for dull reasons — a Wi-Fi blip, a restart. Closing the room instantly ejects everyone and forces a retype of a code that no longer exists |
| `CODE_ALPHABET` | 24 letters, no I or O | Read aloud off a TV, typed on a phone |
| `REPLACED_CLOSE_CODE` | 4001 | Distinct from 1000 on purpose |

That last one is not cosmetic. A displaced socket's client-side `onclose` cannot tell an ordinary
`1000` apart from the host itself vanishing, and reconnecting in response is exactly the join
ping-pong that once made a phone re-join roughly once a second, all match long. `relay/server.js`
exports the literal so `controller/js/net.js` compares against the same one.

### The device funnel

`DEVICE_FUNNEL` reports `associated ≥ pageOpen ≥ playing` — how far the phones on the hotspot have
got. It is a count of **devices, never a roster of people**: the relay can only key it by IP.
`TurnManager.Players` is authoritative for who is playing. The funnel exists so the TV can nudge
someone who is stuck at the stage they are stuck at.

## Invariants

- **`RelayClient` is created in `Bootstrap` and never scene-local.** A scene-local socket is torn
  down on every transition and every phone bounces back to the join screen. This reads as a
  networking bug and is a lifecycle one. It has bitten this project before.
- **`AIM` is cosmetic and may be dropped.** Never feed it to the simulation. `THROW` is the
  authoritative event for a turn.
- **All sends funnel through one queue.** `ClientWebSocket` is thread-affine for sends. This is a
  documented deviation from the original plan, which specified NativeWebSocket; `ClientWebSocket`
  was chosen to add zero dependencies, and the queue is the cost of that.
- **`AimPreview` drops any frame whose sequence number is not strictly newer.** Correct for
  ordering, and the reason a rejoining player's preview once stayed hidden for the rest of a
  session — the phone restarts its sequence at zero. Fixed in `f3e37cc`; the shape of the bug is
  worth remembering.

## Traps

**`LocalServices` reuses any relay already listening.** Stopping and restarting Play does *not*
pick up a relay code change if the previous relay outlived its session. Two test cycles have been
spent measuring stale code. Kill the `node server.js` process first; `relay/diagnose-hotspot.js`
step 4 is the cheapest proof of which code is actually running.

**A room outlives its host by 60 seconds,** so `RelayClient`'s retry delay is capped low
(`MaxRetryDelay = 4s`) to reclaim it inside the window rather than after it.

**A single connect attempt is bounded at 8s.** Without it the client wedges permanently: a TCP
connect against an address that silently drops packets never returns on its own.

## Verification

```bash
cd relay && npm test
```

45 tests across `test-relay.js`, `test-dns.js`, `test-funnel.js`, `test-probe.js`. With Play
running, expect 44 pass / 1 skip — the game's relay owns `:80`, so `test-probe.js` skips that
case by design.

`node relay/diagnose-hotspot.js` walks a phone's whole chain against a live game. It requires Play
mode with the hotspot up, and fails fast and says so otherwise. `npm run loadtest` for throughput.
