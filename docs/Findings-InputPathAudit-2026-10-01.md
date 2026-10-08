# Findings: phone → relay → game input path (2026-10-01)

**Prompted by:** Ayden's report that input drops more often, the Console shows more "Rejected AIM
frame" lines, and the stone sometimes vanishes mid-swing and then launches about a second later,
after the swing is over, often pointing backwards. Separately, iPhones show a pop-up on almost
every touch.

**Method:** I read the whole input path in code: `controller/js/{app,net,throw,sensors}.js`,
`controller/index.html`, `relay/server.js`, `Assets/Scripts/Net/RelayClient.cs`,
`Assets/Scripts/Game/{AimPreview,RockLauncher,RockController}.cs` and
`Assets/Scripts/Managers/TurnManager.cs`. I checked the code against
[`Findings-NetworkAndThrowStability.md`](Findings-NetworkAndThrowStability.md) (2026-09-10) and
against `git log` since that date.

**Evidence available:**

- **No runtime logs survive.** Unity's `Editor.log` is reset on each launch and holds no play
  session.
- **`rock-throws.csv` has one player only.** Every row since 09-30 is device `4XO7R9DH4R33`.
- **Baseline suites were green before any change.** That was controller `npm test` and relay
  `npm test` (48/48).

**Confidence labels:**

- **REPRODUCED** means a failing test was written against the current code before the fix.
- **CONFIRMED** means the code path exists exactly as described.
- **PLAUSIBLE** means the mechanism exists and fits the symptom, but has not been seen happening.

## What the 09-10 plan left undone

That plan ([`plans/Plan-NetworkAndThrowFixes.md`](plans/Plan-NetworkAndThrowFixes.md)) landed
groups 0–3 only: the relay crash, SpeedScale, and the accidental-throw guard. Groups 4–13 never
landed. None of the following exists in the code today:

- `THROW_RESULT`
- seat holding
- PING liveness
- the message allowlist
- AIM latest-wins

The findings below are either new since 09-10, or old ones that turn out to cause today's
symptoms.

## Symptom → cause

| Symptom | Cause | Finding |
|---|---|---|
| Stone launches "backwards", or way off line | Aim reference re-centres at the top of the backswing | **N1** |
| Launches about a second after the swing ended | 900 ms timeout fire reads aim from the follow-through | **N2** |
| Input dropped | Phone lets you arm and throw while your stone is still out; the TV drops or doubles it | **N3**, **N4** |
| Ghost vanishes mid-swing, then everything lands at once | No latest-wins on AIM: a Wi-Fi stall queues frames and THROW waits behind them | **N5** |
| Throw lost after a stall | Relay rate limit drops THROW like any AIM frame | **N6** |
| "Rejected AIM" in the Console | Lobby, Choosing and Results frames log as rejections; it's noise | **N7** |
| iPhone pop-up on every touch | Safari text-selection / callout is not disabled | **N8** |

## Findings

### N1 — Aim reference re-centres mid-swing while armed (regression, `1163575`, 09-17) — REPRODUCED

`MotionSource._advanceCalibration` (`controller/js/sensors.js:691-705`) drops back from
**Aiming** to **Settling** after `settleSamples` (12, about 200 ms) under
`settleRateThresholdDegS` (8°/s). Settling then nlerps the reference toward the current pose with
a 175 ms time constant (`:668-678`).

**That holds while armed.** A normal pause at the top of the backswing is longer than 200 ms, so
the reference slides to the backswing pose. The forward swing is then measured from there, and
the release attitude comes out rotated by the whole backswing angle. That's the "backwards"
throw. The ghost swings off by the same amount mid-gesture.

Before `1163575` the reference was frozen at the tap. Issue #23 fixed the same mechanism for the
lobby pointer (`trackingEnabled = false`), but nothing stops it once a throw is armed.


**Status:** fixed in `62dfc30` (#53), test `a pause at the top of the backswing does not slide the reference` (39.5° before, 1.6° after). **Not yet checked on a phone.**

### N2 — A timeout fire aims with the follow-through — CONFIRMED (path) / PLAUSIBLE (frequency)

**How the timeout fires.** `ThrowController._onSample` fires `maxSwingMs` (900 ms) after the peak
if three calm samples never arrive (`controller/js/throw.js:599-602`).

**Where its aim comes from.** `computeThrowDescriptor` reads attitude from the **last** sample
(`throw.js:177`, `:195`). On the calm-stop path that's correct: the stop is the release. On the
timeout path, the last sample is 900 ms into the follow-through, so the stone launches about a
second late, aimed wherever the arm ended up.

**Why it's plausible.** Residual gravity on the `exact` path (A5 in the 09-10 doc) is a known
reason the calm run can fail to arrive.


**Status:** fixed in `f72ac51` (#54), test `the timed-out throw takes the release attitude, not the last sample`. **Not yet checked on a phone.**

### N3 — The phone re-offers TAP TO AIM while your own stone is still out — CONFIRMED

**The phone side.** After `_fire()`, the IDLE transition calls `startTurnAim()`
(`controller/js/app.js:315-318`). `myTurn` is still true, because TURN only moves on
`ThrowCompletedEvent`. So the grip button is live again, and the phone streams AIM through the
whole flight, sink and settle. That's B3 from 09-10.

**The TV side.** A second throw in that window is either:

- silently dropped by the TV while the first stone flies (`RockLauncher.cs:133`), or
- launched as a second stone while the first is sinking (N4).


**Status:** fixed in `7c3ecea` (#55). Checked in a desktop browser against `relay/fake-host.js`, two throw cycles: one THROW per throw, extra taps sent nothing, the button unlocked on the next TURN. The AIM-stop part cannot be seen on a desktop (no sensors).

### N4 — The TV launches a second stone while the first is sinking — CONFIRMED

`RockLauncher.OnThrow` refuses only while `IsFlying` (`Assets/Scripts/Game/RockLauncher.cs:133`).
`IsSinking` stays true through the settle, until `ThrowCompletedEvent`
(`RockController.cs:296-309`).

**What a throw in that window does:**

- **The stone count.** It overwrites `_active`, and the old stone's completion then advances the
  turn while the new stone is in the air. That's A3 in the 09-10 doc.
- **The depth flag.** It flips the water depth flag under the sinking stone, which can make a
  stone disappear.
- **Silence.** The drop is never logged.


**Status:** fixed in `fae1824` (#56). Compiled by Unity batch mode; EditMode 18/18 green. **Not exercised in Play mode.**

### N5 — AIM is not latest-wins — CONFIRMED (B1, 09-10)

**The unused helper.** `RelayConnection.sendIfClear` (`controller/js/net.js:222-227`) is still
uncalled.

**What happens in a stall.** Every AIM goes through `send`, so a Wi-Fi stall queues frames in
TCP. The TV sees no frames for over 0.5 s, so `staleAfterSeconds` hides the ghost
(`AimPreview.cs:60`, `:348`). The backlog then arrives in one burst, and the THROW, queued behind
it, lands late.


**Status:** fixed in `72f53b2` (#57), test `skips while bytes are still queued`.

### N6 — The relay rate limit counts THROW like AIM — CONFIRMED (B2, 09-10)

`isRateLimited` runs before the message type is read (`relay/server.js:682`). It uses a fixed 1 s
bucket of 60 and drops silently. A stall's burst plus steady 20 Hz streaming can fill the bucket,
and the THROW behind it is discarded.


**Status:** fixed in `5c81e62` (#58), test `fix: an AIM burst cannot rate-limit the THROW behind it` (failed before, passes after; relay 49/49).

### N7 — "Rejected AIM" is mostly noise — CONFIRMED (B12, 09-10)

The phase guard (`AimPreview.cs:413-417`) logs every lobby, Choosing and Results frame as a
rejection. ShoreRocks consumes those same frames legitimately. In solo play this is the only
source of the line, so it says nothing about lost input.


**Status:** fixed in `391c985` (#59). Compiled; EditMode 18/18 green.

### N8 — iOS Safari selection callout — CONFIRMED (by reading) / not reproduced on a device

The page sets `user-select: none` unprefixed (`controller/index.html:41`). Safari only honours
`-webkit-user-select`. There is also no `-webkit-touch-callout: none`. So on iPhone, a press
(including the finger resting on the screen through a swing) opens the selection loupe and the
Copy / Look Up callout.


**Status:** fixed in `314c07f` (#60). Computed styles checked in Chromium. **Needs an iPhone to confirm the pop-up is gone.**

## Deferred: still open from the 09-10 plan

None of these is needed to explain a solo session's symptoms, and each is a larger change:

- **A2** — a socket drop removes the player and reshuffles turns. With one player, it returns the
  match to the lobby.
- **A4** — THROW has no acknowledgement or retry.
- **B4** — armed state survives a reconnect.
- **B5** — host resume does not re-sync phones.
- **B7** — the phone has no PING liveness.
- **C2** — message allowlist.
- **C3** — seat token.
- **A7** — swing-speed inflation. The CSV now reads 50–65 m/s.

## Status

Tracked as #52 (parent) with #53–#60, one per finding N1–N8, in order.
