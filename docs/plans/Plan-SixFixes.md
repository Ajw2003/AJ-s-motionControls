# Plan — six fixes: sink presentation, throw trigger, UI blocking, player colours, colour persistence, rock-choosing settings

## Context

Six issues raised after playtesting the energy-invariant physics, plus a stale documentation line
found while investigating them.

Work continues on branch `MergeTest2`, one commit per numbered item, pushed after each. Item 0 of
execution is copying this plan to `docs/plans/Plan-SixFixes.md` so it is tracked in the repo.

---

## 0. A stale line in the project docs

`Assets/CLAUDE.md:172` claims the project went "URP → built-in render pipeline". That is
backwards: `Packages/manifest.json` pins URP 17.3.0 and all six quality levels in
`ProjectSettings/QualitySettings.asset` point at `RockSkippingUrp.asset`, so **URP is active**.
`docs/Migration-URP.md` is titled "Built-in → URP migration" and `docs/Findings-URPVisuals.md`
records it as applied.

This matters for item 4: a Built-in surface shader renders magenta under URP, so anyone reading
that line before touching rendering would build the wrong thing. One-line correction.

*(An earlier draft of this plan called `Color.gray3` at `RockLauncher.cs:168` a compile error. It
is not — `UnityEngine.Color` in 6000.3.15f1 exposes `gray1`..`gray9`, confirmed in the installed
`UnityEngine.CoreModule.dll`. The project compiles and the playtest feedback stands.)*

---

## 1. A sunk stone reads as a glitch

Three independent causes, all confirmed in code.

**The turn advances ~16 ms after contact.** `RockController.Finish()` (`RockController.cs:197-213`)
publishes `ThrowCompletedEvent` synchronously on the sinking step;
`TurnManager.OnThrowCompleted` (`TurnManager.cs:304-311`) calls `NextTurn()` immediately. No
delay anywhere in the chain.

**The camera abandons the stone before it sinks.** `Finish()` sets `IsFlying = false` on line 199,
one line *before* publishing. `RockLauncher.FollowActiveRock` (`RockLauncher.cs:111-139`) gates on
`_active.IsFlying`, so the return-home starts on the same frame the sink animation begins. Worse,
`_cameraVelocity` is shared between the follow and return branches and never reset at handover, so
the full chase velocity is fed into the return `SmoothDamp` as initial velocity — the camera
continues forward, then whips back. That is literally the "jerks back". Position uses `SmoothDamp`
while rotation uses a framerate-dependent `Slerp`, so they arrive on different curves.

**There is no sink feedback.** A `SinkRoutine` does exist (`RockController.cs:233-258`, 1.5 m over
0.6 s) but `WaterRipples` subscribes only to `SkipOccurredEvent` (`WaterRipples.cs:96`) — the one
impact that *ends* a throw is the only impact that leaves no ring. `RockSunkEvent`'s only
subscriber in the whole project is `SfxDirector`.

**Fix (chosen: hold + ripple + delay):**

- Split `Finish()`: keep `RockSunkEvent` + `SinkRoutine` immediate, move `ThrowCompletedEvent` to
  the end of a new settle coroutine (~1.2 s, serialized field so it is tunable).
- Keep `IsFlying` true until the sink animation completes, or better, add an explicit
  `IsSinking` the camera also follows — so the camera holds the stone through its descent.
- Reset `_cameraVelocity = Vector3.zero` at the follow→return handover, and drive rotation with the
  same smoothing model as position.
- Keep writing the camera after `_active` is destroyed: cache the return target so `Update` does
  not stop mid-move when `Destroy` nulls `_active` at `despawnDelay`.
- Subscribe `WaterRipples` to `RockSunkEvent` for a ring on the final impact — reuses the existing
  ripple path, no new art.

Files: `Assets/Scripts/Game/RockController.cs`, `RockLauncher.cs`, `WaterRipples.cs`.

---

## 2. Premature throws — research first, per your instruction

### The gravity-path argument is largely DEBUNKED

The claim was that phones falling back to `GravityFilter` (τ = 75 ms) suffer up to ~19.6 m/s² of
phantom acceleration, which would exceed the 14 m/s² swing threshold on its own.

- **The 19.6 figure is sound arithmetic, not a citation.** It is 2 g — the worst case when a lagged
  gravity estimate points opposite to true gravity (`9.81 − (−9.81)`). It is a bound, reachable
  only if the filter is fully inverted, not a typical error.
- **But that path is almost certainly not being taken.** WebKit's own iOS source
  (`WebCoreMotionManager.mm`) populates `DeviceMotionEvent.acceleration` from CoreMotion's
  `userAcceleration` whenever `deviceMotion` is available — i.e. on any device with a gyroscope,
  which is every iPhone since the 4. It only falls back to raw `accelerometerData` (leaving
  `acceleration` null) on gyroless hardware.
- **So the code comment at `controller/js/sensors.js:312` — "expected to return null throughout on
  iOS" — is wrong**, and the `'hardware'` branch is what actually runs on iOS.
- **Android likewise**: Chrome feeds `acceleration` from Android's fused `TYPE_LINEAR_ACCELERATION`,
  present on gyroscope-equipped devices.

**Conclusion:** on both your platforms the `'filter'` fallback is unlikely to be in play, so the
gravity artefact does not explain the bug. That it reproduces on *both* iOS and Android points at
platform-independent logic — and there is plenty.

### The actual cause: the trigger latches on a single sample

`controller/js/throw.js:478-513`:

```js
if (sample.power >= 14) { this._swinging = true; ... return; }   // one sample latches
if (!this._swinging) return;
if (sample.power <= 4) { if (++this._calmRun >= 3) this._fire(); }  // ~50 ms of stillness
```

- `_swinging` is set by **exactly one sample** and is cleared only by `arm`/`cancel`. Any single
  spike — a knock, a grip adjustment, the mechanical impulse of the arming tap itself — latches it
  for the whole turn. From then on the player only has to hold still for ~50 ms to throw.
- **No arming grace period.** `arm()` begins detection on the very next sample;
  `motion.calibrate()` clears the buffer but not the detector.
- **`maxSwingMs: 900` is a second trigger** — line 512 fires unconditionally 900 ms after the peak
  if power merely stays in the 4–14 band, i.e. while lining up.
- **`minValidPeakAccel: 3` is dead code** — reaching `_fire()` guarantees a sample ≥ 14, so it can
  never reject anything.
- `_fire()` reads the whole **1.5 s** ring buffer, so a spurious trigger builds a throw out of
  aiming data, and `spinRate` is the largest twist anywhere in that 1.5 s.

**Fix:**

- Require a **sustained** swing: N consecutive samples above `swingAccel` (or an integrated
  impulse) rather than one, and decay `_swinging` if the swing does not continue.
- Add an **arming grace period** (~250–300 ms) after `arm()` before the detector can fire.
- Bound the release window: compose the throw from the samples around the peak, not the whole ring.
- Make `maxSwingMs` require the peak to have been genuine rather than firing on any stale peak.
- Add the **`gravitySource` diagnostic** to the phone UI (the field already exists at
  `sensors.js:570`, nothing surfaces it) so the debunking above can be confirmed on your actual
  handsets rather than trusted.
- **Add the missing test**: `controller/test-sensors.js` has no case for "one isolated spike, then
  stillness" — the exact signature. Every current negative test either never crosses 14 or keeps
  moving.

Files: `controller/js/throw.js`, `controller/js/sensors.js` (comment correction + diagnostic),
`controller/js/app.js`, `controller/test-sensors.js`.

Findings get written to `docs/Findings-ThrowTrigger.md` with the WebKit/MDN citations, since you
asked for the research documented.

---

## 3. UI blocks the view

Two separate offenders:

- **`RelayDebugHud`** — an opaque **460×460 box, top-left, on by default** (`Bootstrap.cs:43`).
  Its own comment at `RelayDebugHud.cs:13` says "Scaffolding, not shipping UI", and the Bootstrap
  tooltip says "turn off for real play". **Default it off.** One line.
- **`JoinScreen`** — shrinks on *phase*, never on headcount (`JoinScreen.cs:315-316`), so during
  Lobby it is a 60% × 84% panel at alpha 0.92 no matter how many players joined; during a match it
  is a permanent ≤340 px top-right card with no fade or timeout.

**Fix:** make the full→compact decision headcount-aware as well as phase-aware, shrink the compact
card and drop its alpha, and let it fade out a few seconds after the last player joins while
staying re-summonable (F2 already toggles).

`docs/Rule-RenderQR.md` is a binding rule but it mandates *never making someone type a URL* — it
does not mandate a full-screen panel. Shrinking, moving, fading and auto-hiding are all compatible;
removing the QR from the join flow is not. The card stays reachable for latecomers.

Files: `Assets/Scripts/UI/JoinScreen.cs`, `Assets/Scripts/Managers/Bootstrap.cs`.

---

## 4. Player colours on selectors and stones

**No new shader is needed for the basic ask, and I'd rather say so than build one.** The rock
material already comes from `PlaceholderArt.Create` (`PlaceholderArt.cs:143-152`), which makes a
**URP/Lit** material with a settable main colour and correct render queue. Tinting a stone per
player is a colour assignment, not a shader problem.

Where a shader *would* earn its place is showing the rock's own `Tint` and the player's colour at
the same time. Cheaper route that reuses what exists: put the player colour on **emission** via
`PlaceholderArt.PrepareEmission`/`SetEmission` (`PlaceholderArt.cs:83-101`, which already handles
the `_EMISSION` keyword trap), so the stone keeps its material colour and gains a coloured glow.
I'll do that, and only write a custom URP shader if you want a proper rim light after seeing it.

If a custom shader does happen later: it must be **URP** (a Built-in surface shader renders magenta
here), checked in under `Assets/Resources/`, loaded with `Resources.Load<Shader>` not
`Shader.Find` (`PlaceholderArt.cs:157-168` documents why), and its colour property tagged
`[MainColor]` or `PlaceholderArt.Create` silently stops tinting.

**Selection clarity** is a real second bug. `ShoreRocks.UpdateStoneLooks` (`ShoreRocks.cs:322-338`)
collapses every player into two booleans via `_pointers.Values.Any(...)`, so three players
highlighting three stones produce three identical yellow stones, and `confirmedHere` suppresses
`highlightedHere` entirely — a player pointing at a stone someone already confirmed gets **no
feedback at all**. Fix: tint the highlight/confirm by the owning player's colour, and stop letting
confirmed suppress highlight.

**Colour picking (chosen: free picker).** An `<input type="color">` on the phone's join screen,
sent as a new `PICK_COLOUR` message mirroring `PICK_ROCK` exactly (`Messages.cs:136-140` is the
template; the relay needs **no change** — `relay/server.js:276-280` stamps `playerId` and forwards
player→host messages opaquely). Unity validates the hex and is authoritative. Broadcast back in the
existing `SPECTATE` `"rocks"` sub-object pattern (`Messages.cs:253-265`).

One caveat I'll note but not act on unless you say: a free picker lets someone choose near-black or
near-water-blue and become invisible. A luminance floor is one clamp if you want it later.

**Security:** player-supplied colour must be validated as a hex triple before it reaches any
`style` attribute — `app.js:468-473` already documents the equivalent discipline for names.

Files: `controller/index.html`, `controller/js/app.js`, `controller/js/net.js`,
`Assets/Scripts/Net/Messages.cs`, `RelayClient.cs`, `Assets/Scripts/Game/ShoreRocks.cs`,
`RockLauncher.cs`, new `Assets/Scripts/Managers/PlayerColours.cs`.

---

## 5. Colours shuffle between rounds

Root cause found, `ShoreRocks.cs:356`:

```csharp
PointerPalette[Mathf.Abs(playerId.GetHashCode() + ordinal) % PointerPalette.Length]
```

`ordinal` is `_pointers.Count` at creation — **arrival order of the first AIM frame in this
choosing phase**, a race between phones, not an identity. And `_pointers` is wiped on every entry
into a choosing phase (`ShoreRocks.cs:279-293`) and pruned after a 1.5 s AIM timeout
(`ShoreRocks.cs:208-226`). So the hash part is stable and the `+ ordinal` part is not — exactly the
"semi-inconsistent" character you described. Putting the phone down mid-lobby re-rolls your colour.

**Fix:** a new persistent `PlayerColours` manager holding `Dictionary<string, Color>` keyed by
playerId, created in `Bootstrap` **between `RelayClient` (line 89) and `RockSelection` (line 106)**,
cleared **only** on `PlayerLeftEvent` — never on `MatchPhaseChangedEvent`. Copy the reconnect guard
at `RockSelection.cs:74-77`. `ShoreRocks` then reads colour from there and the `+ ordinal` term
disappears. Item 4's picker writes into the same store, so 4 and 5 are one mechanism.

---

## 6. Rock-choosing settings, and the rounds/interval break

**It is broken on your disk right now.** `match-settings.json` has `roundsPerMatch: 2`;
`reChooseEveryRounds` defaults to `3` (`TurnManager.cs:63`). In `NextTurn()` (`TurnManager.cs:325-346`)
the `EndMatch()` check runs **before** the re-choose check, so when the interval ≥ round count the
pause is unreachable — the setting looks configured and silently does nothing.

`reChooseEveryRounds` is also **Inspector-only**: private, no public property, absent from
`MatchSettings`, absent from `SettingsData`, never persisted. It resets every run.

**Fix:**

- Public clamped property on `TurnManager` mirroring `RoundsPerMatch` (`TurnManager.cs:133-137`).
- Third field on `MatchSettingsStore.SettingsData`; `Save`'s positional signature and
  `MatchSettings.Save()` change together.
- New row in `MatchSettings.BuildSettingsList()` — the settings UI is fully generic
  (`MatchSettingsScreen.cs:115-135` loops over `settings.All`), so **zero UI edits**.
- **The cross-validation you asked for:** `IntSetting._max` is a `readonly int`
  (`Setting.cs:76`), so a ceiling that tracks another setting needs a `Func<int>` max. Enforce on
  **both** setters — lowering `roundsPerMatch` must also pull the interval down, not just the
  reverse.
- Add a "0 = off" display so the disabled state is legible, and make it interact sanely with
  Random Rocks (with random on, `RockSelection.Pick` refuses every tap — the pause still happens
  but does nothing, which is its own confusion).

---

## Verification

- **Compile** — Editor Console must be clean after each item. No batch-mode check exists; Unity
  holds the project lock, so this is a manual check on your side.
- `cd relay; npm test` and `node controller/test-sensors.js` — must stay green; item 2 adds cases.
- `RockSkipping/Verify/Print Skip Curve` — unchanged by this work, but confirms physics is intact.
- **Play it** (`pwsh ./deploy/local-https.ps1`, Play on `Bootstrap`): a failed throw holds, ripples
  and sinks before the turn moves; throws fire only when intended; the lake is visible; two phones
  show distinct colours that survive a round boundary and a phone sleep; the settings panel exposes
  the re-choose interval and refuses to exceed the round count.
