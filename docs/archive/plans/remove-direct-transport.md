# Stop the relay/direct flapping, then delete the transport that caused it

## Context

The Unity console shows `[PeerLink] <id>: DIRECT — input is now peer-to-peer` interleaved with
`[TurnManager] <id> joined (1 players)` about twice a second, continuously, mid-match, for one
device. Neither line is a transport "flip" in the sense it reads as — `DIRECT` is only ever
logged when a **brand new** `RTCPeerConnection` finishes negotiating
(PeerLink.cs:269 (deleted in b0e35ba; recover with `git show b0e35ba^:Assets/Scripts/Net/PeerLink.cs`)), and `AcceptOffer` is only reached when the phone
sends a fresh `RTC_OFFER`, which `app.js` only sends on a `JOINED`
([app.js:485](../../../controller/js/app.js:485)).

So the console is not showing a flapping data channel. It is showing **the phone re-joining the
room roughly once a second, all match long**, and the peer link faithfully renegotiating from
scratch each time. The WebRTC layer is the symptom, not the cause.

Two separate problems, and the answer to the second question folds into the first:

**1. A join ping-pong with no exit.** [relay/server.js:250](../../../relay/server.js:250)
kicks whatever socket currently holds a `playerId` when that id joins again
(`existing.close(1000, 'Replaced by a new connection')`). The displaced client cannot tell that
close apart from the host vanishing, so [net.js:111](../../../controller/js/net.js:111)
reconnects and re-joins — kicking the socket that displaced it. Two sockets sharing one
`playerId` therefore evict each other forever, and `backoff` resets to 500ms on every successful
`onopen` ([net.js:78](../../../controller/js/net.js:78)), so it never
backs off. That matches the observed ~0.7s cadence exactly.

The second socket needs no second device. `RelayConnection.connect()` assigns `this.socket`
without closing or detaching the previous one, and three independent paths call it —
`onclose`'s `setTimeout`, `_scheduleRejoin()`'s `else this.connect()`, and `ensureConnection()`.
An orphan's handlers stay live and still reference `this`, so an orphan that opens sends
`JOIN_ROOM` and an orphan that closes schedules another `connect()`. One overlap (an `ERROR` or
`ROOM_CLOSED` arriving while a reconnect is already pending) is enough to enter the loop
permanently. Two tabs on one phone reach the same state via `identity.js`'s persistent
`deviceId`.

**2. The direct transport no longer buys anything.** `PeerLink` exists, by its own docstring, to
avoid "round-tripping through the hosted relay". There is no hosted relay any more — the Fly.io
deployment was torn down ([Bootstrap.cs:81](../../../Assets/Scripts/Managers/Bootstrap.cs:81)), `LocalServices`
runs the relay on the host PC, and in Hotspot mode the phone is associated with that same PC's
soft AP. Relay path and direct path are now the identical single wireless hop to the identical
machine. The one residual advantage — dodging TCP head-of-line blocking — was given up
deliberately by choosing `{ ordered: true }` on a reliable channel
(peer.js:93 (deleted in b0e35ba; recover with `git show b0e35ba^:controller/js/peer.js`)). What remains is
~510 lines across two languages, a native `com.unity.webrtc` dependency, and, in this bug, a
fresh `RTCPeerConnection` constructed and disposed once a second for a whole match.

Decisions taken: delete the direct transport entirely, and strip the already-deprecated LAN mode
in the same pass. Fix the join loop regardless — it is a real bug that survives both deletions
and would still be evicting players over the plain relay.

## Work

### 0. Land this plan in the repo

Copy this plan to `docs/plans/remove-direct-transport.md` as the first action, so it lives with
the other plans in `docs/plans/` rather than only in `~/.claude/plans`.

### 1. Write the finding down first

New `docs/Findings-DirectTransportFlapping.md`: the log-reading above, both mechanisms behind the
ping-pong, the "one hop either way" argument for removing WebRTC, and what the removal covered.
This is the record for the same reason `docs/Findings-DnsAudit.md` exists — the next person
reading `[PeerLink] DIRECT` in an old log needs to know why the class is gone.

### 2. Fix the join ping-pong (the actual bug)

**`controller/js/net.js`** — give `RelayConnection` a one-socket-at-a-time invariant:

- Add a private `_discardSocket()` that nulls `onopen`/`onmessage`/`onclose`/`onerror` on
  `this.socket` before closing it, and call it at the top of `connect()`. Detaching first is the
  point: a closed orphan whose handlers are still attached is what schedules the next
  `connect()`.
- Hold the reconnect `setTimeout` in a field and clear it before scheduling another, the same
  way `rejoinTimer` already is ([net.js:151](../../../controller/js/net.js:151)).
- In `onclose`, return early when a displaced-by-newer-connection close code arrives (below), so
  the losing socket stays down instead of retaliating.

**`relay/server.js`** — make the eviction distinguishable. `handleJoinRoom`'s
`existing.close(1000, …)` becomes an application close code (4001) meaning "you were replaced by
a newer connection for this same player". 1000 is indistinguishable from an ordinary server-side
close, which is exactly why the client retaliates. Apply the same to `resumeRoom`'s host
replacement at [server.js:200](../../../relay/server.js:200) for
consistency, and export the code as a named constant so client and server do not drift.

This pair fixes both routes into the loop: the invariant handles the single-page orphan, and the
close code handles two tabs or two devices sharing a `deviceId`.

### 3. Delete the direct transport

Remove outright:

- `Assets/Scripts/Net/PeerLink.cs` (+ `.meta`)
- `controller/js/peer.js`
- `com.unity.webrtc` from `Packages/manifest.json`; let Unity rewrite `packages-lock.json`

Edit:

- **`Assets/Scripts/Managers/Bootstrap.cs`** — drop the `usePeerConnections` field
  ([:53-56](../../../Assets/Scripts/Managers/Bootstrap.cs:53)) and its creation line ([:92-95](../../../Assets/Scripts/Managers/Bootstrap.cs:92)).
- **`Assets/Scripts/Net/RelayClient.cs`** — delete `IngestFromPeer` ([:416-432](../../../Assets/Scripts/Net/RelayClient.cs:416))
  and the peer reference in the comment at [:104](../../../Assets/Scripts/Net/RelayClient.cs:104). `Dispatch`'s
  `trustedPlayerId` parameter stays — the relay path passes `null` and it is load-bearing there.
- **`Assets/Scripts/Net/Messages.cs`** — drop `RtcOffer`/`RtcAnswer`/`RtcIce` ([:37-39](../../../Assets/Scripts/Net/Messages.cs:37)).
- **`Assets/Scripts/Net/RelayDebugHud.cs`** — drop the direct-peer row ([:163-174](../../../Assets/Scripts/Net/RelayDebugHud.cs:163)).
- **`controller/js/app.js`** — the `PeerLink`/`Transport` import, `PEER_ENABLED`
  ([:85](../../../controller/js/app.js:85)), the `peer` variable and
  its construction in `ensureConnection` ([:741-753](../../../controller/js/app.js:741)),
  `peer.handleSignal` ([:462](../../../controller/js/app.js:462)),
  both `peer.start()` calls, the three `peer?.close()` calls, and the debug line's `input`
  transport row ([:839](../../../controller/js/app.js:839)).
  `sendInput` at [:89](../../../controller/js/app.js:89) collapses to
  the `connection` call it already falls through to.

`relay/server.js` needs no change here: `RTC_*` messages ride the generic `relayToHost` /
`relayToPlayers` `targetPlayerId` routing and are never named in the server.

### 4. Strip LAN mode

LAN is already a hard stop at [LocalServices.cs:232](../../../Assets/Scripts/Net/LocalServices.cs:232), so nothing
about behaviour changes — this only removes the branches that can no longer be reached.

- Delete `Assets/Scripts/Net/NetworkMode.cs` (+ `.meta`): both the enum and `NetworkModeStore`.
- **`LocalServices.cs`** — delete the `Mode` property and `NetworkModeStore.Load()` call
  ([:192](../../../Assets/Scripts/Net/LocalServices.cs:192)), the LAN hard-stop block ([:212-240](../../../Assets/Scripts/Net/LocalServices.cs:212)),
  the `lanAddressOverride` field ([:70](../../../Assets/Scripts/Net/LocalServices.cs:70)), and collapse
  `EffectiveLanAddressOverride` ([:274](../../../Assets/Scripts/Net/LocalServices.cs:274)) to a named
  `HotspotHostAddress = "192.168.137.1"` constant — keeping its remarks, which explain *why* the
  address is forced rather than detected.
- **`CertificateProvider.cs`** — delete `ToHostName`, `LegacyLanSuffix`, the `VirtualAdapter`
  regex ([:69](../../../Assets/Scripts/Net/CertificateProvider.cs:69)), and the auto-detection body of
  `ResolveLanAddress` ([:283 onward](../../../Assets/Scripts/Net/CertificateProvider.cs:283)). With the address
  always forced, only the override branch ([:268-281](../../../Assets/Scripts/Net/CertificateProvider.cs:268)) is
  reachable; what survives becomes a small `ValidateHostAddress(address, out error, out detail)`
  that `Ensure` ([:102](../../../Assets/Scripts/Net/CertificateProvider.cs:102)) calls. This is the single
  largest chunk of dead code the strip reclaims.
- **`HotspotService.cs`** — check and remove any `NotApplicable` self-gating that keyed off
  `LocalServices.Mode`; with one mode it can no longer be reached.
- **`StartScreen.cs`** — the deprecation hint at [:113](../../../Assets/Scripts/UI/StartScreen.cs:113) and the
  class remarks at [:22-25](../../../Assets/Scripts/UI/StartScreen.cs:22) / [:37-40](../../../Assets/Scripts/UI/StartScreen.cs:37).
- **`<see cref="NetworkModeStore"/>` references** in `MatchSettingsStore.cs`, `RoomCodeStore.cs`
  and `HotspotCredentialsStore.cs` — these cite it as the precedent for the
  file-beside-the-executable convention. Re-point at `MatchSettingsStore` (which survives and
  documents the same reasoning) rather than deleting the rationale.
- **Deploy scripts** — the `network-mode.txt` check in `deploy/verify-stack.ps1` ([:202-215]),
  the report block in `deploy/setup-offline.ps1` ([:319-326]), and the file's mention in
  `deploy/MagicDomain.ps1` ([:24]) and `deploy/installer.iss` ([:126]). An existing
  `network-mode.txt` on a player's machine simply becomes inert; nothing needs migrating.

### 5. Docs

`Assets/CLAUDE.md`'s Networking section (the `PeerLink`/WebRTC sentence), `README.md:58`,
`deploy/README.md:93`, and a superseding note at the top of
`docs/plans/magic-domain-replacement.md` Phase 3 saying its "deprecate, don't delete" step is now
completed as a deletion.

## Verification

1. `cd relay && npm test` — must pass, plus a new case in `relay/test-relay.js` asserting a
   displaced player socket receives close code 4001 and that no further `JOIN_ROOM` arrives from
   it. This is the regression test for the actual bug.
2. `cd controller && npx eslint js` — catches every missed `peer` reference.
3. Unity Editor Console after the package removal: zero compile errors. There is no out-of-band
   compile check in this project (`Assets/CLAUDE.md`), so this is read from the open Editor.
4. Play `Bootstrap`, join with one phone, play a full 3-round match. Pass condition:
   `[TurnManager] <id> joined` appears **once**, and no `[PeerLink]` lines exist at all.
5. Second phone joins and plays — confirms nothing in the removal was load-bearing for routing.
6. The deliberate ping-pong test: open the controller page in two tabs on the same phone (same
   `deviceId`). Expected after the fix — the older tab is closed out and stays closed, the newer
   tab holds the seat, and the host logs one join, not an endless stream.
