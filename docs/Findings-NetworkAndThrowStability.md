# Findings — network pipeline and throw gesture (2026-09-10)

**Status:** findings only. **No fixes proposed, by request.** Branch `ArcadePhysics` @ `9d6bbe3`.

**Method:** a static read of the whole input path, compared against `git log` for 2026-08-20 →
2026-09-10. Files read in full:

- `relay/server.js`
- `controller/js/{app,net,throw,sensors,identity}.js`
- `Assets/Scripts/Net/{RelayClient,Messages}.cs`
- `Assets/Scripts/Game/{AimPreview,RockLauncher,RockController}.cs`
- `Assets/Scripts/Managers/{TurnManager,ScoreManager}.cs`
- `rock-throws.csv` (latest 40 rows)

**What this cannot show:** nothing was reproduced on hardware, and no test suite was run.

- **CONFIRMED** means the path exists in the code exactly as described.
- **PLAUSIBLE** means the mechanism exists and fits the symptom, but has not been seen happening.

These parts were **not audited**: `controller/index.html`, `ShoreRocks.cs`, `RelayDebugHud.cs`,
`Bootstrap.cs`, and how the TV renders player names.

## Summary

| ID | Sev | Area | Finding | Confidence |
|---|---|---|---|---|
| A1 | info | Unity | "Rejected AIM frame" is a symptom, never a cause: rejected frames change no state | CONFIRMED |
| A2 | **high** | Unity/relay | Any phone socket drop removes that player from the roster and can hand the turn to someone else mid-throw | CONFIRMED |
| A3 | **high** | Unity | Once the turn has moved mid-throw, a second stone can launch while the first is sinking, and the turn then advances twice | CONFIRMED (path) / PLAUSIBLE (visual) |
| A4 | **high** | all | THROW has no acknowledgement and is silently discarded at five points | CONFIRMED |
| A5 | **high** | phone | One accelerometer sample ≥ 14 m/s² commits a throw; nothing can un-commit it | CONFIRMED |
| A6 | med | Unity | Why "throws too easily" is back: effective launch scaling changed three times in two days, while the gesture code is unchanged since 08-06 | PLAUSIBLE |
| A7 | med | phone | Reported swing speeds are physically implausible, which suggests the integration is inflating them | PLAUSIBLE |
| B1 | med | phone | AIM is not latest-wins: `sendIfClear` exists but is never called | CONFIRMED |
| B2 | med | relay | Rate limit is a fixed window, drops silently, and applies to THROW and to the host | CONFIRMED |
| B3 | low | phone | The thrower streams AIM through the entire flight + sink + settle | CONFIRMED |
| B4 | med | phone | Armed state survives a reconnect; `HOST_LEFT` restarts AIM into a dead host | CONFIRMED |
| B5 | med | Unity | Host resume never re-sends TURN/SPECTATE; a relay restart leaves ghost roster entries | CONFIRMED |
| B6 | med | Unity | No turn timeout: an idle or half-dead active player stalls the match | CONFIRMED |
| B7 | med | phone | No app-level liveness on the phone; a dead path looks CONNECTED and sends vanish | CONFIRMED |
| B8 | low | relay | A registered socket can re-JOIN under another room/id, leaving a ghost seat | CONFIRMED |
| B9 | low | phone | Repeated join taps stack intervals, which then fire a burst of JOIN_ROOMs | CONFIRMED |
| B10 | low | Unity | Receive loop decodes UTF-8 per chunk; sends pile up unbounded on a stalled socket | CONFIRMED (latent) |
| B11 | med | Unity | `ScoreManager._currentSkips` leaks across turns; mid-flight turn change zeroes pending points | CONFIRMED |
| B12 | low | Unity | Lobby/Choosing floods the Console with "Rejected AIM", which reads like an error | CONFIRMED |
| C1 | **high** | relay | Any device on the hotspot can crash the relay with one request containing `%` | CONFIRMED (by reading) |
| C2 | **high** | relay/Unity | Phones can send host-only message types; Unity acts on `ROOM_CREATED` from a phone and saves it to disk | CONFIRMED |
| C3 | **high** | relay/phone | Seat hijack: device id is the only credential and SPECTATE broadcasts every id | CONFIRMED |
| C4 | low | relay | Static-root check is a string prefix; the whole `controller/` tree is served | CONFIRMED |
| C5 | low | relay | No per-IP connection cap; room codes can be brute-forced from the hotspot | CONFIRMED |
| C6 | low | Unity | `SET_NAME` is only trimmed: no length or character limit | CONFIRMED (TV side unaudited) |
| D1 | med | physics | `9c3f201` stopped applying `SpeedScale` in `CreateLaunch`, but launch events and telemetry still divide by it | CONFIRMED |
| D2 | low | physics | `9c3f201` added `FakeSkip : MonoBehaviour` to `SkipPhysics.cs` inside the "pure" physics assembly | CONFIRMED |

---

## Part A — The reported symptoms, traced

### A1. "Rejected AIM frame" does nothing by itself

`AimPreview.OnAim` returns early on both guards, before any state is written:

- the phase guard, at `Assets/Scripts/Game/AimPreview.cs:413-417`
- the active-player guard, at `:419-424`

`_lastAimPlayer`, `_lastSeq`, `_targetAttitude` and `_lastAimTime` are all untouched by a rejected
frame. **A rejected frame cannot break a round.** The log line is evidence that a phone *other
than the one TurnManager thinks is active* is streaming. That points at A2, where the TV and the
phones disagree about whose turn it is.

Every path that produces the log:

1. **Frames already in flight when the turn changes.** Harmless.
2. **The previous thrower's post-throw stream (B3).** It is accepted while the stone flies, then
   rejected the moment TURN moves on.
3. **A player whose turn was taken from them by A2.** Their phone still has `myTurn = true` until
   TURN arrives, so every frame in that window is rejected.
4. **Lobby and Choosing.** Every frame from every phone is rejected by the phase guard (B12).

### A2. A socket drop reshuffles the roster and can move the turn mid-throw

**The chain:**

1. **Relay.** The close handler deletes the player immediately and sends `PLAYER_LEFT`
   (`relay/server.js:730-734`). No grace period is held for players; there is one only for the
   host (`:720-728`).
2. **Unity removes them.** `TurnManager.OnPlayerLeft` removes the player from `_players`
   (`Assets/Scripts/Managers/TurnManager.cs:250-272`). **If they were active, `StartTurn(_current)`
   runs immediately**, and after the removal that index points at the *next* player (`:270`). There
   is no check for a stone in the air.
3. **The rejoin appends.** `OnPlayerJoined` adds the returning player at the **end** of `_players`
   (`:242`). Turn order changes permanently.
4. **The phone rejoins silently.** `net.js` reconnects on its own (`controller/js/net.js:128-146`),
   and the seamless-rejoin work (09-06) means nobody on the couch notices a drop happened.

**Two-player walk-through.** This reproduces symptom 2 exactly.

- Roster `[A, B]`. A throws; the stone is flying.
- A's socket closes. iOS suspends it on screen lock or app switch, or there's a Wi-Fi blip.
- The relay sends `PLAYER_LEFT A`. TurnManager: roster `[B]`, `StartTurn(0)` → **B is up while A's
  stone is still in the air.** B's phone gets TURN and shows the throw screen.
- A's phone reconnects about 0.5 s later. `PLAYER_JOINED A`, roster `[B, A]`.
- A's stone sinks → `ThrowCompletedEvent` → `NextTurn()` → `StartTurn(1)` → **A is up again.**
- B was aiming, or already armed. B gets TURN `yourTurn:false` → `thrower.cancel()`
  (`controller/js/app.js:501-509`). **B's round is gone.** B's in-flight AIM frames log
  "Rejected AIM from B, active is A".

**What triggers it** (any close the relay sees):

- iOS locking the screen or switching apps (common between turns in a couch game)
- Wi-Fi roaming or a blip
- the relay's heartbeat terminate, 30–60 s after a half-open socket (`relay/server.js:742-752`)
- the phone reloading the page

**Age:** `if (wasActive) StartTurn` dates from `1bb0f2a` (2026-07-29). This is **not new code**.
What changed on 2026-09-06 (`2e563e9`, `8af3de3`, `b0e35ba`) is that rejoin became automatic and
invisible. A drop used to land a player on the join form, which was obvious. Now it quietly
reorders the match.

### A3. Turn moved mid-throw → second stone, double advance, sink visuals fighting

This follows on from A2.

- **A second stone can launch.** `RockLauncher.OnThrow` refuses a throw only while the active rock
  **IsFlying** (`Assets/Scripts/Game/RockLauncher.cs:133`), not while it `IsSinking` or is in its
  1.2 s settle (`RockController.cs:302-312`). If the turn has already moved (A2), the new player's
  THROW lands during the old stone's sink. `_active` is overwritten; the old rock keeps its
  coroutine.
- **The turn advances twice.** The old rock still publishes `ThrowCompletedEvent` when its settle
  ends (`RockController.cs:309`). `TurnManager.OnThrowCompleted` → `NextTurn()` (`:304-311`)
  advances the turn **while the new stone is in flight**. The new stone's completion then advances
  it again.
- **The water depth flag gets flipped under a live stone** (PLAUSIBLE: this is what "lagging or
  disappearing" looks like).
  - The new rock's `Launch` forces water depth-writing **off** (`RockController.cs:160-161`) while
    the old rock's `SinkRoutine` has it **on** (`:355-356`). The sinking stone can reappear
    underwater.
  - When the old `SinkRoutine` finishes it turns depth-writing off again (`:397`), and the camera's
    follow target has already jumped to the new `_active`.
  - `AimPreview` hides the ghost while `!CameraSettled` (`AimPreview.cs:374-376`), so the player
    who is now up sees no ghost during all of this.
- **Silent drop, forced re-throw.** Any THROW refused by the guards in `RockLauncher.cs:120-133` is
  dropped with no log and no reply. The phone has already gone IDLE (A4). Symptom 2's "forcing
  them to aim and re-throw" is this.

### A4. THROW is fire-and-forget with no acknowledgement

Once `_fire()` runs, the phone goes IDLE, shows `Speed x.xx m/s` (`controller/js/app.js:108-114`),
and considers the throw done. It can be discarded silently at every hop:

| Where | Condition | Code |
|---|---|---|
| Phone | socket not OPEN (reconnecting, half-open, B7) → `send()` returns false; the return value is ignored | `controller/js/net.js:209-213`, `app.js:81-83` |
| Relay | per-socket rate limit hit (B2) — "drop silently" | `relay/server.js:679` |
| Relay | socket was just replaced (`meta.roomCode = null`) → `NOT_IN_ROOM` error, and the phone's ERROR handler turns that into a rejoin, not a retry | `server.js:285-289`, `app.js:477-479` |
| Unity | phase ≠ Playing, or sender ≠ `ActivePlayer` (A2 makes this common) | `RockLauncher.cs:120-125` |
| Unity | previous rock still `IsFlying` | `RockLauncher.cs:133` |

No `THROW_ACCEPTED` or `THROW_REJECTED` message exists in `Messages.cs` or `server.js`. The phone
cannot tell a throw that landed from one that didn't. The player sees their own speed readout and
a TV that did nothing.

### A5. One sample over 14 m/s² commits a throw, and nothing takes it back

`ThrowController._onSample` (`controller/js/throw.js:502-541`), with `ThrowTuning` at `:37-71`:

- **One sample is enough.** Any single sample with `power ≥ swingAccel (14)` sets
  `_swinging = true` (`:519-523`). Nothing ever sets it back to false while ARMED.
- **The throw then fires one of two ways:**
  - **3 consecutive samples ≤ `stopAccel (4)`** (`:531-535`) — about 50 ms at 60 Hz; or
  - **900 ms after the peak** whatever the phone does (`:540`). **Once `_swinging` is set, a throw
    is guaranteed within 0.9 s.**
- **`minValidPeakAccel: 3` is dead.** `computeThrowDescriptor` only returns null when
  `peak.power < 3` (`:168`), but reaching `_fire` already needed a sample ≥ 14. The "fumble" guard
  can never trip.
- **No minimum swing speed on either side.** Neither `computeThrowDescriptor` nor
  `RockLauncher.OnThrow` / `SkipPhysics.CreateLaunch` (`SkipPhysics.cs:187`) rejects a low
  `swingSpeed` or a tiny `sampleCount`.
- **No arm grace.** `arm()` (`throw.js:411-432`) starts detection the moment it runs. `arm()` runs
  inside the `touchend` handler (`app.js:430-439`), so the jolt of the tap itself — or of bringing
  the phone up to swing — is scored straight away.
- **`power` is residual-gravity-sensitive.** On the `exact` path (every iPhone, per
  `sensors.js:336-337`) `power` is `|accelIncludingGravity − g(fusedAttitude)|`
  (`sensors.js:548-554`). The fused attitude is a gyro integration between orientation events
  (`:510-531`). An attitude error of θ leaves ≈ 9.8·θ(rad) m/s² of fake acceleration, so ~24° of
  lag or drift sits at `stopAccel` and blocks the 3-calm-sample stop. The throw then fires by the
  900 ms timeout instead.

**Status:** fixed in group 3, test `ThrowController: A5/A6 accidental-throw guard`.

**Evidence in `rock-throws.csv`** (2026-09-10 session, two phones). These rows look like fires the
player didn't mean to make — low speed with the phone nowhere near flat:

```
16:03:24 ASOCXMSH48ZY swing 11.148 pitch -12.3 roll -60.8 → 0 skips
16:05:22 P4LF4Y549YHN swing  7.246 pitch  56.7 roll -91.8 → 0 skips
16:06:14 P4LF4Y549YHN swing  9.391 pitch  34.8 roll -16.0 → 0 skips
16:09:48 P4LF4Y549YHN swing  4.813 pitch  56.9 roll  15.0 → 0 skips
16:10:34 P4LF4Y549YHN swing  5.424 pitch   9.5 roll   0.7 → 1 skip
16:17:48 ASOCXMSH48ZY swing  5.583 pitch -11.7 roll -43.8 → 0 skips
```

**"Re-aim throws by itself"** (symptom 2) is A4 followed by A5. The first throw is dropped, the
player taps to arm again, and the tap jolt or the lift into the swing crosses 14 m/s². That commits
a throw, and it fires by the 3-sample stop or the 900 ms timeout.

### A6. Why "throws too easily" is back, when the gesture code hasn't changed

**What has not changed:**

- `swingAccel`, `stopAccel`, `stopSamples` and `maxSwingMs` were introduced in `00803c7`
  (2026-08-06), and `git log -S` shows no change to them since.
- `peakSpin` last changed in `031801e` (08-16).
- The controller's gesture path has had no behavioural change in the review window. `3b1aef3` and
  `a4dfb30` touched only comments and a named constant.

**What has changed** is what an accidental fire *looks like* on the TV:

| Commit | Date | Effective launch scaling |
|---|---|---|
| pre-rewrite | — | `SpeedScale 1.2` |
| `6d8956b` | 09-09 | `SpeedScale 0.3` (new physics) |
| `15a7557` | 09-09 | `SpeedScale 1.2` (reverted) |
| `9c3f201` | 09-10 | **`SpeedScale` no longer applied at all** (`SkipPhysics.cs:187`, see D1); `MinSkipSpeed 2.5 → 6.5` (`:152`) |

With a scale of 0.3, a tap-jolt fire at ~5 m/s launched at 1.5 m/s and dribbled into the water,
which reads as "nothing happened". At 1.0–1.2 it leaves the hand like a real throw. That fits a
trigger that was always too easy becoming *visible again* after the revert. PLAUSIBLE: it has not
been checked against telemetry from 09-09.

**Status:** fixed in group 3, test `ThrowController: A5/A6 accidental-throw guard` (same tests as
A5 — the layered guard removes the accidental fire A6 is about, rather than changing launch scaling).

### A7. The swing speeds reported are too large to be a real hand speed

The CSV `swingSpeed` column reads 25–42 m/s on ordinary throws. Because of D1, that column is the
sent value divided by 1.2, so the phone actually sent **~30–51 m/s**. A human wrist in a sidearm
skim is far below that.

`peakSwingSpeed` (`throw.js:232-270`) integrates `linear` from the last sample with
`power ≤ stopAccel` before the peak. The residual gravity from A5 keeps `power` above 4 through the
wind-up, so that start point walks back a long way and the error adds up across the whole window.

This matters to symptom 3: **the measured speed rises with gesture duration and sensor error, not
only with effort.** PLAUSIBLE — it needs a raw sample capture from a phone to confirm.

---

## Part B — Network pipeline holes and stability

### B1. AIM is not latest-wins

`RelayConnection.sendIfClear` (`controller/js/net.js:222-227`) exists to skip frames while bytes
are still buffered. **Nothing calls it.** `sendAim` goes through `sendInput` → `send`
(`app.js:81-83`, `:158-161`). It has never been wired up: `git log -S sendIfClear` shows only
`67dfb2e` (07-31).

When the radio stalls, every buffered AIM frame is delivered late in TCP order, replaying a swing
the player has already finished. That backlog sits on the same socket ahead of THROW and counts
against the same relay rate window (B2). The school-network doc measured stalls of 593–1161 ms
(`docs/plans/aim-preview-lag-on-shared-radio.md:108-151`), which is 12–23 AIM frames per stall.

### B2. The relay's rate limit is a fixed window, silent, and covers everything

`isRateLimited` (`relay/server.js:117-126`) is commented "rolling-window" but **resets a fixed 1 s
bucket**.

- **What it counts:** every message, before the type is looked at (`:679`) — so THROW, JOIN_ROOM,
  PING and SET_NAME are limited alongside AIM. Host sockets are limited too.
- **What happens at the limit:** the message is dropped, with no error sent back and no
  `/health` counter.
- **Why it bites:** a stall releasing a burst, plus 20 Hz steady streaming, plus a burst landing
  across a window boundary, can exceed 60 in one bucket. The next message may be the THROW.

Host traffic is currently low: `RockPickedEvent` → SPECTATE fires only on confirm
(`RockSelection.cs:121-131`), and the peak is about 2N+1 messages at match start. So the host side
is latent, not active.

### B3. The thrower keeps streaming AIM through flight, sink and settle

`_fire()` → `cancel()` → IDLE → `onStateChange` → `startTurnAim()` (`app.js:115-124`). `myTurn` is
still true because TURN only changes at `ThrowCompletedEvent`, so `startPreviewAim()` starts
streaming again and calls `motion.calibrate()` (`app.js:187`) on the pose at the end of the
follow-through.

That runs for the stone's whole flight + sink + `settleHold` 1.2 s. It is wasted uplink during the
exact seconds another player's phone is about to need the radio, and the tail is what
"Rejected AIM from <previous player>" reports (A1 path 2).

### B4. Phone state survives reconnects and host drops

- **Armed state survives a reconnect.** `JOINED` sets `myTurn = false` and stops the preview
  stream, but **does not `thrower.cancel()`** (`app.js:453-469`). A phone that was ARMED across a
  reconnect stays armed. A throw fired while `status === RECONNECTING` hits A4's first row.
- **`HOST_LEFT` restarts AIM into a dead host.** `HOST_LEFT` calls `thrower.cancel()`
  (`app.js:519-522`) without clearing `myTurn`. The IDLE transition then restarts the turn-idle AIM
  stream into a host that is gone. The `ROOM_CLOSED` branch shows the author knew this exact trap
  (`:529`, "before cancel(), whose IDLE transition would otherwise re-start the stream"), but the
  fix was not applied to `HOST_LEFT`.

### B5. Host reconnect doesn't resync phones; a relay restart leaves ghost players

- **Resume doesn't resync.** On `ROOM_CREATED` with `resumed: true`, `RelayClient.Dispatch` only
  logs and raises `RoomCodeAssigned` (`Assets/Scripts/Net/RelayClient.cs:454-472`). Nothing calls
  `TurnManager.SendTurnStates`. Phones keep whatever TURN/SPECTATE they last had. The relay's
  `players` list in the resume payload is never reconciled against `TurnManager._players`.
- **A restart leaves ghosts.** If the relay process itself restarts (not a resume), phones that
  don't come back never produce `PLAYER_LEFT`. They stay in `_players` and the match waits on their
  turn (B6).

### B6. No turn timeout

`TurnManager` advances only on `ThrowCompletedEvent` (`:304-311`), on the host's N key (`:178`), or
on `PLAYER_LEFT`. A half-open active phone holds the match until the heartbeat terminates it,
30–60 s later (`relay/server.js:34`, `:742-752`). A phone that is connected but never throws holds
it indefinitely.

### B7. No app-level liveness on the phone

- The relay answers `PING` with `PONG` (`server.js:703-705`), but **the controller never sends
  PING** and has no receive timeout.
- Browsers don't surface protocol-level pings to JS.
- A phone whose Wi-Fi path has silently died stays `readyState === OPEN` until the OS TCP stack
  gives up. Meanwhile `send()` "succeeds" into a dead buffer and every AIM/THROW is lost (A4).

### B8. A registered socket can register again

`handleJoinRoom` (`relay/server.js:227-281`) blocks hosts but has **no guard for a socket that is
already a player**.

- **Same socket, different room:** a JOIN_ROOM for another room rewrites `meta.roomCode` and leaves
  the socket in the first room's `players` map. The close handler then looks up only the new room
  (`:714-718`), so **the first room never gets `PLAYER_LEFT`** and keeps a ghost seat (B6).
- **Same socket, different `playerId`:** this leaves two map entries pointing at one socket.

### B9. Join taps stack intervals

Each `join()` call adds a `setInterval` (`app.js:722-732`) that clears itself only once the
connection reaches CONNECTED/JOINED. Several taps while connecting, or retries after an ERROR,
leave several intervals. On connect they each send `JOIN_ROOM`. Every extra one becomes a
`PLAYER_JOINED reconnected:true` → `SendTurnStates` storm (`TurnManager.cs:240-248`).

### B10. Latent problems in `RelayClient`

- **UTF-8 is decoded per chunk.** `ReceiveLoopAsync` runs `Encoding.UTF8.GetString` on each
  `ReceiveAsync` chunk (`RelayClient.cs:416`). A multi-byte character split across chunks decodes
  to replacement characters. Only reachable with messages near the 8 KB buffer, since inbound is
  capped at 4096 by the relay.
- **Sends pile up on a stalled socket.** `Send` is fire-and-forget (`:557-561`). On a slow socket
  each call queues another task on `_sendLock` with no bound or drop policy.

### B11. Score state leaks across turns

- **`_currentSkips` never resets.** It is reset only at the end of `OnThrowCompleted`
  (`ScoreManager.cs:92`), never in `OnTurnStarted` (`:41-47`) or `ResetScores` (`:102-106`). A
  throw that never completes (rock destroyed while flying, match reset) carries its skip count into
  the next throw, so a zero-skip throw can score.
- **A2 wipes points mid-flight.** `OnTurnStarted` zeroes `_pendingThrowPoints`. When A2 starts the
  next turn mid-flight, the in-air stone's skip points before that moment are lost, and the ones
  after it are kept.
- **Dead code.** The `else` branch zeroes `_pendingThrowPoints` and then adds it (`:80-89`), so it
  always adds 0.

### B12. The Console fills with "Rejected AIM" in the lobby

Every lobby and Choosing AIM frame from every phone goes through AimPreview's phase guard and logs
`phase is Lobby, not Playing` once a second (`AimPreview.cs:413-417`, `:464-469`). ShoreRocks
consumes those same frames legitimately, so the log is noise — and it looks like the error being
hunted, which is what makes A1 hard to read from the Console.

---

## Part C — Security

**Threat model:** anyone who can join the host's hotspot Wi-Fi, which includes every guest.

### C1. One request crashes the relay

`serveStatic` calls `decodeURIComponent` on the raw request path (`relay/server.js:338`). A
malformed escape such as `https://play.skiprocking.ca:8443/%` throws `URIError`.

- The throw happens synchronously inside the `request` handler.
- There is no `try` around it, and no `process.on('uncaughtException')` anywhere in `relay/`.
- Node exits. The relay is a child of Unity (`LocalServices`), so **every phone disconnects and
  the match ends**.

Nothing on `:80` reaches this path (`handleProbeRequest`, `:923-933`).

CONFIRMED by reading plus JS semantics; not reproduced against a live relay.

**Status:** fixed in group 1, test `fix: a malformed percent-encoded path answers 400, not a crash`.
**Hardware check:** passed 2026-09-10 — on a phone, `https://play.skiprocking.ca:8443/%` showed the Bad request page and a joined phone kept playing (Ayden).

### C2. Phones can send host-only message types, and Unity believes them

- **The relay forwards everything.** The default branch sends *any* `type` from a player to the
  host (`server.js:706-709`, `:284-294`). It only stamps `playerId`.
- **Unity doesn't check the source.** `RelayClient.Dispatch` switches on type alone
  (`RelayClient.cs:452-553`). A phone can send:
  - **`ROOM_CREATED`** → `RoomCode` is overwritten **and saved to disk** via `RoomCodeStore.Save`
    (`:456-470`). The TV shows the wrong code and QR, and the next launch asks the relay for the
    wrong `preferredCode`.
  - **`PLAYER_LEFT` / `PLAYER_JOINED`** (for itself, since the id is stamped) → force A2 on demand:
    drop its own seat mid-turn, or skip its own turn.
  - **`DEVICE_FUNNEL`** → fake join-funnel counts on the TV.
  - **`ERROR`** → log spam.

### C3. Seat hijack by device id

- **The id is the only credential.** The playerId is the stored device id
  (`controller/js/identity.js`), and `JOIN_ROOM` needs nothing else.
- **Every phone can read every id.** Each SPECTATE broadcast carries all of them as keys of
  `scores`, `names`, `rocks` and `colours`, plus `activePlayer` and `lastPlayer`
  (`Messages.cs:304-394`).
- **The victim stays kicked.** A phone joining with someone else's id takes their seat: the relay
  closes the victim with 4001 (`server.js:262-265`), and **the victim's controller deliberately
  never reconnects on 4001** (`net.js:136`). The attacker inherits the seat, score, colour and turn.

The same thing happens without an attacker: the page open in two tabs on one phone (shared
localStorage) silently kills the older tab.

### C4. Static serving: prefix check, and the whole folder is public

`resolved.startsWith(STATIC_DIR)` (`server.js:343`) has no trailing separator, so a sibling
directory whose name starts with `controller` would be readable. None exists today.

Everything under `controller/` is served, including `package.json`, `eslint.config.js`,
`test-sensors.js`, and `node_modules/` if it has been installed there. Information disclosure only.

**Status:** fixed in group 1, test `fix: static serving is an allowlist, not a directory`.

### C5. Connection and brute-force limits

- **No caps.** Rate limiting is per socket; there is no per-IP connection cap and no cap on
  unregistered sockets.
- **Codes are guessable.** Room codes are 24⁴ = 331,776. Many sockets × 60 JOIN_ROOM/s can
  enumerate them from the hotspot.

Low severity for a single-room couch relay.

### C6. Names aren't sanitised

`PlayerNames.OnSetName` only trims (`Assets/Scripts/Managers/PlayerNames.cs:55-69`). There is no
length cap beyond the relay's 4 KB message limit and no control-character filter.

Phones render names with `textContent`, which is safe (`app.js:669-695`). **How the TV renders
names (IMGUI / rich text / layout) was not audited.**

---

## Part D — Adjacent problems found on the way (not networking)

### D1. `SpeedScale` is half-removed

`9c3f201` changed `CreateLaunch` to `speed = Mathf.Max(0f, swingSpeed)` (`SkipPhysics.cs:187`),
dropping `* constants.SpeedScale`. But:

- `SkipConstants.asset` still holds `SpeedScale: 1.2`.
- `RockController.Launch` still **divides** by it to recover `RockLaunchedEvent.SwingSpeed` and
  `power` (`RockController.cs:177-178`).
- `RockLaunchedEvent.cs:22` still documents the multiplication.
- `Assets/CLAUDE.md` and `docs/4-systems/SkipPhysics.md` still call it "the one conversion".

**Effect:** every `rock-throws.csv` row since `9c3f201` under-reports swing speed by 1.2×, and
`SkipCurve`'s speed sweep now means stone speed rather than hand speed. `9c3f201` also dropped the
`SpinMultiplier` clamp from launch spin (`:212`).

**Status:** fixed in group 2, test `CreateLaunch_ScalesSwingSpeedBySpeedScale`.
**Hardware check:** passed 2026-09-10 — five throws felt unchanged, and each new rock-throws.csv swingSpeed matched the phone's Speed readout (Ayden).

### D2. A stray MonoBehaviour in the pure physics file

`9c3f201` appended `public class FakeSkip : MonoBehaviour` in the **global namespace** to
`SkipPhysics.cs` (`:382`), plus `using Game;` inside a file already in `namespace Game`.

- **It breaks a documented rule.** `SkipPhysics.cs` is in the `RockSkipping.Physics` assembly,
  which `Assets/CLAUDE.md` says must stay pure and dependency-free.
- **Unity will complain.** It flags a MonoBehaviour whose class name doesn't match its file name.

**Status:** fixed in group 2 — `FakeSkip` moved to `Assets/Scripts/Game/FakeSkip.cs`
(Assembly-CSharp), the stray `using Game;` removed from `SkipPhysics.cs`; verified by
`tools/verify.ps1`'s Unity compile stage (no new Console errors) rather than a dedicated test,
since there is no behaviour here for an EditMode test to check.

---

## Comparison against the last two weeks

| Area | Last behavioural change | Bearing on the symptoms |
|---|---|---|
| Gesture thresholds (`throw.js`) | `00803c7` 08-06 | A5 is original behaviour, not a regression in this code |
| Launch scaling | `6d8956b`/`15a7557` 09-09, `9c3f201` 09-10 | A6 — why accidental fires became visible again |
| Roster removal on `PLAYER_LEFT` | `1bb0f2a` 07-29 | A2 is old code |
| Silent auto-rejoin, device-id identity, 4001 no-reconnect | `2e563e9`/`8af3de3`/`b0e35ba` 09-06 | A2 now happens invisibly; C3 introduced |
| Camera-settled gating of the ghost | `57a3a20` 09-08 | Ghost hidden while A3's stones fight over the camera |
| Hotspot band matching | `796854a` 09-09 | Fewer stalls; does not remove B1/B2 |
| Relay routing / rate limit | unchanged in window | B2, C1, C2 are long-standing |

**No single commit in the window introduces the multiplayer break.** The paths it uses are older.
The 09-06 rejoin work made the drop → roster reshuffle → rejoin cycle silent, which is the likeliest
reason it now presents as "a new bug" rather than as "my phone got kicked to the join screen".
