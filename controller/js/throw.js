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

import { normalize01, spinMagnitude } from './sensors.js';

export const ThrowTuning = {
  // Peak linear acceleration, m/s^2, mapped onto power 0..1. A gentle underarm
  // toss sits near the low end; a hard swing saturates the high end.
  minPeakAccel: 6,
  maxPeakAccel: 35,

  // How sharply the swing has to stop for full timing quality. A flick that snaps
  // to a halt right after its peak scores 1; one that trails off scores lower.
  peakWindowMs: 250,

  // Below this peak, treat the gesture as a fumble rather than a throw — stops a
  // pocket bump or a shaky hand registering as a real attempt.
  minValidPeakAccel: 3,

  // --- release detection ---------------------------------------------------
  // The gesture is "accelerate hard, then stop dead". There is no button release:
  // the stop IS the release, which is why orientation is read raw at that instant
  // rather than inferred from earlier in the swing. A throwing motion ends pointing
  // where it was aimed, so the stop sample is the aim.

  // Acceleration that counts as a real swing rather than hand tremor.
  swingAccel: 14,

  // Once swinging, acceleration falling below this counts as stopping.
  stopAccel: 4,

  // Consecutive calm samples needed to call it a stop. One is not enough: a swing
  // passes through zero acceleration at its own turnaround and would fire mid-arc.
  stopSamples: 3,

  // If the phone never settles, throw anyway this long after the peak rather than
  // leaving the player armed forever.
  maxSwingMs: 900,

  // --- power ---------------------------------------------------------------
  // Which measurement becomes `power`. 'swingSpeed' integrates acceleration into a
  // hand speed; 'peakAccel' is the original peak magnitude. Both are always computed
  // and both cross the wire, so this is a one-word switch and the loser stays
  // available — see peakSwingSpeed for why the default changed.
  powerMetric: 'swingSpeed',

  // Hand speed at power 0 and power 1, m/s. Starting estimates only: calibration
  // replaces them per player, because throwing strength varies far more between
  // people than any single pair of constants can cover.
  minSwingSpeed: 0.9,
  maxSwingSpeed: 6.5,

  // --- spin ----------------------------------------------------------------
  // How long a spin peak is held before it has decayed away. Spin is peaky: a
  // wrist flick is over in about 100ms, so a 20Hz aim frame samples it up to 50ms
  // late and mostly catches the tail. Holding the peak briefly is what turns that
  // into a ghost that spins up and winds down instead of one that flickers.
  //
  // The throw uses the same envelope, so the stone spins at the rate the ghost was
  // showing. Raise this if a slow release is bleeding away spin the player earned.
  spinEnvelopeTauMs: 250,
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

  // Sits at SkipTuning's saturation point (MaxSpinToleranceBonus / SpinToleranceScale),
  // which is also what AimPreview draws its arc at. Below it a desktop seat throws with
  // measurably less angle tolerance than the arc it was shown promised — at the old 120
  // it got 17.4 degrees against a real phone's 33.
  spinRate: 900,
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

    // No gesture happened, so neither power metric has a real reading. Zero rather
    // than a plausible-looking number, so a simulated throw can never be mistaken for
    // measured data during calibration.
    swingSpeed: 0,

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
  ARMED: 'ARMED',
};

/**
 * Reduce a whole swing to the handful of numbers Unity needs.
 *
 * Pure: takes samples, returns a descriptor. No device access, no side effects,
 * so it is fully unit testable without a phone.
 *
 * @param {Array} samples oldest-to-newest, from MotionSource's ring buffer
 * @param {object} tuning
 * @param {number} [spinRate] the live spin envelope at release — see the spinRate
 *   field below. Omitting it replays the envelope over `samples`, which keeps this
 *   function usable from a buffer alone.
 * @returns {object|null} descriptor, or null if the gesture was too weak to count
 */
export function computeThrowDescriptor(samples, tuning = ThrowTuning, spinRate) {
  if (!samples || samples.length === 0) return null;

  // The last sample is the moment the phone stopped, which is the release. Nothing
  // is inferred, averaged or reached back for: what the sensors said at that instant
  // is what the stone does.
  const stop = samples[samples.length - 1];

  // Peak, not integral. Peak matches perceived effort and is far more forgiving
  // of the sample rate wobbling, which it does on real phones.
  let peak = samples[0];
  for (const sample of samples) {
    if (sample.power > peak.power) peak = sample;
  }

  if (peak.power < tuning.minValidPeakAccel) return null;

  const swingSpeed = peakSwingSpeed(samples, tuning);
  const power = tuning.powerMetric === 'peakAccel'
    ? normalize01(peak.power, tuning.minPeakAccel, tuning.maxPeakAccel)
    : normalize01(swingSpeed, tuning.minSwingSpeed, tuning.maxSwingSpeed);

  // How abruptly the swing ended. Snapping to a halt right after the peak is the
  // skill beat now that there is no button release to time.
  const releaseDelayMs = Math.abs(stop.t - peak.t);

  return {
    power,
    timingQuality: 1 - normalize01(releaseDelayMs, 0, tuning.peakWindowMs),

    // Unclamped, deliberately. Throwing backwards, sideways or straight up is a
    // legitimate thing to attempt, and clamping quietly rewrote what the player did
    // into something they did not ask for.
    aimDeg: stop.dAlpha,
    attackAngleDeg: stop.dBeta,
    rollDeg: stop.dGamma,

    // The one field deliberately NOT read from the stop sample. Aim, attack and roll
    // are orientations, and where the phone ended up pointing IS the throw. Spin is a
    // rate: it peaks mid-swing and is spent by the time the phone halts — and the
    // throw fires precisely because it halted. Reading it off `stop` sampled the one
    // instant the wrist had finished turning, so a hard flick and a limp one both
    // arrived as roughly zero and the stone never visibly span.
    spinRate: spinRate ?? peakSpinEnvelope(samples, tuning),

    // Diagnostics — useful on the TV debug readout while tuning, ignored by physics.
    // Both power metrics ride along whichever one is selected, so a calibration swing
    // measures them side by side and the choice can be made on data rather than
    // argument. See ThrowTuning.powerMetric.
    peakAccel: peak.power,
    swingSpeed,
    releaseDelayMs,
    sampleCount: samples.length,
  };
}

/**
 * A peak-hold envelope over the spin signal: jumps straight to any new peak, then
 * decays exponentially toward zero.
 *
 * Pure, and takes the elapsed time rather than counting samples, because the device
 * sample rate wobbles on real phones — a per-sample decay constant would make the
 * ghost wind down faster on a phone that happens to be reporting quickly.
 *
 * @param {number} previous  the envelope's last value
 * @param {number} instantaneous  spin magnitude from the newest sample
 * @param {number} dtMs  time since the last update
 */
/**
 * Peak hand speed during the swing, in m/s, by integrating linear acceleration.
 *
 * The physically correct measure of how hard a throw was. Peak *acceleration* — what
 * this used to key off exclusively — is a poor proxy: a short sharp jerk and a long
 * powerful pull peak identically, and on real hardware the useful range was squashed
 * into the top of the window, so a throw with two to three times less effort still
 * read within about 13% of a maximum one. Velocity is the integral of exactly the
 * thing that differs between them, so it separates what peak cannot.
 *
 * Integrated from the start of the swing rather than the whole buffer, because any
 * residual bias accumulates and a 1.5s window would collect far more of it than a
 * 300ms one. Tracks the running peak instead of the endpoint: the swing ends with the
 * phone stopped, so the value at the stop is by definition near zero — it is the
 * maximum along the way that the stone leaves the hand at.
 *
 * Only as good as the gravity removal feeding it; see sensors.js gravityInDeviceFrame.
 *
 * Pure.
 */
export function peakSwingSpeed(samples, tuning = ThrowTuning) {
  if (!samples || samples.length < 2) return 0;

  let peakIdx = 0;
  for (let i = 1; i < samples.length; i++) {
    if (samples[i].power > samples[peakIdx].power) peakIdx = i;
  }

  // Walk back from the peak to the last calm sample before it: that is where this
  // swing began, and anything earlier belongs to lining the shot up.
  let start = peakIdx;
  while (start > 0 && samples[start - 1].power > tuning.stopAccel) start--;

  let vx = 0, vy = 0, vz = 0, best = 0;
  for (let i = start + 1; i < samples.length; i++) {
    // Stop at the release. Past the peak, a calm sample means the phone has been let
    // go of and anything after it is the hand settling — integrating through that
    // adds speed the throw never had, and adds more of it the longer the player
    // stands still afterwards.
    if (i > peakIdx && samples[i].power <= tuning.stopAccel) break;

    const dtMs = samples[i].t - samples[i - 1].t;
    if (!(dtMs > 0) || dtMs > 250) continue;

    const a = samples[i].linear;
    if (!a) continue;

    const dt = dtMs / 1000;
    vx += a.x * dt;
    vy += a.y * dt;
    vz += a.z * dt;
    best = Math.max(best, Math.hypot(vx, vy, vz));
  }

  return best;
}

export function decaySpinEnvelope(previous, instantaneous, dtMs, tauMs = ThrowTuning.spinEnvelopeTauMs) {
  const decayed = previous * Math.exp(-Math.max(dtMs, 0) / tauMs);
  return Math.max(decayed, instantaneous);
}

/**
 * Replay decaySpinEnvelope across a whole buffer and return where it ended up.
 *
 * ThrowController already tracks this envelope live, so the real device path passes
 * its value straight in. This is for callers holding only samples — tests, tooling,
 * and computeThrowDescriptor's own fallback — and it agrees with the live tracker by
 * construction, because it is the same function over the same samples.
 *
 * The tau does the windowing: spin from before the swing, while the player was
 * lining the shot up, has decayed away by the time the buffer reaches the release,
 * so there is no separate window constant to keep in sync.
 *
 * Pure.
 */
export function peakSpinEnvelope(samples, tuning = ThrowTuning) {
  if (!samples || samples.length === 0) return 0;

  let spin = 0;
  let prevT = null;

  for (const sample of samples) {
    const dtMs = prevT === null ? 0 : sample.t - prevT;
    prevT = sample.t;
    spin = decaySpinEnvelope(spin, spinMagnitude(sample.rotationRate), dtMs, tuning.spinEnvelopeTauMs);
  }

  return spin;
}

/**
 * Cosmetic aim frame. Never authoritative — see the module header.
 *
 * Raw and unclamped, matching the throw descriptor exactly. Clamping here while the
 * throw went through unclamped would put the preview and the stone back into
 * disagreement, which is the whole class of bug this pass exists to remove.
 *
 * `spinRate` is passed in rather than read off the sample because the useful value is
 * the envelope tracked at sample rate — see ThrowController. Omitting it falls back to
 * this instant's spin, which keeps the function usable on its own and honest either way.
 */
export function buildAimPayload(sample, seq, tuning = ThrowTuning, spinRate) {
  if (!sample) return null;
  return {
    type: 'AIM',
    seq,
    aimDeg: sample.dAlpha,
    attackAngleDeg: sample.dBeta,
    rollDeg: sample.dGamma,
    windup: normalize01(sample.power, tuning.minPeakAccel, tuning.maxPeakAccel),
    spinRate: spinRate ?? spinMagnitude(sample.rotationRate),
  };
}

/**
 * Owns the arm/swing gesture and the AIM stream while armed.
 *
 * The gesture is: <b>tap to arm, then swing</b>. The throw fires when the phone
 * stops, not when a finger lifts — accelerate hard, stop dead, and the stone goes.
 * A second tap disarms instead, so a player who armed with the phone pointing the
 * wrong way can re-aim and tap again to recalibrate.
 *
 * Firing on the stop is what lets orientation be read raw. A throwing motion ends
 * pointing where it was thrown, so the stop sample already is the aim — nothing has
 * to be inferred from earlier in the swing, and nothing can disagree with it.
 *
 * Deliberately does not know about the network — it calls onAim/onThrow and lets
 * the caller decide what to do with them, so this can be tested with plain stubs.
 */
export class ThrowController {
  constructor(motion, { onAim, onThrow, onStateChange, aimHz = 20, tuning = ThrowTuning } = {}) {
    this.motion = motion;
    this.onAim = onAim ?? (() => {});
    this.onThrow = onThrow ?? (() => {});
    this.onStateChange = onStateChange ?? (() => {});
    this.aimIntervalMs = 1000 / aimHz;
    this.tuning = tuning;

    this.state = ThrowState.IDLE;
    this.seq = 0;
    this._timer = null;
    this._armedAt = 0;

    this._swinging = false;
    this._peak = null;
    this._calmRun = 0;

    // Live spin for the preview, and when it was last updated. Null rather than 0,
    // because a timestamp of 0 is a legitimate reading and must not be mistaken for
    // "no previous sample" — that would skip the decay for the sample after it.
    this._spin = 0;
    this._spinT = null;

    // Detection runs per sample rather than on the aim timer: a stop is a handful of
    // milliseconds and polling at 20Hz reads the phone well after it has settled.
    if (motion) motion.onSample = (sample) => this._onSample(sample);
  }

  /** Tap. Arms if idle, disarms if already armed. */
  toggle() {
    if (this.state === ThrowState.IDLE) return this.arm();
    return this.disarm();
  }

  /** Captures the aim reference and starts streaming. */
  arm() {
    if (this.state !== ThrowState.IDLE) return null;

    this._armedAt = performance.now();
    this._swinging = false;
    this._peak = null;
    this._calmRun = 0;
    this._spin = 0;
    this._spinT = null;

    this.motion.calibrate();
    this._setState(ThrowState.ARMED);

    this._timer = setInterval(() => {
      const payload = buildAimPayload(this.motion.latest, ++this.seq, this.tuning, this._spin);
      if (payload) this.onAim(payload);
    }, this.aimIntervalMs);

    return null;
  }

  /**
   * Second tap. Stands down without throwing so the player can re-aim — arming
   * again recalibrates, which is the only way to fix a reference captured while
   * pointing the wrong way.
   *
   * On a device with no motion sensors this throws instead, because otherwise a
   * desktop browser can arm and then never fire, deadlocking the match. A phone
   * populates `latest` within a frame of start(), long before anyone can tap.
   */
  disarm() {
    if (this.state !== ThrowState.ARMED) return null;

    const heldMs = performance.now() - this._armedAt;
    this.cancel();

    if (this.motion.latest === null) {
      const simulated = computeSimulatedThrowDescriptor(heldMs);
      this.onThrow({ type: 'THROW', ...simulated });
      return simulated;
    }

    return null;
  }

  /** Turn ended or connection dropped while armed — stand down without throwing. */
  cancel() {
    this._stopTimer();
    this._swinging = false;
    this._peak = null;
    this._calmRun = 0;
    this._spin = 0;
    this._spinT = null;
    this._setState(ThrowState.IDLE);
  }

  // ---------------------------------------------------------------- detection

  _onSample(sample) {
    if (this.state !== ThrowState.ARMED) return;

    // Tracked here rather than on the aim timer, for the same reason release
    // detection is: a wrist flick peaks and is gone well inside one 20Hz interval,
    // so sampling spin there would read mostly tail and miss what the player did.
    const dtMs = this._spinT === null ? 0 : sample.t - this._spinT;
    this._spinT = sample.t;
    this._spin = decaySpinEnvelope(
      this._spin, spinMagnitude(sample.rotationRate), dtMs, this.tuning.spinEnvelopeTauMs);

    if (sample.power >= this.tuning.swingAccel) {
      this._swinging = true;
      this._calmRun = 0;
      if (this._peak === null || sample.power > this._peak.power) this._peak = sample;
      return;
    }

    if (!this._swinging) return;

    // A swing passes through near-zero acceleration at its own turnaround, so one
    // calm sample is not a stop. Requiring a short run of them is what stops the
    // throw firing halfway through the arc.
    if (sample.power <= this.tuning.stopAccel) {
      this._calmRun++;
      if (this._calmRun >= this.tuning.stopSamples) this._fire();
      return;
    }

    this._calmRun = 0;

    // Never leave a player armed because their phone did not quite settle.
    if (this._peak && sample.t - this._peak.t > this.tuning.maxSwingMs) this._fire();
  }

  _fire() {
    // Spin is read from the live envelope rather than left to the buffer replay, so
    // the stone leaves at exactly the rate the ghost was showing a frame earlier —
    // the preview/live agreement this codebase keeps closing everywhere else. Read
    // before cancel(), which zeroes it.
    const descriptor = computeThrowDescriptor(
      this.motion.buffer.toArray(), this.tuning, this._spin);
    this.cancel();

    if (!descriptor) return null; // a twitch, not a throw — stay armed-off and let them retry

    this.onThrow({ type: 'THROW', ...descriptor });
    return descriptor;
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
