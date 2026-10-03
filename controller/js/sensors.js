/**
 * Sensor normalization for the phone controller.
 *
 * The whole point of this module is that Unity never sees a platform difference.
 * Raw DeviceMotion/DeviceOrientation values disagree between iOS and Android in
 * ways that produce wrong behaviour rather than errors, so everything is converted
 * here into device-agnostic values: gravity-removed linear acceleration, and
 * orientation expressed as a delta from a calibration reference.
 *
 * Pure math is exported separately from the browser wiring so it can be unit
 * tested in Node with no device — see test-sensors.js.
 *
 * TEST STATUS: verified on real hardware across multiple iPhone generations (Safari)
 * and multiple Samsung and Pixel generations (Chrome). Per-device results, and what
 * "verified" means step by step, are in docs/DeviceMatrix.md — add a row there rather
 * than a comment here when a new device is tested.
 *
 * The pure math below is additionally covered by test-sensors.js, which runs in Node
 * with no device at all.
 */

// ---------------------------------------------------------------------------
// Pure math — no browser APIs below this line until the MotionSource section.
// ---------------------------------------------------------------------------

// --------------------------------------------------------------- quaternions
//
// Orientation is handled as quaternions rather than as three independent Euler
// deltas, and that is not a stylistic preference — the Euler version was broken in
// the exact pose this game is played in.
//
// DeviceOrientationEvent reports intrinsic Z-X'-Y'' Tait-Bryan angles with beta in
// [-180, 180) and gamma confined to [-90, 90). beta = 90 is "phone upright, screen
// facing you", which is how you hold a phone to aim at a television. So the grip
// reference sits ON the gimbal singularity, not near it, and three things go wrong at
// once: alpha and gamma become individually indeterminate (only their sum is real, so
// sensor noise alone swings aim by tens of degrees); the browser's branch selection
// flips and injects genuine 180-degree steps; and beta's derivative mirrors, so
// pitching further back starts reporting as pitching forward.
//
// Differencing quaternions has none of that. The delta from grip to now is exact at
// every pose, and — this is the part that matters — it is never decomposed at all.
// It goes on the wire as four floats and Unity applies it to the stone directly, so
// there is no three-angle parameterisation anywhere for the singularity to break.
// An earlier version differenced quaternions and THEN decomposed the delta into
// aim/attack/roll, which fixed the conditioning but kept a lossy re-composition (and
// two hand-found sign flips) on the Unity side. That is what "the rock's rotation
// doesn't match my phone" was.

const DEG2RAD = Math.PI / 180;

/**
 * DeviceOrientationEvent angles (degrees) to a unit quaternion.
 *
 * Composition order is Z(alpha) then X'(beta) then Y''(gamma), which is what the
 * DeviceOrientation spec defines. Getting this order wrong produces a quaternion
 * that is correct for small angles and subtly wrong for large ones — the kind of
 * bug that survives desk testing and only shows up mid-throw.
 */
export function orientationToQuaternion(alpha = 0, beta = 0, gamma = 0) {
  const halfZ = ((alpha ?? 0) * DEG2RAD) / 2;
  const halfX = ((beta ?? 0) * DEG2RAD) / 2;
  const halfY = ((gamma ?? 0) * DEG2RAD) / 2;

  const cZ = Math.cos(halfZ),
    sZ = Math.sin(halfZ);
  const cX = Math.cos(halfX),
    sX = Math.sin(halfX);
  const cY = Math.cos(halfY),
    sY = Math.sin(halfY);

  return {
    w: cX * cY * cZ - sX * sY * sZ,
    x: sX * cY * cZ - cX * sY * sZ,
    y: cX * sY * cZ + sX * cY * sZ,
    z: cX * cY * sZ + sX * sY * cZ,
  };
}

/** Hamilton product. `a` then `b` applied in a's frame — order matters. */
export function quaternionMultiply(a, b) {
  return {
    w: a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
    x: a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
    y: a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
    z: a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
  };
}

/** Inverse, for unit quaternions. */
export function quaternionConjugate(q) {
  return { w: q.w, x: -q.x, y: -q.y, z: -q.z };
}

export function quaternionNormalize(q) {
  const n = Math.hypot(q.w, q.x, q.y, q.z);
  if (n < 1e-12) return { w: 1, x: 0, y: 0, z: 0 };
  return { w: q.w / n, x: q.x / n, y: q.y / n, z: q.z / n };
}

/**
 * A small rotation, given as a rotation vector in radians about the device's own
 * x/y/z axes. Used to carry orientation forward between orientation events using
 * the gyroscope — see MotionSource.
 *
 * Exact rather than the usual small-angle approximation, because the whole reason
 * this exists is the moments when the phone is turning fastest.
 */
export function quaternionFromRotationVector(rx, ry, rz) {
  const angle = Math.hypot(rx, ry, rz);
  if (angle < 1e-9) return { w: 1, x: 0, y: 0, z: 0 };

  const half = angle / 2;
  const scale = Math.sin(half) / angle;
  return { w: Math.cos(half), x: rx * scale, y: ry * scale, z: rz * scale };
}

/**
 * Normalized lerp from `a` toward `b` by `t` (0..1), shortest path.
 *
 * The low-pass primitive continuous calibration tracking is built on: nlerp rather
 * than slerp because this runs every motion sample (up to 60Hz) while Settling, and
 * at the small per-sample steps a tracking filter takes, nlerp and slerp are
 * indistinguishable but nlerp costs no trig. The dot-product flip is what stops a
 * lerp between q and -q (the same rotation, opposite sign) taking the long way
 * around and momentarily reporting a near-180-degree reference jump.
 */
export function quaternionNlerp(a, b, t) {
  const flip = a.w * b.w + a.x * b.x + a.y * b.y + a.z * b.z < 0 ? -1 : 1;
  return quaternionNormalize({
    w: a.w + (flip * b.w - a.w) * t,
    x: a.x + (flip * b.x - a.x) * t,
    y: a.y + (flip * b.y - a.y) * t,
    z: a.z + (flip * b.z - a.z) * t,
  });
}

/**
 * How fast the phone is turning overall, in deg/s, across all three axes.
 *
 * The signal the Settling/Aiming gate watches — unsigned and unsplit by axis on
 * purpose, unlike screenSpinRate: a deliberate tilt to aim can turn about any
 * combination of axes, so what matters here is "moving at all", not which way.
 */
export function rotationRateMagnitude(rate) {
  return Math.hypot(rate?.alpha ?? 0, rate?.beta ?? 0, rate?.gamma ?? 0);
}

/** Apply a rotation to a vector. */
export function rotateVectorByQuaternion(q, v) {
  const tx = 2 * (q.y * v.z - q.z * v.y);
  const ty = 2 * (q.z * v.x - q.x * v.z);
  const tz = 2 * (q.x * v.y - q.y * v.x);

  return {
    x: v.x + q.w * tx + (q.y * tz - q.z * ty),
    y: v.y + q.w * ty + (q.z * tx - q.x * tz),
    z: v.z + q.w * tz + (q.x * ty - q.y * tx),
  };
}

/**
 * The rotation that takes `qRef` to `qNow`, expressed in the device's own frame.
 *
 * This is the entire control scheme. Identity means the phone has not moved since the
 * player tapped, and Unity draws that as a flat stone facing forward; every physical
 * degree of rotation from there is one degree on the stone.
 *
 * Left as a quaternion on purpose. Anything that turns it into angles — here or on the
 * far side — reintroduces a parameterisation that has a pole in it, and the pole sits
 * exactly where this game is played. Unity applies the four floats directly.
 */
export function orientationDelta(qRef, qNow) {
  return quaternionNormalize(quaternionMultiply(quaternionConjugate(qRef), qNow));
}

/**
 * How far a device-frame attitude has tilted off level, as pitch/roll degrees.
 *
 * Rotates the device's own forward axis (+z, out through the glass) by the attitude
 * and reads the two axes of tilt off the result via atan2/asin — the same
 * "rotate a known vector, read the angle back" shape as gravityInDeviceFrame, not a
 * second decomposition of the quaternion itself. Shared by D2 (the live tilt bubble,
 * which only needs a pixel offset) and D8 (the post-throw recap's "12° low"), so the
 * geometry exists in exactly one place.
 */
export function tiltAngleDeg(attitude) {
  const q = attitude ?? { w: 1, x: 0, y: 0, z: 0 };
  const forward = rotateVectorByQuaternion(q, { x: 0, y: 0, z: 1 });
  return {
    pitchDeg: Math.asin(clamp(forward.y, -1, 1)) * (180 / Math.PI),
    rollDeg: Math.atan2(forward.x, forward.z) * (180 / Math.PI),
  };
}

/** Rotate a vector's X/Y about the Z axis by `degrees` (counter-clockwise). */
export function rotateXY(x, y, degrees) {
  const radians = (degrees * Math.PI) / 180;
  const cos = Math.cos(radians);
  const sin = Math.sin(radians);
  return { x: x * cos - y * sin, y: x * sin + y * cos };
}

export function magnitude(v) {
  return Math.hypot(v.x, v.y, v.z);
}

/**
 * How fast the phone is spinning about its own screen normal, in deg/s. Signed.
 *
 * The single definition of "spin" in the codebase, and the axis a player means by the
 * word: the frisbee flick, the one that makes a stone skip. `rotationRate.alpha` is
 * the rate about device Z, which is the axis out through the glass.
 *
 * This used to be hypot(alpha, beta, gamma) — unsigned, and across all three axes at
 * once. Two things were wrong with that. The arm's swing is a far larger rotation than
 * the wrist's twist, so the number mostly reported how hard the phone was being waved
 * rather than how it was spun. And with the sign discarded, every stone turned the same
 * way no matter which way the wrist actually went.
 */
export function screenSpinRate(rate) {
  return rate?.alpha ?? 0;
}

export function clamp(value, min, max) {
  return value < min ? min : value > max ? max : value;
}

/** Map a value from one range to another, clamped to 0..1. */
export function normalize01(value, min, max) {
  if (max === min) return 0;
  return clamp((value - min) / (max - min), 0, 1);
}

export const GRAVITY_MS2 = 9.80665;

/**
 * Longest gap between two samples that is still worth integrating across, in ms.
 *
 * At 60Hz samples land ~17ms apart. A gap this long means the browser stopped
 * delivering events — the phone was backgrounded, the screen locked, the tab was
 * throttled — and the motion during it was never measured. Integrating velocity or
 * attitude across that invents a swing out of a pause, which reads as a throw the
 * player never made.
 *
 * Not to be confused with ThrowTuning.spinEnvelopeTauMs, which is also 250 and means
 * something entirely unrelated.
 */
export const MAX_SAMPLE_GAP_MS = 250;

/**
 * The gravity component an accelerometer reads, in device coordinates, for a device
 * at the given attitude.
 *
 * An accelerometer at rest reads the reaction force, so it reports +g along whichever
 * device axis points UP — flat on a table, that is +z. Rotating world up into the
 * device frame and scaling by g reproduces exactly that, with no estimation involved.
 *
 * This is what replaces GravityFilter wherever orientation is available, and it is
 * the single biggest correction to how hard a throw measures. The filter estimates
 * gravity in DEVICE coordinates, so when the phone turns quickly the gravity vector
 * sweeps through that frame faster than the filter can track — and the lag comes out
 * as spurious "user acceleration" of up to ~19.6 m/s^2. Every throw involves a large
 * wrist rotation regardless of how hard it was thrown, so that error sat under every
 * reading as a big effort-independent offset, compressing genuinely different throws
 * into the top of the range. Subtracting a computed vector has no lag to leak.
 */
export function gravityInDeviceFrame(q, g = GRAVITY_MS2) {
  return rotateVectorByQuaternion(quaternionConjugate(q), { x: 0, y: 0, z: g });
}

/**
 * Separates gravity from user motion by low-pass estimating it.
 *
 * The fallback path, kept for devices with no usable orientation stream — see
 * gravityInDeviceFrame for the exact method and why it is preferred. Read that
 * comment before tuning tau: this filter cannot be made accurate during a fast
 * rotation, only less wrong.
 */
export class GravityFilter {
  /**
   * @param {number} [tauMs] time constant. The old constructor took a per-sample
   *   retention factor k with no dt term, which made the real time constant depend
   *   on the device's sample rate — 45ms at 100Hz, 149ms at 30Hz, from one number.
   *   A k in (0,1) is still accepted and converted, assuming the nominal 60Hz.
   */
  constructor(tauMs = 75) {
    this.tauMs = tauMs > 0 && tauMs < 1 ? -(1000 / SAMPLE_HZ) / Math.log(tauMs) : tauMs;
    this.gravity = null;
    this.lastT = null;
  }

  /** @returns {{x:number,y:number,z:number}} linear acceleration, gravity removed */
  update(accel, tMs = null) {
    if (this.gravity === null) {
      // Seed from the first sample. Starting at zero would report a full 9.8 m/s^2
      // of fake "user acceleration" on the first frame and spike the power reading.
      this.gravity = { x: accel.x, y: accel.y, z: accel.z };
    } else {
      // Time-based, so the response no longer depends on how fast the device reports.
      const dtMs =
        tMs === null || this.lastT === null
          ? 1000 / SAMPLE_HZ
          : Math.min(Math.max(tMs - this.lastT, 0), MAX_SAMPLE_GAP_MS);
      const k = Math.exp(-dtMs / this.tauMs);

      this.gravity.x = k * this.gravity.x + (1 - k) * accel.x;
      this.gravity.y = k * this.gravity.y + (1 - k) * accel.y;
      this.gravity.z = k * this.gravity.z + (1 - k) * accel.z;
    }

    this.lastT = tMs;

    return {
      x: accel.x - this.gravity.x,
      y: accel.y - this.gravity.y,
      z: accel.z - this.gravity.z,
    };
  }

  reset() {
    this.gravity = null;
    this.lastT = null;
  }
}

/**
 * Fixed-capacity ring buffer of recent samples. Sized to hold the whole swing so
 * the release analysis can look back over it, without allocating during a throw.
 */
export class RingBuffer {
  constructor(capacity) {
    this.capacity = capacity;
    this.items = new Array(capacity);
    this.length = 0;
    this._next = 0;
  }

  push(item) {
    this.items[this._next] = item;
    this._next = (this._next + 1) % this.capacity;
    if (this.length < this.capacity) this.length++;
  }

  /** Oldest-to-newest. */
  toArray() {
    const out = [];
    const start = this.length < this.capacity ? 0 : this._next;
    for (let i = 0; i < this.length; i++) {
      out.push(this.items[(start + i) % this.capacity]);
    }
    return out;
  }

  clear() {
    this.length = 0;
    this._next = 0;
  }
}

/**
 * THE iOS GUARD. This is the only place in the codebase permitted to read
 * acceleration off a DeviceMotionEvent.
 *
 * Confirmed on device: iOS exposes `accelerationIncludingGravity` but NOT
 * `acceleration`. Android exposes both, so an Android-only test can never catch
 * a regression that reintroduces `event.acceleration` — which would read
 * undefined on every iPhone. Keeping the read in one function is the guard.
 * See docs/DeviceMatrix.md.
 */
export function readAcceleration(event) {
  const a = event.accelerationIncludingGravity;
  if (!a || (a.x === null && a.y === null && a.z === null)) return null;
  return { x: a.x ?? 0, y: a.y ?? 0, z: a.z ?? 0 };
}

/**
 * The hardware's own gravity-free acceleration, or null where it does not exist.
 *
 * The other half of the iOS guard, and it does not weaken it. The failure the guard
 * exists to prevent is *depending* on `event.acceleration`, which reads undefined on
 * every iPhone; returning null and letting the caller fall back is the opposite of
 * depending on it. When it is present it is fused in hardware, so it has none of the
 * rotation artefact that both software methods have to work around.
 *
 * Confirmed on device: returns null throughout on iOS, so the iOS path always falls
 * through to exact quaternion subtraction. See docs/DeviceMatrix.md.
 */
export function readLinearAcceleration(event) {
  const a = event.acceleration;
  if (!a) return null;
  if (a.x === null && a.y === null && a.z === null) return null;
  if (a.x === undefined && a.y === undefined && a.z === undefined) return null;
  return { x: a.x ?? 0, y: a.y ?? 0, z: a.z ?? 0 };
}

export function readRotationRate(event) {
  const r = event.rotationRate;
  if (!r) return { alpha: 0, beta: 0, gamma: 0 };
  return { alpha: r.alpha ?? 0, beta: r.beta ?? 0, gamma: r.gamma ?? 0 };
}

/**
 * Tuning for the Settling/Aiming reference state machine — see MotionSource._onMotion.
 * Seed values: the design doc (docs/plans/continuous-calibration.md) defers exact
 * numbers to on-device feel, the same way ThrowTuning's guard constants were.
 */
export const CalibrationTuning = {
  // Below this rotation rate (deg/s), the phone counts as held still and the
  // reference keeps tracking it. Below settleRateThresholdDegS while Aiming, for
  // settleSamples in a row, re-enters Settling.
  settleRateThresholdDegS: 8,

  // Above this rotation rate (deg/s), for aimSamples in a row, the reference freezes
  // and the phone is Aiming. Higher than settleRateThresholdDegS on purpose — a
  // single shared boundary would flicker between the two states on noise sitting
  // right at the edge; the gap between them is the hysteresis.
  aimRateThresholdDegS: 20,

  // Consecutive still samples required to re-enter Settling from Aiming, ~200ms at
  // 60Hz. Slow on purpose: re-entering Settling mid-aim on a brief pause would start
  // tracking away from a reference the player is still actively holding.
  settleSamples: 12,

  // Consecutive fast samples required to leave Settling for Aiming, ~50ms at 60Hz.
  // Quick on purpose, matching the precedent ThrowTuning.swingOnsetSamples already
  // sets for "commit fast, once it's real."
  aimSamples: 3,

  // Time constant (ms) for the low-pass tracking nlerp while Settling — same
  // exp-decay shape as GravityFilter's tauMs.
  trackingTauMs: 175,
};

// ---------------------------------------------------------------------------
// Browser wiring
// ---------------------------------------------------------------------------

/**
 * Confirmed on device: iOS 13+ requires an explicit permission grant for motion and
 * orientation, requested from inside a user gesture. Android has no such API at
 * all, so calling requestPermission unconditionally throws there — hence the
 * feature detection rather than a try/catch or a UA sniff. See docs/DeviceMatrix.md.
 */
export async function requestMotionPermission() {
  const needsMotion = typeof DeviceMotionEvent?.requestPermission === 'function';
  const needsOrientation = typeof DeviceOrientationEvent?.requestPermission === 'function';

  if (!needsMotion && !needsOrientation) {
    return { granted: true, prompted: false };
  }

  try {
    const results = await Promise.all([
      needsMotion ? DeviceMotionEvent.requestPermission() : 'granted',
      needsOrientation ? DeviceOrientationEvent.requestPermission() : 'granted',
    ]);
    return { granted: results.every((r) => r === 'granted'), prompted: true };
  } catch (err) {
    return { granted: false, prompted: true, error: err?.message ?? String(err) };
  }
}

/** True when motion sensors can work at all. They require a secure context. */
export function isSecureContextOk() {
  return typeof window !== 'undefined' && window.isSecureContext === true;
}

const SAMPLE_HZ = 60;
const BUFFER_SECONDS = 1.5;

/** Monotonic clock, with a Date fallback so this module can be driven from tests. */
const now = () =>
  typeof performance !== 'undefined' && performance.now ? performance.now() : Date.now();

/**
 * Streams normalized samples from the device's motion and orientation sensors.
 *
 * Orientation is always reported relative to a calibration reference captured on
 * grip, never as an absolute. That is what makes the output comparable across
 * platforms: iOS and Android disagree on absolute heading (different norths, and
 * they count in opposite directions), but a delta from a captured zero agrees.
 */
export class MotionSource {
  /**
   * @param {object}   options
   * @param {number}   [options.gravityK]  low-pass constant for the gravity estimate
   * @param {function} [options.onSample]  called with every sample as it arrives
   * @param {function} [options.now]  clock source, so tests can drive gyro integration
   *   across controlled intervals instead of racing the real one
   */
  constructor({
    gravityK = 0.8,
    onSample,
    now: clock,
    calibrationTuning = CalibrationTuning,
    onCalibrationStateChange,
  } = {}) {
    this.now = clock ?? now;
    this.gravityFilter = new GravityFilter(gravityK);
    this.buffer = new RingBuffer(Math.ceil(SAMPLE_HZ * BUFFER_SECONDS));
    this.calibrationTuning = calibrationTuning;

    // Sample-rate, not poll-rate. Release detection watches for a sharp stop, and
    // polling for that at the 20Hz aim cadence would miss it by up to 50ms and read
    // the orientation well after the phone had already come to rest.
    this.onSample = onSample ?? (() => {});

    this.latestOrientation = { alpha: 0, beta: 0, gamma: 0 };
    this.latest = null;
    this.running = false;

    // Grip reference and current attitude, as quaternions. See the quaternion
    // section above for why these are not three Euler deltas. null means "not yet
    // seeded" — lazy-initialised from the first sample in _onMotion, or explicitly by
    // recentre().
    this.referenceQuaternion = null;
    this.orientationQuaternion = { w: 1, x: 0, y: 0, z: 0 };

    // Settling/Aiming reference state machine — see docs/4-systems/MotionPipeline.md
    // and _onMotion. Fires onCalibrationStateChange exactly once per flip, mirroring
    // ThrowController.onStateChange's edge-triggered shape rather than polling.
    this.calState = 'settling';

    // Whether Settling actually moves the reference. On for throwing, where "zero follows
    // wherever the phone rests" is the point. Off for the lobby's shore-pointing picker,
    // where the same tracking dragged the pointer back to centre while a player held it
    // on a rock (issue #23) — there, the zero is the explicit recentre() alone. The state
    // machine and its badge edges still run either way; only the nlerp is gated.
    this.trackingEnabled = true;

    // While a throw is armed, once the phone has gone Aiming it must stay Aiming until
    // the throw ends: a pause at the top of the backswing is not "the phone is at rest",
    // and re-settling there slid the reference to the backswing pose (issue #53).
    // Settling -> Aiming is unaffected. ThrowController sets this in arm()/cancel().
    this.holdAimLock = false;
    this._aimRun = 0;
    this._settleRun = 0;
    this.onCalibrationStateChange = onCalibrationStateChange ?? (() => {});

    // Attitude carried forward by the gyroscope between orientation events, and the
    // timestamp it is current as of.
    //
    // deviceorientation and devicemotion are separate, unsynchronised streams. The
    // old code read whatever orientation happened to be latched when a motion sample
    // arrived, so on a device whose orientation event is slower, the same triple got
    // stamped onto several consecutive samples — a staircase in the aim stream,
    // worst exactly when the phone is moving fastest. rotationRate arrives on the
    // motion event at motion rate, so integrating it across the gap costs nothing and
    // removes the staircase where it is most visible.
    this._fusedQuaternion = { w: 1, x: 0, y: 0, z: 0 };
    this._fusedAtMs = null;
    this._orientationAtMs = null;

    this._onMotion = this._onMotion.bind(this);
    this._onOrientation = this._onOrientation.bind(this);
  }

  start() {
    if (this.running) return;
    window.addEventListener('devicemotion', this._onMotion);
    window.addEventListener('deviceorientation', this._onOrientation);
    this.running = true;
  }

  stop() {
    if (!this.running) return;
    window.removeEventListener('devicemotion', this._onMotion);
    window.removeEventListener('deviceorientation', this._onOrientation);
    this.running = false;
  }

  /**
   * Snap the reference to the current attitude immediately, and force the state
   * machine back to Settling. The direct replacement for what the old tap-triggered
   * `calibrate()` did to the reference — used by the two non-throw call sites
   * (the lobby preview aim, and the RE-CENTRE button) that still want an explicit
   * "zero right now" rather than the continuous Settling/Aiming tracking arm() relies
   * on. See docs/4-systems/MotionPipeline.md.
   */
  recentre() {
    this.referenceQuaternion = this._fusedQuaternion;
    this._aimRun = 0;
    this._settleRun = 0;
    if (this.calState !== 'settling') {
      this.calState = 'settling';
      this.onCalibrationStateChange('settling');
    }
  }

  /**
   * Reset the swing-measurement state ahead of a throw. Renamed from the old
   * `calibrate()` — arming no longer captures a reference here at all; the
   * Settling/Aiming state machine in _onMotion already keeps one current, and
   * ThrowController.arm() calls recentre() alongside this when it wants an
   * immediate, guaranteed-fresh zero on top of that.
   */
  prepareForThrow() {
    this.gravityFilter.reset();
    this.buffer.clear();
  }

  _onOrientation(event) {
    // Only alpha/beta/gamma are read. webkitCompassHeading is deliberately ignored
    // even though iOS provides it: it is absolute and counts the opposite way from
    // Android's alpha, so using it would make aiming steer backwards on one
    // platform. Deltas from a calibrated reference sidestep that entirely.
    this.latestOrientation = {
      alpha: event.alpha ?? 0,
      beta: event.beta ?? 0,
      gamma: event.gamma ?? 0,
    };

    this.orientationQuaternion = orientationToQuaternion(
      this.latestOrientation.alpha,
      this.latestOrientation.beta,
      this.latestOrientation.gamma,
    );

    // A real reading supersedes whatever the gyro had carried forward, which is what
    // stops integration drift accumulating.
    this._fusedQuaternion = this.orientationQuaternion;
    this._orientationAtMs = this.now();
    this._fusedAtMs = this._orientationAtMs;
  }

  /**
   * Advance the fused attitude to `tMs` using the gyroscope.
   *
   * rotationRate is deg/s about the device's own axes, and the spec names them for
   * the Euler angle each drives: alpha about Z, beta about X, gamma about Y. That
   * naming is the easiest thing in this file to get backwards.
   */
  _advanceFused(rate, tMs) {
    if (this._fusedAtMs === null) {
      this._fusedAtMs = tMs;
      return;
    }

    const dt = (tMs - this._fusedAtMs) / 1000;
    this._fusedAtMs = tMs;

    // Negative dt means clocks disagreed; a long gap means the phone was backgrounded
    // and integrating across it would fling the aim somewhere arbitrary.
    if (!rate || dt <= 0 || dt > MAX_SAMPLE_GAP_MS / 1000) return;

    const step = quaternionFromRotationVector(
      (rate.beta ?? 0) * DEG2RAD * dt,
      (rate.gamma ?? 0) * DEG2RAD * dt,
      (rate.alpha ?? 0) * DEG2RAD * dt,
    );

    // Right-multiplied: the turn happens about the device's own axes, not the world's.
    this._fusedQuaternion = quaternionNormalize(quaternionMultiply(this._fusedQuaternion, step));
  }

  /**
   * The Settling/Aiming reference state machine. See docs/4-systems/MotionPipeline.md.
   *
   * Lazy-inits the reference on the very first sample. While Settling, the reference
   * nlerps toward the current fused attitude by a tau-derived alpha, so it quietly
   * follows wherever the phone actually rests; while Aiming it is left alone. Gated
   * on rotation rate rather than a timer, with a hysteresis pair of thresholds and
   * debounce sample counts — the same threshold-and-hysteresis idiom
   * ThrowTuning's swing detector already uses, applied to the reference instead.
   */
  _advanceCalibration(rotationRate, prevFusedAtMs, tMs) {
    if (this.referenceQuaternion === null) {
      this.referenceQuaternion = this._fusedQuaternion;
    }

    const tuning = this.calibrationTuning;
    const rateMag = rotationRateMagnitude(rotationRate);

    if (this.calState === 'settling') {
      const dtMs = prevFusedAtMs === null ? 0 : tMs - prevFusedAtMs;
      if (this.trackingEnabled && dtMs > 0 && dtMs <= MAX_SAMPLE_GAP_MS) {
        const alpha = 1 - Math.exp(-dtMs / tuning.trackingTauMs);
        this.referenceQuaternion = quaternionNlerp(
          this.referenceQuaternion,
          this._fusedQuaternion,
          alpha,
        );
      }

      if (rateMag > tuning.aimRateThresholdDegS) {
        this._aimRun++;
        if (this._aimRun >= tuning.aimSamples) {
          this.calState = 'aiming';
          this._aimRun = 0;
          this._settleRun = 0;
          this.onCalibrationStateChange('aiming');
        }
      } else {
        this._aimRun = 0;
      }
    } else {
      // Aiming: the reference is frozen — deliberately not touched here.
      if (this.holdAimLock) {
        this._settleRun = 0; // armed throw: stillness here is a backswing pause (#53)
      } else if (rateMag < tuning.settleRateThresholdDegS) {
        this._settleRun++;
        if (this._settleRun >= tuning.settleSamples) {
          this.calState = 'settling';
          this._aimRun = 0;
          this._settleRun = 0;
          this.onCalibrationStateChange('settling');
        }
      } else {
        this._settleRun = 0;
      }
    }
  }

  /**
   * Gravity-free acceleration, by the best method this device supports.
   *
   * Ordered by how much rotation artefact each leaves in the reading, because that
   * artefact is what was making a hard throw and a gentle one measure nearly the
   * same: hardware fusion has none, exact subtraction has none once orientation is
   * known, and the low-pass estimate has up to ~19.6 m/s^2 of it during a fast turn.
   */
  _linearAcceleration(event, tMs) {
    const hardware = readLinearAcceleration(event);
    if (hardware) return { linear: hardware, source: 'hardware' };

    const accel = readAcceleration(event);
    if (!accel) return { linear: null, source: 'none' };

    if (this._orientationAtMs !== null) {
      const g = gravityInDeviceFrame(this._fusedQuaternion);
      return {
        linear: { x: accel.x - g.x, y: accel.y - g.y, z: accel.z - g.z },
        source: 'exact',
      };
    }

    return { linear: this.gravityFilter.update(accel, tMs), source: 'filter' };
  }

  _onMotion(event) {
    const t = this.now();
    // Captured before _advanceFused, which overwrites this._fusedAtMs as a side
    // effect — the calibration tick's own dtMs has to come from the pre-advance
    // snapshot, or the nlerp alpha below is computed against the wrong interval on
    // every single frame.
    const prevFusedAtMs = this._fusedAtMs;
    const rotationRate = readRotationRate(event);
    this._advanceFused(rotationRate, t);
    this._advanceCalibration(rotationRate, prevFusedAtMs, t);

    const { linear: linearRaw, source: gravitySource } = this._linearAcceleration(event, t);
    if (!linearRaw) return;

    // Keep axes stable if the device rotates. Portrait lock is requested elsewhere
    // but is best-effort — not all browsers honour it — so compensate regardless.
    // typeof-guarded so this class can be driven from Node by the tests; it used to
    // be unreachable outside a browser, which is why it had no coverage at all.
    const screenAngle =
      typeof window === 'undefined' ? 0 : (window.screen?.orientation?.angle ?? 0);
    const rotated = rotateXY(linearRaw.x, linearRaw.y, -screenAngle);
    const linear = { x: rotated.x, y: rotated.y, z: linearRaw.z };

    // Uncalibrated compares against itself and therefore reports identity, which is the
    // same safe behaviour as the zeros this used to report.
    const reference = this.referenceQuaternion ?? this._fusedQuaternion;

    const sample = {
      t,
      linear,
      power: magnitude(linear),

      // The phone's rotation since the tap, and the only orientation this module
      // reports. See orientationDelta.
      attitude: orientationDelta(reference, this._fusedQuaternion),

      rotationRate,

      // How stale the last real orientation reading is. Surfaced rather than hidden
      // so the debug readout can show when the aim is running on gyro alone.
      orientationAgeMs: this._orientationAtMs === null ? null : t - this._orientationAtMs,

      // Which gravity-removal method produced `linear` — 'hardware', 'exact' or
      // 'filter'. Worth reading on a real device before trusting any power tuning:
      // 'filter' means the rotation artefact is still in the numbers.
      gravitySource,
    };

    this.latest = sample;
    this.buffer.push(sample);
    this.onSample(sample);
  }
}
