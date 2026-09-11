# Motion pipeline

**Source:** `controller/js/sensors.js`, `throw.js`, `app.js`;
`Assets/Scripts/Game/Physics/PhoneAttitude.cs`

## What it owns

Turning what the player physically does with their phone into a rotation and a throw that Unity
can use — with **no platform difference visible to Unity**.

## The governing rule

**Phones normalize before sending.** Every iOS/Android difference is resolved on the phone. Unity
receives one shape of data and never branches on platform. If a platform quirk reaches Unity, that
is a bug in the controller, not something to special-case in C#.

## The path

```
DeviceOrientationEvent          grip reference quaternion captured on tap
DeviceMotionEvent          ─┐
                            ├─▶  delta = current ⊗ grip⁻¹        (on the phone)
                            │         ↓ four floats on the wire
                            └─▶  PhoneAttitude.ToUnity(w,x,y,z)  (one basis change)
                                      ↓
                                 applied to the stone verbatim
```

Tilt the phone ten degrees, the stone tilts ten degrees. That is the whole design goal, and it
took three attempts.

## Why quaternions, and why it is not a style preference

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

## `PhoneAttitude` — the one conversion

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

## Platform landmines

**iOS has no `acceleration`** — only `accelerationIncludingGravity`. The controller seeds its
gravity estimate from the first sample rather than from zero; starting at zero would report a full
9.8 m/s² of fake user acceleration on the first frame and spike the power reading. The estimator
is time-based, so its response does not depend on how fast the device reports.

**iOS compass headings run the opposite direction to Android's.** Normalized on the phone.

**`DeviceMotionEvent.requestPermission()` must be called synchronously inside the user gesture.**
Deferring it behind any other `await` loses gesture context and iOS rejects the request. See the
join handler in `controller/js/app.js`.

## The throw gesture

`controller/js/throw.js` turns the sample buffer into a throw descriptor: peak swing speed, a spin
envelope about the screen normal, and the attitude at release. Release is detected as a sharp stop
rather than a threshold crossing — the player swings and *stops*, and the stop is the signal.

`SimulatedThrow` and `computeSimulatedThrowDescriptor` exist so the whole path can be exercised
from a desktop browser with no sensors at all. That is what makes two-tab desktop testing possible;
see the root [`README.md`](../../README.md).

## Verification

`Assets/Tests/EditMode/PhoneAttitudeTests.cs` — NUnit, runs against the dependency-free
`RockSkipping.Physics` assembly. `controller/test-sensors.js` covers the JS side.

Real-device coverage is recorded in [`../DeviceMatrix.md`](../DeviceMatrix.md) — **which is
currently all placeholders.** iOS and Android both work; the specific models are not written down
anywhere.
