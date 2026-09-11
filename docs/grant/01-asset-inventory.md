# Asset inventory

What exists today, with the evidence to cite. Line counts and paths re-verified against the
tree on 2026-09-11 (previously 2026-09-07 — the codebase moves fast; re-check before every
submission). Everything in this file is checkable by a reviewer who clones the repo,
which is the point.

## Scale

| Component | Path | Size |
|---|---|---|
| Unity game and host logic | `Assets/Scripts/` | ~15,300 lines C#, 4 scenes |
| Networking layer (Unity side) | `Assets/Scripts/Net/` | 3,490 lines across 11 files |
| Relay / DNS / captive portal | `relay/` | `server.js` 1,024, `dns.js` 409, `funnel.js` 168 |
| Phone controller web app | `controller/` | `app.js` 899, `sensors.js` 605, `throw.js` 640, `net.js` 248, `identity.js` 83 |
| Sensor math tests | `controller/test-sensors.js` | 1,333 lines |
| Relay integration tests | `relay/test-*.js` | 48 tests across 4 files (captive 5, dns 11, funnel 19, relay 13) |
| Design and failure records | `docs/` | ~7,400 lines (excluding this grant kit and docs/archive/) |
| Throw dataset | `rock-throws.csv` | 265 logged throws, 11 columns |

237 commits, 2026-07-29 to 2026-09-11 — 223 by the developer (across four git identities used
on different machines) and 14 authored directly by Claude Code under their direction.

## The offline network stack

`relay/server.js` is one Node process serving five roles at once. That consolidation is
deliberate and is what makes the offline story work — the page and the WebSocket share one
origin by construction, so they cannot drift onto different hosts:

- static HTTPS server for `controller/`
- WebSocket relay, room-code keyed, 8 players per room, 4 KB message cap, 60 msg/s rate
  limit, 30 s heartbeat, 60 s host grace period so a host blip does not eject a room
- `/qr` endpoint (`qrcode-generator`) — Unity fetches the matrix rather than generating it
- authoritative DNS responder for `play.skiprocking.ca` on the hotspot subnet (`relay/dns.js`)
- captive-portal probe responder (`relay/funnel.js`) that deliberately reports "no internet"
  until the player has the page open, so the phone OS pops its portal sheet as a nudge

Host side: `Assets/Scripts/Net/LocalServices.cs` (574) spawns and supervises the relay child
process; `HotspotService.cs` (610) drives Windows Mobile Hotspot via `hotspot/hotspot-control.ps1`;
`CertificateProvider.cs` (469) fetches and caches the certificate with a seed copy in
StreamingAssets as the floor; `ChildProcessGroup.cs` / `ProcessUtil.cs` handle child-process
lifetime so a crashed session does not wedge the next one.

**The finding worth showing a reviewer** is `docs/Decision-OfflineHosting.md`. Windows Mobile
Hotspot was believed unable to start without an internet connection. It can — the blocker was
`GetInternetConnectionProfile()` returning null by definition when offline, where
`GetConnectionProfiles()` enumerates every profile including ones carrying no connectivity.
Tethering starts from a profile whose adapter is *physically unplugged*. The document records
the measurement, the four-line fix, and an unusually candid post-mortem of the hours spent
testing on phones before checking the host.

## Motion sensor normalization

`controller/js/sensors.js` — the part a developer cannot reproduce in a weekend:

- **Quaternion-only orientation.** The Euler version was broken in the exact pose the game is
  played in: `DeviceOrientationEvent` reports intrinsic Z-X'-Y'' angles, and `beta = 90`
  ("phone upright, screen facing you" — how you hold a phone aimed at a TV) sits *on* the
  gimbal singularity, not near it. The delta quaternion goes on the wire as four floats and is
  applied verbatim; it is never decomposed.
- **Calibration at arm time.** `MotionSource.calibrate()` captures a zero on the tap that arms
  a throw. iOS and Android disagree on absolute heading; a delta from a captured zero agrees.
  `webkitCompassHeading` is deliberately ignored.
- **Three-tier gravity removal**, ordered by artifact: hardware `event.acceleration` → exact
  subtraction via `gravityInDeviceFrame(q)` → low-pass `GravityFilter` fallback. The cost of
  getting this wrong is quantified in-source: filter lag leaks up to ~19.6 m/s² of phantom
  user acceleration during a fast wrist rotation, which was collapsing hard and soft throws
  into the same reading. Which tier produced a sample is reported per-sample as `gravitySource`.
- **Gyro fusion** between orientation events (`_advanceFused`) to remove an aim "staircase";
  `MAX_SAMPLE_GAP_MS = 250` refuses to integrate across a backgrounded tab.
- **Screen-rotation compensation** (`rotateXY(-screen.orientation.angle)`), and spin defined as
  `rotationRate.alpha` only — the wrist flick — after an unsigned three-axis magnitude proved wrong.
- **One documented basis change**, host side: `Assets/Scripts/Game/Physics/PhoneAttitude.cs`,
  `new Quaternion(-x, -z, -y, w)`, with the standing rule "if a rotation comes out mirrored,
  fix it here — never with a negation at a call site."

Reported by the developer as working across multiple iPhone generations and multiple Samsung
and Pixel generations — but **not yet checkable evidence**: [../DeviceMatrix.md](../DeviceMatrix.md)
is a template with every row still `TODO`. Filling it in is a tracked Week-1 task
([09-fundability-roadmap.md](09-fundability-roadmap.md)); cite it only once real rows exist.

## Gesture recognition and the two-channel protocol

`controller/js/throw.js` is a state machine (IDLE/ARMED) with tunable thresholds exposed as
`ThrowTuning` (`swingAccel 14`, `stopAccel 4`, `stopSamples 3`, `maxSwingMs 900`,
`spinEnvelopeTauMs 250`, plus the accidental-throw guard added 2026-09-10 — `armGraceMs 250`,
`swingOnsetSamples`, `minThrowSwingSpeed`; `minValidPeakAccel` was removed as dead code, see
`controller/js/throw.js:38-42`). Release is detected as "accelerate hard, then stop
dead" — the stop *is* the release, and orientation is sampled raw at that instant.

The channel split is the transferable idea:

| Channel | Rate | Guarantee | Role |
|---|---|---|---|
| `AIM` | 20 Hz | lossy, sequenced, droppable | **cosmetic only** — "nothing in AIM may feed the simulation" |
| `THROW` | once per gesture | authoritative | computed from the whole buffered swing at release |

Smoothing lives on the host (`Assets/Scripts/.../AimPreview.cs`, frame-rate-independent
exponential smoothing), not on the wire. A `RingBuffer` sized 1.5 s at 60 Hz allocates nothing
mid-throw. Wire format is flat JSON for Unity's `JsonUtility` (`Assets/Scripts/Net/Messages.cs`).

A `SimulatedThrow` path lets sensorless desktop browsers take a seat, gated to devices that
have never emitted a motion event and labelled with `sampleCount: 0` so a weak gesture on a
real phone still counts as a fumble.

## Onboarding instrumentation

The relay tracks each device through ASSOCIATED → PAGE_OPEN → PLAYING and broadcasts
`DEVICE_FUNNEL` to the host, so the TV can say *where* a phone got stuck rather than that it
did. `docs/archive/plans/join-failure-reduction.md` (1,013 lines) is the design record. Onboarding
drop-off is the real product problem in phone-as-controller, and almost nobody measures it.

## Recorded negative results

Unusual for a solo project and worth citing directly:

- `docs/archive/Findings-DirectTransportFlapping.md` — a WebRTC DataChannel per phone was built, then
  removed. Once the relay moved onto the host PC, the direct transport was one hop either way
  and bought nothing but ~510 lines and a native dependency.
- `docs/archive/plans/remove-direct-transport.md` — plain LAN mode removed, not deprecated, with the
  certificate reasoning that forced it.
- `docs/Decision-OfflineHosting.md` — Wi-Fi Direct evaluated and rejected: it works as a radio
  but leaves port 53 free and builds no DNS proxy, so phones cannot resolve a name, so it
  cannot serve an HTTPS origin without also shipping a DHCP server.
- `docs/archive/Findings-DnsAudit.md`, `docs/archive/Findings-AimPreviewAfterRejoin.md`.

## Dataset

`rock-throws.csv` — 265 logged throws from real phones and tablets:
`timestamp, matchSeed, rockId, player, swingSpeed, spinRate, pitchDeg, rollDeg, yawDeg, skips, distance`.
Small, but empirical, and releasable openly as a gesture-recognition baseline.
