# Controller stream

**Source:** `controller/pad.html`, `controller/js/pad.js`, `controller/js/motion-sample.js`,
`relay/pad-hub.js`, the `/pad` and `/api/pads` routes in `relay/server.js`,
`controller/monitor.html`. Built for issue #2.

## What it owns

Getting each phone's raw motion readings to the PC at the phone's own rate, giving each phone a
controller slot, and publishing the samples for whatever turns them into controller input (the DSU
server, #3). It does not own sensor normalisation (that is
[`MotionPipeline.md`](MotionPipeline.md)) or hosting and HTTPS
([`HostingAndCertificates.md`](HostingAndCertificates.md)).

## How it works

```
devicemotion ──▶ makeMotionMessage ──▶ WebSocket /pad ──▶ pad hub ──▶ 'sample' event (slot, sample)
 (phone page)     (units for DSU)       (same server       (slot,      └▶ /api/pads ▶ monitor.html
                                          as the game)      checks)
```

- **Phone.** After the Start tap (iOS only grants motion inside a user gesture), every
  `devicemotion` event becomes one `MOTION` message
  ([`controller/js/pad.js:102`](../../controller/js/pad.js)):
  `{ type, seq, ts, accel:[x,y,z], gyro:[x,y,z] }`. Units are already DSU's: acceleration in g with
  gravity included, rotation in deg/s, timestamp in whole microseconds of absolute time
  (`performance.timeOrigin + event.timeStamp`, [`pad.js:63`](../../controller/js/pad.js)).
  Readings come only through `readAcceleration` and `readRotationRate` in `sensors.js`.
- **PC.** A WebSocket opened on the path `/pad` goes to the pad hub before any game-room code runs
  ([`relay/server.js:697`](../../relay/server.js)). The hub gives it the first free slot of four,
  replies `PAD_ACCEPTED` with the slot, or `PAD_REJECTED` and closes it with 1013 when all four
  are taken.
- **Samples.** Each message is validated at the boundary (`parseMotionMessage`,
  [`relay/pad-hub.js:28`](../../relay/pad-hub.js)), counted, and emitted as
  `events.on('sample', (slot, sample) => …)`. A jump in `seq` counts the gap as lost
  ([`pad-hub.js:108`](../../relay/pad-hub.js)); a late, older sample is still emitted but never
  replaces the newest as `latest`.
- **Readout.** `GET /api/pads` returns one entry per slot (rate over the last second, counts,
  latest sample). It answers only loopback, because it names each phone's address.
  `monitor.html` on the PC polls it four times a second.
- **Latency probe.** A `PING` with a phone time `t` gets `PONG` echoing `t`, for #8.

## Invariants

- **Units are converted on the phone, once.** Nothing on the PC rescales accel or gyro. The DSU
  server can copy the numbers straight into packets.
- **Rotation rates: `alpha` is x, `beta` is y, `gamma` is z,** right-hand rule, so
  `gyro = [alpha, beta, gamma]` ([`motion-sample.js:45`](../../controller/js/motion-sample.js)).
  This is what browsers implement: the current W3C spec (§6.3.2), Chromium's
  `device_motion_event_pump.cc` (gyro x, y, z into `alpha`, `beta`, `gamma`) and WebKit's
  `DeviceMotionClientIOS.mm` (CoreMotion x, y, z, likewise). The older spec text and MDN say
  `beta` about x, `gamma` about y, `alpha` about z, and the page followed that until 2026-10-10;
  every axis came out crossed (#30). `tools/check-gyro-axes.js` checks a capture against gravity,
  and the test "rotation rates map alpha→x" guards the mapping.
- **Gravity's sign comes from the orientation angles, not the browser (#34).** Safari on iPhone
  mirrors `accelerationIncludingGravity` (WebKit passes CoreMotion's `userAcceleration + gravity`
  through, `WebCoreMotionManager.mm`; W3C public-geolocation, 2014). As Rock Skipping does, the
  page takes "up" from `deviceorientation` (`gravityInDeviceFrame`, `sensors.js`) and
  `GravitySign` (`motion-sample.js`) flips the reading after a steady run of near-still readings
  point the other way. No browser is named. The phone's status panel shows "Gravity: flipped".
- **`/pad` sockets never join game rooms,** and game sockets never reach the pad hub. The rate
  budgets differ: the game's AIM cap is 60 messages a second, the pad cap is 400
  ([`pad-hub.js:13`](../../relay/pad-hub.js)).
- **A pad socket sets `ws.meta.isAlive`** ([`pad-hub.js:70`](../../relay/pad-hub.js)), because the
  server's heartbeat sweep reads it on every client.

## Traps

- **Motion needs a secure context.** On the PC itself `http://localhost` counts as secure, which is
  how the headless check runs. A real phone needs the page over HTTPS. Rock Skipping's Unity host
  starts the relay with `TLS_CERT`/`TLS_KEY` on Play, and `deploy/verify-stack.ps1 -KeepRunning`
  starts it the same way without Unity (a verification script, not a product launcher; it expects
  the hotspot and the one-time setup to be in place already). This repo has no launcher of its own
  yet.
- **Headless checks use synthetic events.** `tools/verify-pad-stream.js` dispatches constructed
  `DeviceMotionEvent`s at 60 Hz. It proves the page, socket, hub and readout path, not the phone's
  real sensor rate. Real rates come from real phones.
