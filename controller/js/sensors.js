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
 * Minimum forward lean of the phone's pointing axis before its heading is trusted.
 * 0.35 is about 70 degrees of pitch either side of level.
 */
export const MIN_POINTING_CONFIDENCE = 0.35;

/**
 * Rotation speed above which the fused orientation reading is not trusted at all.
 *
 * `deviceorientation` is sensor-fused and lags real motion by tens of milliseconds —
 * fine while aiming, useless mid-swing, which is precisely when accuracy matters.
 */
export const FAST_ROTATION_DEG_PER_SEC = 120;

/** How hard a trusted orientation reading pulls the integrated heading, per update. */
export const HEADING_CORRECTION_GAIN = 0.15;

/**
 * Rate of change of the pointing bearing, in deg/s, from the gyroscope.
 *
 * `rotationRate` arrives with `devicemotion` at the full sensor rate and is very close
 * to instantaneous, where `deviceorientation` is a fused estimate that both lags and
 * arrives on its own slower schedule.
 *
 * Derived from the Z-X'-Y'' Euler kinematics and checked numerically against a
 * finite-difference of the rotation matrix: exact to ~5e-7 deg/s across 600 poses up
 * to 500 deg/s. The 1/cos(beta) term is the same singularity `pointingHeading` guards,
 * which is why this scalar is used only as a turn-speed signal (the debug display, and
 * gating how hard the fused correction pulls). The heading itself is integrated as a
 * full quaternion attitude, which has no such singularity — see quatIntegrate and
 * pointingFromQuat.
 *
 * @param rotationRate DeviceMotionEvent.rotationRate, deg/s (alpha about Z, beta about
 *                     X, gamma about Y)
 */
export function headingRateFromGyro(beta, gamma, rotationRate) {
  const b = (beta * Math.PI) / 180;
  const g = (gamma * Math.PI) / 180;

  const cosBeta = Math.cos(b);
  if (Math.abs(cosBeta) < 1e-3) return 0;

  return (rotationRate.beta * Math.sin(g) - rotationRate.alpha * Math.cos(g)) / cosBeta;
}

/**
 * Compass-style bearing of the direction the top of the phone points, plus how much
 * to trust it.
 *
 * WHY NOT JUST USE `alpha`: alpha is the Euler yaw of a Z-X'-Y'' rotation, and it is
 * degenerate when the phone pitches toward vertical — exactly what a throwing swing
 * does. At beta = 89 the heading reads -30; at beta = 91 the same phone reads +150.
 * A two degree difference in pitch mid-swing flips the aim completely around, which
 * is why physically straight throws veered hard left or right at random.
 *
 * Instead this takes the device's +Y axis (out of the top of the phone, the end you
 * point at the screen), rotates it into world space, and projects it onto the ground.
 * Deriving it from the full rotation rather than one Euler angle is what removes the
 * singularity. `confidence` is the length of that projection, signed by whether the
 * phone points forward or backward — near zero means the phone is aimed at the sky or
 * the floor and its heading means nothing.
 *
 * gamma is deliberately absent: rolling the phone about its long axis cannot change
 * where its top points, so aim is immune to wrist roll for free.
 *
 * @returns {{headingDeg: number, confidence: number}} bearing, clockwise-positive
 */
export function pointingHeading(alpha, beta) {
  const a = (alpha * Math.PI) / 180;
  const b = (beta * Math.PI) / 180;

  // Device +Y expressed in world coordinates (X east, Y north, Z up).
  const cosBeta = Math.cos(b);
  const east = -Math.sin(a) * cosBeta;
  const north = Math.cos(a) * cosBeta;

  return {
    headingDeg: (Math.atan2(east, north) * 180) / Math.PI,
    confidence: cosBeta,
  };
}

// ---------------------------------------------------------------------------
// Quaternion attitude — {w,x,y,z}, unit, mapping device-frame vectors into the
// world frame (X east, Y north, Z up).
//
// WHY A FULL ATTITUDE AND NOT A SCALAR HEADING: the pointing bearing is
// degenerate while the phone is near vertical — exactly the middle of a swing.
// Integrating the bearing directly forces a choice there between freezing (all
// yaw applied through the steep part of the swing is lost forever) and dividing
// by cos(pitch) (gyro noise amplified without bound). Integrating the full 3D
// attitude has neither failure: rotation is well-defined in every pose, and the
// bearing is simply *read out* of the attitude whenever the phone points
// somewhere near the horizon again — with everything that happened in between
// accounted for.
//
// Integrating the world-vertical rotation component alone is NOT equivalent and
// was tried and reverted: rolling a tilted phone about its long axis sweeps a
// large world-vertical rotation while the top of the phone keeps pointing
// exactly where it did. The attitude form keeps that roll-immunity by
// construction — rotation about the pointing axis cannot move the pointing axis.
// ---------------------------------------------------------------------------

/** Intrinsic Z-X'-Y'' (deviceorientation alpha/beta/gamma) -> quaternion. */
export function quatFromEuler(alphaDeg, betaDeg, gammaDeg) {
  const a = (alphaDeg * Math.PI) / 360; // half angles
  const b = (betaDeg * Math.PI) / 360;
  const g = (gammaDeg * Math.PI) / 360;
  const qz = { w: Math.cos(a), x: 0, y: 0, z: Math.sin(a) };
  const qx = { w: Math.cos(b), x: Math.sin(b), y: 0, z: 0 };
  const qy = { w: Math.cos(g), x: 0, y: Math.sin(g), z: 0 };
  return quatMultiply(quatMultiply(qz, qx), qy);
}

export function quatMultiply(q, r) {
  return {
    w: q.w * r.w - q.x * r.x - q.y * r.y - q.z * r.z,
    x: q.w * r.x + q.x * r.w + q.y * r.z - q.z * r.y,
    y: q.w * r.y - q.x * r.z + q.y * r.w + q.z * r.x,
    z: q.w * r.z + q.x * r.y - q.y * r.x + q.z * r.w,
  };
}

export function quatNormalize(q) {
  const n = Math.hypot(q.w, q.x, q.y, q.z);
  if (n < 1e-12) return { w: 1, x: 0, y: 0, z: 0 };
  return { w: q.w / n, x: q.x / n, y: q.y / n, z: q.z / n };
}

/**
 * Advance an attitude by one gyro sample. `rotationRate` is deg/s in the DEVICE
 * frame (alpha about Z, beta about X, gamma about Y), so the delta rotation is
 * applied on the right — a body-frame update.
 */
export function quatIntegrate(q, rotationRate, dtMs) {
  const dt = dtMs / 1000;
  const wx = ((rotationRate.beta * Math.PI) / 180) * dt;
  const wy = ((rotationRate.gamma * Math.PI) / 180) * dt;
  const wz = ((rotationRate.alpha * Math.PI) / 180) * dt;
  const angle = Math.hypot(wx, wy, wz);
  if (angle < 1e-9) return q;

  const s = Math.sin(angle / 2) / angle;
  const dq = { w: Math.cos(angle / 2), x: wx * s, y: wy * s, z: wz * s };
  return quatNormalize(quatMultiply(q, dq));
}

/** Shortest-path slerp; t=0 returns a, t=1 returns b. */
export function quatSlerp(a, b, t) {
  let dot = a.w * b.w + a.x * b.x + a.y * b.y + a.z * b.z;
  let bw = b.w, bx = b.x, by = b.y, bz = b.z;
  if (dot < 0) { dot = -dot; bw = -bw; bx = -bx; by = -by; bz = -bz; }

  // Nearly parallel: lerp, which is faster and numerically safer here.
  if (dot > 0.9995) {
    return quatNormalize({
      w: a.w + (bw - a.w) * t,
      x: a.x + (bx - a.x) * t,
      y: a.y + (by - a.y) * t,
      z: a.z + (bz - a.z) * t,
    });
  }

  const theta = Math.acos(Math.min(1, dot));
  const sinTheta = Math.sin(theta);
  const wa = Math.sin((1 - t) * theta) / sinTheta;
  const wb = Math.sin(t * theta) / sinTheta;
  return quatNormalize({
    w: wa * a.w + wb * bw,
    x: wa * a.x + wb * bx,
    y: wa * a.y + wb * by,
    z: wa * a.z + wb * bz,
  });
}

/**
 * Bearing of the device's +Y axis (the top of the phone) read out of an
 * attitude, plus the length of its horizontal projection as confidence.
 * Agrees with pointingHeading(alpha, beta) for the poses that function accepts;
 * unlike it, this stays meaningful for an attitude the gyro carried through the
 * steep part of a swing.
 */
export function pointingFromQuat(q) {
  // R(q) * (0,1,0) — the middle column of the rotation matrix. Only the
  // horizontal components matter for a bearing.
  const east = 2 * (q.x * q.y - q.w * q.z);
  const north = q.w * q.w - q.x * q.x + q.y * q.y - q.z * q.z;

  return {
    headingDeg: (Math.atan2(east, north) * 180) / Math.PI,
    confidence: Math.hypot(east, north),
  };
}

export function clamp(value, min, max) {
  return value < min ? min : value > max ? max : value;
}

/** Map a value from one range to another, clamped to 0..1. */
export function normalize01(value, min, max) {
  if (max === min) return 0;
  return clamp((value - min) / (max - min), 0, 1);
}

/**
 * Separates gravity from user motion.
 *
 * We cannot use `DeviceMotionEvent.acceleration` (which would already exclude
 * gravity) because iOS does not provide it — see readAcceleration below. So we
 * low-pass the gravity-inclusive signal to estimate the gravity vector, then
 * subtract it. A low K reacts fast but leaks motion into the gravity estimate;
 * a high K is stable but lags when the phone is re-oriented.
 */
export const REFERENCE_SAMPLE_INTERVAL_MS = 1000 / 60;

export class GravityFilter {
  constructor(k = 0.8) {
    this.k = k;
    this.gravity = null;

    // The configured k is a per-SAMPLE weight, which silently encodes a sample
    // rate. Convert it once into a time constant so the filter can hold the same
    // behaviour in wall-clock terms on a device that reports at a different rate.
    // Android Chrome runs ~60Hz; iOS Safari is commonly slower, which would
    // otherwise make the gravity estimate adapt at half the intended speed and
    // leak the swing into the estimate it is supposed to be subtracting.
    this.tauMs = k > 0 && k < 1
      ? -REFERENCE_SAMPLE_INTERVAL_MS / Math.log(k)
      : 0;
  }

  /**
   * @param accel gravity-inclusive acceleration
   * @param dtMs time since the previous sample. Defaults to the 60Hz reference, so
   *             at 60Hz this is bit-for-bit the old behaviour.
   * @returns {{x:number,y:number,z:number}} linear acceleration, gravity removed
   */
  update(accel, dtMs = REFERENCE_SAMPLE_INTERVAL_MS) {
    if (this.gravity === null) {
      // Seed from the first sample. Starting at zero would report a full 9.8 m/s^2
      // of fake "user acceleration" on the first frame and spike the power reading.
      this.gravity = { x: accel.x, y: accel.y, z: accel.z };
    } else {
      const k = this.tauMs > 0
        ? Math.exp(-Math.max(dtMs, 0) / this.tauMs)
        : this.k;

      this.gravity.x = k * this.gravity.x + (1 - k) * accel.x;
      this.gravity.y = k * this.gravity.y + (1 - k) * accel.y;
      this.gravity.z = k * this.gravity.z + (1 - k) * accel.z;
    }

    return {
      x: accel.x - this.gravity.x,
      y: accel.y - this.gravity.y,
      z: accel.z - this.gravity.z,
    };
  }

  reset() {
    this.gravity = null;
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

/**
 * Streams normalized samples from the device's motion and orientation sensors.
 *
 * Orientation is always reported relative to a calibration reference captured on
 * grip, never as an absolute. That is what makes the output comparable across
 * platforms: iOS and Android disagree on absolute heading (different norths, and
 * they count in opposite directions), but a delta from a captured zero agrees.
 */
export class MotionSource {
  constructor({ gravityK = 0.8 } = {}) {
    this.gravityFilter = new GravityFilter(gravityK);
    this.buffer = new RingBuffer(Math.ceil(SAMPLE_HZ * BUFFER_SECONDS));

    this.reference = null;
    this.latestOrientation = { alpha: 0, beta: 0, gamma: 0 };
    this.latest = null;
    this.running = false;

    // Full device attitude, integrated from the gyro at the sensor rate and slowly
    // corrected toward the fused deviceorientation reading. The bearing below is
    // READ OUT of this whenever the phone points somewhere near the horizon, and
    // held through the steep middle of a swing where a bearing means nothing — but
    // the attitude keeps integrating there, so every degree of turn applied while
    // the phone was steep is still present in the bearing that comes back out.
    this._attitude = null;

    // Bearing of the phone's +Y axis, and the bearing captured at calibration.
    this.headingDeg = null;
    this.referenceHeading = null;

    // Set when a grip was taken before the first confident heading existed; the
    // reference latches the moment one does — see calibrate().
    this._awaitingReferenceHeading = false;

    // Called with every normalized sample. The aim stream hangs off this so a
    // frame leaves the phone the instant the sensor produced it, instead of
    // waiting for a timer tick to come around.
    this.onSample = null;

    // Last gyro-derived turn rate, deg/s. Also decides how far the fused orientation
    // reading is allowed to pull the estimate — see _onOrientation.
    this.headingRateDegPerSec = 0;

    // Measured, not assumed. The whole cross-platform question comes down to
    // whether the two devices are actually feeding us data at comparable rates,
    // and this is the number that answers it.
    this.sampleHz = 0;
    this._lastSampleT = 0;
    this._intervalMs = 0;

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
    this.referenceHeading = this.headingDeg;

    // Gripped before the first confident heading fix (first turn of a session, or
    // the phone held steep): latch the reference the moment a heading exists,
    // instead of pinning dYaw to zero for the whole grip and sending every rock
    // dead straight regardless of aim.
    this._awaitingReferenceHeading = this.headingDeg === null;

    this.gravityFilter.reset();
    this.buffer.clear();
  }

  hasReference() {
    return this.reference !== null;
  }

  /**
   * Read the bearing out of the integrated attitude. Held (not updated) while the
   * horizontal projection of the pointing axis is too short to mean anything; the
   * attitude keeps integrating regardless, so the first well-conditioned pose
   * after a steep swing yields a bearing with the whole swing accounted for.
   */
  _refreshHeading() {
    const { headingDeg, confidence } = pointingFromQuat(this._attitude);
    if (confidence < MIN_POINTING_CONFIDENCE) return;

    this.headingDeg = headingDeg;

    if (this._awaitingReferenceHeading) {
      this.referenceHeading = headingDeg;
      this._awaitingReferenceHeading = false;
    }
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

    // Only trust the fused pose while the phone is actually pointing somewhere on
    // the horizon — near vertical its Euler angles are degenerate (see
    // pointingHeading) and would steer the attitude with garbage. Through the
    // steep part of a swing the gyro carries the attitude alone.
    const { confidence } = pointingHeading(
      this.latestOrientation.alpha, this.latestOrientation.beta);

    if (confidence < MIN_POINTING_CONFIDENCE) return;

    const fused = quatFromEuler(
      this.latestOrientation.alpha, this.latestOrientation.beta, this.latestOrientation.gamma);

    if (this._attitude === null) {
      this._attitude = fused;
      this._refreshHeading();
      return;
    }

    // Correct rather than overwrite, and only in proportion to how slowly the phone is
    // turning. This reading is drift-free but lags; the integrated one is immediate but
    // drifts. Snapping to this mid-swing would hand the lag straight back — so while the
    // phone is whipping around the gyro is believed outright, and the fused value only
    // reasserts itself once things settle, which is when its lag costs nothing.
    const trust = clamp(
      1 - Math.abs(this.headingRateDegPerSec) / FAST_ROTATION_DEG_PER_SEC, 0, 1);

    this._attitude = quatSlerp(this._attitude, fused, HEADING_CORRECTION_GAIN * trust);
    this._refreshHeading();
  }

  _onMotion(event) {
    const accel = readAcceleration(event);
    if (!accel) return;

    const now = performance.now();
    const dtMs = this._lastSampleT > 0 ? now - this._lastSampleT : REFERENCE_SAMPLE_INTERVAL_MS;
    this._lastSampleT = now;

    // Smoothed so one hitched frame does not swing the reported rate around.
    this._intervalMs = this._intervalMs > 0 ? this._intervalMs * 0.9 + dtMs * 0.1 : dtMs;
    this.sampleHz = this._intervalMs > 0 ? 1000 / this._intervalMs : 0;

    const rotationRate = readRotationRate(event);

    // Turn-speed signal only (the debug display, and the trust gate in
    // _onOrientation). The heading itself no longer integrates this scalar: near
    // vertical it is singular, and the old freeze-past-70° gate silently
    // discarded every degree of turn applied through the steep part of a swing.
    this.headingRateDegPerSec = headingRateFromGyro(
      this.latestOrientation.beta, this.latestOrientation.gamma, rotationRate);

    // Advance the full attitude by the gyro at the sensor rate. This is the half
    // of the filter that makes a fast swing survive: orientation events arrive on
    // their own, slower schedule and are already stale when they do, so between
    // them the attitude would otherwise sit frozen at a pose from tens of
    // milliseconds ago — and the release frame would report exactly that stale angle.
    if (this._attitude !== null) {
      this._attitude = quatIntegrate(this._attitude, rotationRate, dtMs);
      this._refreshHeading();
    }

    const linearRaw = this.gravityFilter.update(accel, dtMs);

    // Keep axes stable if the device rotates. Portrait lock is requested elsewhere
    // but is best-effort — not all browsers honour it — so compensate regardless.
    const screenAngle = window.screen?.orientation?.angle ?? 0;
    const rotated = rotateXY(linearRaw.x, linearRaw.y, -screenAngle);
    const linear = { x: rotated.x, y: rotated.y, z: linearRaw.z };

    const ref = this.reference ?? this.latestOrientation;
    const sample = {
      t: now,
      linear,
      power: magnitude(linear),

      // Live, every sample, right through the swing. Aim is whatever the phone is
      // pointing at now — the throw is taken from the last sample before release, so
      // freezing this earlier would throw away the aim the player actually released
      // on. Zero until a confident heading exists, so an uncalibrated throw goes
      // straight rather than somewhere arbitrary.
      dYaw: this.headingDeg !== null && this.referenceHeading !== null
        ? shortestAngleDelta(this.headingDeg, this.referenceHeading)
        : 0,

      dBeta: shortestAngleDelta(this.latestOrientation.beta, ref.beta),
      dGamma: shortestAngleDelta(this.latestOrientation.gamma, ref.gamma),
      rotationRate,
    };

    this.latest = sample;
    this.buffer.push(sample);
    this.onSample?.(sample);
  }
}
