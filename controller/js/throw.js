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
  normalize01,
  screenSpinRate,
  quaternionFromRotationVector,
  MAX_SAMPLE_GAP_MS,
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
  // --- accidental-throw guard (Findings-NetworkAndThrowStability.md A5/A6) --
  // `minValidPeakAccel` used to live here as the "fumble" guard, but it never could
  // trip: reaching computeThrowDescriptor already required a sample past swingAccel
  // (14), so a peak below this constant's value of 3 was unreachable dead code (A5).
  // These three fields are its real replacement, layered so no single accelerometer
  // sample — however hard — can commit a throw by itself.

  // Samples within this long of arm() never start a swing. A5: arm() runs inside
  // the touchend handler, so without a grace window the jolt of the tap itself (or
  // of lifting the phone up into a swing) was scored as the start of the gesture.
  armGraceMs: 250,

  // Consecutive samples at or above swingAccel needed before a gesture counts as a
  // real swing rather than a single spike or a tap jolt. A5: one sample used to be
  // enough and nothing could ever un-commit it once _swinging was set.
  swingOnsetSamples: 3,

  // A computed swingSpeed below this is a swing too weak to be a real throw — a
  // twitch that still happened to clear the onset run. Discarded in _fire without
  // cancelling the arm; see ThrowController._fire.
  minThrowSwingSpeed: 3,

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

  // How long a spin peak is held; the throw and the ghost share it so the stone spins at the rate the ghost showed.
  // doc-ref 10f3 docs/4-systems/MotionPipeline.md
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
    0,
  );

  return {
    swingSpeed:
      SimulatedThrow.minSwingSpeed +
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

// Pure: reduces a whole swing to the numbers Unity needs, with no device access or side effects.
// doc-ref b68d docs/4-systems/MotionPipeline.md
export function computeThrowDescriptor(samples, tuning = ThrowTuning, spinRate, releaseIndex) {
  if (!samples || samples.length === 0) return null;

  // The last sample is the moment the phone stopped, which is the release. Nothing
  // is inferred, averaged or reached back for: what the sensors said at that instant
  // is what the stone does. `releaseIndex` overrides this only for the timeout path,
  // where the last sample is the follow-through, not the release (#54).
  const stop = samples[releaseIndex ?? samples.length - 1];

  const swingSpeed = peakSwingSpeed(samples, tuning);

  return {
    // Raw m/s is the only thing that decides range; Unity converts it once, via SkipConstants.SpeedScale.
    // doc-ref 22d2 docs/4-systems/MotionPipeline.md
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
 * Index of the calmest (lowest power) sample strictly after the peak-power sample, or
 * the last index if the peak is the final sample. Used for the release attitude when a
 * swing times out without ever settling (#54): the phone was let go of at its calmest
 * point after the peak, not ~900ms later where the arm ended up.
 *
 * Pure. Returns -1 for an empty buffer.
 */
export function calmestIndexAfterPeak(samples) {
  if (!samples || samples.length === 0) return -1;

  let peakIdx = 0;
  for (let i = 1; i < samples.length; i++) {
    if (samples[i].power > samples[peakIdx].power) peakIdx = i;
  }

  let best = -1;
  for (let i = peakIdx + 1; i < samples.length; i++) {
    if (best === -1 || samples[i].power < samples[best].power) best = i;
  }
  return best === -1 ? samples.length - 1 : best;
}

// Peak hand speed during the swing in m/s, integrated from the swing start and tracked as a running peak,
// not an endpoint.
// doc-ref a10f docs/4-systems/MotionPipeline.md
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

  let vx = 0,
    vy = 0,
    vz = 0,
    best = 0;
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

// Signed peak-hold with exponential decay, used for spin and the live power estimate.
// doc-ref 4519 docs/4-systems/MotionPipeline.md
export function decaySpinEnvelope(
  previous,
  instantaneous,
  dtMs,
  tauMs = ThrowTuning.spinEnvelopeTauMs,
) {
  const decayed = previous * Math.exp(-Math.max(dtMs, 0) / tauMs);
  return Math.abs(instantaneous) > Math.abs(decayed) ? instantaneous : decayed;
}

// Replays decaySpinEnvelope across a buffer, for callers that hold only samples.
// doc-ref b6d3 docs/4-systems/MotionPipeline.md
export function peakSpinEnvelope(samples, tuning = ThrowTuning) {
  if (!samples || samples.length === 0) return 0;

  let spin = 0;
  let prevT = null;

  for (const sample of samples) {
    const dtMs = prevT === null ? 0 : sample.t - prevT;
    prevT = sample.t;
    spin = decaySpinEnvelope(
      spin,
      screenSpinRate(sample.rotationRate),
      dtMs,
      tuning.spinEnvelopeTauMs,
    );
  }

  return spin;
}

// Cosmetic aim frame, never authoritative, and raw and unclamped so the preview and the stone agree.
// doc-ref b388 docs/4-systems/MotionPipeline.md
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

// Owns tap-to-arm, the swing and the AIM stream while armed, and fires on the stop;
// it knows nothing about the network.
// doc-ref f8d8 docs/4-systems/MotionPipeline.md
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

    // A5: the clock the grace window is measured against. Null means "no motion
    // sample seen yet since arm()" — see _onSample, which stamps this from the
    // first sample it gets rather than from performance.now() in arm(). arm()'s
    // own clock and a sample's `t` are not guaranteed to share an origin (the unit
    // tests stamp samples from 0), and on a real phone the first sample lands
    // within about one ~17ms sensor tick of the tap either way, so the behaviour
    // on hardware is the same.
    this._graceStartT = null;

    this._swinging = false;
    this._peak = null;
    this._calmRun = 0;

    // A5: consecutive samples at or above swingAccel, reset by any sample below
    // it. Only once this reaches swingOnsetSamples does _swinging get set —
    // one hard sample is a spike, not a swing.
    this._onsetRun = 0;

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
    this._graceStartT = null; // A5: re-armed at the next sample seen, not now
    this._resetSwingState();
    this._spin = 0;
    this._spinT = null;
    this._windVelocity = { x: 0, y: 0, z: 0 };
    this._windSpeed = 0;

    this.motion.recentre();
    this.motion.prepareForThrow();
    this._setState(ThrowState.ARMED);
    // After _setState: its listeners may rewrite motion state. Holds Aiming through a
    // backswing pause (#53).
    this.motion.holdAimLock = true;

    this._timer = setInterval(() => {
      const payload = buildAimPayload(this.motion.latest, ++this.seq, this._spin, this._windSpeed);
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
    this._graceStartT = null;
    this._resetSwingState();
    this._spin = 0;
    this._spinT = null;
    this._windVelocity = { x: 0, y: 0, z: 0 };
    this._windSpeed = 0;
    if (this.motion) this.motion.holdAimLock = false;
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
    this._windSpeed = decaySpinEnvelope(
      this._windSpeed,
      speed,
      dtMs,
      this.tuning.spinEnvelopeTauMs,
    );
  }

  // ---------------------------------------------------------------- detection

  /** Onset-run and swing-in-progress state, shared by arm(), cancel() and a discard in _fire(). */
  _resetSwingState() {
    this._swinging = false;
    this._peak = null;
    this._calmRun = 0;
    this._onsetRun = 0;
  }

  _onSample(sample) {
    if (this.state !== ThrowState.ARMED) return;

    // A5: the grace window starts at the first sample seen while ARMED, not at
    // arm()'s own performance.now() — see the comment on _graceStartT's field.
    if (this._graceStartT === null) this._graceStartT = sample.t;
    const inGrace = sample.t - this._graceStartT < this.tuning.armGraceMs;

    // Tracked here rather than on the aim timer, for the same reason release
    // detection is: a wrist flick peaks and is gone well inside one 20Hz interval,
    // so sampling spin there would read mostly tail and miss what the player did.
    const dtMs = this._spinT === null ? 0 : sample.t - this._spinT;
    this._spinT = sample.t;
    this._spin = decaySpinEnvelope(
      this._spin,
      screenSpinRate(sample.rotationRate),
      dtMs,
      this.tuning.spinEnvelopeTauMs,
    );

    this._trackWindup(sample, dtMs);

    if (sample.power >= this.tuning.swingAccel) {
      // A5: a sample inside the grace window can never start a swing, however hard
      // it reads — that is the tap jolt itself, or the jolt of lifting the phone up
      // to swing it. Not counted toward the onset run at all.
      if (inGrace) {
        this._onsetRun = 0;
        return;
      }

      // A5: swingOnsetSamples consecutive samples over threshold, not one, before
      // this counts as a real swing rather than a single spike.
      this._onsetRun++;
      if (this._onsetRun >= this.tuning.swingOnsetSamples) this._swinging = true;

      this._calmRun = 0;
      if (this._peak === null || sample.power > this._peak.power) this._peak = sample;
      return;
    }

    this._onsetRun = 0;

    // this._swinging only becomes true once a sustained onset run has landed above,
    // so both the stop-detection path below and the maxSwingMs fallback after it are
    // unreachable from a lone spike that never got that far (A5/A6).
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

    // Never leave a player armed because their phone did not quite settle. Only
    // reachable once _swinging is true, i.e. after a sustained onset run (A5/A6) —
    // a single spike can no longer arm this fallback and then fire on a timer.
    if (this._peak && sample.t - this._peak.t > this.tuning.maxSwingMs) {
      this._fire({ timedOut: true });
    }
  }

  _fire({ timedOut = false } = {}) {
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

    // #54: on a timeout the last sample is ~900ms of follow-through, so the stone
    // launched aimed wherever the arm ended up. Read the attitude from the calmest
    // sample after the peak instead. The calm-stop path keeps the last sample.
    const releaseIndex = timedOut ? calmestIndexAfterPeak(samples) : undefined;
    const descriptor = computeThrowDescriptor(samples, this.tuning, peakSpin, releaseIndex);

    // A discarded gesture resets only the swing detection and stays ARMED;
    // _graceStartT is deliberately not reset.
    // doc-ref 5b13 docs/4-systems/MotionPipeline.md
    if (!descriptor || descriptor.swingSpeed < this.tuning.minThrowSwingSpeed) {
      this._resetSwingState();
      return null;
    }

    this.cancel();
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
