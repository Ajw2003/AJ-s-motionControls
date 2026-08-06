# Rock Skipping

A couch party game: the game runs on a PC driving a TV, and players use their phones as
motion controllers through a web page — no app download, Jackbox-style.

**Read [PLAN.md](PLAN.md) first.** It has the full architecture, the cross-platform sensor
problem, the physics design, and the milestone breakdown. This file only covers how to run
what currently exists.

## Layout

| Path | What it is |
|---|---|
| `Assets/` | Unity 6 project (6000.3.10f1) — the game screen |
| `relay/` | Node WebSocket relay, and the HTTP server for the controller page |
| `controller/` | Phone controller web app — sensors, throw gestures, join screen |
| `deploy/` | Deployment runbook, Caddyfile and systemd unit for the relay |
| `PLAN.md` | Full design and implementation plan |
| `HostingPlan.md` | How hosting works and why — supersedes PLAN.md on that subject |

## Status

**M0 and M1 complete, M2 in progress.** The relay is hardened and tested, the controller
app normalizes sensors and sends `AIM`/`THROW`, and the pure skip solver plus the aim
preview are in. Verified end to end with one Android phone.

Hosting is one click and hosts install nothing: pressing Play connects out to the deployed
relay, then puts the room code, join URL and a QR on screen. A match runs a fixed number of
rounds and ends with a standings screen — see Match flow below.

Still open in M2: `RockDefinition`/`RockSpawner` and the four specials, `RingGate`
(nothing publishes `RingPassedEvent` yet, so `ScoreManager.pointsPerRing` is currently
dead), and an authored lake scene to replace the runtime placeholder geometry.

Not yet run with more than one real player — turn rotation, the spectator view and the
results screen are written and unit-tested but have not been exercised at a party.

## Running the game

Open the **Bootstrap** scene and press Play. Nothing starts on this machine:

1. `RelayClient` opens an outbound socket to `wss://rockskipping-relay.fly.dev` — the
   deployed service, which also serves the controller page
2. it derives the page origin from that socket URL, so the page and the socket cannot drift
   onto different hosts
3. `JoinScreen` shows the room code, the join URL and a scannable QR once connected

Players scan the QR — it pre-fills the room code via `?room=CODE` — and type only a name.

The address is overridable without a rebuild — one line in a `relay-url.txt` beside the
built executable (or next to the project folder in the editor). Why the service exists and
what it carries: [HostingPlan.md](HostingPlan.md).

**Requires nothing installed.** Hosting is an outbound connection, so there is no tunnel to
install, no port to open, no router page and no firewall prompt.

Why the page comes from the service rather than from this machine: phone motion sensors need
a secure context, and an HTTPS page cannot open a plain `ws://` socket to a LAN address. Both
problems disappear when the page and the socket share one origin, which is why the relay
serves the controller itself. [HostingPlan.md](HostingPlan.md) §2 has the full derivation.

### Where aim and throw actually travel

`AIM` and `THROW` ride a **direct WebRTC DataChannel** to each phone by default, so gameplay
does not round-trip through the service. A phone that cannot negotiate one falls back to the
relay socket by itself, per phone, and plays normally — only with slightly softer aim
tracking. Roster, turn state, scores and the spectator view always go over the relay.

**F1** on the TV reports `N/M direct` and names which phones are on which transport, so a
silent downgrade is visible rather than merely slower. Turn it off with **Use Peer
Connections** on `Bootstrap`, or per-phone with `?direct=0` on the controller URL.

### Pointing at a local relay for development

Two things, and both are needed — either alone does nothing useful:

1. Tick **Start Local Services** on `Bootstrap`. It is **off by default**, because a normal
   host runs no relay at all.
2. Put `ws://localhost:3000` in a `relay-url.txt` beside the executable, so `RelayClient`
   actually points at it. The file is gitignored, so it stays a per-machine choice.

That origin is not secure, so motion sensors will not work on it — this is for desktop
testing. Real-phone testing goes through the deployed service.

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
and `/qr`. Deploying it publicly is [deploy/README.md](deploy/README.md) — `fly deploy` from
the repo root, since the image needs `controller/` alongside `relay/`.

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
"C:\Users\Ayden's mini\Desktop\UnityVersions\6000.3.10f1\Editor\Unity.exe" -batchmode -nographics -projectPath . -executeMethod RockSkippingEditor.ProjectSetup.RunVerification -logFile verify.log
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
