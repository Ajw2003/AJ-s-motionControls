# Rock Skipping — Phone-as-Controller Party Game

## Context

We're building a couch party game in the mould of Lego Party and Jackbox: the game runs on a PC connected to a TV, and each player uses their own phone as a motion controller. No app download — players open a URL, type a 4-letter room code, and their phone becomes a Wiimote.

The game itself is a rock skipping game modelled on **Wii Play: Motion**'s "Skip Skimmer" minigame. In the original, you hold a button, swing the remote toward the lake, and release at the right moment; tilting the remote sets the stone's angle, which determines how it bounces. Swing too hard and it sinks; too soft and it dies early. We're reproducing that feel with phone accelerometer and gyroscope data.

**Current state:** `RockSkipping/` is an empty repo (`git init` done, remote set to `Ajw2003/RockSkipping`, zero commits). Greenfield.

**Existing prior art to draw from (do not start from scratch):**
- `PartyGameNonC# Code\server.js` — working room-code WebSocket relay. ~80% of what we need; needs hardening (see M0).
- `PartyGameNonC# Code\ServerHtml.Html` — working join screen + controller UI. Good starting layout; its `ws://localhost` transport must change.
- `RelayServer.js` — earlier naive broadcast version. Superseded; ignore.
- `Maui\MauiApp` — untouched stock template, an abandoned native-app-controller attempt. The web controller replaces this entirely. Do not continue it.

**Environment:** Unity 6 (`6000.3.10f1` is the house standard across existing projects; `6000.3.15f1` also installed). Node v22.19.0, npm 11.6.0.

### Assumptions

Confirmed with the project owner:
- **Controllers only** on iOS/Android — the game itself runs on a Windows PC driving the TV. No app store, no mobile Unity build.
- **Android is the only test platform available.** iOS code paths are written from spec but unverified. See Test device policy below.
- **Turn-based, 2–8 players.** One player throws at a time. Only one rock in flight.
- **Vertical slice quality.** One polished lake, motion pipeline working end-to-end.

The **event manager and base scripts are copied in from FactoryTM** (paths in Scoring below); scoring and other event-driven systems build on those rather than being written from scratch.

---

## Architecture

> **Superseded — see [HostingPlan.md](HostingPlan.md).** Neither hosted component exists.
> Cloudflare Pages was never used (the relay serves the controller page itself, from
> `STATIC_DIR`), and the Fly.io deployment has been torn down. The relay and the page both run
> on the host PC now, over HTTPS on a borrowed `*.local-ip.sh` certificate. The connection flow
> below is still accurate — only the boxes have moved.

Three separately-deployed components:

```
  Phone (Safari/Chrome)          Relay (Node+ws)           Unity 6 Desktop
  Cloudflare Pages, HTTPS   <--wss-->   Fly.io    <--wss-->   TV / PC
  normalizes sensors                 routes by room         simulates physics
```

**Connection flow:**
1. Unity boots → connects to relay → `CREATE_ROOM` → relay generates and returns a 4-letter code
2. TV displays the code, the controller URL, and a QR code
3. Phone opens URL → enters code (QR pre-fills it) → `JOIN_ROOM`
4. Relay notifies Unity `PLAYER_JOINED`
5. Phone streams aim + sends throws → relay → Unity
6. Unity broadcasts turn/score/spectator state → relay → phones

**Authority model: the PC is authoritative for everything.** Phones are dumb input devices; they never simulate. There is no Unity-to-Unity networking anywhere in this project — which has a useful consequence noted under Rock Data below.

### Why a hosted relay rather than direct LAN connection

> **Superseded twice — see [HostingPlan.md](HostingPlan.md).** The reasoning below is still
> correct about *why* HTTPS is mandatory. Its conclusion is not: the relay was hosted (on Fly),
> and is now **run on the host PC**, serving the controller page over HTTPS on a borrowed
> `*.local-ip.sh` certificate. Gameplay still goes phone-to-PC directly over the LAN by WebRTC
> DataChannel.
>
> Note what that means for the section title, because it is the one thing this plan got
> backwards: the answer is now *direct LAN connection after all* — reached not by defeating the
> HTTPS requirement below, but by borrowing a public certificate that happens to cover private
> addresses. "Hosts configure nothing" no longer holds either; hosting is a script and a
> firewall prompt.

Two hard blockers make LAN-direct a dead end:

- **iOS requires a secure context for motion sensors.** You cannot obtain a public TLS certificate for `192.168.x.x`. Self-signed means installing a trust profile on every guest's phone, which defeats the "scan and play" premise.
- **Mixed content.** Once the controller page is HTTPS (which it must be, per above), the browser blocks it from opening an insecure `ws://` connection to a LAN IP.

A public `wss://` relay solves both, and additionally solves NAT traversal so players aren't required to be on the same Wi-Fi.

### Why not AirConsole

Worth recording since it was the initial instinct. AirConsole is a legitimate option, but games built with its plugin **only run as Unity WebGL builds hosted on AirConsole's own servers**. The cost isn't money — it's WebGL performance limits, slow build iteration, and no path to a Steam release later.

Critically, **AirConsole would not have reduced the hard part of this project.** The controller is still your own HTML/JS either way; AirConsole solves HTTPS, pairing, and transport, which Cloudflare Pages plus a small relay also solve. The cross-platform sensor normalization work below is identical under both.

---

## The motion pipeline

This is the hard part of the project and the main technical risk. Budget accordingly.

### The cross-platform problem

iOS and Android disagree in ways that will silently produce wrong behaviour rather than errors:

| Behavior | iOS | Android |
|---|---|---|
| `DeviceMotionEvent.acceleration` | **Not available** | Available |
| `accelerationIncludingGravity` | Available | Available |
| `requestPermission()` | Required, must follow a user touch | **API does not exist** |
| Compass heading | `webkitCompassHeading`, true north, **clockwise** | `alpha`, magnetic north, **counter-clockwise** |
| Vibration | Unreliable | Works |

Two of these are landmines:

- **iOS has no `acceleration` property**, only the gravity-inclusive one. Code developed on Android will read `undefined` on every iPhone. Use `accelerationIncludingGravity` on both platforms and remove gravity manually.
- **Compass heading runs in opposite directions** on the two platforms, off different norths. Any aiming logic built on absolute yaw will steer backwards on one platform.

Also note `requestPermission()` **throws** on Android if called unconditionally — it must be feature-detected.

### Test device policy — Android only for now

**No iOS device is available to test on.** That constrains what any milestone can honestly claim:

- **All testing and every acceptance criterion is Android (Chrome).** A milestone is "done" when it works on Android. Do not gate progress on iOS behaviour that cannot be observed.
- **iOS code paths are still written, but treated as unverified.** The permission gate, the `accelerationIncludingGravity` fallback, and the compass-heading handling all go in now — writing them later means retrofitting the normalization layer, which is exactly the rework this design exists to avoid. They are implemented from spec and marked as untested.
- **Every iOS-specific branch carries an `// UNVERIFIED (iOS):` comment** naming what it assumes. That makes the untested surface greppable the day a device shows up, instead of a hunt through the whole file.
- **Do not claim cross-platform parity.** The 10% iPhone-vs-Android agreement check in Verification is deferred, not passed. Until it runs, treat iOS as designed-for but unproven.

The single highest-risk unverified assumption is that **iOS lacks `DeviceMotionEvent.acceleration`**, so the code must never read it. Because Android *does* expose it, an Android-only test will never catch a regression that reintroduces it. Guard this in code rather than by discipline — read `accelerationIncludingGravity` behind one accessor in `sensors.js` and never touch `event.acceleration` anywhere else.

When an iPhone becomes available, the catch-up work is: run the same gesture on both devices, compare descriptor values, and confirm the permission prompt appears and is honoured. That is a session's work, not a redesign — provided the branches were written now.

### The governing rule

**Never send raw sensor values to Unity. Normalize on the phone.**

The controller emits device-agnostic values. Unity never sees a platform difference. This is both the correctness fix and a clean reusable abstraction boundary.

The mechanism that makes it work is **calibration plus deltas**: capture a reference orientation when the player arms, and report everything relative to it. A delta from a calibrated zero is consistent across platforms even when the absolute values are not — which sidesteps the compass problem entirely. It also doubles as a gameplay beat: *"Point your phone at the lake and tap."*

### Controller state machine

`IDLE → ARMED → (phone stops) → IDLE`

- **IDLE** — not this player's turn; showing spectator view
- **ARMED** — entered by a **tap**. Captures reference `(alpha0, beta0, gamma0)`, records samples at ~60Hz into a ring buffer holding the last ~1.5s, and streams `AIM` at 20Hz. A second tap disarms so the player can re-aim and recalibrate.
- **The stop is the release.** No `touchend`: acceleration crossing `swingAccel` marks a swing, and three consecutive samples under `stopAccel` mark the stop, at which point the buffer is analysed and one `THROW` is sent.

**This replaced a press-and-hold gesture, and the reason is worth keeping.** Holding a button
through a throwing motion is awkward, and — more importantly — it meant orientation had to be
read at the instant a finger lifted, mid-swing, when the phone's attitude is dominated by the
swing rather than by where the player was pointing. Stones went places nobody had aimed.
Attempts to reconstruct the "intended" aim from earlier in the swing all failed, because the
gesture had already thrown that information away.

Firing on the stop dissolves the problem rather than solving it: **a throwing motion ends
pointing where it was thrown**, so the stop sample already *is* the aim. Nothing is inferred,
so nothing can disagree with the preview.

Detection runs on a per-sample hook in `MotionSource`, not the 20Hz aim timer — a stop lasts a
few milliseconds and polling at 50ms reads the phone long after it has settled. Three calm
samples rather than one, because a swing passes through near-zero acceleration at its own
turnaround and would otherwise fire mid-arc.

### Normalization (`controller/js/sensors.js`)

```js
// 1. iOS lacks .acceleration — always use the gravity-inclusive one
const a = event.accelerationIncludingGravity;

// 2. Remove gravity with a low-pass estimate, subtract to get linear accel
gravity.x = K * gravity.x + (1 - K) * a.x;   // K ≈ 0.8
// ...same for y, z
const linear = { x: a.x - gravity.x, y: a.y - gravity.y, z: a.z - gravity.z };

// 3. Compensate for screen rotation so axes stay stable
const angle = screen.orientation?.angle ?? 0;
rotateXY(linear, -angle);

// 4. Orientation ALWAYS as delta from calibration — never absolute
const dAlpha = shortestAngleDelta(event.alpha, alpha0);
const dBeta  = shortestAngleDelta(event.beta,  beta0);
const dGamma = shortestAngleDelta(event.gamma, gamma0);
```

Lock the page to portrait (`screen.orientation.lock('portrait')` where supported) so `beta`/`gamma` don't swap physical axes mid-throw. Treat the lock as best-effort and keep the rotation compensation in step 3 as the real fix.

Permission gate, which must sit behind a real tap:

```js
async function requestMotion() {
  if (typeof DeviceMotionEvent?.requestPermission === 'function') {
    const res = await DeviceMotionEvent.requestPermission();   // iOS only
    return res === 'granted';
  }
  return true;   // Android: no permission API
}
```

---

## Two-channel input design

The aim preview is a **required** feature, so input splits into two channels with deliberately different reliability characteristics.

### `AIM` — continuous, lossy, cosmetic only

Streamed at a flat 20Hz while ARMED. Never authoritative — dropping every one of these packets must not change a throw's outcome, only degrade preview smoothness.

```json
{ "type":"AIM", "roomCode":"ABCD", "playerId":"AJ", "seq":142,
  "aimDeg":-8.5, "attackAngleDeg":22.0, "rollDeg":4.0, "windup":0.35 }
```

`seq` is a monotonic counter. Unity discards any packet whose `seq` is not greater than the last one seen, which cheaply handles out-of-order and stale arrivals. `windup` is normalized current acceleration magnitude, driving a "charging" indicator.

Flat 20Hz is ~1.5 KB/s for the single active player — trivial. Don't bother with deadbanding unless profiling says otherwise.

### `THROW` — single, authoritative

Computed once when the phone stops, from the sample buffer:

- **power** — peak magnitude of linear acceleration during the swing, mapped 0..1. Use peak, not integral: more forgiving and better matched to perceived effort.
- **timingQuality** — how *abruptly* the swing ended, scored from `|t_stop − t_peak|`. With no button to release, snapping to a halt right after the peak is the skill beat that replaces Skip Skimmer's "release at the right moment."
- **aimDeg** — `dAlpha` at the stop, **raw and unclamped**
- **attackAngleDeg** — `dBeta` at the stop, raw. The stone's pitch relative to the water; the single most important variable.
- **rollDeg** — `dGamma` at the stop, raw
- **spinRate** — `rotationRate` magnitude at the stop, feeding gyroscopic stability

**Nothing is clamped.** Aim used to be limited to ±45° so a player who twisted too far still
got a usable throw — but that quietly rewrote what they did into something they had not asked
for. Throwing backwards is a legitimate thing to attempt, and being allowed to miss is part of
the gesture feeling honest.

```json
{ "type":"THROW", "roomCode":"ABCD", "playerId":"AJ",
  "power":0.72, "timingQuality":0.91, "aimDeg":-8.5,
  "attackAngleDeg":22.0, "rollDeg":4.0, "spinRate":190.0 }
```

The split matters for robustness: network hiccups make the preview stutter but can never cause a "the game read my throw wrong" moment.

---

## TV aim preview (`Assets/Scripts/Game/AimPreview.cs`)

**Required feature — the TV must give live aiming feedback.** At 20Hz over a jittery connection, naive application of incoming values will look choppy, so smoothing is not optional polish here; it's what makes the feature work at all.

```
On AIM received:
  if (msg.seq <= lastSeq) return;        // stale or out-of-order
  lastSeq = msg.seq;
  targetAim = msg.aimDeg; targetAttack = msg.attackAngleDeg; ...
  lastAimTime = Time.time;

Every frame:
  curAim    = Mathf.SmoothDampAngle(curAim,    targetAim,    ref vAim,    0.08f);
  curAttack = Mathf.SmoothDampAngle(curAttack, targetAttack, ref vAttack, 0.08f);
  if (Time.time - lastAimTime > 0.5f) FadeOutPreview();   // surfaces connection loss
```

**What to render**, in priority order:

1. **Predicted trajectory arc** — run `SkipPhysics` in a lightweight predict-only mode at nominal power (~0.7) and draw the resulting skip path as a dotted line. This is the highest-value visual because it makes attack angle directly legible and teaches the ~20° sweet spot without a tutorial.
2. **Ghost rock** at the throw origin, rotated to the live attitude, so players see the stone's tilt.
3. **Windup/charge indicator** driven by `windup`.

The predict-mode requirement is why `SkipPhysics` should be written as a **pure, side-effect-free stepping function** that both the real simulation and the preview call — see below.

---

## Skip physics (`Assets/Scripts/Game/SkipPhysics.cs`)

Use a **custom solver, not Unity's rigidbody collisions.** Skipping needs precise per-bounce control over attitude and energy loss that PhysX won't give you cleanly — and the aim preview needs to run the same maths headlessly, which rigidbodies make awkward.

Structure it as a static/pure function so preview and live simulation share one code path:

```csharp
// No scene mutation, no side effects — safe to run N steps ahead for the preview
public static SkipStep Step(in SkipState s, in RockStats stats, in SkipTuning t, float dt);
```

Launch:
```
speed = lerp(minSpeed, maxSpeed, power) * lerp(0.6, 1.0, timingQuality)
dir   = Quaternion.AngleAxis(aimDeg, Vector3.up) * forward
vel   = dir * speed + Vector3.up * sin(launchPitch)
```

Per water contact:
```
incidence = angle between velocity and water plane
attitude  = angle between rock's flat-face normal and water plane

// ~20° is the documented optimum for real stone skipping
angleError = abs(attitude - stats.optimalAngle)
tolerance  = stats.baseTolerance + spinBonus(spin)   // spin widens the window

if (speed < stats.minSkipSpeed)  -> sink
if (incidence > t.maxIncidence)  -> sink   // too steep, plows in
if (angleError > tolerance)      -> sink

restitution    = f(speed, angleError, spin) * stats.restitution
vel.y          = -vel.y * restitution
vel.horizontal *= stats.drag
spin           *= stats.spinDecay
skipCount++
-> record Bounced=true in the returned SkipStep (do NOT publish here — see below)
```

### `SkipPhysics.Step` must never publish events

This is the sharpest edge where the pure-solver design meets the event system. The aim preview runs `Step` speculatively, many frames ahead, every frame — for a throw that hasn't happened. If `Step` published `SkipOccurredEvent`, **the preview would score imaginary throws**, inflating the scoreboard continuously while a player merely aims.

So: `Step` records outcomes in its returned `SkipStep` struct and publishes nothing. `RockController` inspects the returned struct and publishes — and only when it is driving a real throw, never a prediction. Keep the live/predict distinction as an explicit flag on the caller, not a condition inside the solver.

The same reasoning applies to audio, VFX, haptics, and camera shake: anything observable belongs in `RockController`, not in `Step`.

Spin widening the tolerance window is both physically accurate (gyroscopic stability holds the stone's attitude) and good design — it gives players a reason to flick their wrist, adding a second skill dimension beyond raw power.

Expect tuning to take longer than implementation.

---

## Rock data model

Four specials — **deer, cat, dog, Galaga ship** — alongside plain basic rocks. Both categories roll randomized stats within configurable min/max ranges.

### Storage: use ScriptableObjects

The owner flagged a concern about ScriptableObjects and multiplayer networking. **That concern doesn't apply to this architecture.** The usual SO/netcode problems come from replicating SO state between Unity peers — but here there are no Unity peers. Phones are browsers sending JSON; the PC is the sole simulator. SOs never cross the wire, so they're the right tool.

The one rule: when a rolled stat needs to appear on a phone (e.g. showing "Galaga Ship — Bouncy!" on the controller), send the **rolled plain values as JSON**, never an SO reference.

```csharp
[CreateAssetMenu] public class RockDefinition : ScriptableObject {
    public string displayName;
    public RockCategory category;      // Basic | Special
    public GameObject prefab;
    [Header("Rolled per-instance between min/max")]
    public Vector2 restitution, drag, spinDecay, baseTolerance, optimalAngle, minSkipSpeed, mass;
    public float specialWeight;        // spawn rarity
}
```

`RockStats` is the **rolled, immutable** struct produced from a `RockDefinition` at spawn time and passed into `SkipPhysics.Step`. Roll once at spawn and log the seed so odd throws are reproducible when tuning.

Suggested flavour hooks for the specials (design's call, but each should feel distinct through data first, code second):
- **Cat** — lands on its feet: unusually wide `baseTolerance`, forgiving of bad angles
- **Deer** — bounds: high `restitution`, tall arcs, fewer but longer skips
- **Dog** — eager and erratic: good restitution with a small random yaw kick per bounce
- **Galaga ship** — high `spinDecay` resistance and a one-time mid-flight boost

Only the dog's yaw kick and the ship's boost need code beyond data; hold that line if scope tightens.

---

## Event system (copied from FactoryTM)

### Files to copy

Copy these into `RockSkipping/Assets/Scripts/`, preserving the folder shape:

| Source (FactoryTM) | Destination |
|---|---|
| `Assets\Scripts\Managers\EventManager.cs` | `Assets/Scripts/EventSystems/EventManager.cs` |
| `Assets\Scripts\EventTypes\IEvent.cs` | `Assets/Scripts/EventTypes/IEvent.cs` |
| `Assets\Scripts\Utils\Singleton.cs` | `Assets/Scripts/Utils/Singleton.cs` |

**`Singleton.cs` is not optional.** `EventManager` is declared `SingletonBase<EventManager>`, so copying only the first two files will not compile.

### Two cleanups required during the copy

**1. Strip the dead `using` directives.** Every one of these files opens with the same block of FactoryTM-specific namespaces:

```csharp
using Managers; using Placeables; using Ui; using Weapons; using Nodes; using EventTypes;
```

None of those namespaces exist in RockSkipping, and none are actually used by the files themselves (`IEvent.cs` carries all six for an empty interface). Delete them on copy or nothing compiles. Keep only `using UnityEngine;`, `using System;`, and `using System.Collections.Generic;` where genuinely needed.

**2. Normalize the namespace.** In FactoryTM, `EventManager` sits in `Code.Scripts.EventSystems` while `IEvent` sits in `EventSystems`, bridged by a `using EventSystems;`. Put both in `EventSystems` in the new project — it's free to fix in a greenfield repo and confusing to live with later.

### Conventions to follow

Match the existing event style exactly — see `EventTypes\PlayerEvents\PlayerHealthChangedEvent.cs` and `EventTypes\BuildingEvents\BuildingDiedEvent.cs` as the reference implementations. Namespace `EventTypes.<Category>Events`, `using EventSystems;` placed *inside* the namespace block, an XML `<summary>`, and immutable get-only auto-properties assigned in the constructor:

```csharp
namespace EventTypes.SkipEvents
{
    using EventSystems;

    /// <summary>Published each time the rock successfully bounces off the water.</summary>
    public class SkipOccurredEvent : IEvent
    {
        public int SkipIndex { get; }
        public float Speed { get; }

        public SkipOccurredEvent(int skipIndex, float speed)
        {
            SkipIndex = skipIndex;
            Speed = speed;
        }
    }
}
```

Publish and subscribe through the singleton, always null-conditional:

```csharp
EventManager.Instance?.Publish(new SkipOccurredEvent(skipCount, speed));

EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, e => AddScore(e.SkipIndex));
// in OnDestroy:
EventManager.Instance?.UnsubscribeFromAllEvents(this);
```

Subscriptions are held as `WeakReference`s and are auto-purged when a target is destroyed, but explicit unsubscription in `OnDestroy` is still the expected hygiene. Set `EventManager.DebugLoggingEnabled = true` while wiring — `Publish` swallows per-subscriber exceptions into `Debug.LogError`, so a broken handler degrades quietly rather than failing loudly.

### Multi-scene architecture — three required changes to the copied code

The project uses multiple scenes, which the copied `EventManager` does not currently support. Note the direction of the fix: **`persistBetweenScenes` must be set to `true`.** It is hardcoded to `false` today, and that is exactly what destroys the manager on every scene load — `SingletonBase` only calls `DontDestroyOnLoad` when the flag is `true`.

**1. Persist the manager**

```csharp
protected override void Awake()
{
    persistBetweenScenes = true;   // was false — required for multi-scene
    base.Awake();
}
```

Hardcode it rather than relying on the `[SerializeField]`. `SingletonBase.Instance` auto-creates the manager via `new GameObject` + `AddComponent` when none exists in the scene, and an auto-created instance has no inspector value set. `detachFromParent` already defaults to `true`, which matters because `DontDestroyOnLoad` only works on root objects.

**2. Fix destroyed-subscriber detection — this becomes critical with multiple scenes**

`Publish` currently does:

```csharp
var target = subscription.TargetReference.Target;
if (target == null) { /* mark for removal */ }
```

`TargetReference.Target` is typed `object`, so `target == null` is plain reference equality and **bypasses Unity's overloaded `==`** that reports destroyed objects as null. A destroyed-but-not-yet-collected MonoBehaviour therefore still gets invoked, throwing `MissingReferenceException` into the per-subscriber catch and logging an error on every publish.

In a single scene this is rare enough to go unnoticed. Multi-scene it fires on every transition, because each scene change destroys every subscriber in the outgoing scene. Fix:

```csharp
if (target == null || (target is UnityEngine.Object uo && uo == null))
```

**3. Restore the base `OnDestroy` call**

`EventManager.OnDestroy()` overrides without calling `base.OnDestroy()`, so `SingletonBase._instance` is never cleared on teardown. Add `base.OnDestroy();` after `Clear();` — a stale static otherwise survives, which bites when domain reload is disabled in Play Mode settings.

### Scene layout and persistent managers

**Bootstrap → Lobby → Game → Results.**

`Bootstrap` creates the persistent managers and immediately loads `Lobby`. Anything that must outlive a scene transition is created here and nowhere else:

| Manager | Why it must persist |
|---|---|
| `EventManager` | subscriptions and publishing span all scenes |
| `RelayClient` | **the WebSocket must not drop** — re-establishing it would kick every phone back to the join screen |
| `RoomManager` | room code and player roster survive past the lobby |
| `ScoreManager` | scores accumulate across rounds and scenes |
| `TurnManager` | drives match flow, including which scene loads next |

`RelayClient` is the one that hurts most if missed. Dropping and reopening the socket on every scene load would disconnect all players mid-match, and the failure looks like a networking bug rather than a lifecycle one.

**Every scene-local subscriber** must subscribe in `OnEnable`/`Start` and call `EventManager.Instance?.UnsubscribeFromAllEvents(this)` in `OnDestroy`. With multiple scenes this is mandatory rather than good hygiene — and it is what fix #2 above backstops when it's forgotten.

---

## Scoring

Scoring is a base `int` raised through the event manager above. The game's job is to *publish well-named events at the right moments*; a `ScoreManager` subscribes and owns the point values, so tuning scoring never touches gameplay code.

New event types under `Assets/Scripts/EventTypes/SkipEvents/`:

| Event | Published when | Properties |
|---|---|---|
| `SkipOccurredEvent` | each successful water bounce | `SkipIndex`, `Speed` |
| `RingPassedEvent` | rock passes through a ring gate | `RingIndex` (0/1/2) |
| `ThrowCompletedEvent` | rock sinks or leaves play | `TotalSkips`, `Distance`, `PlayerId` |

Three ring gates in the lake, per Skip Skimmer's Score mode. Keep ring detection as simple trigger colliders on the rock's path — this is one of the few places stock Unity physics is a good fit, since it's detection only and doesn't feed the custom solver.

`TurnManager` owns turn order and round count; `RingGate` and `RockController` publish. None of them may compute score — that lives solely in `ScoreManager`.

---

## Spectator view

Idle phones show a live spectator view rather than a dead "waiting" screen. Unity broadcasts a low-rate (~4Hz, sufficient for text) state message to all non-active players:

```json
{ "type":"SPECTATE", "activePlayer":"AJ", "rockName":"Galaga Ship",
  "liveSkips":4, "liveDistance":31.2, "scores":{"AJ":120,"SAM":95} }
```

Render as: whose turn it is, the rock they drew, a live-incrementing skip counter, and the standings. Cheap to build, and it keeps the other 3–7 players engaged, which is most of what makes a party game work.

---

## Files

**Unity — `RockSkipping/`** (create with Unity 6000.3.10f1, URP, 3D)

> **Superseded — the project is on the built-in render pipeline, not URP.** No URP package is
> in `Packages/manifest.json` and `GraphicsSettings.asset` has no custom pipeline asset
> assigned. Anything below that assumes URP — shader authoring in particular — should be read
> against the built-in pipeline instead.

*Scenes:*
- `Assets/Scenes/Bootstrap.unity` — creates persistent managers, loads Lobby
- `Assets/Scenes/Lobby.unity`, `Game.unity`, `Results.unity`

*Copied in from FactoryTM (strip dead usings, normalize namespace, apply the three multi-scene fixes):*
- `Assets/Scripts/EventSystems/EventManager.cs`
- `Assets/Scripts/EventTypes/IEvent.cs`
- `Assets/Scripts/Utils/Singleton.cs`

*New event types, following the `PlayerHealthChangedEvent` pattern:*
- `Assets/Scripts/EventTypes/SkipEvents/SkipOccurredEvent.cs`
- `Assets/Scripts/EventTypes/SkipEvents/RingPassedEvent.cs` — **NOT BUILT**; nothing publishes
  it, which is why `ScoreManager.pointsPerRing` is currently dead code
- `Assets/Scripts/EventTypes/SkipEvents/ThrowCompletedEvent.cs`
- `Assets/Scripts/EventTypes/MatchEvents/TurnStartedEvent.cs`, `PlayerJoinedEvent.cs`

*New game code:*
- `Assets/Scripts/Managers/Bootstrap.cs` — instantiates persistent managers, then loads Lobby
- `Assets/Scripts/Managers/ScoreManager.cs` — subscribes to skip events, owns all point values; persists
- `Assets/Scripts/Net/RelayClient.cs` — WebSocket connection, reconnect, dispatch
- `Assets/Scripts/Net/Messages.cs` — serializable DTOs mirroring the JSON contract
- `Assets/Scripts/Net/RoomManager.cs` — room code, player roster, join/leave
- `Assets/Scripts/Game/ThrowInput.cs` — `THROW` descriptor → launch parameters
- `Assets/Scripts/Game/AimPreview.cs` — `AIM` smoothing + predicted arc rendering
- `Assets/Scripts/Game/SkipPhysics.cs` — pure custom bounce solver
- `Assets/Scripts/Game/RockController.cs` — per-rock flight state; publishes skip events **only when live, never when predicting**
- `Assets/Scripts/Game/RockSpawner.cs` — picks a definition, rolls `RockStats` — **NOT BUILT**
- `Assets/Scripts/Game/RockDefinition.cs` — ScriptableObject — **NOT BUILT**
- `Assets/Scripts/Game/RingGate.cs` — trigger, publishes `RingPassedEvent` — **NOT BUILT**
- `Assets/Scripts/Game/TurnManager.cs` — turn order, rounds, UI state; publishes match events, computes no score
- `Assets/Scripts/Game/SkipTuning.cs` — ScriptableObject of global tuning constants
- `Assets/Scripts/UI/LobbyUI.cs`, `HudUI.cs`, `QrCodeGen.cs`

**Controller — `controller/`** (start from `PartyGameNonC# Code\ServerHtml.Html`)
- `index.html`
- `js/sensors.js` — permission, gravity removal, orientation compensation, calibration
- `js/throw.js` — state machine, AIM streaming, THROW descriptor
- `js/net.js` — WebSocket client with reconnect
- `js/ui.js` — join screen, turn prompts, throw feedback, spectator view

**Relay — `relay/`** (evolve from `PartyGameNonC# Code\server.js`)
- `server.js`, `package.json`, ~~`fly.toml`~~ *(deleted with the Fly deployment; the relay is
  started by `deploy/local-https.ps1` now)*

**Unity WebSocket library:** use **NativeWebSocket** (MIT, free). Unity has no built-in WebSocket client for native builds. `System.Net.WebSockets.ClientWebSocket` would also work on desktop and is dependency-free, but NativeWebSocket is simpler and keeps a WebGL build path open if that's ever wanted.

> **Superseded — the code went the other way.** `RelayClient` uses
> `System.Net.WebSockets.ClientWebSocket`, and the deviation is documented at
> [RelayClient.cs:25](Assets/Scripts/Net/RelayClient.cs:25): it ships with .NET, so it adds no
> dependency. The cost is that `ClientWebSocket` is thread-affine for sends, which is why all
> sends funnel through a single queue. Swapping to NativeWebSocket remains the move if a WebGL
> build is ever wanted.

---

## Milestones

### M0 — Infrastructure spike
Proves the two risky things first: HTTPS sensors on a real iPhone, and relay round-trip.

Scaffold the Unity project with the four scenes (Bootstrap/Lobby/Game/Results) and copy in the three event system files — stripping dead usings, normalizing the namespace, and applying all three multi-scene fixes above. Confirm a publish/subscribe round-trip survives a `Bootstrap → Lobby → Game` transition before building anything on it.

Harden `server.js`. The existing prototype has real defects to fix:
- Room codes generated **server-side**, excluding ambiguous characters (no `O`/`0`, `I`/`1`)
- `JSON.parse` wrapped in try/catch — currently *any* malformed message crashes the entire server
- Reject joins to non-existent rooms — the current client optimistically shows the game screen because the server stays silent
- Clean up rooms on host disconnect (currently leaks forever)
- Player cap, heartbeat ping to detect dead sockets, per-socket rate limiting
- Route `AIM`/`THROW` host-ward and `SPECTATE` player-ward

Deploy relay to Fly.io, controller to Cloudflare Pages.
*(Superseded: neither happened. The relay serves the controller itself and now runs on the host
PC — [HostingPlan.md](HostingPlan.md).)*

**Done when:** a real Android phone loads the HTTPS page and logs live sensor values to a Unity console via the relay. (iOS deferred — no device.)

### M1 — Motion pipeline + aim preview
Sensor normalization, calibration, state machine, both message channels. `AimPreview` smoothing with the predicted arc.

**Done when:** an on-screen debug readout in Unity shows sane, repeatable values from an **Android** phone — the same gesture twice produces descriptors within ~10% of each other — and the TV arc tracks phone movement smoothly with no visible stutter. iOS branches are written and commented `// UNVERIFIED (iOS):`, but are not part of the done criteria.

### M2 — Skip physics + rocks 🟡 IN PROGRESS
Pure solver, `SkipTuning`, `RockDefinition` assets for basic + all four specials, spawner with stat rolling, one lake scene, three ring gates, follow camera.

**Done when:** a good throw yields 5+ skips, a bad throw sinks, all four specials feel distinguishable, and the difference is legible from the couch.

**Built:** the solver, `SkipTuning`, a single rock, the follow camera, and a placeholder lake.
**Still missing:** `RockDefinition`/`RockSpawner` and the four specials, `RingGate` and ring
scoring, and an authored lake scene to replace the runtime placeholder geometry. **This is the
only milestone with outstanding work.**

**The retention model changed during this milestone.** The fixed `SpeedKeptSoft`/`SpeedKeptHard`
pair described elsewhere in this document is gone. A single `DynamicSpeedRetention` is now
calculated once per throw from power and entry angle and applied on every bounce
([SkipPhysics.cs:208](Assets/Scripts/Game/SkipPhysics.cs:208)). This removed the emergent
22-skip ceiling and made the number of skips a function of throw quality rather than of the
tuning constants. Background: [history/feature_documentation.md](history/feature_documentation.md),
with the investigation that prompted it in [history/research_log.md](history/research_log.md).

### M3 — Turn-based multiplayer + scoring ✅ DONE, VERIFIED
Lobby with QR code, 2–8 players, turn order, rounds, spectator view, `ScoreManager` subscribing to the skip events.

**Done when:** four phones complete a full match, scores accumulate correctly from rings/distance/skips, and idle phones show live spectator state.

**Met, and exceeded on player count — verified with a real 5-player session across iOS and
Android**, covering scoring, turn rotation, late-join and lobby-rejoin. Note the one caveat the
criterion cannot cover: scoring from *rings* was not exercised, because `RingGate` is still
unbuilt (M2). Distance and skips were. **This milestone was completed ahead of M2.**

### M4 — Polish 🟡 PARTIALLY STARTED, EARLY
Water shader and ripples, splash/skip VFX and SFX, camera framing, controller haptics **on Android only** (iOS vibration is unreliable — do not design around it), reconnect handling, results screen.

**Already built, ahead of the milestone sequence:** the water and ripple shader, camera framing,
and reconnect handling ([deploy/RECONNECT-TEST.md](../deploy/RECONNECT-TEST.md)). VFX/SFX and
haptics are not started.

---

## Verification

**Test on real hardware from M0 onward.** Desktop browser device emulation does **not** reproduce mobile motion behaviour — notably it reports `acceleration` values that no real iPhone provides, so emulation actively hides the biggest iOS pitfall.

- **Current device matrix: Android (Chrome) only.** Test on it at every milestone rather than at the end. See Test device policy for what this does and does not let us claim.
- **Repeatability check (runnable now):** the same physical gesture performed twice on the same Android device must produce descriptor values within roughly 10% of each other. Noisy or drifting output means the gravity filter or calibration is wrong — fix it before building anything on top.
- **Cross-platform parity check (deferred, needs an iPhone):** the same gesture on iPhone and Android must agree within roughly 10%. Until this runs, iOS is designed-for but unproven; do not report it as working.
- **Aim preview under stress:** throttle the connection (Chrome DevTools → Slow 3G) and confirm the preview degrades to sluggish-but-smooth, never jittery, and that throws still register correctly. This is the key test that the two-channel split is working.
- **Predict/live parity:** the previewed arc at nominal power must match an actual throw at that same power. Divergence means preview and live have drifted onto different code paths — they should be calling the same `SkipPhysics.Step`.
- **Relay resilience:** kill the relay mid-match and confirm both Unity and phones reconnect and resume. Send deliberately malformed JSON and confirm the server survives.
- **Latency:** measure grip-to-preview-response. Target under 100ms on the same network; if exceeded, suspect relay host region before code.
- **Scoring integration:** with `EventManager.DebugLoggingEnabled = true`, verify each event publishes exactly once per occurrence — ring gates in particular are prone to double-firing if the rock's collider clips the trigger across two frames. `Publish` catches subscriber exceptions into `Debug.LogError`, so watch the console rather than assuming silence means success.
- **Preview must not score:** aim continuously for 30 seconds without releasing, and confirm the score stays at zero and no `SkipOccurredEvent` is logged. A rising score here means `SkipPhysics.Step` is publishing, or `RockController` isn't distinguishing predict from live.
- **Scene transitions:** run a full Lobby → Game → Results → Lobby loop twice and confirm no `MissingReferenceException` appears in the console, the socket never reconnects, and scores carry across correctly. Repeated exceptions on transition mean the destroyed-subscriber fix didn't land; a phone bounced to the join screen means `RelayClient` isn't persisting.
- **Physics tuning:** if players can't tell why a throw failed, add a post-throw readout on the TV ("too steep!", "too soft!") — the original Skip Skimmer leans heavily on this feedback.

**Local development loop:** tick **Start Local Services** on `Bootstrap` to run the relay on this machine (or start it by hand with `node relay/server.js`), and point Unity at it with a `relay-url.txt` containing `ws://localhost:3000` — both steps are needed. That origin is not secure, so motion sensors will not work on it, which makes this a desktop-only loop. **Real-phone testing goes through `pwsh ./deploy/local-https.ps1`**, which serves the same relay from this machine over HTTPS on a borrowed certificate — there is no deployed relay any more, and no tunnel to run either ([deploy/README.md](../deploy/README.md)).
