# Continuous calibration and intuitiveness pass

**Status:** D1, D2, D3, D4, D7 and D8 shipped (phone-side only) — see
`controller/js/sensors.js` (`MotionSource`'s Settling/Aiming state machine, `CalibrationTuning`),
`controller/js/throw.js` (`ThrowController.arm()`), and `controller/js/app.js`/`index.html` (the
tilt indicator, status badge, haptics, arm-progress ring and post-throw recap). D5 (TV mirror
diagram) and D6 (explicit practice toss) remain deferred — no Unity C# was touched this pass; the
wire format is unchanged. `CalibrationTuning`'s rotation-rate thresholds and sample counts are
seed values, not yet felt out on a real phone.

## Context

Calibration today is a single invisible snapshot. `MotionSource.calibrate()`
(`controller/js/sensors.js:462-473`) captures one orientation sample the instant the player taps
the grip button, and `ThrowController.arm()` (`controller/js/throw.js:433-446`) calls it as part
of arming. Every subsequent tilt is read as a delta from that one frozen sample. If the tap itself
catches a jolt, a settling hand, or an awkward wrist angle, that noise is permanently baked into
the reference for the whole throw — and there is no on-screen way for the player to see *why*
their aim reads wrong. Their only recourse is to tap again and hope the next instant is cleaner.

The existing tutorial slide "Your tap is the zero" (`controller/js/app.js:546-556`) already tells
players this is how it works. That's the right mental model to keep — the problem isn't that
players don't understand what a tap does, it's that the mechanism the slide describes is noisier
than it needs to be, and there's no live feedback loop to make a bad zero visible or fixable in
the moment.

There is also no feedback on the phone itself during aiming — everything the player can currently
see lives on the TV, across the room — and several smaller gaps exist between what the motion
sensors are actually doing and what the player understands is happening. This document collects
both: the core calibration redesign, and a survey of smaller intuitiveness ideas surfaced
alongside it.

## D1 — Replace the snapshot with steadiness-gated continuous calibration

The core decision: instead of one tap-triggered snapshot, run a three-state model that extends
the same threshold-and-hysteresis idiom the swing detector already uses (`swingOnsetSamples`,
`stopSamples` in `ThrowTuning`, `controller/js/throw.js:37-89`). This is deliberately not a new
kind of logic — it's the same pattern the codebase already trusts for swing detection, applied to
the reference orientation instead.

| State | Trigger | Reference behaviour |
|---|---|---|
| **Settling** | Rotation rate below a small threshold (phone held roughly still) | Reference continuously low-pass-tracks current attitude — "zero" quietly follows wherever the phone actually rests |
| **Aiming** | Rotation rate rises above that threshold (deliberate tilt) | Reference **freezes** at its last value; tilt from here reads as aim, exactly as today |
| **Swinging / released** | Existing `swingAccel`/`stopAccel` detection fires | Unchanged |

The obvious failure mode to worry about with any "continuous" calibration is a filter that's fast
enough to erase hand tremor but slow enough to also erase a deliberately held aim tilt — which
would mean tuning a decay window against a 1-3 second held aim, a genuinely hard number to pick
well. Gating on rotation rate rather than on a time constant sidesteps that problem entirely: the
reference simply stops moving the instant the player starts tilting on purpose, because tracking
only happens during genuine stillness. There's no decay window to fight, because there's no decay.

The practical effect on feel is that a player no longer needs to land the tap pose exactly right —
the reference has already been quietly following their actual grip the whole time leading up to
it. This also makes the existing "tap again to disarm and re-aim" escape hatch largely redundant:
the condition it exists to recover from (a bad tap-instant snapshot) mostly stops happening.

**Lower-risk fallback variant**, worth recording alongside the three-state model: keep tap-to-lock
as the trigger, but instead of reading one raw orientation sample at the tap instant, average the
trailing ~150-250ms of samples up to the tap. This keeps the exact same mental model — "your tap
is the zero" stays literally true — while removing the single-sample-noise problem that motivates
this whole document. It's a much smaller change than the three-state model, and is what to ship
first if the full state machine is judged too large a step to take in one go.

## D2 — On-phone live tilt indicator during Aiming

A simple level/bubble readout shown while Aiming, so the player gets feedback on the same screen
they're actually looking at while tilting, rather than only across the room on the TV. Today all
aim feedback lives on the TV; this puts the first read of "am I tilting the way I think I am" in
the player's hand.

## D3 — Three-state status text extending the existing button label

The grip button already swaps its label based on state (`TAP TO AIM` / `SWING!`,
`controller/index.html:535`, `app.js:184-185`). Extending that same element with a third label —
something like "Settling…" / "Aim locked" — gives the player a visible cue for which of the three
states they're in without introducing a new UI element to design, place, and test.

## D4 — Haptic pulses on state transitions

The Vibration API is entirely unused in `controller/js/*` today. A short tick on the
Settling→Aiming lock, and a distinct one on release, would give eyes-free confirmation of the two
moments that matter most, for near-zero implementation cost.

## D5 — First-throw-only TV mirror diagram

A small live phone-tilt→stone-tilt diagram, overlaid on the TV only for a player's first throw of
a match. This teaches the cause-and-effect relationship during real play, with a real throw on the
line, rather than relying solely on the pre-game tutorial slides to have made it stick.

## D6 — An explicit practice toss before a player's first scored throw

Reframes the existing disarm/re-arm path as a sanctioned warm-up rather than only an
error-recovery move a player discovers by accident. The mechanism already exists; this decision is
about surfacing it as an intended step the first time a player picks up the phone.

## D7 — Visible steady-hold gate before the swing can arm

The existing `armGraceMs` guard (`controller/js/throw.js:47-49`) already requires a moment of
steadiness before arming, but it's invisible — a timing rule the player can't see and has no way
to anticipate. A small fill-ring on the phone that fills as the hold requirement is satisfied turns
that same guard into something the player can watch happen, instead of an invisible rule they only
notice when they trip it.

## D8 — Post-throw numeric recap on the phone

A short recap after each throw — for example "swing: fast · release: 12° low · light spin" —
pairs the TV's pass/fail outcome with the inputs that produced it. The TV shows what happened; this
shows why, on the device the player was actually holding, so players can build intuition about
their own motion over repeated throws instead of guessing at what to change.

## What this plan does not decide

- The exact rotation-rate threshold and hysteresis sample counts for the Settling/Aiming
  transition — these are seed values, to be tuned by feel once the state machine exists, the same
  way `docs/plans/presentation-and-ui.md` defers its own concrete colour values.
- Which of the two calibration variants — the full three-state model (D1) or the trailing-average
  fallback (D1) — ships first.
- The exact haptic pulse durations and patterns for D4.
- Whether the practice toss in D6 is opt-in or forced on a player's first throw.
