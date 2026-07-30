/**
 * Throw gesture: state machine plus the descriptor computed on release.
 *
 * Two channels leave this module, with deliberately different reliability:
 *
 *   AIM   — streamed ~20Hz while gripped. Lossy and cosmetic. Dropping every one
 *           of these must only make the TV preview stutter, never change a throw.
 *   THROW — one authoritative payload on release, computed from the whole swing.
 *
 * That split is why a network hiccup can never cause a "the game read my throw
 * wrong" moment. Keep it: nothing in AIM may feed the simulation.
 */

import { clamp, normalize01 } from './sensors.js';

export const ThrowTuning = {
  // Peak linear acceleration, m/s^2, mapped onto power 0..1. A gentle underarm
  // toss sits near the low end; a hard swing saturates the high end.
  minPeakAccel: 6,
  maxPeakAccel: 35,

  // How far from the acceleration peak a release can land before timing quality
  // decays to zero. This is the Skip Skimmer "release at the right moment" beat.
  peakWindowMs: 250,

  // Aim is clamped so a player who twists too far still gets a usable throw
  // rather than one aimed off the lake entirely.
  maxAimDeg: 45,

  minAttackDeg: -60,
  maxAttackDeg: 60,

  // Below this peak, treat the gesture as a fumble rather than a throw — stops a
  // pocket bump or a shaky hand registering as a real attempt.
  minValidPeakAccel: 3,
};

export const ThrowState = {
  IDLE: 'IDLE',
  GRIPPED: 'GRIPPED',
};

/**
 * Reduce a whole swing to the handful of numbers Unity needs.
 *
 * Pure: takes samples, returns a descriptor. No device access, no side effects,
 * so it is fully unit testable without a phone.
 *
 * @param {Array} samples oldest-to-newest, from MotionSource's ring buffer
 * @returns {object|null} descriptor, or null if the gesture was too weak to count
 */
export function computeThrowDescriptor(samples, tuning = ThrowTuning) {
  if (!samples || samples.length === 0) return null;

  const release = samples[samples.length - 1];

  // Peak, not integral. Peak matches perceived effort and is far more forgiving
  // of the sample rate wobbling, which it does on real phones.
  let peak = samples[0];
  for (const sample of samples) {
    if (sample.power > peak.power) peak = sample;
  }

  if (peak.power < tuning.minValidPeakAccel) return null;

  const releaseDelayMs = Math.abs(release.t - peak.t);

  return {
    power: normalize01(peak.power, tuning.minPeakAccel, tuning.maxPeakAccel),
    timingQuality: 1 - normalize01(releaseDelayMs, 0, tuning.peakWindowMs),
    aimDeg: clamp(release.dAlpha, -tuning.maxAimDeg, tuning.maxAimDeg),
    attackAngleDeg: clamp(release.dBeta, tuning.minAttackDeg, tuning.maxAttackDeg),
    rollDeg: release.dGamma,
    spinRate: Math.hypot(
      release.rotationRate.alpha,
      release.rotationRate.beta,
      release.rotationRate.gamma
    ),

    // Diagnostics — useful on the TV debug readout while tuning, ignored by physics.
    peakAccel: peak.power,
    releaseDelayMs,
    sampleCount: samples.length,
  };
}

/** Cosmetic aim frame. Never authoritative — see the module header. */
export function buildAimPayload(sample, seq, tuning = ThrowTuning) {
  if (!sample) return null;
  return {
    type: 'AIM',
    seq,
    aimDeg: clamp(sample.dAlpha, -tuning.maxAimDeg, tuning.maxAimDeg),
    attackAngleDeg: clamp(sample.dBeta, tuning.minAttackDeg, tuning.maxAttackDeg),
    rollDeg: sample.dGamma,
    windup: normalize01(sample.power, tuning.minPeakAccel, tuning.maxPeakAccel),
  };
}

/**
 * Drives the grip/release gesture and owns the AIM stream while gripped.
 *
 * Deliberately does not know about the network — it calls onAim/onThrow and lets
 * the caller decide what to do with them, so this can be tested with plain stubs.
 */
export class ThrowController {
  constructor(motion, { onAim, onThrow, onStateChange, aimHz = 20 } = {}) {
    this.motion = motion;
    this.onAim = onAim ?? (() => {});
    this.onThrow = onThrow ?? (() => {});
    this.onStateChange = onStateChange ?? (() => {});
    this.aimIntervalMs = 1000 / aimHz;

    this.state = ThrowState.IDLE;
    this.seq = 0;
    this._timer = null;
  }

  /** Player pressed and held. Captures the aim reference and starts streaming. */
  grip() {
    if (this.state !== ThrowState.IDLE) return;

    this.motion.calibrate();
    this._setState(ThrowState.GRIPPED);

    this._timer = setInterval(() => {
      const payload = buildAimPayload(this.motion.latest, ++this.seq);
      if (payload) this.onAim(payload);
    }, this.aimIntervalMs);
  }

  /** Player let go. Analyses the swing and emits exactly one THROW. */
  release() {
    if (this.state !== ThrowState.GRIPPED) return null;

    this._stopTimer();
    this._setState(ThrowState.IDLE);

    const descriptor = computeThrowDescriptor(this.motion.buffer.toArray());
    if (descriptor) this.onThrow({ type: 'THROW', ...descriptor });
    return descriptor;
  }

  /** Turn ended or connection dropped mid-grip — abandon without throwing. */
  cancel() {
    this._stopTimer();
    this._setState(ThrowState.IDLE);
  }

  _stopTimer() {
    if (this._timer !== null) {
      clearInterval(this._timer);
      this._timer = null;
    }
  }

  _setState(next) {
    if (this.state === next) return;
    this.state = next;
    this.onStateChange(next);
  }
}
