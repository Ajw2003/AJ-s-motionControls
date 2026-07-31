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
      dAlpha: shortestAngleDelta(this.latestOrientation.alpha, ref.alpha),
      dBeta: shortestAngleDelta(this.latestOrientation.beta, ref.beta),
      dGamma: shortestAngleDelta(this.latestOrientation.gamma, ref.gamma),
      rotationRate: readRotationRate(event),
    };

    this.latest = sample;
    this.buffer.push(sample);
  }
}
