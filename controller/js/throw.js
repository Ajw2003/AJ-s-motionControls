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

/**
 * Shape of a throw synthesized on a device with no motion sensors.
 *
 * This exists so a desktop browser can occupy a seat during multi-player testing.
 * A second phone is the real test; a second PC is the one most people can actually
 * arrange, and without this it can join, take its turn, and then deadlock the match
 * — the turn only advances when a throw completes.
 */
export const SimulatedThrow = {
  minHoldMs: 150,
  maxHoldMs: 1200,
  minPower: 0.2,
  maxPower: 0.95,

  // attackAngleDeg is a delta from the player's grip, and SkipTuning maps a neutral
  // grip onto the stone's optimal angle — so 0 is already a good throw. The jitter
  // stays well inside the tolerance window so simulated throws skip rather than
  // sinking, which would make every test score identically zero.
  attackJitterDeg: 6,
  aimJitterDeg: 10,
  spinRate: 120,
  timingQuality: 0.85,
};

/**
 * Build a throw from nothing but how long the button was held.
 *
 * Pure, and takes its randomness as a parameter, so tests can pin it.
 *
 * @param {number} holdMs how long the grip was held
 * @param {function} random source of 0..1 randomness
 */
export function computeSimulatedThrowDescriptor(holdMs, random = Math.random) {
  const charge = normalize01(holdMs, SimulatedThrow.minHoldMs, SimulatedThrow.maxHoldMs);
  const jitter = (range) => (random() * 2 - 1) * range;

  return {
    power: SimulatedThrow.minPower + charge * (SimulatedThrow.maxPower - SimulatedThrow.minPower),
    timingQuality: SimulatedThrow.timingQuality,
    aimDeg: jitter(SimulatedThrow.aimJitterDeg),
    attackAngleDeg: jitter(SimulatedThrow.attackJitterDeg),
    rollDeg: 0,
    spinRate: SimulatedThrow.spinRate,

    // Zero samples is the tell. A real gesture always has some, so the TV debug
    // readout distinguishes a simulated throw from a genuine one at a glance.
    peakAccel: 0,
    releaseDelayMs: 0,
    sampleCount: 0,
    simulated: true,
  };
}

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
    this._grippedAt = 0;
  }

  /** Player pressed and held. Captures the aim reference and starts streaming. */
  grip() {
    if (this.state !== ThrowState.IDLE) return;

    this._grippedAt = performance.now();
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

    const holdMs = performance.now() - this._grippedAt;

    this._stopTimer();
    this._setState(ThrowState.IDLE);

    const descriptor = computeThrowDescriptor(this.motion.buffer.toArray());
    if (descriptor) {
      this.onThrow({ type: 'THROW', ...descriptor });
      return descriptor;
    }

    // A real device that produced samples but a weak gesture is a fumble, and must
    // stay one. Only a device that has never produced a single motion event has no
    // sensors at all — a phone sets `latest` within a frame of start(), long before
    // anyone can grip, so this branch is unreachable on real hardware.
    if (this.motion.latest === null) {
      const simulated = computeSimulatedThrowDescriptor(holdMs);
      this.onThrow({ type: 'THROW', ...simulated });
      return simulated;
    }

    return null;
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
