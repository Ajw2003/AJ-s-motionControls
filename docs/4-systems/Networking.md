# Networking

**Source:** `Assets/Scripts/Net/RelayClient.cs`, `Messages.cs`, `LocalServices.cs`;
`relay/server.js`, `relay/funnel.js`, `relay/dns.js`

## What it owns

Getting input from phones to the PC and state from the PC to phones. Nothing else — it does not
interpret a throw, score it, or decide whose turn it is.

## How it works

### Shape

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

### The wire format

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

#### Field names that look wrong and are not

`ok`, `resumed`, `reconnected` and `randomRocks` do not read as questions, against the usual
naming rule. They are wire-format names matched by string across `Messages.cs` and
`relay/server.js`, plus one persisted `match-settings.json` key. Renaming them is a coordinated
cross-stack change with a save-file migration, not a naming fix.

### Relay behaviour worth knowing

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

#### The device funnel

`DEVICE_FUNNEL` reports `associated ≥ pageOpen ≥ playing` — how far the phones on the hotspot have
got. It is a count of **devices, never a roster of people**: the relay can only key it by IP.
`TurnManager.Players` is authoritative for who is playing. The funnel exists so the TV can nudge
someone who is stuck at the stage they are stuck at.

### `Assets/Scripts/Net/Messages.cs`

#### The phone's rotation relative to where it was when the player tapped to aim.
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

#### How fast the phone is being swung right now, raw m/s — the same quantity ThrowMessage.swingSpeed carries, j...
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

#### A phone choosing the colour it is drawn in.
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

#### A phone announcing (or changing) the display name for its seat.
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

#### A phone asking the match to move on — start from the lobby, resume from a re-pick, or begin the next match...
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

#### Broadcast match state to every phone.
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

### `Assets/Scripts/Net/RelayDebugHud.cs`

#### Spread as a percentage of the mean — the repeatability number.
<!-- ref:f61d -->

Was `Assets/Scripts/Net/RelayDebugHud.cs:192-200`.

Spread as a percentage of the mean — the repeatability number.

Takes the list and a selector rather than an IEnumerable&lt;float&gt; because this
runs inside OnGUI, twice a frame, for as long as the HUD is up. The LINQ form
allocated an iterator, a closure and a backing List per call to produce three
numbers that one pass already has in hand. The selectors are cached statics for
the same reason — a method group or lambda written at the call site would
allocate a fresh delegate every frame.

### `relay/diagnose-hotspot.js`

#### What diagnose-hotspot answers
<!-- ref:c5cf -->

Was `relay/diagnose-hotspot.js:3-14`.

Answers "why does the phone say No internet?" against a LIVE game.

Run it with Unity in Play mode and the hotspot up. It touches the relay the same way a
phone does — the same ports, the same paths, the same Host headers — and reports each
link in the chain separately, because the symptom on the phone ("connected, no internet")
is identical whether DNS failed, :80 never bound, or :80 answered the wrong thing.

Read-only. It sends requests and prints what came back; it changes nothing.

Usage:  node diagnose-hotspot.js

### `relay/funnel.js`

#### Funnel options
<!-- ref:6311 -->

Was `relay/funnel.js:67-77`.

@param {object}    options
@param {function}  options.isTracked  (ip) => boolean. Confines tracking to the hotspot
                                      subnet: on a developer's machine every request
                                      comes from 127.0.0.1 and none of this means
                                      anything, so the funnel must stay silent there
                                      rather than report a phantom device.
@param {function} [options.onChange]  called with `counts()` whenever they change, which
                                      is the only thing worth waking the host for.
@param {function} [options.now]       injectable clock, for tests.

#### Why stale timeouts differ per stage
<!-- ref:8b00 -->

Was `relay/funnel.js:31-45`.

How long a silent device stays counted, per stage it reached.

One timeout cannot serve both ends of the funnel. A device that reached PAGE_OPEN or
PLAYING may legitimately go quiet for a long time — someone mid-match is not sending DNS
queries, and dropping them would make the TV nag a player who is holding a rock. A device
that only ever appeared in a DNS log is different: both platforms re-probe every 30–60s
while associated, so one that has said nothing for two minutes has almost certainly left
the network, and every second it stays counted is a second the TV tells the room to scan a
code for a phone that is not there.

That asymmetry is what makes the stale-count symptom self-correct in about two minutes
instead of ten. It does not make the count trustworthy — IP keying rules that out, which is
why the TV no longer prints it — it just stops the count outliving the phone that caused it.

#### The per-device join funnel
<!-- ref:9607 -->

Was `relay/funnel.js:3-21`.

Per-device join funnel.

Joining is three events, not one, and until now only the last of them reached Unity —
so every earlier drop-off looked identical on the TV: the player count simply did not
go up. See docs/plans/join-failure-reduction.md §2.

  ASSOCIATED  the phone is on the hotspot Wi-Fi     (a DNS query arrives from its IP)
  PAGE_OPEN   the phone loaded the controller       (index.html served to its IP)
  PLAYING     the phone joined a room               (a JOIN_ROOM websocket from its IP)

The TV depends on the distinction, so it can say "phone connected — now scan the second
code" instead of saying nothing.

Keyed by IP, which is the only identifier available at the DNS layer — there is no
cookie, no user agent worth trusting, and no session. That makes a
count of devices, never a roster of people: two phones behind one address would count
once, and a phone that changes address counts twice. Callers must treat it as a hint.

### `relay/loadtest.js`

#### What the load test models
<!-- ref:ff84 -->

Was `relay/loadtest.js:3-26`.

Capacity measurement: simulates concurrent parties and reports what they cost.

Each room is 1 host + PLAYERS_PER_ROOM phones. One phone streams AIM at 20Hz —
turn-based, so only the active player aims. That is the real rate, from `aimHz`
in throw.js, not an invented one.

The host sends SPECTATE only on state changes — a join, a turn, a completed throw,
the match ending — so it is not modelled as steady traffic here. It used to go out
at 4Hz to every player for the whole time a rock was in the air, which dominated
this measurement; a match now sends single-digit SPECTATEs per turn instead of
dozens. If you reintroduce anything periodic on the host side, model it here or
these numbers stop meaning anything.

  npm run loadtest                    25 rooms x 4 players, 15s
  node loadtest.js 250 8 20           250 rooms x 8 players, 20s
  RELAY_URL=ws://127.0.0.1:3100 node loadtest.js 100 6 15

Reads the counters that /health exposes, so point it at a relay running this
version or newer. Run it against a LOCAL relay for capacity planning; pointing
it at production is a denial-of-service against your own players.

Measured numbers and what they imply: HostingPlan.md §14.

### `relay/server.js`

#### The :80 listener degrades quietly and logs loudly
<!-- ref:f70b -->

Was `relay/server.js:995-1005`.

Plain-HTTP listener on :80 for the connectivity probes — the game
itself is never served here (see handleProbeRequest), only redirected to.

Per the plan's resolved open question: some machines already have :80 taken (IIS, Docker
Desktop, Skype's old binding), and a failed launch is a worse outcome than a phone that
shows "No internet" while the game still works perfectly over :8443. So this DEGRADES
QUIETLY — EADDRINUSE and friends are caught and logged, never thrown past this function —
and LOGS LOUDLY, because the resulting symptom is easy to misdiagnose as a network
problem rather than a cosmetic one.

#### What :80 answers, and the probe-access log
<!-- ref:f8e4 -->

Was `relay/server.js:928-952`.

What :80 answers. It has its own handler rather than reusing `handleRequest`, and that is
a bug fix, not tidying: `handleRequest` ends in `serveStatic`, so this listener used to
serve the ENTIRE CONTROLLER over plain HTTP on every interface the host has, despite the
comment below claiming it served the probes only. A guest who typed the host name without
`https://` got a controller page that loaded, looked correct, and could not read a single
sensor — a secure context is required for DeviceMotion and there is none here. Redirecting
instead turns that silent dead end into the right page. See
docs/plans/join-failure-reduction.md §1.
Appends one line to probe-access.log, beside this file.

The :80 listener and the DNS responder are the only parts of the relay a phone touches
before it has joined, and they are the parts nobody can see: the relay is a child of the
Unity Editor, so its stdout reaches the Console only when LocalServices.verboseLogging is
on. Three separate diagnoses in this project were blind exactly here — netstat proves a
connection arrived but not what it asked for, and "the phone shows No internet" looks
identical whether the probe never came, came for a path we do not answer, or was answered
correctly and ignored by the platform. A file sidesteps the Unity plumbing entirely, and
survives the session that produced it.

Best-effort by design: a logging failure must never take down a listener a guest is
mid-request on, so every error here is swallowed.

#### Answering the OS connectivity probes ourselves
<!-- ref:144d -->

Was `relay/server.js:530-543`.

Android and iOS each probe a domain of their choosing right after joining a network, to
decide whether to show "No internet" and whether to keep cellular as the default route.
Unless SHARE_INTERNET is on, guests are isolated from the host's internet by design, so
these probes always fail on their own, and "it just stopped working" reports are very
likely a phone that decided this network was useless rather than an actual game bug. Answering them ourselves (this list, plus
/generate_204 and /hotspot-detect.html below) fixes the symptom without re-enabling
forwarding and undoing the isolation decision — see docs/plans/magic-domain-replacement.md
Phase 4 and docs/6-decisions/Decision-OfflineHosting.md.

Every name here is one we intend to SERVE: the phone's plain-HTTP probe reaches :80 and gets
a 204 or "Success". Adding a name in order to have it refused is a different idea and does
not belong here — `www.google.com`, Android's HTTPS probe host, was added on exactly that
reasoning and removed again when hardware testing falsified it. Android resolved it, got our
address, and still issued no HTTP probe.

#### Generic routing, allowlisted static files, per-request error handling
<!-- ref:2e34 -->

Was `relay/server.js:3-25`.

Rock Skipping relay server.

Routes messages between one Unity host (the PC driving the TV) and up to
MAX_PLAYERS_PER_ROOM phone controllers, keyed by a short room code.

Routing is deliberately generic: the relay knows nothing about AIM, THROW or
SPECTATE. Anything a player sends goes to the host; anything the host sends
goes to every player, or to one player if it carries `targetPlayerId`. New
message types can be added to the game without touching this file.

Static file serving is deliberately an allowlist rather than a directory listing
(see `isAllowedRelativePath`): a bare prefix check on the resolved path let a
phone read anything checked into `controller/` — package.json, eslint config,
test scripts, whatever gets added next to `js/` later — with nothing to notice
a new file had joined the public surface (C4, docs/Findings-NetworkAndThrowStability.md).
Naming exactly what a phone needs (`/`, `index.html`, `js/<name>.js`) keeps that
surface from silently growing. Request handling is also per-request try/catch'd
rather than guarded by a global `uncaughtException` handler, because a malformed
request path used to throw synchronously with nothing catching it and took the
whole process — every room on it — down with it (C1, same doc). A handler scoped
to one request can never do that; a global one would only paper over the crash.

### `relay/watch-guests.js`

#### What watch-guests shows
<!-- ref:a16c -->

Was `relay/watch-guests.js:3-20`.

Watches, live, every TCP connection a hotspot guest opens to this host.

`diagnose-hotspot.js` answers "does the relay serve the right thing?" from the host. This
answers the question that one structurally cannot: "did the phone's request arrive at all?"
Those look identical on the phone — a page that never loads is the same screen whether the
request was blocked, was answered wrongly, or was never sent over the Wi-Fi in the first
place. Only the host's connection table separates them.

The distinction matters most for Android. A network flagged "no internet" is not made the
device's default network, so ordinary apps — Chrome included — can route to cellular while
the OS's own connectivity probe stays bound to the Wi-Fi. When that happens the probe
shows up here and the browser never does, from the same phone, at the same moment.

Read-only: it shells out to `netstat` and prints. It changes nothing and touches no socket.

Usage:  node watch-guests.js          (Ctrl+C to stop)

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

### `Assets/Scripts/Net/RelayClient.cs`

#### Host-side connection to the relay.
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

## Traps

**`LocalServices` reuses any relay already listening.** Stopping and restarting Play does *not*
pick up a relay code change if the previous relay outlived its session. Two test cycles have been
spent measuring stale code. Kill the `node server.js` process first; `relay/diagnose-hotspot.js`
step 4 is the cheapest proof of which code is actually running.

**A room outlives its host by 60 seconds,** so `RelayClient`'s retry delay is capped low
(`MaxRetryDelay = 4s`) to reclaim it inside the window rather than after it.

**A single connect attempt is bounded at 8s.** Without it the client wedges permanently: a TCP
connect against an address that silently drops packets never returns on its own.

### `Assets/Scripts/Net/Messages.cs`

#### How far the phones on the hotspot have got through joining — see relay/funnel.js and docs/plans/join-failur...
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

### `Assets/Scripts/Net/RelayClient.cs`

#### How long a single connect attempt may take before it is abandoned.
<!-- ref:a5fc -->

Was `Assets/Scripts/Net/RelayClient.cs:164-174`.

How long a single connect attempt may take before it is abandoned.

Without this the client can wedge permanently. A TCP connect against a
half-torn-down network — Wi-Fi toggled off and back on, a laptop waking, a
relay being restarted — can sit unresolved far longer than any useful
retry interval, and while it does, `_connecting` stays true and Update
refuses to start another attempt. The symptom is a host that never comes
back even though the network is fine again.

### `relay/diagnose-hotspot.js`

#### Why a host-side PASS is hedged
<!-- ref:acb3 -->

Was `relay/diagnose-hotspot.js:194-203`.

Deliberately hedged. This request originates on the host and is destined for the host's
own address, so Windows does not evaluate it against the inbound firewall rules the way
it evaluates a packet arriving over the wireless interface from a phone. A PASS here
proves the socket is listening and answering; it does NOT prove a guest can reach it.

Nor does opening the URL on the phone's browser, which is what this used to advise. On
Android a Wi-Fi network flagged "no internet" is not the default network, so the
browser can route to cellular and time out while the platform's own probe — bound to
the Wi-Fi — reaches :80 fine. Only the host's connection table settles it, which is
what watch-guests.js prints.

## Verification

```bash
cd relay && npm test
```

55 tests across `test-relay.js`, `test-dns.js`, `test-funnel.js`, `test-probe.js`. With Play
running, expect 54 pass / 1 skip — the game's relay owns `:80`, so `test-probe.js` skips that
case by design.

`node relay/diagnose-hotspot.js` walks a phone's whole chain against a live game. It requires Play
mode with the hotspot up, and fails fast and says so otherwise. `npm run loadtest` for throughput.
