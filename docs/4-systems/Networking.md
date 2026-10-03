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

`relay/dns.js` answers the game name locally. With `SHARE_INTERNET=1` it also forwards other names
(and the phones' connectivity probes, with a 1.5 s local fallback) to the host's resolver; see
[`HostingAndCertificates.md`](HostingAndCertificates.md).

In both modes it answers `mask.icloud.com` and `mask-h2.icloud.com` with NXDOMAIN, which turns
iCloud Private Relay off for this network. Otherwise Safari would resolve the game name through
Apple and reach the public parking page (#79). On `:80`, any path on an Apple probe host gets
Apple's exact Success page. A redirect there is what iOS reads as a captive portal (#78). See
[`../Findings-iPhoneWifiJoin-2026-10-02.md`](../Findings-iPhoneWifiJoin-2026-10-02.md).

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
| `RATE_MAX_AIM` | 60/s per socket | `AIM` streams at 20Hz; it is lossy, so its own budget caps a stall's backlog (#58) |
| `RATE_MAX_MESSAGES` | 300/s per socket | Flood cap for everything else. One shared 60/s bucket let an AIM backlog get the THROW behind it dropped |
| `HOST_GRACE_MS` | 60s | A host socket drops for dull reasons — a Wi-Fi blip, a restart. Closing the room instantly ejects everyone and forces a retype of a code that no longer exists |
| `CODE_ALPHABET` | 24 letters, no I or O | Read aloud off a TV, typed on a phone |
| `REPLACED_CLOSE_CODE` | 4001 | Distinct from 1000 on purpose |

**The phone sends AIM latest-wins and THROW always.** `sendAim` (`controller/js/app.js`) uses
`RelayConnection.sendIfClear`, which skips a frame while bytes are still queued, so a stall
cannot build a backlog ahead of the THROW (#57). THROW goes through plain `send`.

**One stone at a time, end to end.**

- **The phone.** After a THROW it shows STONE IN THE AIR, stops AIM, and ignores taps until the
  next TURN, or for 30 s if no TURN comes (#55).
- **The TV.** `RockLauncher.OnThrow` refuses, and logs, any THROW until the previous stone's
  `IsSinking` clears (#56).
- **Where the gap is.** TURN arrives on every player join, so a join mid-flight unlocks the
  phone early, and the TV's guard catches that throw. The fallback timer exists because nothing
  acknowledges a THROW yet (A4 in `docs/Findings-NetworkAndThrowStability.md`).

**Testing without Unity.** `relay/fake-host.js` stands in for the TV. It gives every joining
phone a TURN, logs each THROW, re-sends TURN after `SETTLE_MS`, and prints each phone's AIM rate
once a second.

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

55 tests across `test-relay.js`, `test-dns.js`, `test-funnel.js`, `test-probe.js`. With Play
running, expect 54 pass / 1 skip — the game's relay owns `:80`, so `test-probe.js` skips that
case by design.

`node relay/diagnose-hotspot.js` walks a phone's whole chain against a live game. It requires Play
mode with the hotspot up, and fails fast and says so otherwise. `npm run loadtest` for throughput.

## Notes moved from source comments

The author's reasoning, moved verbatim out of the code (issue #84/#85). Each note cites the code it describes; the code carries a `doc-ref` pointer to its note.

### `Assets/Scripts/Net/Messages.cs` — How far the phones on the hotspot have got through joining — see relay/funnel.js and docs/plans/join-failur...
<!-- ref:6112 -->

Was `Assets/Scripts/Net/Messages.cs:68-81`.

How far the phones on the hotspot have got through joining — see relay/funnel.js and
docs/plans/join-failure-reduction.md. Sent unprompted whenever the numbers change, and
once on ROOM_CREATED so a host that just restarted is not blind until they next do.


A count of devices, never a roster of people: the relay can only key this by IP, so
two phones behind one address count once and a phone that changes address counts
twice. Fine for deciding what the TV says; never use it as a player count — that is
TurnManager.Players, which is authoritative.

The counts are cumulative, so <c>associated &gt;= pageOpen &gt;= playing</c> always
holds and the interesting numbers are the differences between them.

### `Assets/Scripts/Net/Messages.cs` — The phone's rotation relative to where it was when the player tapped to aim.
<!-- ref:504c -->

Was `Assets/Scripts/Net/Messages.cs:112-124`.

The phone's rotation relative to where it was when the player tapped to aim.


Four floats rather than three angles, and that is the point. The phone used to
decompose its attitude into aim / attack / roll and Unity re-composed it with
two hand-found sign flips; a quaternion has no decomposition to get wrong and
no degenerate pose. Identity means "however the player was holding it at the
tap", drawn as a flat stone facing away from the camera.

Still in the phone's own right-handed frame here. Game.PhoneAttitude.ToUnity is
the only thing permitted to convert it — see that file.

### `Assets/Scripts/Net/Messages.cs` — How fast the phone is being swung right now, raw m/s — the same quantity ThrowMessage.swingSpeed carries, j...
<!-- ref:ccd6 -->

Was `Assets/Scripts/Net/Messages.cs:135-149`.

How fast the phone is being swung right now, raw m/s — the same quantity
ThrowMessage.swingSpeed carries, just sampled mid-swing instead of at release.
See ThrowController.windup.


Not normalised. An earlier version sent a 0..1 `power` here, derived the same
way ThrowMessage.power was — but that meant two lossy conversions of the same
swing had to agree, on both the phone and in Unity, or the arc and the stone
would disagree by construction. Sending the raw measurement once and converting
it once, in Unity (SkipConstants.SpeedScale), removes the second chance to drift.

Only ever an estimate, because the swing has not finished yet. AimPreview
floors it at a nominal so a player holding still to aim still sees an arc.

### `Assets/Scripts/Net/Messages.cs` — A phone choosing the colour it is drawn in.
<!-- ref:5aea -->

Was `Assets/Scripts/Net/Messages.cs:165-181`.

A phone choosing the colour it is drawn in.


Unlike PickRockMessage this DOES carry its payload, because the TV cannot know what
a player wants: a rock pick means "confirm the highlight the TV is already showing",
but a colour comes off a picker the phone owns and the TV never sees.

Sent as "#rrggbb" rather than three floats. That is what &lt;input type="color"&gt;
produces natively and what CSS consumes on the way back, so the phone never converts
and there is no float formatting to get wrong across a culture. Parsed with
ColorUtility.TryParseHtmlString; an unparseable string is dropped, exactly as
PickRockMessage's unknown rock id is.

A request, not an instruction — Managers.PlayerColours decides what the player
actually gets, and the settled colour comes back in the next SPECTATE.

### `Assets/Scripts/Net/Messages.cs` — A phone announcing (or changing) the display name for its seat.
<!-- ref:1757 -->

Was `Assets/Scripts/Net/Messages.cs:189-201`.

A phone announcing (or changing) the display name for its seat. See
docs/plans/seamless-rejoin.md Workstream C: playerId is now the device's stable
identity, not the typed name, so the name has to travel as its own message rather
than being read out of playerId — Managers.PlayerNames is the one place it lands.


Sent every time this device's controller reaches JOINED, first join or rejoin
alike, and again whenever the player edits their name mid-match — a rename, not a
new player, since playerId does not change. relayToHost stamps playerId
server-side exactly as it does for PickColourMessage, so a phone cannot claim to
be renaming someone else.

### `Assets/Scripts/Net/Messages.cs` — A phone asking the match to move on — start from the lobby, resume from a re-pick, or begin the next match...
<!-- ref:99ee -->

Was `Assets/Scripts/Net/Messages.cs:209-221`.

A phone asking the match to move on — start from the lobby, resume from a re-pick,
or begin the next match from the results screen.


Carries no target phase on purpose, exactly like PickRockMessage carries no rock id:
the TV owns the phase, so this only means "whatever the button on my screen said".
TurnManager runs the same switch for this as it does for the host's Space key, so the
keyboard and the phones cannot advance the match to different places.

Any connected phone may send it. A couch game has no host player, and adding one
would need a rule for what happens when that particular phone drops.

### `Assets/Scripts/Net/Messages.cs` — Broadcast match state to every phone.
<!-- ref:7c11 -->

Was `Assets/Scripts/Net/Messages.cs:261-303`.

Broadcast match state to every phone. Hand-written because the score map
is a dictionary, which JsonUtility cannot serialise.


Sent on state changes only — a player joining or leaving, a turn starting, a
throw completing, the match ending. Never on a timer. This used to also go out
four times a second for the whole time a rock was in the air, to drive a live
readout on phones nobody watches during a couch game; that was the single most
expensive thing on the wire, and it is gone.

<param name="phase">LOBBY, PLAYING or RESULTS. Without it an idle phone cannot
tell "waiting to start" from "final scores" and shows the wrong caption in two
of the three phases.</param>
<param name="lastPlayer">Who threw the <paramref name="lastSkips"/> result. Named
separately from activePlayer on purpose: the turn has already advanced by the time
this goes out, so without it a phone would credit the throw to the wrong player.</param>
<param name="playerRocks">Every connected player's rock, so an idle phone in the
lobby can show what everyone has claimed — not just <paramref name="rockName"/>,
which only ever names the active player's stone. Null is fine; the "rocks" object
is simply empty, which is what a build without RockSelection running degrades to.</param>
<param name="playerColours">Every player's settled colour, as "#rrggbb", so a phone
can paint its own accent to match its dot on the TV and tell the other players
apart in the scores. Null is fine; the "colours" object is simply empty, which is
what a build without PlayerColours running degrades to — the same tolerance
<paramref name="playerRocks"/> has.</param>
<param name="palette">The quick-pick swatches the phone's colour picker offers.
Sent rather than duplicated in the controller so the palette is authored in one
place — the PlayerColourPalette asset — and a phone needs no rebuild when it
changes. Null leaves the picker on its custom-colour input alone.</param>
<param name="choosing">True for a mid-match re-pick pause. Cosmetic only, and the
one thing the phone knows about MatchPhase.Choosing: the wire phase stays "LOBBY"
(see TurnManager.BroadcastSpectate) because a choose screen behaves identically
either way, but the advance button on it must not offer to "start" a match that
is already half played.</param>
<param name="playerNames">Every connected player's display name, id → name — see
docs/plans/seamless-rejoin.md Workstream C. Deliberately a parallel field rather
than a substitution into <paramref name="activePlayer"/>/<paramref name="lastPlayer"/>
or the <paramref name="scores"/> keys: those stay opaque device ids on the wire so
app.js's identity comparisons (`=== myPlayerId`) keep working, and so two players
who happen to type the same display name never collide as one dictionary key. Null
is fine; the "names" object is simply empty, the same tolerance
<paramref name="playerRocks"/> and <paramref name="playerColours"/> have.</param>

### `Assets/Scripts/Net/RelayClient.cs` — Host-side connection to the relay.
<!-- ref:5e27 -->

Was `Assets/Scripts/Net/RelayClient.cs:15-35`.

Host-side connection to the relay. Creates the room, receives phone input,
and broadcasts match state back out.


MUST live in the Bootstrap scene as a persistent manager. If it is created
per-scene instead, the socket is torn down on every transition and every
phone bounces back to the join screen — a lifecycle bug that presents as a
networking one.

DEVIATION FROM PLAN: uses System.Net.WebSockets.ClientWebSocket rather than
the NativeWebSocket package the plan named. ClientWebSocket ships with .NET,
so there is no external dependency to vendor, and it works fine for a native
desktop host. The tradeoff is that it does NOT work in WebGL builds — which
is acceptable because the plan already rejected WebGL as a target. Swap to
NativeWebSocket if a browser build ever becomes a requirement.

ClientWebSocket is thread-affine for sends, so all sends funnel through a
semaphore, and all received messages are marshalled onto the main thread via
a queue drained in Update — Unity APIs cannot be touched from the socket task.

### `Assets/Scripts/Net/RelayClient.cs` — How long a single connect attempt may take before it is abandoned.
<!-- ref:a5fc -->

Was `Assets/Scripts/Net/RelayClient.cs:164-174`.

How long a single connect attempt may take before it is abandoned.


Without this the client can wedge permanently. A TCP connect against a
half-torn-down network — Wi-Fi toggled off and back on, a laptop waking, a
relay being restarted — can sit unresolved far longer than any useful
retry interval, and while it does, `_connecting` stays true and Update
refuses to start another attempt. The symptom is a host that never comes
back even though the network is fine again.

### `Assets/Scripts/Net/RelayDebugHud.cs` — Spread as a percentage of the mean — the repeatability number.
<!-- ref:f61d -->

Was `Assets/Scripts/Net/RelayDebugHud.cs:192-200`.

Spread as a percentage of the mean — the repeatability number.

Takes the list and a selector rather than an IEnumerable&lt;float&gt; because this
runs inside OnGUI, twice a frame, for as long as the HUD is up. The LINQ form
allocated an iterator, a closure and a backing List per call to produce three
numbers that one pass already has in hand. The selectors are cached statics for
the same reason — a method group or lambda written at the call site would
allocate a fresh delegate every frame.
