# Latency & Correctness Fix Plan

## Context

Phone rotation lags the in-game aim preview, and the rock is thrown toward a direction that
doesn't match where the player released. A full audit of all three tiers (controller web app,
Node relay, Unity host) found three compounding latency causes and several correctness bugs.
The throw itself is computed from the freshest sensor sample (≤17 ms old) — **the throw is
right and the preview is late**; the visible "snap" at release is the accumulated pipeline
latency being revealed. Fix the latency and most of the release mismatch disappears; the
remaining mismatch cases are real sensor-math bugs fixed in Phase 2.

Root causes, ranked:

1. **Unity intake is capped at ~1 message/frame.** `RelayClient.ReceiveLoopAsync` is started
   from the main thread with no `Task.Run`/`ConfigureAwait(false)`, so every
   `await socket.ReceiveAsync` resumes via UnitySynchronizationContext. The phone streams AIM
   at 60 Hz; vsync=1 caps Unity at 60 fps → zero headroom, so any hitch adds backlog that
   **never drains**. Lag grows over a grip. This is the dominant cause.
2. **The phone never drops stale frames.** `net.js send()` ignores `ws.bufferedAmount`; when
   the radio stalls, stale AIM frames queue unboundedly and replay in order (TCP).
3. **AIM is sampled by a timer, not the sensor event** — up to 16.7 ms staleness + mobile
   timer jitter per frame.

The cloudflared tunnel adds a fixed RTT (~40–160 ms round trip) that cannot be removed (iOS
sensors require a secure context, so the tunnel stays per the launch architecture). Phase 1.6
adds measurement so app lag and tunnel cost can be told apart.

Decision (confirmed with Ayden): **hold-to-aim / release-to-throw is the default gesture.**

---

## Phase 0 — Baseline (before touching code)

Record: (a) F1 HUD "Aim Hz" during a 10 s grip, (b) subjective lag on slow rotation,
(c) whether the arc drifts progressively further behind over a long grip (the backlog
signature). This is the before-picture for verification.

---

## Phase 1 — Kill the latency (ordered; 1.1 is the dominant fix)

### 1.1 `Assets/Scripts/Net/RelayClient.cs` — receive loop off the main thread

- Line 106: `_ = ReceiveLoopAsync(_socket, _cts.Token);` → `_ = Task.Run(() => ReceiveLoopAsync(_socket, _cts.Token));`
- Line 130: add `.ConfigureAwait(false)` to `await socket.ReceiveAsync(...)`.
- **Line 149 (critical interaction):** `_nextRetryTime = Time.time + _retryDelay;` now runs on
  a pool thread and `Time.time` throws off the main thread. Replace with
  `private volatile bool _receiveLoopEnded;` set to `true` there, and at the top of
  `Update()` add:
  ```csharp
  if (_receiveLoopEnded) { _receiveLoopEnded = false; _nextRetryTime = Time.time + _retryDelay; }
  ```
  The `Debug.LogWarning` calls in the catch blocks are thread-safe and stay.
- **Leave `ConnectAsync` alone** (no `ConfigureAwait(false)` there): its catch uses
  `Time.time`/`Mathf.Min` and it's only ever called from `Start`/`Update` (main thread).
- `SendRawAsync` lines 219 & 224: add `.ConfigureAwait(false)` to both awaits.
- Add `[DefaultExecutionOrder(-100)]` on the class so its `Update` drain runs before
  `AimPreview.Update` (no MonoManager order exists today → a wrong ordering silently costs
  16 ms every frame).
- In the `Update` drain, wrap `Dispatch(json)` in `try { } catch (Exception e) { Debug.LogException(e); }`
  — today one bad message/subscriber aborts the rest of the queue (parses at lines 172–202
  are unguarded).
- Add `public int PendingInbound => _inbound.Count;` (used by 1.6).

### 1.2 `ProjectSettings/ProjectSettings.asset` — `runInBackground: 0` → `1` (line 86)

An unfocused window currently stops draining the socket entirely.

### 1.3 `controller/js/net.js` — latest-wins AIM send (land BEFORE 1.4)

Add to `RelayConnection`:

```js
/** Latest-wins send for cosmetic streams: skipped when the socket is congested. */
sendIfClear(payload) {
  if (this.socket?.readyState !== WebSocket.OPEN) return false;
  if (this.socket.bufferedAmount > 0) return false; // never queue stale AIM behind a stalled radio
  this.socket.send(JSON.stringify(payload));
  return true;
}
```

`controller/js/app.js:83`: `onAim` uses `sendIfClear`. THROW keeps using `send()` — it must
always transmit.

### 1.4 `controller/js/throw.js` + `controller/js/sensors.js` — event-driven AIM

- `sensors.js` `MotionSource`: add `this.onSample = null;` in the constructor; at the end of
  `_onMotion` (after `this.buffer.push(sample)`, line 452): `this.onSample?.(sample);`
- `throw.js` `grip()`: delete the AIM send from the 16.7 ms interval; instead
  `this.motion.onSample = (sample) => { const p = buildAimPayload(sample, ++this.seq); if (p) this.onAim(p); };`
  Keep a slow timer (e.g. 250 ms) solely for the TAP `maxArmMs` expiry check, logic verbatim.
- `release()` and `cancel()`: clear `this.motion.onSample = null` (put it in/next to `_stopTimer`
  so both paths hit it).
- Remove the now-dead `aimHz`/`aimIntervalMs` option; delete the `aimHz:` args in
  `test-sensors.js` lines 371, 386, 398, 412.
- The 1.3 gate is the flood control for 120 Hz iPhones — that's why 1.3 lands first.

### 1.5 `relay/server.js` — backpressure + single stringify

- Top of file: `const LATEST_WINS_TYPES = new Set(['AIM', 'SPECTATE']);` and
  `const BACKPRESSURE_BYTES = 4096;`
- `relayToHost` (line ~201): before sending, `if (LATEST_WINS_TYPES.has(data.type) && room.host.bufferedAmount > BACKPRESSURE_BYTES) return;`
- `relayToPlayers`: hoist `const json = JSON.stringify(data);` above the fan-out loop, add a
  `sendRaw(ws, json)` helper mirroring `send()`, apply the same latest-wins drop per recipient.

### 1.6 Latency instrumentation

- `relay/server.js` PING handler (line ~365): echo the timestamp — `send(ws, { type: 'PONG', t: data.t });`
- `app.js`: every 2 s while joined, `connection.send({ type: 'PING', t: performance.now() })`;
  on PONG compute `rttMs = performance.now() - message.t`; show in the debug panel. This
  isolates the fixed tunnel cost from app-side lag.
- `Assets/Scripts/Net/RelayDebugHud.cs` (~line 120): add `queued {_client.PendingInbound}` to
  the Aim line. Pre-fix it climbs during a grip; post-fix it should sit at 0–2.

---

## Phase 2 — Throw-direction correctness

(Phase 1 already fixes the main "release ≠ throw" symptom — the arc was stale, the throw was right.)

### 2.1 `controller/js/sensors.js` — `referenceHeading` can latch null (aim pinned to 0)

`calibrate()` (line 349) copies `this.headingDeg` which is `null` before the first confident
orientation event → `dYaw` is hard-0 for the entire grip → rock always flies straight.
Fix: in `calibrate()`, if `headingDeg === null` set `this._awaitingReferenceHeading = true`;
in `_onOrientation`, whenever `headingDeg` becomes non-null (first-fix branch, line 379–382)
and the flag is set, latch `this.referenceHeading = this.headingDeg` and clear the flag.
Clear the flag in `calibrate()` when a heading already exists.

### 2.2 `controller/js/sensors.js` — heading freezes mid-swing (the mathy one)

Today: integration is gated on `cos(beta) >= 0.35` (lines 418–421), so heading **freezes**
when the phone pitches past ~70° mid-swing — the release reports a pre-swing heading. Worse,
`headingRateFromGyro` divides by `cos(beta)` (~2.9× amplification at the gate edge).

Replace `headingRateFromGyro` (lines 79–87) with the world-frame projection — valid at every
pitch, no gate, no division:

```js
export function headingRateFromGyro(beta, gamma, rotationRate) {
  const b = (beta * Math.PI) / 180;
  const g = (gamma * Math.PI) / 180;
  const cb = Math.cos(b), sb = Math.sin(b), cg = Math.cos(g), sg = Math.sin(g);
  // heading rate = -(R · ω)·ẑ with R = Rz(α)·Rx(β)·Ry(γ); alpha drops out of the z-row.
  return cb * sg * rotationRate.beta - sb * rotationRate.gamma - cb * cg * rotationRate.alpha;
}
```

Derivation (for the doc comment): deviceorientation α/β/γ are intrinsic Z-X'-Y'' Tait-Bryan;
`rotationRate` is deg/s in the device frame, ω_device = (rate.beta, rate.gamma, rate.alpha)
in (X, Y, Z) order; world yaw rate = (R·ω)·ẑ = z-row of `Rx(β)·Ry(γ)` · ω =
`(-cosβ·sinγ, sinβ, cosβ·cosγ)`·ω; negate because compass heading is clockwise-positive.
Sanity anchor: at β=γ=0 this yields `-rate.alpha`, identical to the old formula — and it
matches the old formula exactly at β=0 for any γ; the divergence is only at steep pitch,
where the old one amplified then froze.

In `_onMotion`: delete the `forwardLean` gate (lines 418–421) — always integrate when
`headingDeg !== null`; clamp the per-sample rate to ±1500 °/s. Keep the
`MIN_POINTING_CONFIDENCE` gate in `_onOrientation` (correction-only) unchanged — the
correction filter is what bounds long-grip drift now that `maxAimDeg` is 180.

### 2.3 `Assets/Scripts/Game/AimPreview.cs` — seq guard kills reloaded controllers

A page reload resets the phone's `seq` to 1 while Unity's `_lastSeq` stays high (only reset on
playerId change) → **every aim frame dropped forever**. Replace line 199:

```csharp
if (aim.seq <= _lastSeq)
{
    // A page reload resets the phone's counter; a merely-stale frame is only slightly
    // behind. A large backward jump means a new session — resync.
    if (_lastSeq - aim.seq < 1000) return;
    _lastSeq = -1;
}
```

### 2.4 Deferred — gyro-filtered pitch/roll

`dBeta`/`dGamma` still carry the fused-orientation lag (tens of ms) that yaw no longer has.
Complementary-filter them with `rotationRate.beta`/`.gamma` the same way as yaw — but only
after 2.2 is validated on hardware. Attack angle is smoothed Unity-side; this is a
tens-of-ms polish, not the 180°-class failure yaw had.

---

## Phase 3 — Robustness

**Unity**
- `RelayClient.cs` OnDestroy (line 240): delete `_sendLock.Dispose();` — it races in-flight
  sends; `SemaphoreSlim` needs no dispose unless `AvailableWaitHandle` is used (it isn't).
- `RelayClient.cs`: handle `ROOM_CLOSED` — log, clear `RoomCode`, re-send CREATE_ROOM. Land
  **after** the relay change below, else CREATE_ROOM bounces with ALREADY_REGISTERED.
- `Assets/Scripts/Managers/ScoreManager.cs` (line 25): `_pendingThrowPoints` is a single
  global bucket — zero it on turn start (subscribe to the turn-started event) so an aborted
  throw's points can't leak into the next player's total.
- `Assets/Scripts/Managers/TurnManager.cs` (line 78): `ThrowCompletedEvent → NextTurn()` must
  check the event's PlayerId equals `ActivePlayer` — otherwise an active player disconnecting
  mid-flight advances the turn twice and the next player loses their throw.
- `Assets/Scripts/Game/AimPreview.cs` line 266: ghost pitch must match the physics attitude —
  use `-(_curAttack + tuning.AttackAngleOffsetDeg)` (physics is `attack + 20°`, SkipPhysics.cs:73).
- `AimPreview.cs` line 249: replace `_line.SetPositions(_path.ToArray())` with a reused,
  grown-on-demand `Vector3[]` buffer (set `_line.positionCount` first) — currently ~6 KB of
  garbage per frame on the input path.

**Relay (`relay/server.js`)**
- `closeRoom` (lines 102–111): notify the host (`ROOM_CLOSED`) and reset
  `room.host.meta.role/roomCode` to null so its re-sent CREATE_ROOM succeeds (skip when the
  host socket itself is the reason for closure). Today an idle sweep bricks the host forever.
- `handleJoinRoom` (line 139): if the connection already has a player registration under a
  different (room, name), remove the old slot first and send `PLAYER_LEFT` to that room's host
  — otherwise ghost slots accumulate toward `MAX_PLAYERS_PER_ROOM`.
- Static path check (line ~251): `resolved === STATIC_DIR || resolved.startsWith(STATIC_DIR + path.sep)`
  — the bare `startsWith` accepts sibling dirs like `controller-evil`.
- `relayToPlayers` (line 214): run `targetPlayerId` through `sanitizePlayerId` so a >16-char
  name matches the truncated stored key.

**Controller**
- `app.js` join wait-loop (lines 277–282): keep the interval handle, `clearInterval` any
  existing one before starting a new one, and add a ~10 s deadline that shows "Could not reach
  the server" and re-enables the join button. Today an unreachable server leaks intervals and
  a second press double-joins.
- `app.js` `onStatus`: on `RECONNECTING`/`ERROR` call `thrower.cancel()` (kills the aim stream
  on a dead socket) and actually render the `ERROR` state (currently never displayed).
- `controller/package.json`: delete the `"serve": "node serve.js"` script — the file doesn't
  exist; the relay serves the page.

---

## Phase 4 — Docs, comments, gesture alignment

- **Gesture (decided): HOLD is the default.** Keep `app.js:51–53` as-is; change
  `throw.js:240` constructor default from `ThrowGesture.TAP` to `ThrowGesture.HOLD` so the
  two files agree. Tap stays reachable via `?gesture=tap`.
- Stale "~20Hz" comments → 60 Hz / "sensor-rate, event-driven": `throw.js:6`,
  `AimPreview.cs:13,151`, `RelayDebugHud.cs:60–61,120` ("(target 20)"), `Messages.cs:66`.
- `throw.js:34` JSDoc: sign default is `+1`, not `-1`.
- `throw.js:64` `maxAimDeg = 180`: add one comment noting drift is now bounded by the
  correction filter (2.2), not by clipping.

---

## Verification

**Latency (before vs after)**
1. F1 HUD: "Aim Hz" ≈ 60 during a grip; new "queued" count holds at 0–2 for a full 10 s grip
   (pre-fix it climbs continuously — that's the smoking gun of 1.1).
2. Phone debug panel: RTT readout separates the fixed tunnel cost (~40–160 ms round trip)
   from app lag. Everything above the tunnel floor should vanish.
3. Feel test: slow steady rotation → arc trails only by the intentional ~35 ms `smoothTime`;
   a long grip shows no progressive drift; release no longer visibly snaps.

**Controller tests** — `node controller/test-sensors.js` (imports the real modules; follow the
existing `globalThis.window = { screen: {} }` stub pattern). Update: remove `aimHz` args. Add:
1. Calibrate-before-first-heading: `calibrate()` before any orientation event, then a
   confident `_onOrientation` → `referenceHeading` latches; `dYaw` becomes non-zero as the
   heading moves (regression test for 2.1).
2. Swing through β=90°: sweep β 0→120° with constant `rotationRate.alpha` → `headingDeg`
   keeps integrating (no freeze), no >2× amplification near 70–90° (regression for 2.2).
3. `headingRateFromGyro` numeric check: finite-difference the yaw of R(t) under known ω at
   β ∈ {0°, 45°, 85°, 90°, 95°}; pin the sign anchor (β=γ=0 → `-rate.alpha`).
4. Event-driven aim: grip → three `onSample` calls → `onAim` seq 1,2,3; `release()`/`cancel()`
   clears `onSample`.
5. `sendIfClear`: stub socket `bufferedAmount` 0 vs >0 → sends vs drops.

**Unity/relay manual checks**: controller page reload mid-session → arc resumes (2.3);
temporarily lower `ROOM_IDLE_TIMEOUT_MS` → host survives room close and re-creates (Phase 3);
kill/restart relay while Unity runs → reconnect works (exercises the 1.1 retry-flag path);
active player disconnects while a rock is in flight → next player keeps their turn.

**Ordering constraints**: 1.3 before 1.4 (gate before flood); the `Time.time`→flag change
lands atomically with the `Task.Run` change in 1.1; relay host-meta reset before Unity's
ROOM_CLOSED handler; doc updates last.
