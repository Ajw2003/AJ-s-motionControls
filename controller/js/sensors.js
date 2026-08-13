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
 * TEST STATUS: verified on Android (Chrome). iOS branches are written from spec
 * and marked `UNVERIFIED (iOS)`; grep that tag when a device is available.
 */

// ---------------------------------------------------------------------------
// Pure math — no browser APIs below this line until the MotionSource section.
// ---------------------------------------------------------------------------

/**
 * Signed shortest distance from `reference` to `current`, in degrees, in (-180, 180].
 * Used so a throw that crosses the 0/360 wrap doesn't produce a ~360 degree jump.
 */
export function shortestAngleDelta(current, reference) {
  let delta = (current - reference) % 360;
  if (delta > 180) delta -= 360;
  if (delta <= -180) delta += 360;
  return delta;
}

// --------------------------------------------------------------- quaternions
//
// Orientation is handled as quaternions rather than as three independent Euler
// deltas, and that is not a stylistic preference — the Euler version was broken in
// the exact pose this game is played in.
//
// DeviceOrientationEvent reports intrinsic Z-X'-Y'' Tait-Bryan angles with beta in
// [-180, 180) and gamma confined to [-90, 90). beta = 90 is "phone upright, screen
// facing you", which is how you hold a phone to aim at a television. So the grip
// reference sits ON the gimbal singularity, not near it, and four things go wrong at
// once: alpha and gamma become individually indeterminate (only their sum is real, so
// sensor noise alone swings aim by tens of degrees); the browser's branch selection
// flips and injects genuine 180-degree steps; beta's derivative mirrors, so pitching
// further back starts reporting as pitching forward; and shortestAngleDelta is simply
// the wrong operator for gamma, which is folded rather than circular — a 2 degree
// physical roll across the fold reported as -178.
//
// Differencing quaternions has none of that. The delta from grip to now is near
// identity for ordinary aiming, so decomposing THAT is well-conditioned. The pole
// moves from "phone upright" to "phone rotated 90 degrees away from however you
// gripped it", and even there the decomposition saturates rather than flipping.

const DEG2RAD = Math.PI / 180;
const RAD2DEG = 180 / Math.PI;

/**
 * DeviceOrientationEvent angles (degrees) to a unit quaternion.
 *
 * Composition order is Z(alpha) then X'(beta) then Y''(gamma), which is what the
 * DeviceOrientation spec defines. Getting this order wrong produces a quaternion
 * that is correct for small angles and subtly wrong for large ones — the kind of
 * bug that survives desk testing and only shows up mid-throw.
 */
export function orientationToQuaternion(alpha = 0, beta = 0, gamma = 0) {
  const halfZ = (alpha ?? 0) * DEG2RAD / 2;
  const halfX = (beta ?? 0) * DEG2RAD / 2;
  const halfY = (gamma ?? 0) * DEG2RAD / 2;

  const cZ = Math.cos(halfZ), sZ = Math.sin(halfZ);
  const cX = Math.cos(halfX), sX = Math.sin(halfX);
  const cY = Math.cos(halfY), sY = Math.sin(halfY);

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
 * Decompose back to Z-X'-Y'' angles in degrees, as {yaw, pitch, roll}.
 *
 * Intended for a DELTA quaternion, where the answer is near zero and therefore far
 * from the pole. Pitch is clamped before asin so that at the pole it saturates at
 * +/-90 instead of returning NaN or flipping sign — degrading smoothly is the entire
 * point of the exercise.
 */
export function quaternionToYawPitchRoll(q) {
  const { w, x, y, z } = quaternionNormalize(q);

  const sinPitch = Math.max(-1, Math.min(1, 2 * (y * z + w * x)));
  const pitch = Math.asin(sinPitch);

  const yaw = Math.atan2(2 * (w * z - x * y), 1 - 2 * (x * x + z * z));
  const roll = Math.atan2(2 * (w * y - x * z), 1 - 2 * (x * x + y * y));

  return { yaw: yaw * RAD2DEG, pitch: pitch * RAD2DEG, roll: roll * RAD2DEG };
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
 * Aim, attack and roll of `qNow` relative to `qRef`, in degrees.
 *
 * Deliberately NOT a Euler decomposition of the delta quaternion, which is the
 * obvious implementation and is wrong here. At the pose this game is played in —
 * phone upright, beta near 90 — alpha and gamma are the same physical rotation, so
 * no three-angle parameterisation can keep them apart. Turning to aim left shows up
 * partly as alpha and partly as gamma depending on nothing but sensor noise.
 *
 * So the axes are defined by what the game actually means instead:
 *
 *   aim     azimuth of where the phone points, about world up. Turning your body
 *           left is aiming left, however the browser chose to split that between
 *           alpha and gamma.
 *   attack  elevation of the same pointing axis. This is the stone's face angle and
 *           the most important number in the game.
 *   roll    twist about the pointing axis — genuinely independent of the other two,
 *           where gamma was not.
 *
 * Three real degrees of freedom with no degeneracy at the play pose. The world frame
 * is the DeviceOrientation spec's: X east, Y north, Z up.
 */
export function orientationDelta(qRef, qNow) {
  // Out the back of the phone. Horizontal at the natural upright grip, which is what
  // makes azimuth and elevation well conditioned exactly where they are needed.
  const BODY_POINT = { x: 0, y: 0, z: -1 };
  const BODY_UP = { x: 0, y: 1, z: 0 };

  const pRef = rotateVectorByQuaternion(qRef, BODY_POINT);
  const pNow = rotateVectorByQuaternion(qNow, BODY_POINT);

  const azimuth = (v) => Math.atan2(v.y, v.x) * RAD2DEG;
  const elevation = (v) => {
    const len = Math.hypot(v.x, v.y, v.z);
    return len < 1e-9 ? 0 : Math.asin(Math.max(-1, Math.min(1, v.z / len))) * RAD2DEG;
  };

  // Still a circular quantity, so the 0/360 seam still has to be handled — this is
  // the one place shortestAngleDelta was always the right tool.
  const aim = shortestAngleDelta(azimuth(pNow), azimuth(pRef));
  const attack = elevation(pNow) - elevation(pRef);

  // Twist about the pointing axis: project both "up" vectors perpendicular to it and
  // take the signed angle between them. Exact while the pointing axis has barely
  // moved, which is the case whenever roll is worth reading; it is cosmetic only.
  const axisLen = Math.hypot(pNow.x, pNow.y, pNow.z);
  let roll = 0;
  if (axisLen > 1e-9) {
    const n = { x: pNow.x / axisLen, y: pNow.y / axisLen, z: pNow.z / axisLen };
    const perp = (v) => {
      const d = v.x * n.x + v.y * n.y + v.z * n.z;
      return { x: v.x - d * n.x, y: v.y - d * n.y, z: v.z - d * n.z };
    };

    const a = perp(rotateVectorByQuaternion(qRef, BODY_UP));
    const b = perp(rotateVectorByQuaternion(qNow, BODY_UP));

    const cross = {
      x: a.y * b.z - a.z * b.y,
      y: a.z * b.x - a.x * b.z,
      z: a.x * b.y - a.y * b.x,
    };
    const sin = cross.x * n.x + cross.y * n.y + cross.z * n.z;
    const cos = a.x * b.x + a.y * b.y + a.z * b.z;
    if (Math.hypot(sin, cos) > 1e-9) roll = Math.atan2(sin, cos) * RAD2DEG;
  }

  return { aim, attack, roll };
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
 * How fast the phone is turning, in deg/s, regardless of which axis it turns about.
 *
 * The single definition of "spin" in the codebase. The release descriptor and the
 * live aim stream both call it, so the spin the preview shows and the spin the
 * stone is launched with can never be two different measurements.
 */
export function spinMagnitude(rate) {
  if (!rate) return 0;
  return Math.hypot(rate.alpha ?? 0, rate.beta ?? 0, rate.gamma ?? 0);
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
      const dtMs = tMs === null || this.lastT === null
        ? 1000 / SAMPLE_HZ
        : Math.min(Math.max(tMs - this.lastT, 0), 250);
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
 * UNVERIFIED (iOS): assumes iOS exposes `accelerationIncludingGravity` but NOT
 * `acceleration`. Android exposes both, so an Android-only test can never catch
 * a regression that reintroduces `event.acceleration` — which would read
 * undefined on every iPhone. Keeping the read in one function is the guard.
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
 * UNVERIFIED (iOS): expected to return null throughout on iOS.
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

// ---------------------------------------------------------------------------
// Browser wiring
// ---------------------------------------------------------------------------

/**
 * UNVERIFIED (iOS): iOS 13+ requires an explicit permission grant for motion and
 * orientation, requested from inside a user gesture. Android has no such API at
 * all, so calling requestPermission unconditionally throws there — hence the
 * feature detection rather than a try/catch or a UA sniff.
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
  (typeof performance !== 'undefined' && performance.now ? performance.now() : Date.now());

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
  constructor({ gravityK = 0.8, onSample, now: clock } = {}) {
    this.now = clock ?? now;
    this.gravityFilter = new GravityFilter(gravityK);
    this.buffer = new RingBuffer(Math.ceil(SAMPLE_HZ * BUFFER_SECONDS));

    // Sample-rate, not poll-rate. Release detection watches for a sharp stop, and
    // polling for that at the 20Hz aim cadence would miss it by up to 50ms and read
    // the orientation well after the phone had already come to rest.
    this.onSample = onSample ?? (() => {});

    this.reference = null;
    this.latestOrientation = { alpha: 0, beta: 0, gamma: 0 };
    this.latest = null;
    this.running = false;

    // Grip reference and current attitude, as quaternions. See the quaternion
    // section above for why these are not three Euler deltas.
    this.referenceQuaternion = null;
    this.orientationQuaternion = { w: 1, x: 0, y: 0, z: 0 };

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
   * Capture the current orientation as the zero reference. Called on grip, which
   * is what turns "point your phone at the lake and hold" into a calibration step
   * the player never notices they are performing.
   */
  calibrate() {
    this.reference = { ...this.latestOrientation };
    this.referenceQuaternion = this._fusedQuaternion;
    this.gravityFilter.reset();
    this.buffer.clear();
  }

  hasReference() {
    return this.reference !== null;
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
      this.latestOrientation.alpha, this.latestOrientation.beta, this.latestOrientation.gamma);

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
    if (!rate || dt <= 0 || dt > 0.25) return;

    const step = quaternionFromRotationVector(
      (rate.beta ?? 0) * DEG2RAD * dt,
      (rate.gamma ?? 0) * DEG2RAD * dt,
      (rate.alpha ?? 0) * DEG2RAD * dt);

    // Right-multiplied: the turn happens about the device's own axes, not the world's.
    this._fusedQuaternion = quaternionNormalize(
      quaternionMultiply(this._fusedQuaternion, step));
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
    const rotationRate = readRotationRate(event);
    this._advanceFused(rotationRate, t);

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

    // Uncalibrated compares against itself and therefore reports zeros, which is the
    // same safe behaviour as before.
    const reference = this.referenceQuaternion ?? this._fusedQuaternion;
    const { aim, attack, roll } = orientationDelta(reference, this._fusedQuaternion);

    const sample = {
      t,
      linear,
      power: magnitude(linear),
      dAlpha: aim,
      dBeta: attack,
      dGamma: roll,
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
