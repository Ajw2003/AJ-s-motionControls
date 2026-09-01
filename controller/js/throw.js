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

import {
  normalize01, screenSpinRate, quaternionFromRotationVector, MAX_SAMPLE_GAP_MS,
} from './sensors.js';

/** Identity — the phone as it was held at the tap. A flat stone facing forward. */
const IDENTITY_ATTITUDE = { w: 1, x: 0, y: 0, z: 0 };

/**
 * Flatten an attitude quaternion onto the wire.
 *
 * Flat rather than a nested object because Unity parses these with JsonUtility, which
 * is happiest with flat public fields — and because a flat format gives the two
 * runtimes one less shape to disagree about. Both AIM and THROW use this, so they
 * cannot spell it differently.
 */
function attitudeFields(q) {
  const a = q ?? IDENTITY_ATTITUDE;
  return { qw: a.w, qx: a.x, qy: a.y, qz: a.z };
}

export const ThrowTuning = {
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

  // Raw m/s, standing in for a real swing measurement — a hold longer than
  // minHoldMs synthesizes a harder "throw" between these two.
  minSwingSpeed: 1.5,
  maxSwingSpeed: 6,

  // Identity attitude is a flat stone facing forward, which SkipTuning treats as a good
  // throw — the ~20 degrees the water wants is applied inside the bounce, not to the
  // stone. Jitter is a small rotation about the phone's own axes, in degrees: x pitches
  // the face, y turns the aim. Both stay well inside the tolerance window so simulated
  // throws skip rather than sinking, which would make every test score identically zero.
  pitchJitterDeg: 6,
  yawJitterDeg: 10,

  // A plausible wrist flick. Visual only now, so unlike the old value this cannot drift
  // out of sync with anything on the C# side — there is nothing there to match.
  spinRate: 900,
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
  const jitterRad = (deg) => ((random() * 2 - 1) * deg * Math.PI) / 180;

  const attitude = quaternionFromRotationVector(
    jitterRad(SimulatedThrow.pitchJitterDeg),
    jitterRad(SimulatedThrow.yawJitterDeg),
    0);

  return {
    swingSpeed: SimulatedThrow.minSwingSpeed +
      charge * (SimulatedThrow.maxSwingSpeed - SimulatedThrow.minSwingSpeed),
    ...attitudeFields(attitude),
    spinRate: SimulatedThrow.spinRate,

    // Zero samples is the tell. A real gesture always has some, so the TV debug
    // readout distinguishes a simulated throw from a genuine one at a glance.
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

  return {
    // Raw m/s. The only thing that decides how far the stone goes — Unity converts
    // it once, via SkipConstants.SpeedScale. There used to be a normalised `power`
    // here and a timingQuality alongside it, scaling launch speed by how fast the
    // phone came to rest after its peak — but a hard swing takes longer to stop, so
    // it landed hardest on the throws it should have rewarded, and the extra
    // normalisation was a second lossy conversion of the same measurement Unity was
    // about to convert again. Effort and result now agree, and there is only one
    // conversion left, on the Unity side.
    swingSpeed,

    // Unclamped, deliberately. Throwing backwards, sideways or straight up is a
    // legitimate thing to attempt, and clamping quietly rewrote what the player did
    // into something they did not ask for.
    ...attitudeFields(stop.attitude),

    // The one field deliberately NOT read from the stop sample. Attitude is an
    // orientation, and where the phone ended up pointing IS the throw. Spin is a
    // rate: it peaks mid-swing and is spent by the time the phone halts — and the
    // throw fires precisely because it halted. Reading it off `stop` sampled the one
    // instant the wrist had finished turning, so a hard flick and a limp one both
    // arrived as roughly zero and the stone never visibly span.
    spinRate: spinRate ?? peakSpinEnvelope(samples, tuning),

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
    if (!(dtMs > 0) || dtMs > MAX_SAMPLE_GAP_MS) continue;

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

/**
 * Peak-hold with an exponential decay: jumps straight to any new peak, then falls back
 * toward zero. Signed — the peak is chosen by magnitude but keeps its direction.
 *
 * Generic, despite the name it grew up with. Spin uses it so a wrist flick that is over
 * in 100ms still reaches a 20Hz aim frame; the live power estimate uses it for the same
 * reason with a different time constant.
 *
 * Signed because spin is now a signed rate about the phone's screen normal. Taking a
 * plain max() would have let a decaying positive peak win over a genuine negative one,
 * so a counter-clockwise flick following a clockwise one would read as clockwise.
 */
export function decaySpinEnvelope(previous, instantaneous, dtMs, tauMs = ThrowTuning.spinEnvelopeTauMs) {
  const decayed = previous * Math.exp(-Math.max(dtMs, 0) / tauMs);
  return Math.abs(instantaneous) > Math.abs(decayed) ? instantaneous : decayed;
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
    spin = decaySpinEnvelope(spin, screenSpinRate(sample.rotationRate), dtMs, tuning.spinEnvelopeTauMs);
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
export function buildAimPayload(sample, seq, spinRate, swingSpeed = 0) {
  if (!sample) return null;
  return {
    type: 'AIM',
    seq,
    ...attitudeFields(sample.attitude),
    spinRate: spinRate ?? screenSpinRate(sample.rotationRate),

    // Raw m/s — how fast the phone is being swung right now, unnormalised. See
    // ThrowController._windSpeed. The TV draws the arc at this, so winding up
    // visibly extends it.
    swingSpeed,
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

    // Live swing speed, so the aim arc can be drawn at how hard the phone is being
    // swung right now. `_windVelocity` is acceleration integrated since the phone was
    // last calm; `_windSpeed` is the decaying peak of its magnitude.
    this._windVelocity = { x: 0, y: 0, z: 0 };
    this._windSpeed = 0;

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
    this._windVelocity = { x: 0, y: 0, z: 0 };
    this._windSpeed = 0;

    this.motion.calibrate();
    this._setState(ThrowState.ARMED);

    this._timer = setInterval(() => {
      const payload = buildAimPayload(
        this.motion.latest, ++this.seq, this._spin, this._windSpeed);
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
    this._windVelocity = { x: 0, y: 0, z: 0 };
    this._windSpeed = 0;
    this._setState(ThrowState.IDLE);
  }

  _trackWindup(sample, dtMs) {
    // Calm means the swing is over, or has not started. Resetting the integrator here is
    // what keeps sensor bias from accumulating into phantom speed while someone lines a
    // shot up — the same reason peakSwingSpeed starts from the last calm sample rather
    // than from the top of the buffer.
    if (sample.power <= this.tuning.stopAccel) {
      this._windVelocity = { x: 0, y: 0, z: 0 };
    } else if (dtMs > 0 && dtMs <= MAX_SAMPLE_GAP_MS && sample.linear) {
      const dt = dtMs / 1000;
      this._windVelocity = {
        x: this._windVelocity.x + sample.linear.x * dt,
        y: this._windVelocity.y + sample.linear.y * dt,
        z: this._windVelocity.z + sample.linear.z * dt,
      };
    }

    const speed = Math.hypot(this._windVelocity.x, this._windVelocity.y, this._windVelocity.z);

    // Held briefly so the arc does not snap back to nothing between swings, and so a
    // 20Hz aim frame lands on the peak rather than on whatever the phone was doing 50ms
    // later. Same envelope as spin, same reason.
    this._windSpeed = decaySpinEnvelope(this._windSpeed, speed, dtMs, this.tuning.spinEnvelopeTauMs);
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
      this._spin, screenSpinRate(sample.rotationRate), dtMs, this.tuning.spinEnvelopeTauMs);

    this._trackWindup(sample, dtMs);

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
    // Spin is read from the absolute peak during the swing, rather than the live decayed envelope.
    // A fast wrist flick peaks and decays hundreds of milliseconds before the phone finally comes 
    // to a halt (which is what triggers this release). Using the live envelope meant the rock 
    // left the hand with barely any spin.
    let peakSpin = 0;
    const samples = this.motion.buffer.toArray();
    for (const sample of samples) {
      const s = screenSpinRate(sample.rotationRate);
      if (Math.abs(s) > Math.abs(peakSpin)) peakSpin = s;
    }

    const descriptor = computeThrowDescriptor(
      samples, this.tuning, peakSpin);
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
