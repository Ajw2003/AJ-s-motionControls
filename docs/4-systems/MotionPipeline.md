# Motion pipeline

**Source:** `controller/js/sensors.js`, `throw.js`, `app.js`;
`Assets/Scripts/Game/Physics/PhoneAttitude.cs`

## What it owns

Turning what the player physically does with their phone into a rotation and a throw that Unity
can use — with **no platform difference visible to Unity**.

## How it works

### The governing rule

**Phones normalize before sending.** Every iOS/Android difference is resolved on the phone. Unity
receives one shape of data and never branches on platform. If a platform quirk reaches Unity, that
is a bug in the controller, not something to special-case in C#.

### The path

```
DeviceOrientationEvent          reference quaternion, tracked continuously (Settling ⇄ Aiming)
DeviceMotionEvent          ─┐
                            ├─▶  delta = current ⊗ reference⁻¹   (on the phone)
                            │         ↓ four floats on the wire
                            └─▶  PhoneAttitude.ToUnity(w,x,y,z)  (one basis change)
                                      ↓
                                 applied to the stone verbatim
```

Tilt the phone ten degrees, the stone tilts ten degrees. That is the whole design goal, and it
took three attempts.

### Calibration: a tracked reference, not a tap snapshot

The reference orientation used to be a single sample read the instant the player tapped the grip
button (`MotionSource.calibrate()`) — any noise in that one instant was baked into the whole
throw. It is now a continuous, steadiness-gated reference, kept in `MotionSource.referenceQuaternion`
and driven every motion sample by `_advanceCalibration` (`controller/js/sensors.js`):

| State | Trigger | Reference behaviour |
|---|---|---|
| **Settling** | Rotation rate below `CalibrationTuning.settleRateThresholdDegS`, for `settleSamples` in a row | Continuously nlerps toward the current attitude, with a `trackingTauMs` time constant — "zero" quietly follows wherever the phone actually rests |
| **Aiming** | Rotation rate above `CalibrationTuning.aimRateThresholdDegS`, for `aimSamples` in a row | Frozen — tilt from here reads as aim |

Gated on rotation rate rather than a decay window, the same threshold-and-hysteresis idiom
`ThrowTuning`'s swing detector already uses (`swingOnsetSamples`, `stopSamples`) — there is no
tracking-vs-held-tilt tradeoff to tune, because tracking simply stops the instant the player
starts moving on purpose.

`recentre()` is the explicit "zero right now" escape hatch this replaces the old `calibrate()`
with at the two call sites that still want one outside of arming: the lobby's shore-pointing
preview, and the RE-CENTRE button. `ThrowController.arm()` calls both `recentre()` (a
guaranteed-fresh zero at the moment that matters most) and `prepareForThrow()` (the
gravity-filter/buffer reset the old `calibrate()` also did, now split out because arming no
longer implies capturing a reference).

**While a throw is armed, Aiming is one-way.** `arm()` sets `MotionSource.holdAimLock`, and
`cancel()` clears it (`controller/js/throw.js`). While it is set, stillness never drops Aiming
back to Settling (`controller/js/sensors.js` `_advanceCalibration`). A pause at the top of the
backswing is longer than `settleSamples`, so without the lock the reference slid to the backswing
pose and the release came out rotated by the whole backswing (#53). Settling → Aiming still works
after the tap, so a grip adjustment before the wind-up is still absorbed.

**Where the release attitude comes from.** On the calm-stop path it is the last sample. On the
`maxSwingMs` timeout path it is the calmest sample after the peak (`calmestIndexAfterPeak`),
because the last sample there is 900 ms into the follow-through (#54).

Design record: `docs/plans/continuous-calibration.md`.

### Why quaternions, and why it is not a style preference

The Euler version was broken **in the exact pose this game is played in**.

`DeviceOrientationEvent` reports intrinsic Z-X'-Y'' Tait-Bryan angles, with `beta` in [-180, 180)
and `gamma` confined to [-90, 90). `beta = 90` is "phone upright, screen facing you" — which is
how you hold a phone to aim at a television. So the grip reference sits **on** the gimbal
singularity, not near it, and three things go wrong at once (`sensors.js:32-40`):

- `alpha` and `gamma` become individually indeterminate. Only their sum is real, so sensor noise
  alone swings aim by tens of degrees
- The browser's branch selection flips, injecting genuine 180° steps
- `beta`'s derivative mirrors, so pitching further back starts reporting as pitching forward

Differencing quaternions has none of that. The delta from grip to now is exact at every pose, and
— this is the part that matters — **it is never decomposed at all**. Four floats go on the wire
and Unity applies them directly, so there is no three-angle parameterisation anywhere for the
singularity to break.

An intermediate version differenced quaternions and *then* decomposed the delta into
aim/attack/roll, which fixed the conditioning but kept a lossy re-composition and two hand-found
sign flips (`-message.aimDeg, -message.attackAngleDeg`) on the Unity side. That version is what
"the rock's rotation doesn't match my phone" was.

### `PhoneAttitude` — the one conversion

`Assets/Scripts/Game/Physics/PhoneAttitude.cs` is the single place phone space becomes Unity
space, and it is reused everywhere a heading is needed — the throw path and rock selection both.
No ad-hoc Euler maths at call sites.

The device frame is right-handed: x across the screen to the right, y toward the top edge, z out
through the glass. Unity is left-handed, y up, z forward. The neutral pose — phone flat, screen
up, top edge pointing at the TV — must produce a flat stone facing away from the camera, and that
pins the axis map to `(x, y, z) → (x, z, y)`. Swapping two axes flips handedness, which flips the
sense of every rotation, so the vector part is negated as well as remapped; the scalar part is
untouched, because the angle is the same and only its direction reverses.

| Function | Returns | Used by |
|---|---|---|
| `ToUnity(w,x,y,z)` | The device-frame rotation as a Unity rotation | Everything |
| `HeadingDirection(q)` | Which way the stone points, flattened to the water | The launch |
| `HeadingDeg(q)` | Compass heading in degrees | Readouts only |
| `FaceAngleDeg(q)` | Face angle above horizontal | The bounce |
| `RollDeg(q)`, `FaceNormal(q)` | Bank; the face normal | The bounce |

`HeadingDirection` returns a vector directly rather than going through `HeadingDeg` and rebuilding
one with `Quaternion.AngleAxis`. Dropping that round trip also drops the only native call in the
solver, which is what lets the whole thing run headlessly outside Unity — and therefore what lets
`Assets/Tests/EditMode/` test it.

### The throw gesture

`controller/js/throw.js` turns the sample buffer into a throw descriptor: peak swing speed, a spin
envelope about the screen normal, and the attitude at release. Release is detected as a sharp stop
rather than a threshold crossing — the player swings and *stops*, and the stop is the signal.

`SimulatedThrow` and `computeSimulatedThrowDescriptor` exist so the whole path can be exercised
from a desktop browser with no sensors at all. That is what makes two-tab desktop testing possible;
see the root [`README.md`](../../README.md).

### `Assets/Scripts/Game/Physics/PhoneAttitude.cs`

#### Heading comes straight from the rotated forward vector
<!-- ref:268d -->

Was `Assets/Scripts/Game/Physics/PhoneAttitude.cs:49-61`.

Which way a rotated stone points, flattened onto the water.

This is what the launch actually needs, so it is what the launch takes. It used
to go through HeadingDeg and rebuild a direction from the angle
with Quaternion.AngleAxis — an atan2 and a trig pair to recover a vector that
was already in hand. Dropping the round trip also drops the only native call in
the solver, which is what lets the whole thing run headlessly outside Unity.

Straight up or straight down has no heading; fall back to forward rather than
to a direction derived from floating-point noise.

### `controller/js/sensors.js`

#### The Settling/Aiming reference state machine
<!-- ref:5d35 -->

Was `controller/js/sensors.js:657-666`.

The Settling/Aiming reference state machine. See docs/4-systems/MotionPipeline.md.

Lazy-inits the reference on the very first sample. While Settling, the reference
nlerps toward the current fused attitude by a tau-derived alpha, so it quietly
follows wherever the phone actually rests; while Aiming it is left alone. Gated
on rotation rate rather than a timer, with a hysteresis pair of thresholds and
debounce sample counts — the same threshold-and-hysteresis idiom
ThrowTuning's swing detector already uses, applied to the reference instead.

#### Gyro-carried attitude between orientation events
<!-- ref:9b76 -->

Was `controller/js/sensors.js:541-550`.

Attitude carried forward by the gyroscope between orientation events, and the
timestamp it is current as of.

deviceorientation and devicemotion are separate, unsynchronised streams. The
old code read whatever orientation happened to be latched when a motion sample
arrived, so on a device whose orientation event is slower, the same triple got
stamped onto several consecutive samples — a staircase in the aim stream,
worst exactly when the phone is moving fastest. rotationRate arrives on the
motion event at motion rate, so integrating it across the gap costs nothing and
removes the staircase where it is most visible.

#### Computed gravity instead of a filtered estimate
<!-- ref:6874 -->

Was `controller/js/sensors.js:251-267`.

The gravity component an accelerometer reads, in device coordinates, for a device
at the given attitude.

An accelerometer at rest reads the reaction force, so it reports +g along whichever
device axis points UP — flat on a table, that is +z. Rotating world up into the
device frame and scaling by g reproduces exactly that, with no estimation involved.

This is what replaces GravityFilter wherever orientation is available, and it is
the single biggest correction to how hard a throw measures. The filter estimates
gravity in DEVICE coordinates, so when the phone turns quickly the gravity vector
sweeps through that frame faster than the filter can track — and the lag comes out
as spurious "user acceleration" of up to ~19.6 m/s^2. Every throw involves a large
wrist rotation regardless of how hard it was thrown, so that error sat under every
reading as a big effort-independent offset, compressing genuinely different throws
into the top of the range. Subtracting a computed vector has no lag to leak.

#### Longest sample gap worth integrating across
<!-- ref:43e3 -->

Was `controller/js/sensors.js:237-248`.

Longest gap between two samples that is still worth integrating across, in ms.

At 60Hz samples land ~17ms apart. A gap this long means the browser stopped
delivering events — the phone was backgrounded, the screen locked, the tab was
throttled — and the motion during it was never measured. Integrating velocity or
attitude across that invents a swing out of a pause, which reads as a throw the
player never made.

Not to be confused with ThrowTuning.spinEnvelopeTauMs, which is also 250 and means
something entirely unrelated.

#### Tilt off level as pitch/roll
<!-- ref:230e -->

Was `controller/js/sensors.js:177-186`.

How far a device-frame attitude has tilted off level, as pitch/roll degrees.

Rotates the device's own forward axis (+z, out through the glass) by the attitude
and reads the two axes of tilt off the result via atan2/asin — the same
"rotate a known vector, read the angle back" shape as gravityInDeviceFrame, not a
second decomposition of the quaternion itself. Shared by D2 (the live tilt bubble,
which only needs a pixel offset) and D8 (the post-throw recap's "12° low"), so the
geometry exists in exactly one place.

#### The device-frame delta rotation
<!-- ref:3086 -->

Was `controller/js/sensors.js:162-172`.

The rotation that takes `qRef` to `qNow`, expressed in the device's own frame.

This is the entire control scheme. Identity means the phone has not moved since the
player tapped, and Unity draws that as a flat stone facing forward; every physical
degree of rotation from there is one degree on the stone.

Left as a quaternion on purpose. Anything that turns it into angles — here or on the
far side — reintroduces a parameterisation that has a pole in it, and the pole sits
exactly where this game is played. Unity applies the four floats directly.

#### Normalized lerp for calibration tracking
<!-- ref:87f9 -->

Was `controller/js/sensors.js:118-127`.

Normalized lerp from `a` toward `b` by `t` (0..1), shortest path.

The low-pass primitive continuous calibration tracking is built on: nlerp rather
than slerp because this runs every motion sample (up to 60Hz) while Settling, and
at the small per-sample steps a tracking filter takes, nlerp and slerp are
indistinguishable but nlerp costs no trig. The dot-product flip is what stops a
lerp between q and -q (the same rotation, opposite sign) taking the long way
around and momentarily reporting a near-180-degree reference jump.

#### Why orientation is handled as quaternions
<!-- ref:6102 -->

Was `controller/js/sensors.js:26-48`.

Orientation is handled as quaternions rather than as three independent Euler
deltas, and that is not a stylistic preference — the Euler version was broken in
the exact pose this game is played in.

DeviceOrientationEvent reports intrinsic Z-X'-Y'' Tait-Bryan angles with beta in
[-180, 180) and gamma confined to [-90, 90). beta = 90 is "phone upright, screen
facing you", which is how you hold a phone to aim at a television. So the grip
reference sits ON the gimbal singularity, not near it, and three things go wrong at
once: alpha and gamma become individually indeterminate (only their sum is real, so
sensor noise alone swings aim by tens of degrees); the browser's branch selection
flips and injects genuine 180-degree steps; and beta's derivative mirrors, so
pitching further back starts reporting as pitching forward.

Differencing quaternions has none of that. The delta from grip to now is exact at
every pose, and — this is the part that matters — it is never decomposed at all.
It goes on the wire as four floats and Unity applies it to the stone directly, so
there is no three-angle parameterisation anywhere for the singularity to break.
An earlier version differenced quaternions and THEN decomposed the delta into
aim/attack/roll, which fixed the conditioning but kept a lossy re-composition (and
two hand-found sign flips) on the Unity side. That is what "the rock's rotation
doesn't match my phone" was.

### `controller/js/throw.js`

#### ThrowController: tap to arm, fire on the stop
<!-- ref:f8d8 -->

Was `controller/js/throw.js:387-409`.

Owns the arm/swing gesture and the AIM stream while armed.

The gesture is: <b>tap to arm, then swing</b>. The throw fires when the phone
stops, not when a finger lifts — accelerate hard, stop dead, and the stone goes.
A second tap disarms instead, so a player who armed with the phone pointing the
wrong way can re-aim.

Calibration itself is no longer something the tap does. MotionSource tracks a
steadiness-gated reference continuously (Settling ⇄ Aiming — see
docs/4-systems/MotionPipeline.md), so most of the time the reference is already
wherever the phone actually rests by the time this arms. arm() still calls
`motion.recentre()` for a guaranteed-fresh zero at the exact moment that matters
most, alongside `motion.prepareForThrow()` for the gravity/buffer reset that used
to live in the same call.

Firing on the stop is what lets orientation be read raw. A throwing motion ends
pointing where it was thrown, so the stop sample already is the aim — nothing has
to be inferred from earlier in the swing, and nothing can disagree with it.

Deliberately does not know about the network — it calls onAim/onThrow and lets
the caller decide what to do with them, so this can be tested with plain stubs.

#### Replaying the spin envelope over a buffer
<!-- ref:b6d3 -->

Was `controller/js/throw.js:327-340`.

Replay decaySpinEnvelope across a whole buffer and return where it ended up.

ThrowController already tracks this envelope live, so the real device path passes
its value straight in. This is for callers holding only samples — tests, tooling,
and computeThrowDescriptor's own fallback — and it agrees with the live tracker by
construction, because it is the same function over the same samples.

The tau does the windowing: spin from before the swing, while the player was
lining the shot up, has decayed away by the time the buffer reaches the release,
so there is no separate window constant to keep in sync.

Pure.

#### Signed peak-hold with exponential decay
<!-- ref:4519 -->

Was `controller/js/throw.js:305-316`.

Peak-hold with an exponential decay: jumps straight to any new peak, then falls back
toward zero. Signed — the peak is chosen by magnitude but keeps its direction.

Generic, despite the name it grew up with. Spin uses it so a wrist flick that is over
in 100ms still reaches a 20Hz aim frame; the live power estimate uses it for the same
reason with a different time constant.

Signed because spin is now a signed rate about the phone's screen normal. Taking a
plain max() would have let a decaying positive peak win over a genuine negative one,
so a counter-clockwise flick following a clockwise one would read as clockwise.

#### Peak hand speed by integrating acceleration
<!-- ref:a10f -->

Was `controller/js/throw.js:233-264`.

A peak-hold envelope over the spin signal: jumps straight to any new peak, then
decays exponentially toward zero.

Pure, and takes the elapsed time rather than counting samples, because the device
sample rate wobbles on real phones — a per-sample decay constant would make the
ghost wind down faster on a phone that happens to be reporting quickly.

@param {number} previous  the envelope's last value
@param {number} instantaneous  spin magnitude from the newest sample
@param {number} dtMs  time since the last update
Peak hand speed during the swing, in m/s, by integrating linear acceleration.

The physically correct measure of how hard a throw was. Peak *acceleration* — what
this used to key off exclusively — is a poor proxy: a short sharp jerk and a long
powerful pull peak identically, and on real hardware the useful range was squashed
into the top of the window, so a throw with two to three times less effort still
read within about 13% of a maximum one. Velocity is the integral of exactly the
thing that differs between them, so it separates what peak cannot.

Integrated from the start of the swing rather than the whole buffer, because any
residual bias accumulates and a 1.5s window would collect far more of it than a
300ms one. Tracks the running peak instead of the endpoint: the swing ends with the
phone stopped, so the value at the stop is by definition near zero — it is the
maximum along the way that the stone leaves the hand at.

Only as good as the gravity removal feeding it; see sensors.js gravityInDeviceFrame.

Pure.

#### Why swingSpeed is raw m/s
<!-- ref:22d2 -->

Was `controller/js/throw.js:183-190`.

Raw m/s. The only thing that decides how far the stone goes — Unity converts
it once, via SkipConstants.SpeedScale. There used to be a normalised `power`
here and a timingQuality alongside it, scaling launch speed by how fast the
phone came to rest after its peak — but a hard swing takes longer to stop, so
it landed hardest on the throws it should have rewarded, and the extra
normalisation was a second lossy conversion of the same measurement Unity was
about to convert again. Effort and result now agree, and there is only one
conversion left, on the Unity side.

#### Reducing a swing to a throw descriptor
<!-- ref:b68d -->

Was `controller/js/throw.js:158-170`.

Reduce a whole swing to the handful of numbers Unity needs.

Pure: takes samples, returns a descriptor. No device access, no side effects,
so it is fully unit testable without a phone.

@param {Array} samples oldest-to-newest, from MotionSource's ring buffer
@param {object} tuning
@param {number} [spinRate] the live spin envelope at release — see the spinRate
  field below. Omitting it replays the envelope over `samples`, which keeps this
  function usable from a buffer alone.
@returns {object|null} descriptor, or null if the gesture was too weak to count

#### How long a spin peak is held
<!-- ref:10f3 -->

Was `controller/js/throw.js:80-87`.

How long a spin peak is held before it has decayed away. Spin is peaky: a
wrist flick is over in about 100ms, so a 20Hz aim frame samples it up to 50ms
late and mostly catches the tail. Holding the peak briefly is what turns that
into a ghost that spins up and winds down instead of one that flickers.

The throw uses the same envelope, so the stone spins at the rate the ghost was
showing. Raise this if a slow release is bleeding away spin the player earned.

## Invariants

- **If a rotation comes out mirrored, fix it in `PhoneAttitude`.** Never with a negation at a call
  site. That is precisely how the previous version got into the state it was in — and if the basis
  change is wrong, every axis is wrong together and obviously, instead of one axis being subtly
  wrong.
- **One conversion, reused everywhere.** There is no second place that converts phone space.
- **Sample rate is not poll rate.** Sensors are sampled at 60Hz into a 1.5s buffer even though aim
  frames go out at 20Hz. Release detection watches for a sharp stop, and polling for that at 20Hz
  misses it by up to 50ms — reading the orientation well after the phone has already come to rest.
- **Straight up or straight down has no heading.** Fall back to forward, never to a direction
  derived from floating-point noise.

### `Assets/Scripts/Game/Physics/PhoneAttitude.cs` — Phone attitude is one quaternion and one basis change
<!-- ref:f406 -->

Was `Assets/Scripts/Game/Physics/PhoneAttitude.cs:5-25`.

The one place phone space becomes Unity space.

The phone is the rock. It sends the rotation from where the player was holding it
when they tapped to where they are holding it now, as a quaternion, and this turns
that into a Unity rotation that gets applied to the stone verbatim. Tilt the phone
ten degrees and the stone tilts ten degrees.

It used to be three angles. The phone decomposed its attitude into aim / attack /
roll, and Unity re-composed them with two sign flips
(`-message.aimDeg, -message.attackAngleDeg`) that were arrived at by trying
both. That is why the stone never quite matched the phone: three chances to lose a
degree of freedom, two hand-tuned negations, and a decomposition whose own neutral
pose sat on a degenerate axis. One quaternion and one basis change has none of
those failure modes — and if the basis change is wrong, every axis is wrong
together and obviously, instead of one axis being subtly wrong.

**If a rotation comes out mirrored, fix it here.** Never with a negation at a
call site — that is how the old version got into this state.

### `Assets/Scripts/Game/Physics/PhoneAttitude.cs`

#### The axis map (x,y,z) to (x,z,y) flips handedness
<!-- ref:12a1 -->

Was `Assets/Scripts/Game/Physics/PhoneAttitude.cs:28-44`.

A DeviceOrientation-frame rotation as a Unity rotation.

The device frame is right-handed: x runs right across the screen, y toward the
top edge, z out through the glass. Unity is left-handed with y up and z forward.

The neutral pose is the phone flat, screen up, top edge pointing at the TV, and
it must produce a flat stone facing away from the camera. That pins the axis
map: device x (right) is Unity x, device y (toward the top edge, so forward) is
Unity z, and device z (out of the glass, so up) is Unity y — i.e.
(x, y, z) -> (x, z, y).

Swapping two axes flips handedness, which flips the sense of every rotation, so
the vector part is negated as well as remapped. The scalar part is untouched:
the angle is the same, only its direction reverses.

### `controller/js/sensors.js`

#### The one definition of spin
<!-- ref:ea00 -->

Was `controller/js/sensors.js:208-220`.

How fast the phone is spinning about its own screen normal, in deg/s. Signed.

The single definition of "spin" in the codebase, and the axis a player means by the
word: the frisbee flick, the one that makes a stone skip. `rotationRate.alpha` is
the rate about device Z, which is the axis out through the glass.

This used to be hypot(alpha, beta, gamma) — unsigned, and across all three axes at
once. Two things were wrong with that. The arm's swing is a far larger rotation than
the wrist's twist, so the number mostly reported how hard the phone was being waved
rather than how it was spun. And with the sign discarded, every stone turned the same
way no matter which way the wrist actually went.

### `controller/js/throw.js`

#### A discarded gesture must not cancel the arm
<!-- ref:5b13 -->

Was `controller/js/throw.js:653-660`.

A5/A6: a discarded gesture — no descriptor at all, or a computed swingSpeed
below minThrowSwingSpeed — must not cancel() the arm. cancel() drops the
player back to IDLE, forcing a re-tap, and the re-tap's own jolt is exactly
the kind of accidental sample this guard exists to stop (A5's "re-aims by
itself"). Resetting only the swing-detection state and staying ARMED lets a
player who is still genuinely swinging just keep going. _graceStartT is
deliberately NOT reset here — the grace window applies once, right after
arm(), not again after every discard.

#### The cosmetic aim frame
<!-- ref:b388 -->

Was `controller/js/throw.js:361-371`.

Cosmetic aim frame. Never authoritative — see the module header.

Raw and unclamped, matching the throw descriptor exactly. Clamping here while the
throw went through unclamped would put the preview and the stone back into
disagreement, which is the whole class of bug this pass exists to remove.

`spinRate` is passed in rather than read off the sample because the useful value is
the envelope tracked at sample rate — see ThrowController. Omitting it falls back to
this instant's spin, which keeps the function usable on its own and honest either way.

## Traps

### Platform landmines

**iOS has no `acceleration`** — only `accelerationIncludingGravity`. The controller seeds its
gravity estimate from the first sample rather than from zero; starting at zero would report a full
9.8 m/s² of fake user acceleration on the first frame and spike the power reading. The estimator
is time-based, so its response does not depend on how fast the device reports.

**iOS compass headings run the opposite direction to Android's.** Normalized on the phone.

**`DeviceMotionEvent.requestPermission()` must be called synchronously inside the user gesture.**
Deferring it behind any other `await` loses gesture context and iOS rejects the request. See the
join handler in `controller/js/app.js`.

### `controller/js/sensors.js`

#### The hardware's gravity-free acceleration, or null
<!-- ref:5a07 -->

Was `controller/js/sensors.js:377-388`.

The hardware's own gravity-free acceleration, or null where it does not exist.

The other half of the iOS guard, and it does not weaken it. The failure the guard
exists to prevent is *depending* on `event.acceleration`, which reads undefined on
every iPhone; returning null and letting the caller fall back is the opposite of
depending on it. When it is present it is fused in hardware, so it has none of the
rotation artefact that both software methods have to work around.

Confirmed on device: returns null throughout on iOS, so the iOS path always falls
through to exact quaternion subtraction. See docs/DeviceMatrix.md.

## Verification

`Assets/Tests/EditMode/PhoneAttitudeTests.cs` — NUnit, runs against the dependency-free
`RockSkipping.Physics` assembly. `controller/test-sensors.js` covers the JS side.

Real-device coverage is recorded in [`../DeviceMatrix.md`](../DeviceMatrix.md) — **which is
currently all placeholders.** iOS and Android both work; the specific models are not written down
anywhere.
