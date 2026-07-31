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

/**
 * Sign that converts device heading into game yaw. This is the one place the two
 * coordinate conventions meet, so it is the one place that can get it backwards.
 *
 * `dYaw` is a compass-style bearing delta from `pointingHeading` — clockwise-positive
 * viewed from above. Unity's `Quaternion.AngleAxis(deg, up)` is also clockwise viewed
 * from above, taking +Z toward +X. So the two already agree and the default is +1.
 *
 * (This was -1 while aim came straight from Euler `alpha`, which counts the other way.
 * Deriving the heading from the pointing vector changed the handedness of the source
 * value, so the correction that used to be needed would now reintroduce the error.)
 *
 * GAME-SPACE CONTRACT: positive aimDeg means aim to the player's right.
 *
 * Overridable per-device from the URL (?yawSign=-1) — see setYawSign.
 */
let yawSign = 1;

/** @param {number} sign -1 (default) or +1 */
export function setYawSign(sign) {
  yawSign = sign < 0 ? -1 : 1;
}

export function getYawSign() {
  return yawSign;
}

/** Device heading delta -> game yaw. Positive is right. */
export function toGameYaw(dYaw) {
  return yawSign * dYaw;
}

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

  // How far back from the release a swing can reach. Bounded in TIME, not in
  // samples: the ring buffer holds a fixed sample count sized for a 60Hz device,
  // so on a device reporting at ~30Hz it spans twice as long in wall-clock terms.
  // Without this bound such a device can select a "peak" from seconds before the
  // swing, which makes releaseDelayMs huge and collapses timingQuality to zero.
  swingWindowMs: 1500,
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
 * How the player marks the moment the rock leaves their hand.
 *
 * HOLD — press and hold, swing, let go. The release is a finger lift.
 * TAP  — tap to arm, swing, tap again to throw. The release is a deliberate press.
 *
 * TAP exists because lifting a finger mid-swing is an imprecise moment: the thumb
 * leaves the glass whenever the swing shakes it loose, not necessarily when the
 * player means to let go. A second tap is a positive action they choose to make.
 */
export const ThrowGesture = {
  HOLD: 'hold',
  TAP: 'tap',
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
  const windowStart = release.t - tuning.swingWindowMs;

  // Peak, not integral. Peak matches perceived effort and is far more forgiving
  // of the sample rate wobbling, which it does on real phones. Restricted to the
  // recent window so a low-rate device cannot reach back past the swing itself.
  let peak = null;
  for (const sample of samples) {
    if (sample.t < windowStart) continue;
    if (peak === null || sample.power > peak.power) peak = sample;
  }

  if (peak === null || peak.power < tuning.minValidPeakAccel) return null;

  const releaseDelayMs = Math.abs(release.t - peak.t);

  return {
    power: normalize01(peak.power, tuning.minPeakAccel, tuning.maxPeakAccel),
    timingQuality: 1 - normalize01(releaseDelayMs, 0, tuning.peakWindowMs),
    aimDeg: clamp(toGameYaw(release.dYaw), -tuning.maxAimDeg, tuning.maxAimDeg),
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
    aimDeg: clamp(toGameYaw(sample.dYaw), -tuning.maxAimDeg, tuning.maxAimDeg),
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
  constructor(motion, {
    onAim, onThrow, onStateChange, onExpire, aimHz = 20,
    gesture = ThrowGesture.TAP,
    minArmMs = 150,
    maxArmMs = 10_000,
  } = {}) {
    this.motion = motion;
    this.onAim = onAim ?? (() => {});
    this.onThrow = onThrow ?? (() => {});
    this.onStateChange = onStateChange ?? (() => {});
    this.onExpire = onExpire ?? (() => {});
    this.aimIntervalMs = 1000 / aimHz;

    this.gesture = gesture;

    // Shortest gap between arming and throwing. Below this a double-tap, or one
    // bouncy tap, would arm and fire in the same motion and launch a rock with no
    // swing behind it at all.
    this.minArmMs = minArmMs;

    // In TAP mode the armed state is invisible — no finger is on the glass — so a
    // player who gets distracted mid-turn would otherwise stream aim frames forever
    // and stall the match. In HOLD mode the held finger is unambiguous intent, so
    // this does not apply there.
    this.maxArmMs = maxArmMs;

    this.state = ThrowState.IDLE;
    this.seq = 0;
    this._timer = null;
    this._grippedAt = 0;
  }

  /**
   * One tap, dispatched by state: the first arms the throw, the second releases it.
   * Only used in TAP mode; HOLD mode drives grip/release directly.
   *
   * @returns {object|null} the throw descriptor if this tap threw, else null
   */
  tap() {
    if (this.state === ThrowState.IDLE) {
      this.grip();
      return null;
    }

    if (performance.now() - this._grippedAt < this.minArmMs) return null;

    return this.release();
  }

  /** Player pressed and held. Captures the aim reference and starts streaming. */
  grip() {
    if (this.state !== ThrowState.IDLE) return;

    this._grippedAt = performance.now();
    this.motion.calibrate();
    this._setState(ThrowState.GRIPPED);

    this._timer = setInterval(() => {
      // Only TAP mode can be armed-and-forgotten; see maxArmMs.
      if (this.gesture === ThrowGesture.TAP &&
          this.maxArmMs > 0 &&
          performance.now() - this._grippedAt > this.maxArmMs) {
        this.cancel();
        this.onExpire();
        return;
      }

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
      // sampleHz rides along for diagnostics: it is the number that tells you
      // whether two platforms are actually feeding the same quality of data.
      this.onThrow({
        type: 'THROW',
        ...descriptor,
        sampleHz: Math.round(this.motion.sampleHz ?? 0),
      });
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
