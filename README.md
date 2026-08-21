# Rock Skipping

A couch party game: the game runs on a PC driving a TV, and players use their phones as
motion controllers through a web page — no app download, Jackbox-style.

**Read [PLAN.md](docs/PLAN.md) first.** It has the full architecture, the cross-platform sensor
problem, the physics design, and the milestone breakdown. This file only covers how to run
what currently exists.

## Layout

| Path | What it is |
|---|---|
| `Assets/` | Unity 6 project (6000.3.10f1) — the game screen |
| `relay/` | Node WebSocket relay, and the HTTP server for the controller page |
| `controller/` | Phone controller web app — sensors, throw gestures, join screen |
| `deploy/` | Hosting runbook and `local-https.ps1`, which serves the controller over HTTPS |
| `docs/` | Project documentation and plans |
| `docs/PLAN.md` | Full design and implementation plan |
| `docs/HostingPlan.md` | How hosting works and why — supersedes PLAN.md on that subject |

## Status

**M0 and M1 complete, M2 in progress.** The relay is hardened and tested, the controller
app normalizes sensors and sends `AIM`/`THROW`, and the pure skip solver plus the aim
preview are in. Verified end to end on both Android and iOS.

A match runs a fixed number of rounds and ends with a standings screen — see Match flow
below.

Still open in M2: `RockDefinition`/`RockSpawner` and the four specials, `RingGate`
(nothing publishes `RingPassedEvent` yet, so `ScoreManager.pointsPerRing` is currently
dead), and an authored lake scene to replace the runtime placeholder geometry.

**Multiplayer is done and verified.** A real 5-player session across iOS and Android
exercised scoring, turn rotation, late-join and lobby-rejoin, and all four behaved. That
was M3, and it landed ahead of the remaining M2 work above.

> **Hosting has changed and this matters before you press Play.** There is no longer a
> deployed relay. The Fly.io deployment was torn down and its files deleted, but
> `RelayClient.relayUrl` still *compiles in* `wss://rockskipping-relay.fly.dev` as its
> default — that address is dead, and pressing Play without overriding it will simply fail
> to connect. You run the relay on this machine now; see **Running the game** below.

## Running the game

Start the relay on this machine first, then press Play. One command does the whole setup:

```bash
pwsh ./deploy/local-https.ps1
```

It detects this machine's LAN IP, borrows the published `*.local-ip.sh` certificate for the
matching IP-encoded name (`10.0.0.213` → `10-0-0-213.local-ip.sh`), writes `relay-url.txt`
and `relay-env.txt` for you, starts the relay over TLS, and verifies the chain before you
pick up the phone. **Ctrl+C undoes all of it.** Full runbook:
[deploy/README.md](deploy/README.md).

⚠️ **Its last verification step is broken** — it calls `node test-deployed.js`, a file deleted
along with the Fly config, so the script aborts at the end of an otherwise successful setup
(the relay is already up by then). **If the game currently plays for you, you are not running
this script** — the config and certificate from an earlier run persist outside the repo and
Unity starts the relay from them. It becomes blocking the moment that state expires or the
machine's IP changes. Details and workaround: [deploy/README.md](deploy/README.md).

Then open **Bootstrap** and press Play:

1. `RelayClient` reads `relay-url.txt` and opens a socket to the relay on this machine
2. it derives the page origin from that socket URL, so the page and the socket cannot drift
   onto different hosts
3. `JoinScreen` shows the room code, the join URL and a scannable QR once connected

Players scan the QR — it pre-fills the room code via `?room=CODE` — and type only a name.

**The phone must be on the same network as the PC.** The name resolves to a private address,
so it is unreachable from mobile data — a guest on cellular cannot join. That is the standing
limitation of local hosting and is not a bug; [HostingPlan.md](docs/HostingPlan.md) §2 has the
derivation, and [deploy/LOCAL-HTTPS-SPIKE.md](deploy/LOCAL-HTTPS-SPIKE.md) has the rest of
what it costs.

"The same network" is looser than it sounds, though. A 5-player iOS/Android session ran on a
home setup with an ISP router *and* a second unit in AP mode on its own SSID, with phones
joining from either. And if guests cannot get onto the network — no password, or enterprise
Wi-Fi wanting per-user credentials — **host a mobile hotspot from the PC and have phones join
that**. The PC stays on the venue network and bridges them, which sidesteps AP isolation and
sign-in walls entirely. Confirmed on a school network that a phone could not join.

Why HTTPS at all, rather than plain `ws://` to a LAN IP: phone motion sensors need a secure
context, and an HTTPS page cannot open a plain `ws://` socket. Both problems disappear when
the page and the socket share one secure origin, which is why the relay serves the controller
itself and why the certificate is worth the trouble.

### Where aim and throw actually travel

`AIM` and `THROW` ride a **direct WebRTC DataChannel** to each phone by default, so gameplay
does not round-trip through the service. A phone that cannot negotiate one falls back to the
relay socket by itself, per phone, and plays normally — only with slightly softer aim
tracking. Roster, turn state, scores and the spectator view always go over the relay.

**F1** on the TV reports `N/M direct` and names which phones are on which transport, so a
silent downgrade is visible rather than merely slower. Turn it off with **Use Peer
Connections** on `Bootstrap`, or per-phone with `?direct=0` on the controller URL.

### Plain HTTP, for desktop-only testing

`local-https.ps1` is the path for anything involving a real phone. If you only need two
desktop browsers, you can skip the certificate entirely:

1. Tick **Start Local Services** on `Bootstrap`. It is **off by default**.
2. Put `ws://localhost:3000` in a `relay-url.txt` beside the executable, so `RelayClient`
   actually points at it. The file is gitignored, so it stays a per-machine choice.

That origin is not secure, so motion sensors will not work on it — which is exactly why it is
desktop-only. Anything involving a phone needs the HTTPS path above.

## Match flow

`TurnManager` owns a three-phase lifecycle, and nothing advances on its own:

| Phase | What is on screen | How it advances |
|---|---|---|
| **Lobby** | room code, join URL, QR, roster | press **Space** to start |
| **Playing** | the lake; join details shrink to a corner card | each throw completing rotates the turn |
| **Results** | final standings, winner highlighted | press **Space** to play again |

A match is `Rounds Per Match` rounds (default 3), each giving every player one throw. The
turn advances on `ThrowCompletedEvent` — a sunk rock is what drives the match forward — so
**Space** and **N** (force next turn) are the only keys needed to run a session.

The match never starts by itself: somebody arriving mid-lobby should not begin it for
everyone already waiting. Throws are refused outside the Playing phase, so a stale phone
cannot fling rocks across the results screen.

### Verifying with a second PC

A desktop browser has no motion sensors, so a real swing produces nothing. Rather than
letting that seat deadlock the match, the **second tap** synthesizes a throw from how long
the controller was armed — but **only on a device that has never produced a single motion
event**, which a phone does within a frame of joining. A weak gesture on a real phone
stays a fumble. Simulated throws are labelled on the controller and report zero samples,
which is how the TV debug readout (**F1**) tells them apart.

So: open the join URL on a second PC, join with a different name, and press Space on the
host. Both seats take turns, scores accumulate, and the match ends on the final round.

## Running the relay directly

```bash
cd relay && npm install && npm start
```

Listens on `:3000`, or `$PORT` if set, serving the controller page, the WebSocket, `/health`
and `/qr`. Set `TLS_CERT` and `TLS_KEY` and it serves HTTPS instead — which is what
`local-https.ps1` does for you. Running it for real play is [deploy/README.md](deploy/README.md).

Integration tests — routing, identity stamping, reconnect, the QR endpoint, and each
hardening fix:

```bash
cd relay && npm test
```

## Verifying the Unity event system

The event system was ported from FactoryTM and needed four changes to work across scene
loads. The verification proves those hold at runtime rather than by inspection.

In the editor: **RockSkipping → Verify → Run Event System Verification**. Results land in the
Console and in `verification-result.txt`.

Headless — note the deliberate absence of `-quit`, since the method enters play mode:

```bash
"<your-unity-install>/6000.3.10f1/Editor/Unity.exe" -batchmode -nographics -projectPath . -executeMethod RockSkippingEditor.ProjectSetup.RunVerification -logFile verify.log
```

Poll for `verification-result.txt`, then kill the process — exiting the editor from the
domain reload that follows play mode is unreliable in batchmode.

Expected output:

```
PASS
  PASS  publish/subscribe delivers
  PASS  scene-local subscriber receives
  PASS  EventManager survived Bootstrap -> Lobby
  PASS  persistent subscriber still receives after Lobby load
  PASS  EventManager survived Lobby -> Game
  PASS  persistent subscriber still receives after Game load
  PASS  destroyed subscriber pruned (3 -> 2)
  PASS  no publish errors logged
```

`Assets/Scripts/Verification/` is a scaffolding aid, not shipping code. Delete it once the
event system stops changing.

## Scenes

Build order is **Bootstrap → Lobby → Game → Results**. `Bootstrap` must stay at index 0: it
creates the managers that have to outlive scene transitions, then loads `Lobby`.

`RelayClient` is created in `Bootstrap` alongside the other persistent managers. It must
stay there: if it lives in a scene instead, the WebSocket is torn down on every transition
and every phone bounces back to the join screen — which looks like a networking bug but is
a lifecycle one. The same applies to `LocalServices`, which owns the relay child process on
the machines where it is enabled at all, and `JoinScreen`, so a latecomer can still scan in
during the Game scene.

## Notes for whoever picks this up

- The project is on the **built-in render pipeline**, not URP as PLAN.md assumes. Switching
  is cheapest right now while there are no materials to break.
- Every scene-local subscriber must call `EventManager.Instance?.UnsubscribeFromAllEvents(this)`
  in `OnDestroy`. The destroyed-subscriber fix backstops forgetting, but don't rely on it.
- `EventManager.DebugLoggingEnabled` is exposed on the `Bootstrap` component. Turn it on
  while wiring new events — publish failures are otherwise swallowed into `Debug.LogError`.
