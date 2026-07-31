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
| `PLAN.md` | Full design and implementation plan |

## Status

**M0 and M1 complete, M2 in progress.** The relay is hardened and tested, the controller
app normalizes sensors and sends `AIM`/`THROW`, and the pure skip solver plus the aim
preview are in. Verified end to end with one Android phone.

Hosting is one click: pressing Play brings up the relay and a public HTTPS tunnel, then
puts the room code, join URL and a QR on screen. A match runs a fixed number of rounds and
ends with a standings screen — see Match flow below.

Still open in M2: `RockDefinition`/`RockSpawner` and the four specials, `RingGate`
(nothing publishes `RingPassedEvent` yet, so `ScoreManager.pointsPerRing` is currently
dead), and an authored lake scene to replace the runtime placeholder geometry.

Not yet run with more than one real player — turn rotation, the spectator view and the
results screen are written and unit-tested but have not been exercised at a party.

## Running the game

Open the **Bootstrap** scene and press Play. `LocalServices` then:

1. reuses a relay already listening on `:3000`, or runs `npm install` (first launch only)
   and starts one
2. starts `cloudflared` and waits for its public `https://….trycloudflare.com` address
3. hands the URL to `JoinScreen`, which shows it with the room code and a scannable QR

Players scan the QR — it pre-fills the room code via `?room=CODE` — and type only a name.

## How aiming works

Two conventions have to agree, and both were wrong in the first playtest.

**On the phone**, aim is the compass bearing of the direction the top of the phone
points — derived from the device's full rotation and projected onto the ground, *not*
from the DeviceOrientation `alpha` angle. Alpha is a Euler yaw and is degenerate near
vertical: at `beta = 89` a phone reads a bearing of -30°, and at `beta = 91` the same
phone reads +150°. A throwing swing sweeps straight through that singularity, so
physically straight throws veered hard left or right depending on where in the arc the
release landed. `pointingHeading` returns a `confidence` alongside the bearing, and the
heading is only updated while the phone is actually pointing at the horizon — through
the steep middle of a swing the last good bearing is held. That is what makes "point at
the screen and let go" behave.

A side effect worth keeping: wrist roll (`gamma`) cannot change where the top of the
phone points, so aim is immune to it for free.

**In Unity**, `aimDeg = 0` means "along `AimPreview.ThrowForward`", which is derived
from the aiming camera flattened onto the horizontal plane. Straight has to mean
straight *to the player looking at the TV*; a camera framed even slightly off-axis makes
a centred throw visibly drift, and no amount of correct phone aiming can fix it. The
preview arc, the ghost rock and the live throw all read the same value, so the preview
cannot promise a line the throw does not take.

The aiming camera is therefore square on to the lake — identity rotation, looking
straight down +Z, so `ThrowForward` comes out as exactly +Z. Keep it that way when the
real lake is authored: any yaw left in the aiming view is yaw the player has to
unconsciously correct for.

The camera only moves while a rock is in flight. Once it has returned it snaps exactly
onto the home pose and stops being touched at all — the smoothing used to run every
idle frame, and since both interpolations approach their target asymptotically, the
aiming view never quite settled.

Bearings and Unity's yaw are both clockwise-from-above, so the sign passes straight
through. If a platform turns out to disagree, open the controller with `?yawSign=-1` to
confirm it on the device before changing any code.

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
letting that seat deadlock the match, `release()` synthesizes a throw from how long the
grip button was held — but **only on a device that has never produced a single motion
event**, which a phone does within a frame of joining. A weak gesture on a real phone
stays a fumble. Simulated throws are labelled on the controller and report zero samples,
which is how the TV debug readout (**F1**) tells them apart.

So: open the join URL on a second PC, join with a different name, and press Space on the
host. Both seats take turns, scores accumulate, and the match ends on the final round.

**Requires** Node 20+ and cloudflared:

```bash
winget install Cloudflare.cloudflared
```

A tunnel is not optional: phone motion sensors need a secure context, and an HTTPS page
cannot open a plain `ws://` socket to a LAN address. Both problems disappear when the page
and the socket share one public origin, which is why the relay serves the controller itself.

The quick tunnel's hostname is random per launch. Nobody types it — that is what the QR is
for — but it does mean a URL from a previous session is dead. For a stable address, switch
to a named tunnel; only the `cloudflared` arguments in `LocalServices` change.

To run the pieces by hand instead, untick **Start Local Services** on `Bootstrap`.

## Running the relay directly

```bash
cd relay && npm install && npm start
```

Listens on `:3000`, or `$PORT` if set, serving the controller page, the WebSocket, `/health`
and `/qr`. Deploy with `fly deploy` (see `relay/fly.toml`).

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
a lifecycle one. The same applies to `LocalServices`, which owns the two child processes,
and `JoinScreen`, so a latecomer can still scan in during the Game scene.

## Notes for whoever picks this up

- The project is on the **built-in render pipeline**, not URP as PLAN.md assumes. Switching
  is cheapest right now while there are no materials to break.
- Every scene-local subscriber must call `EventManager.Instance?.UnsubscribeFromAllEvents(this)`
  in `OnDestroy`. The destroyed-subscriber fix backstops forgetting, but don't rely on it.
- `EventManager.DebugLoggingEnabled` is exposed on the `Bootstrap` component. Turn it on
  while wiring new events — publish failures are otherwise swallowed into `Debug.LogError`.
