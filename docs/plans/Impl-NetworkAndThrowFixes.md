# Implementation plan — network pipeline and throw gesture fixes

**For the implementing agent.** Priority and reasoning are in
[`Plan-NetworkAndThrowFixes.md`](Plan-NetworkAndThrowFixes.md). Finding IDs refer to
[`../Findings-NetworkAndThrowStability.md`](../Findings-NetworkAndThrowStability.md). Read that
finding before starting its group.

## Rules for every group

1. **Red first.** Write the regression test, run it, and confirm it fails *for the reason the
   finding describes*. A test that passes before the fix does not guard anything. Then implement,
   then go green.
2. **Run `tools/verify.ps1` at the end of every group** (see Commands), not just the new test. A
   group is not done until it prints `VERIFY PASSED`. Put its Summary block in the hand-off note.
3. **Never run mutating git.** Read-only git (`status`, `diff`, `log`) is fine. At the end of each
   group, stop and ask Ayden to commit with the group number in the message. One group per commit
   is what makes a regression bisectable.
4. **Check Unity yourself, through the open Editor.** `tools/verify.ps1` compiles, reads new
   Console errors and runs the EditMode tests via the Unity CLI and `com.unity.pipeline`, without
   closing or focusing Unity. Never claim C# compiles from reading it — the `unity compile` stage
   is the evidence. If the Editor is unreachable, the script says so: ask Ayden to open Unity on
   the project; if it is already open, run `unity pipeline list` to check for Safe Mode (compile
   errors hide the Pipeline server — fix the `error CS####` lines from
   `%LOCALAPPDATA%\Unity\Editor\Editor.log`, then ask Ayden to restart Unity).
5. **New Unity files need `.meta` files Unity generates.** Don't hand-write GUIDs. After creating
   files, run `tools/verify.ps1` (its recompile refreshes the asset database), then confirm with
   `git status` that each new file has its `.meta`. If one is missing, ask Ayden to focus the
   Editor. The metas go in the group's commit.
6. **Before any Play-mode or hardware check after a relay change,** Ayden must kill the stale
   relay — `LocalServices` reuses any relay already listening (`docs/4-systems/Networking.md`,
   Traps). Hand that as a step card; don't kill processes yourself.
7. **Hardware checks stay Ayden's.** Phones, Play mode and anything on the TV cannot be automated
   here. Hand each group's hardware check over as step cards after `VERIFY PASSED`.
8. **Don't overwrite hand tuning.** `Assets/Resources/SkipConstants.asset` carries Ayden's own
   edits (e.g. `LiftShare: 0.05`). When a group edits that file, change only the named field.
9. **Comments follow the house style.** They explain *why* and name the finding ID. Density must
   not be trimmed (`Assets/CLAUDE.md`).
10. **Update the findings doc** with a status line per ID as it lands:
    `**Status:** fixed in group N, test <name>`.

## Commands

All run in PowerShell from any folder. Each was run on this machine on 2026-09-10.

- **Everything** — the one to run at the end of every group:
  `powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\aj\Desktop\GameDev\RockSkipping\tools\verify.ps1"`
  - **Pass:** it ends with `VERIFY PASSED` and exits 0.
  - Stages, in order: `standards` (`tools/audit/check-standards.js`), relay tests and lint,
    controller tests and lint, then Unity: Editor reachable and not in Play mode, compile, new
    Console errors since the run started, EditMode tests.
  - Any stage failing prints the failing tests, compile errors or Console entries, ends with
    `VERIFY FAILED - N stage(s)`, and exits 1.
- **While iterating on one area** (still finish the group with the full run):
  - Unity only, one test class (swap `SkipPhysicsTests` for the class you are working on, e.g.
    `TurnOrderTests` once group 4 creates it):
    `powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\aj\Desktop\GameDev\RockSkipping\tools\verify.ps1" -Skip standards,relay,controller -TestFilter SkipPhysicsTests`
    A filter that matches no tests is a FAIL, so a typo can't pass. `-Skip` takes a comma list;
    an unknown stage name exits 2 before anything runs.
  - Relay suite: `npm --prefix "C:\Users\aj\Desktop\GameDev\RockSkipping\relay" test`
    (**pass:** `# fail 0`; with Unity in Play mode `test-captive.js` skips one case by design).
  - Controller suite: `npm --prefix "C:\Users\aj\Desktop\GameDev\RockSkipping\controller" test`
    (**pass:** exit 0 and no line starting `FAIL`; group 6 chains more test files).
- **Unity CLI directly**, for anything the script doesn't cover:
  `unity command` lists what the Editor exposes (`recompile`, `recompile_status`, `run_tests`,
  `get_console_logs`, `console`, `editor_status`, …). If `unity` isn't on PATH in an older shell,
  use `& "$env:LOCALAPPDATA\Unity\bin\unity.exe"`.
- **Kill the stale relay** — Ayden's step card, never run by the agent:
  `Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object CommandLine -like '*server.js*' | ForEach-Object { Stop-Process -Id $_.ProcessId }`

**Tooling on this machine** (checked 2026-09-10): Node v22.19.0, npm, git; Unity CLI
1.0.0-beta.9 at `%LOCALAPPDATA%\Unity\bin\unity.exe`; Unity Editor 6000.3.15f1 with
`com.unity.pipeline` 0.6.0-exp.1; eslint installed in `relay/` and `controller/`.

---

## Group 0 — Baseline ✅ done 2026-09-10

Recorded before any fix:

| Stage | Result |
|---|---|
| standards | 23 checks, 0 failing |
| relay tests | 46 tests, 0 failed (45 + 1 skipped while Unity was in Play mode) |
| relay lint | clean |
| controller tests | `PASS — all controller math checks green` |
| controller lint | clean — after adding the missing `crypto` global to `controller/eslint.config.js` |
| unity | compiles, no Console errors, EditMode 16 passed |

Nothing is red. Start at group 1.

---

## Group 1 — C1 relay crash, C4 static serving

**Files:** `relay/server.js`, `relay/test-relay.js`

**Tests first** (in `test-relay.js`, using the existing `httpGet` helper):
- `GET /%` answers **400**, and `/health` still answers 200 afterwards.
  - Before the fix the server process dies. The harness's `exit` handler prints
    "Server exited unexpectedly", and every later test fails. That is the red.
- `GET /package.json`, `/eslint.config.js` and `/test-sensors.js` answer **404**.
- `GET /js/app.js` and `GET /` answer **200**.
- `GET /..%2fpackage.json` answers **404** or **400**, never 200.

**Implement:**
1. **Guard the decode.** In `serveStatic`, wrap `decodeURIComponent` in `try/catch` and answer 400
   on `URIError`.
2. **Catch per request.** Wrap the body of `handleRequest` in `try/catch`, answering 500 and
   logging. This is request-scoped, so one bad request can never exit the process. Do **not** add
   a global `uncaughtException` handler.
3. **Serve an allowlist.** Replace the prefix check with `/` → `index.html`, `index.html`, and
   `js/<name>.js`. Anything else is a 404. Keep the resolved-path containment check too, comparing
   against `STATIC_DIR + path.sep`.
4. **Update the header comment.** Explain why this is an allowlist rather than a directory, citing
   C1 and C4.

**Done:** relay suite green.

**Hardware check** (Ayden): during Play, open `https://play.skiprocking.ca:8443/%` on a phone.
It shows an error page, and a joined second phone keeps playing.

---

## Group 2 — D1 SpeedScale, D2 FakeSkip

**Files:**
- `Assets/Scripts/Game/Physics/SkipPhysics.cs`
- `Assets/Resources/SkipConstants.asset`
- `Assets/Tests/EditMode/SkipPhysicsTests.cs`
- new `Assets/Scripts/Game/FakeSkip.cs`

**Tests first** (in `SkipPhysicsTests.cs`):
- `CreateLaunch_ScalesSwingSpeedBySpeedScale`: with `SpeedScale = 2`, launch velocity magnitude is
  `2 × swingSpeed` (within tolerance). Red today, since the scale is ignored.
- `CreateLaunch_ClampsSpinMultiplier`: restore the `[0.8, 1.2]` clamp dropped in `9c3f201`. With
  `SpinMultiplier = 5`, launch spin is `1.2 × spinRate`.

**Implement:**
1. **Restore the scale.** `var speed = Mathf.Max(0f, swingSpeed) * constants.SpeedScale;`
2. **Restore the spin clamp.** `Spin = spinRate * Mathf.Clamp(tuning.SpinMultiplier, 0.8f, 1.2f)`
3. **Update the asset.** `SkipConstants.asset`: `SpeedScale: 1.2` → `SpeedScale: 1`. This keeps
   today's tuned feel exactly.
4. **Move `FakeSkip`.** Cut the class from `SkipPhysics.cs` into `Assets/Scripts/Game/FakeSkip.cs`
   (Assembly-CSharp), with its fields and Ayden's design comments kept verbatim. Remove the stray
   `using Game;` from `SkipPhysics.cs`.
5. **Update the `SpeedScale` tooltip** to say the asset is 1.0 as of this group, and why.

**Done** (Ayden):
- Console clean.
- EditMode tests green.
- **`RockSkipping/Verify/Print Skip Curve` still passes its energy invariant.** Its speed sweep now
  reads as hand speed × 1.0.

**Hardware check:** throw 5 times. It should feel identical to before, and the new
`rock-throws.csv` rows' `swingSpeed` should equal what the phone's debug panel shows.

---

## Group 3 — A5/A6 layered accidental-throw guard

**Files:**
- `controller/js/throw.js`
- `controller/js/app.js` (the query overrides only)
- `controller/test-sensors.js`

**Design:** three new `ThrowTuning` fields. Defaults are starting points; Ayden tunes them on
hardware.

| Field | Default | Rule |
|---|---|---|
| `armGraceMs` | 250 | Samples within this long of `arm()` never start a swing |
| `swingOnsetSamples` | 3 | Consecutive samples ≥ `swingAccel` needed to set `_swinging`; a sample below resets the run |
| `minThrowSwingSpeed` | 3 m/s | A computed descriptor below this is discarded and the controller **stays ARMED** (reset the swing state; don't `cancel()`) |

Also:
- **`minValidPeakAccel` is dead** (A5). Delete it, and its test if one exists.
- **The 900 ms `maxSwingMs` fallback stays,** but only applies once a *sustained* swing has begun.

**Tests first** (a new block in `test-sensors.js` after "ThrowController: arm, swing, stop", reusing
`makeMotion`/`swing`/`makeSample`). Each must be red today:
1. **Tap jolt ignored.** One sample of 30 at t=10 ms after arm, then calm: no throw, still ARMED.
2. **Single spike ignored.** A sample of 30 at t=400 ms, then 3 calm samples: no throw.
3. **Two-sample spike ignored:** 30, 30, then calm.
4. **Weak swing kept armed.** A sustained swing whose computed `swingSpeed` < 3: no THROW sent,
   state ARMED, and a following real `hardSwing` (shifted past the grace) **does** fire exactly once.
5. **Sustained hard swing still fires.** Update the existing `hardSwing` fixture so its timestamps
   start after `armGraceMs` and it has ≥3 samples over threshold. **The existing "abrupt stop
   fires" check must still pass.**
6. **Timeout needs a sustained swing.** The `maxSwingMs` fallback never fires after only a single
   spike.

Keep the existing "a single calm sample mid-swing does not fire" and "holding still never fires"
checks unchanged.

**Implement:**
1. Record `_armedAt` from `sample.t`'s clock, not `performance.now()` mixed with sample time.
   `arm()` already stores `performance.now()` and `MotionSource` stamps samples with the same clock.
   Document that they must match.
2. In `_onSample`, add an onset run counter. Skip onset while `sample.t - _armedAt < armGraceMs`.
3. In `_fire`: if the descriptor is null or `swingSpeed < minThrowSwingSpeed`, reset
   `_swinging/_peak/_calmRun/_onsetRun`, **stay ARMED**, and return null.
4. **Query overrides.** In `app.js`, read `?armGraceMs=`, `?onsetSamples=` and `?minThrowSpeed=`
   next to the existing `gravityK` override, and pass a merged `tuning` into `ThrowController`.
5. **Debug readout.** Add `onset`/`grace` state to `updateDebug` so tuning on a phone is observable.

**Done:** controller suite green.

**Hardware check** (Ayden, one phone). Each row must hold:

| Do this | Expect |
|---|---|
| Tap to aim, hold still 5 s | Nothing fires |
| Tap to aim, rap the screen hard with a finger | Nothing fires |
| Tap to aim, slowly turn the phone to face another way | Nothing fires |
| Tap to aim, normal skim throw ×5 | Fires every time, once |

Afterwards, `rock-throws.csv` has no new rows below 3 m/s. If real throws get eaten, tune with the
`?query` overrides and report the values. **Don't change defaults without Ayden's numbers.**

---

## Group 4 — A2/A3/B11 seats survive drops, one stone at a time, clean score tally

This is the largest group. It introduces a **pure, EditMode-testable assembly**, mirroring
`RockSkipping.Physics`, because `TurnManager` lives in Assembly-CSharp and the existing test
assembly cannot reach it.

### 4a. New assembly `RockSkipping.Match`

- `Assets/Scripts/Managers/Match/RockSkipping.Match.asmdef`:
  - name `RockSkipping.Match`, rootNamespace `Match`
  - no references, `autoReferenced: true`, `noEngineReferences: true`
- `Assets/Tests/EditMode/Match/RockSkipping.Match.Tests.asmdef`: copy
  `RockSkipping.Physics.Tests.asmdef`, swapping the reference to `RockSkipping.Match`.

**`TurnOrder.cs`** (pure C#; time is passed in as `double nowSeconds`) owns:
- `Seats` (join order, never reordered by a reconnect)
- `ActiveIndex`
- a disconnected set with a disconnected-since time per seat
- `ThrowInProgressBy` (the thrower's id, or null)

API sketch (names may be refined; behaviour may not):

```csharp
bool Connect(string id, double now);          // true if a brand-new seat; reconnect keeps position
void Disconnect(string id, double now);       // keeps the seat
IReadOnlyList<string> ReleaseDisconnected();  // prune, for Lobby/Results — returns removed ids
void BeginThrow(string id);                   // stone launched by the active seat
bool EndThrow(string id);                     // false (and no state change) if id is not the thrower
bool CanAdvance { get; }                      // false while a throw is in progress
bool ShouldSkipActive(double now, double grace); // active seat disconnected, no throw, grace elapsed
int  NextIndexFrom(int index);                // wraps
```

**`ThrowScoreTally.cs`** replaces `ScoreManager`'s loose fields:
- `RecordSkip()`
- `Complete(float distance, int pointsPerSkip, int pointsPerMetre) → int`
- `Reset()`
- Zero skips means zero points, matching `fe05675`'s rule.

**Tests first** (`TurnOrderTests.cs`, `ThrowScoreTallyTests.cs`). These are new files, so they can't
be red against old code. **Red** means: write them against stub members that throw
`NotImplementedException`, and watch them fail.

- **Reconnect keeps order.** `Connect A, B; Disconnect A; Connect A` → `Seats == [A, B]`.
- **A drop never moves the turn mid-flight.** A active, `BeginThrow(A)`, `Disconnect(A)` →
  `ActiveIndex` unchanged, `CanAdvance == false`.
- **Pause, then skip.** A active, no throw, `Disconnect(A)` at t=0 →
  `ShouldSkipActive(19, 20) == false` and `ShouldSkipActive(20, 20) == true`.
- **Reconnecting inside the grace cancels the skip.**
- **A seat already gone too long is skipped at once.** When the turn lands on a seat disconnected
  for ≥ grace, `ShouldSkipActive` is true immediately.
- **A completion from a non-thrower changes nothing:** `EndThrow(B)` while A threw returns false.
- **Tally:** 3 skips → points; 0 skips → 0 points; `Reset` clears skips.
  - **Leak guard (B11):** `RecordSkip` ×2, `Reset`, then `Complete` with 0 new skips → 0.

### 4b. Wire it in

- **New event `PlayerDisconnectedEvent`** (`EventTypes/MatchEvents/`, copying `PlayerLeftEvent.cs`).
  `RelayClient.Dispatch`'s `PlayerLeft` case publishes **this** instead of `PlayerLeftEvent`.
- **`TurnManager`:**
  - Holds a `TurnOrder`.
  - `[SerializeField] private float disconnectedTurnGraceSeconds = 20f;` — C# initializer, per the
    `AddComponent` note in `Assets/CLAUDE.md`.
  - **On `PlayerDisconnectedEvent`:**
    - During `Playing`/`Choosing`: `Disconnect`, `SendTurnStates`.
    - During `Lobby`/`Results`: release the seat at once and **publish `PlayerLeftEvent`**. That
      keeps every existing `PlayerLeftEvent` consumer's meaning ("seat gone"): RockSelection,
      ShoreRocks, PlayerColours, PlayerNames.
  - **On `PlayerJoinedEvent`:** `Connect`; `SendTurnStates`.
  - **`Update`:** if `Phase == Playing && ShouldSkipActive(Time.unscaledTimeAsDouble, grace)`, log
    `[TurnManager] Skipping <name> — disconnected` and advance through the same code `NextTurn`
    uses.
  - **`NextTurn` and the N key refuse while `!CanAdvance`.** The N key gets a Console warning.
  - **`ReturnToLobby`:** `ReleaseDisconnected()` and publish `PlayerLeftEvent` for each removed id.
  - **Throw boundaries:** subscribe `RockLaunchedEvent` → `BeginThrow(ActivePlayer)`. In
    `OnThrowCompleted`, only advance if `EndThrow(e.PlayerId)` returns true; otherwise log a warning.
  - **`BroadcastSpectate`:** add `"disconnected":[ids]`. Extend `OutboundMessages.BuildSpectate` with
    an optional parameter.
- **`RockLauncher.OnThrow`:** refuse while `_active != null && (_active.IsFlying || _active.IsSinking)`
  (A3). The admission decision moves to group 6.
- **`ScoreManager`** uses `ThrowScoreTally`. `ResetScores` also resets the tally. Remove the dead
  `else` branch.
- **Phone, `app.js` `renderSpectate`:** if `message.activePlayer` is in `message.disconnected`, the
  wait title reads `Waiting for <name> to reconnect…`.

**Done:** Ayden runs Console clean → EditMode green (both test assemblies) → controller suite green.

**Hardware check** (Ayden, two phones A and B, match started):

1. **Drop during flight.** A throws. While the stone flies, lock A's screen. B must **not** get the
   throw screen. Unlock A within 10 s. After A's stone lands, it is B's turn, then A's; the order
   is unchanged.
2. **Drop before throwing.** It's A's turn. Lock A before throwing. B's phone reads "Waiting for A
   to reconnect…". After ~20 s the TV moves to B. Unlock A. On the next round A throws in their
   original position.
3. **Reconnect inside the grace.** Same as 2, but unlock A after 5 s: it's still A's turn and A can
   throw.
4. **Score attribution.** In all three, check that every throw's score goes to the thrower (TV
   results screen).

---

## Group 5 — B5 host resume re-sync, ghost reconciliation

**Files:**
- `Assets/Scripts/Net/RelayClient.cs`
- `Assets/Scripts/Managers/TurnManager.cs`
- `Assets/Scripts/Managers/Match/TurnOrder.cs` + tests

**Tests first** (EditMode): add `TurnOrder.ReconcileWith(IReadOnlyCollection<string> present, double now)`,
which marks seats absent from `present` as disconnected. Tests:
- A relay resume listing `[B]` while seats are `[A, B]` → A is disconnected, B is untouched.
- A restart (`present` empty) → every seat is disconnected, and each seat reconnects as its phone
  rejoins.

**Implement:**
1. **Raise an event on room creation.** `RelayClient` gets `public event Action<RoomCreatedMessage> RoomCreated;`
   raised in the `RoomCreated` case after the existing handling.
2. **`TurnManager` binds it** in `BindRelay`:
   - On `resumed`: `ReconcileWith(players)` and `SendTurnStates`.
   - On a reclaimed/fresh code while a match is in progress: `ReconcileWith(empty)`. Group 4's pause
     and skip then handle anyone who doesn't come back.

**Done:** EditMode green; Console clean.

**Hardware check** (Ayden, two phones, mid-match):
- Turn the PC's Wi-Fi off for 10 s and back on. Both phones return to the correct screen without
  anyone tapping, and it's still the same player's turn.
- Kill the relay (the card from Commands). Unity restarts it. Both phones rejoin, the current
  player's turn is intact, and a phone that is closed during this is skipped after 20 s.

---

## Group 6 — A4/B3/B4 THROW acknowledgement and retry, phone state

### Wire changes

| Type | Direction | Fields |
|---|---|---|
| `THROW` | phone → PC | + `throwId` (int, random 31-bit per throw) |
| `THROW_RESULT` | PC → phone | `targetPlayerId`, `throwId`, `accepted` (bool), `reason` (`ok`, `not_your_turn`, `not_playing`, `stone_in_play`) |

For `THROW_RESULT`, add a type constant, a builder in `OutboundMessages`, and a matching literal.
The relay needs no change yet: host → players is still generic until group 8.

### Unity

- **`ThrowAdmission.cs`** in `RockSkipping.Match`:
  `static ThrowVerdict Judge(bool isPlaying, string activeSeat, string sender, bool stoneInPlay, int throwId, int lastAcceptedThrowId)`.
  **The duplicate check comes first:** a resend of the accepted throw must ack `Accepted` again,
  even while the stone flies, and must **never launch twice**.
- **`RockLauncher.OnThrow`:**
  - Calls `Judge`.
  - Remembers `lastAcceptedThrowId` for the turn, reset on `TurnStartedEvent`.
  - Launches only on a fresh `Accepted`.
  - **Always** sends `THROW_RESULT` via `RelayClient.Send`.
- **`ThrowMessage` gains `public int throwId;`.**

**EditMode tests first** (`ThrowAdmissionTests.cs`):
- one per verdict
- `duplicate_of_accepted_while_stone_flies → Accepted` (flagged duplicate, so the caller doesn't
  launch)
- `different_throwId_while_stone_flies → StoneInPlay`

### Phone

- **New `controller/js/throwSender.js`** (pure; `send`, `setTimeout` and `clearTimeout` are
  injected):
  - `submit(payload)` stamps `throwId` and sends.
  - Resends the same payload every 1000 ms until `onResult(throwId)`, `cancel()` (turn ended), or
    10 attempts.
  - `onReconnected()` resends immediately.
  - A late result for an old `throwId` is ignored.
- **New `controller/test-throw-sender.js`** — tests first:
  - resend until result
  - stops after result
  - stops on cancel
  - caps attempts
  - resends on reconnect
  - ignores a stale result
  - payload unchanged across resends
- **`controller/package.json`:** `"test": "node test-sensors.js && node test-throw-sender.js"`
  (group 7 adds `test-net.js`).
- **`app.js`:**
  - `onThrow` → `throwSender.submit(payload)`. Set `awaitingThrowResult = true`; the result line
    reads `Sending…`.
  - **B3:** `startTurnAim()` returns early while `awaitingThrowResult`. No AIM stream during flight.
  - `THROW_RESULT` handling:
    - **Accepted:** `Thrown!`; keep `awaitingThrowResult` until TURN arrives.
    - **Rejected:** clear it; show `Throw didn't count — tap to aim again` (plus the reason); allow
      the turn-idle stream again.
  - `TURN` → `throwSender.cancel()` and clear `awaitingThrowResult`.
  - **B4:**
    - `JOINED` → `thrower.cancel()`, then `throwSender.onReconnected()`.
    - `HOST_LEFT` → set `myTurn = false` **before** `thrower.cancel()`, mirroring `ROOM_CLOSED`.
      Group 5 guarantees TURN is re-sent on resume.

**Done:** controller suite green; EditMode green; Console clean.

**Hardware check** (Ayden):
1. **Throw during a drop.** Arm, toggle the phone's Wi-Fi off, throw, and turn Wi-Fi back on
   within 5 s. The stone launches **exactly once** after reconnect, and the phone shows `Thrown!`.
2. **Normal throws.** 10 throws in a row: every one shows `Thrown!`, and there are no double stones.
3. **Quiet flight.** While your own stone is flying, the host's debug HUD (F1) shows no AIM rate
   from your phone.

---

## Group 7 — B7 phone liveness

**Files:** `controller/js/net.js`, new `controller/test-net.js`, `controller/package.json`.

**Implement** in `RelayConnection`, with constructor options `pingIntervalMs = 4000` and
`deadAfterMs = 10000`:
- After `JOINED`, send `{type:'PING'}` every interval.
- Record `lastMessageAt` on every `onmessage`.
- If `now - lastMessageAt > deadAfterMs`: `_discardSocket()`, `expectHostBack()`,
  `_setStatus(RECONNECTING)`, `_scheduleReconnect()` — the same path as an unexpected `onclose`.
- Stop the timer in `close()` and `_discardSocket()`.

**Tests first** (`test-net.js`):
- Stub `globalThis.WebSocket` with a fake class exposing `readyState`, `send`, `close`, the `on*`
  handlers and `OPEN = 1`.
- Pass a `url` so `window` is never touched.
- Use small intervals (20/60 ms) with real timers.
- Cases:
  - PINGs are sent after JOINED
  - a silent socket is replaced (a new fake instance is constructed) after `deadAfterMs`
  - a socket receiving PONGs is **not** replaced
  - `close()` stops pinging
  - the 4001 path still doesn't reconnect (regression guard)

Then: `"test": "node test-sensors.js && node test-throw-sender.js && node test-net.js"`.

**Done:** controller suite green; relay suite green, since PING is already answered.

**Hardware check** (Ayden): mid-turn, walk the phone out of Wi-Fi range (or toggle the PC hotspot
off and on). The phone shows `Reconnecting…` within ~10 s rather than staying on a frozen screen,
and recovers when back in range.

---

## Group 8 — C2 message allowlist, B8 re-registration guard

**Files:** `relay/server.js`, `relay/test-relay.js`, `docs/4-systems/Networking.md`

> **This reverses a documented decision.** The relay header says routing is "deliberately
> generic". Update that header and the Networking.md "To add a message" checklist: **adding a
> message is now five points, including the relay allowlist**.

**Tests first** (`test-relay.js`):
- **Spoofed lifecycle messages don't reach the host.** A player sends `ROOM_CREATED`,
  `PLAYER_LEFT`, `PLAYER_JOINED`, `DEVICE_FUNNEL`, `ERROR` and `TURN`, each answered with
  `ERROR`/`UNKNOWN_TYPE`. The player then sends a THROW, and **the host's next message is that
  THROW** (proves nothing spoofed was forwarded).
- **The host can't send phone-only types.** The host sends `AIM` → ERROR; `THROW_RESULT` still
  routes to its target.
- **B8, different room:** a registered player socket sends `JOIN_ROOM` for another room →
  `ERROR`/`ALREADY_REGISTERED`, and the first room still routes to it.
- **B8, different id:** a different `playerId` on the same socket → `ALREADY_REGISTERED`.
- **Same-credential re-join still works.** Sending JOIN_ROOM again on the same socket with the same
  room and id succeeds; `net.js`'s `_scheduleRejoin` relies on this.

**Implement:**
- `PLAYER_TO_HOST = new Set(['AIM','THROW','PICK_ROCK','PICK_COLOUR','SET_NAME','ADVANCE'])`
- `HOST_TO_PLAYER = new Set(['TURN','SPECTATE','THROW_RESULT'])`
- Unknown or wrong-direction types → `sendError(ws, 'UNKNOWN_TYPE', …)`, never forwarded.
- In `handleJoinRoom`: if `ws.meta.role === 'player'` and the room or id differs →
  `ALREADY_REGISTERED`.

**Done:** relay suite green.

**Hardware check** (Ayden): a normal two-phone match — pick rocks, pick colours, rename, advance,
play two rounds. Anything missing from the allowlist shows up here as a dead button.

---

## Group 9 — C3 seat token

**Files:**
- `relay/server.js`, `relay/test-relay.js`
- `controller/js/identity.js`, `controller/js/net.js`, `controller/js/app.js`
- `docs/4-systems/Networking.md`

### Rules (relay)

- **Where tokens live.** Each room holds `seatTokens: Map<playerId, token>` (24 chars,
  `crypto.randomBytes`). **Tokens survive `PLAYER_LEFT`** for the room's lifetime.
- **JOIN_ROOM with no stored token for that id** → accept, mint a token, and return it in `JOINED`.
  This is trust-on-first-use, which also covers the room just after a relay restart.
- **Stored token matches `data.seatToken`** → accept (reconnect).
- **Stored token, missing or wrong `data.seatToken`** → `ERROR SEAT_TAKEN`. **Do not** replace or
  close the current socket.

### Phone

- `identity.js` stores `seatToken` with `room`, and clears it when the room changes.
- `net.js` sends `seatToken` in `JOIN_ROOM` from its credentials, and updates the credentials when
  `JOINED` carries a token.
- `app.js` stores `message.seatToken` on `JOINED` via `updateIdentity`.
- **`app.js` treats `ERROR SEAT_TAKEN` as terminal:** show the join form with "That seat is in use
  on another device", even when `hasJoinedOnce`. Otherwise the retry loop spins forever.

### Tests

**Relay** — tests first:
- the first join gets a token
- a stranger with the same id and no token → `SEAT_TAKEN`, **and the victim still receives a
  targeted SPECTATE**
- a stranger with the wrong token → `SEAT_TAKEN`
- a rejoin with the right token → `reconnected: true` and the old socket closes with 4001
- a token still works after the victim's socket closes (the seat isn't open to a hijacker while
  its owner is away)

**Update the existing tests.** "reconnect reclaims the same slot" and the 4001 test must now pass
the token from `JOINED`. **Changing them without that is not allowed** — they are the guard against
the join ping-pong.

**Controller:** add a `test-net.js` case — `JOIN_ROOM` includes `seatToken` after `JOINED` supplied
one.

**Done:** both suites green.

**Hardware check** (Ayden):
1. Join on phone A. On a laptop browser, copy phone A's device id from a SPECTATE (devtools
   network tab) and try to join with it via the console. It gets refused, and phone A keeps playing.
2. Phone A: reload the page. It rejoins its own seat silently.

---

## Group 10 — B1 AIM latest-wins, B2 rate-limit budgets

**Files:**
- `controller/js/app.js`, `controller/test-net.js`
- `relay/server.js`, new `relay/test-limits.js`, `relay/package.json`

### B1

- `sendAim` → `connection?.sendIfClear(...)`.
- Tests in `test-net.js`:
  - `sendIfClear` skips while `bufferedAmount > 0` and sends when it is 0
  - **a wiring guard** reads `js/app.js` as text and asserts `sendAim` calls `sendIfClear`. This is
    deliberately a source check: `app.js` is DOM-bound and cannot be imported in Node, and this is
    the exact regression B1 was.

### B2

Replace `isRateLimited` with **two token buckets per socket**, and add `drops: { aim, control }` to
`/health`:

| Bucket | Covers | Player capacity / refill | Host |
|---|---|---|---|
| lossy | `AIM` | 40 / 40 per s | ×10 |
| control | everything else | 30 / 15 per s | ×10 |

- **Lossy drops are silent.**
- **Control drops send `ERROR RATE_LIMITED`**, so group 6's `throwSender` retry covers them.
- Fix the "rolling-window" comment.

**Tests first** — `test-limits.js`, a separate spawned server on port 3998 so limits can't disturb
`test-relay.js`:
- A player bursts 150 AIMs, then 1 THROW → **the host receives the THROW**, and
  `/health.drops.aim > 0`.
- 200 control messages in a burst → at least one `RATE_LIMITED`.
- A steady 20 Hz AIM stream for 2 s → zero drops.

Then `relay/package.json` test script: append `test-limits.js`.

**Done:** both suites green.

**Hardware check** (Ayden, on the school network if possible): aim continuously for 30 s. The
ghost doesn't replay old movement after a stall, and F1 shows the AIM rate recovering.

---

## Group 11 — Hardening sweep: C6, C5, B9, B10, B12

Each item is independent. **Commit after each** if Ayden prefers smaller commits.

### C6 — sanitise names

- **Where:** `DisplayNameRules.Sanitize(string) → string` in `RockSkipping.Match`.
  - trim
  - strip control characters and `<` `>` (IMGUI rich text)
  - collapse whitespace
  - cap at 20 characters
  - empty result → null
- **Call it** from `PlayerNames.OnSetName`.
- **Phone:** set `maxlength="20"` on the name inputs in `controller/index.html`.
- **EditMode tests:** rich-text name → tags stripped; 50 characters → 20; control characters
  removed; whitespace-only → null.

### C5 — connection limits

- Per-IP concurrent socket cap: `MAX_SOCKETS_PER_IP`, default 16.
- Unregistered socket closed after 15 s.
- A socket closed after 10 failed `JOIN_ROOM`s.
- All env-overridable. Tests go in `test-limits.js`, with the server spawned at small limits.

### B9 — join waiter

- Move `joinOnceConnected` into `RelayConnection.joinWhenOpen(code, id)`. It **replaces** any pending
  waiter.
- Test in `test-net.js`: three calls while connecting → **exactly one** `JOIN_ROOM` on open.

### B10 — `RelayClient` receive and send

- **Receive:** accumulate bytes in a `MemoryStream` and decode once at `EndOfMessage`.
- **Send:** track pending sends with `Interlocked`. Above 64, drop and log a rate-limited warning.
- Unity only, no automated test possible. Ayden checks: Console clean, and a match with a player
  named `Zoë🙂` shows the name intact on the TV.

### B12 — AimPreview rejection logs

- Don't log phase rejections while in `Lobby`/`Choosing` (expected traffic).
- Active-player rejections log display names.
- Ayden checks: the lobby with two phones pointing produces no `Rejected AIM` spam.

**Done:** both npm suites green; EditMode green; Console clean.

---

## Group 12 — A7 swing-speed inflation: measure, then STOP

**Do not change the integration until Ayden decides.** A correct speed will be much lower, and
every throw's distance changes with it.

1. **Build a tool.** `controller/js/capture.js`, active only with `?capture=1`, keeps the last swing's
   raw samples. A **Copy capture** button in the debug panel puts them on the clipboard as JSON.
   Nothing new is sent over the network.
2. **Add a pure analysis function.** In `throw.js`, `analyseSwing(samples, tuning)` returns:
   - the integration start index
   - the window duration
   - the peak speed
   - the speed if the window were limited to a 250 ms lead-in before onset

   Unit-test it against synthetic samples with a constant 5 m/s² residual bias. **The current method
   must be shown to inflate**, and the bounded window must not.
3. **Hand Ayden a card:** throw 10 skims with `?capture=1&debug=1` and paste the captures into
   `docs/plans/Impl-NetworkAndThrowFixes.md` under an "A7 measurements" heading.
4. **STOP.** Report both speeds per throw. Ayden then chooses one of:
   - leave it
   - switch to the bounded window and retune `SpeedScale` to preserve feel
   - something else

---

## Group 13 — Docs sweep

- **`docs/4-systems/Networking.md`:**
  - the wire table: `THROW_RESULT`, `throwId`, `seatToken`, `PING` liveness, `disconnected` in
    SPECTATE
  - the allowlist and the new five-point "add a message" list
  - rate budgets
  - the verification counts
- **`docs/4-systems/MotionPipeline.md`:** the three guard tunables and their `?query` overrides.
- **`docs/4-systems/ScoringAndTurns.md`:**
  - seats persist through a drop
  - the 20 s pause and skip
  - one stone at a time
  - **B6 won't-fix: no idle timeout, by decision on 2026-09-10**
- **`docs/4-systems/SkipPhysics.md` and `Assets/CLAUDE.md`:** SpeedScale is 1.0, and why. Also
  `RockSkipping.Match` as a second pure assembly with EditMode tests.
- **`docs/Findings-NetworkAndThrowStability.md`:** a status line on every ID.
- **`docs/3-state/ProjectState.md`:** only if a milestone's status changed.

**Done:** every `file:line` cited in the changed docs resolves against the working tree.
