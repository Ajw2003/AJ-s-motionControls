/**
 * Unit tests for the pure sensor and throw math. No browser, no device.
 *
 * Run with: npm test    (from controller/)
 *
 * The most important test here is "ignores event.acceleration even when present".
 * iOS does not expose that property, Android does — so on an Android-only test
 * setup, nothing else in this project can catch code that reads it. That test is
 * the standing guard described in PLAN.md's Test device policy.
 */

import {
  shortestAngleDelta,
  rotateXY,
  magnitude,
  clamp,
  normalize01,
  GravityFilter,
  RingBuffer,
  readAcceleration,
  readRotationRate,
  spinMagnitude,
} from './js/sensors.js';

import {
  computeThrowDescriptor,
  computeSimulatedThrowDescriptor,
  buildAimPayload,
  decaySpinEnvelope,
  ThrowController,
  ThrowState,
  ThrowTuning,
  SimulatedThrow,
} from './js/throw.js';

let failures = 0;

function check(label, ok, detail) {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ` — ${detail}` : ''}`);
  if (!ok) failures++;
}

function near(actual, expected, tolerance = 1e-6) {
  return Math.abs(actual - expected) <= tolerance;
}

function makeSample(t, power, extra = {}) {
  return {
    t,
    linear: { x: power, y: 0, z: 0 },
    power,
    dAlpha: extra.dAlpha ?? 0,
    dBeta: extra.dBeta ?? 0,
    dGamma: extra.dGamma ?? 0,
    rotationRate: extra.rotationRate ?? { alpha: 0, beta: 0, gamma: 0 },
  };
}

console.log('Controller sensor math\n');

// ---------------------------------------------------------------- angle wrap
console.log('shortestAngleDelta');
check('no wrap', shortestAngleDelta(10, 4) === 6);
check('wraps forward across 0/360', shortestAngleDelta(1, 359) === 2,
  `got ${shortestAngleDelta(1, 359)}`);
check('wraps backward across 0/360', shortestAngleDelta(359, 1) === -2,
  `got ${shortestAngleDelta(359, 1)}`);
check('180 stays positive', shortestAngleDelta(180, 0) === 180);
check('-180 normalises to +180', shortestAngleDelta(-180, 0) === 180);
check('identical angles give zero', shortestAngleDelta(123, 123) === 0);

// ---------------------------------------------------------------- rotation
console.log('\nrotateXY');
const r90 = rotateXY(1, 0, 90);
check('90deg maps +X to +Y', near(r90.x, 0) && near(r90.y, 1), `(${r90.x.toFixed(3)}, ${r90.y.toFixed(3)})`);
const r180 = rotateXY(1, 0, 180);
check('180deg negates X', near(r180.x, -1) && near(r180.y, 0));
const r0 = rotateXY(3, 4, 0);
check('0deg is identity', near(r0.x, 3) && near(r0.y, 4));

console.log('\nhelpers');
check('magnitude 3-4-5-style', near(magnitude({ x: 3, y: 4, z: 0 }), 5));
check('clamp low', clamp(-5, 0, 10) === 0);
check('clamp high', clamp(50, 0, 10) === 10);
check('normalize01 midpoint', near(normalize01(5, 0, 10), 0.5));
check('normalize01 clamps above range', normalize01(99, 0, 10) === 1);
check('normalize01 handles zero-width range', normalize01(5, 3, 3) === 0);
check('spinMagnitude 3-4-12-style', near(spinMagnitude({ alpha: 3, beta: 4, gamma: 12 }), 13));
check('spinMagnitude treats a missing rate as still', spinMagnitude(null) === 0);
check('spinMagnitude tolerates null axes', near(spinMagnitude({ alpha: 5, beta: null, gamma: null }), 5));

// ---------------------------------------------------------------- gravity
console.log('\nGravityFilter');
{
  const filter = new GravityFilter(0.8);
  const first = filter.update({ x: 0, y: 0, z: 9.8 });
  check('seeds on first sample instead of spiking',
    near(first.z, 0), `linear.z was ${first.z.toFixed(3)}`);

  const resting = filter.update({ x: 0, y: 0, z: 9.8 });
  check('reports ~zero while at rest', Math.abs(resting.z) < 1e-9);

  const shoved = filter.update({ x: 0, y: 0, z: 19.6 });
  check('detects a sudden shove', shoved.z > 5, `linear.z ${shoved.z.toFixed(2)}`);

  filter.reset();
  const afterReset = filter.update({ x: 0, y: 0, z: 9.8 });
  check('reset re-seeds', near(afterReset.z, 0));
}

// ---------------------------------------------------------------- ring buffer
console.log('\nRingBuffer');
{
  const buffer = new RingBuffer(3);
  buffer.push(1); buffer.push(2);
  check('partial fill returns in order', JSON.stringify(buffer.toArray()) === '[1,2]');

  buffer.push(3); buffer.push(4); buffer.push(5);
  check('keeps only the newest N', JSON.stringify(buffer.toArray()) === '[3,4,5]',
    JSON.stringify(buffer.toArray()));
  check('length caps at capacity', buffer.length === 3);

  buffer.clear();
  check('clear empties it', buffer.length === 0 && buffer.toArray().length === 0);
}

// ---------------------------------------------------- THE iOS GUARD
console.log('\nreadAcceleration (iOS guard)');
{
  // Android exposes both properties. iOS exposes only the gravity-inclusive one.
  // Reading .acceleration would work fine here and fail on every iPhone, so this
  // test exists specifically to fail loudly if someone switches to it.
  const androidStyle = {
    acceleration: { x: 999, y: 999, z: 999 },
    accelerationIncludingGravity: { x: 1, y: 2, z: 3 },
  };
  const fromAndroid = readAcceleration(androidStyle);
  check('ignores event.acceleration even when present',
    fromAndroid.x === 1 && fromAndroid.y === 2 && fromAndroid.z === 3,
    `got ${JSON.stringify(fromAndroid)}`);

  const iosStyle = { accelerationIncludingGravity: { x: 1, y: 2, z: 3 } };
  check('works with no .acceleration property at all (iOS shape)',
    readAcceleration(iosStyle)?.x === 1);

  check('null when the event carries nothing', readAcceleration({}) === null);
  check('null when all axes are null',
    readAcceleration({ accelerationIncludingGravity: { x: null, y: null, z: null } }) === null);

  const partial = readAcceleration({ accelerationIncludingGravity: { x: 5, y: null, z: null } });
  check('missing axes default to 0 rather than NaN',
    partial.x === 5 && partial.y === 0 && partial.z === 0);

  const noRate = readRotationRate({});
  check('rotationRate defaults to zeros', noRate.alpha === 0 && noRate.beta === 0);
}

// ---------------------------------------------------------------- descriptor
console.log('\ncomputeThrowDescriptor');
{
  // A swing that accelerates to a peak and is released right at it.
  const perfect = [];
  for (let i = 0; i <= 10; i++) perfect.push(makeSample(i * 10, i * 3));
  const d = computeThrowDescriptor(perfect);

  check('peak accel is found', near(d.peakAccel, 30));
  check('power maps peak onto 0..1',
    near(d.power, (30 - ThrowTuning.minPeakAccel) / (ThrowTuning.maxPeakAccel - ThrowTuning.minPeakAccel), 1e-9),
    d.power.toFixed(3));
  check('release at the peak scores perfect timing', near(d.timingQuality, 1), d.timingQuality.toFixed(3));

  // Same swing, but held well past the peak.
  const late = [...perfect];
  for (let i = 1; i <= 25; i++) late.push(makeSample(100 + i * 10, 1));
  const dLate = computeThrowDescriptor(late);
  check('late release scores zero timing', near(dLate.timingQuality, 0), dLate.timingQuality.toFixed(3));
  check('late release keeps the same power', near(dLate.power, d.power));

  // A limp gesture should not register as a throw at all.
  const fumble = [makeSample(0, 1), makeSample(10, 2), makeSample(20, 1)];
  check('weak gesture returns null', computeThrowDescriptor(fumble) === null);

  check('empty input returns null', computeThrowDescriptor([]) === null);
  check('null input returns null', computeThrowDescriptor(null) === null);

  // Orientation is raw and read at the stop. Nothing is clamped: a player who wants
  // to throw backwards is allowed to, and rewriting their input into something
  // "usable" was worse than letting them miss.
  const wild = [makeSample(0, 5), makeSample(10, 30, { dAlpha: 190, dBeta: -120, dGamma: 17 })];
  const dWild = computeThrowDescriptor(wild);
  check('aim is not clamped — backwards throws are allowed', dWild.aimDeg === 190, `${dWild.aimDeg}`);
  check('attack angle is not clamped', dWild.attackAngleDeg === -120);
  check('roll is passed through raw', dWild.rollDeg === 17);

  // The stop sample is the aim: a throwing motion ends pointing where it was thrown.
  const swung = [
    makeSample(0, 1, { dAlpha: 20 }),    // lining the shot up
    makeSample(120, 25, { dAlpha: 70 }), // swinging
    makeSample(180, 30, { dAlpha: 95 }), // peak
    makeSample(200, 2, { dAlpha: 110 }), // stopped, pointing at the target
  ];
  const dSwung = computeThrowDescriptor(swung);
  check('aim comes from the stop, raw', dSwung.aimDeg === 110, `${dSwung.aimDeg}`);
  check('power still comes from the peak', near(dSwung.peakAccel, 30));
  check('a sharp stop right after the peak scores well on timing',
    dSwung.timingQuality > 0.8, dSwung.timingQuality.toFixed(2));

  const spun = [makeSample(0, 5), makeSample(10, 30, { rotationRate: { alpha: 3, beta: 4, gamma: 0 } })];
  check('spin rate is the rotation magnitude', near(computeThrowDescriptor(spun).spinRate, 5));
}

// ---------------------------------------------------------------- aim payload
console.log('\nbuildAimPayload');
{
  const payload = buildAimPayload(makeSample(0, 20, { dAlpha: 60, dBeta: 22, dGamma: 3 }), 42);
  check('carries a sequence number', payload.seq === 42);
  check('is typed AIM', payload.type === 'AIM');
  check('aim is raw, matching the throw descriptor', payload.aimDeg === 60);
  check('attack angle survives', payload.attackAngleDeg === 22);
  check('windup is normalised 0..1', payload.windup > 0 && payload.windup <= 1, payload.windup.toFixed(3));
  check('null sample yields null payload', buildAimPayload(null, 1) === null);

  // Spin is what drives the ghost's visual spin on the TV. It has to be the envelope
  // when one is supplied, because the instantaneous value at a 20Hz tick mostly misses
  // the flick that produced it.
  const spinning = makeSample(0, 20, { rotationRate: { alpha: 0, beta: 30, gamma: 40 } });
  check('falls back to this instant\'s spin when no envelope is given',
    near(buildAimPayload(spinning, 1).spinRate, 50));
  check('carries the supplied envelope in preference to the instant',
    near(buildAimPayload(spinning, 1, ThrowTuning, 320).spinRate, 320));
  check('a still phone reports no spin', buildAimPayload(makeSample(0, 20), 1).spinRate === 0);
}

// ------------------------------------------------------------- spin envelope
console.log('\ndecaySpinEnvelope');
{
  check('rises instantly to a new peak', near(decaySpinEnvelope(100, 900, 16), 900));
  check('holds the peak against a quiet sample', decaySpinEnvelope(900, 0, 16) > 800);
  check('decays toward zero over time', decaySpinEnvelope(900, 0, 1000) < 20);
  check('one tau leaves about 1/e', near(decaySpinEnvelope(900, 0, 250, 250), 900 / Math.E, 1e-9));

  // Time-based, not per-sample: two half-steps must land where one full step does, or
  // the ghost winds down at a rate that depends on how fast the phone reports.
  const twoSteps = decaySpinEnvelope(decaySpinEnvelope(900, 0, 50), 0, 50);
  check('is independent of sample rate', near(twoSteps, decaySpinEnvelope(900, 0, 100), 1e-9),
    `${twoSteps.toFixed(6)} vs ${decaySpinEnvelope(900, 0, 100).toFixed(6)}`);
  check('a negative dt cannot inflate the envelope', decaySpinEnvelope(900, 0, -500) <= 900);
}

// ------------------------------------------------- simulated throw (sensorless)
console.log('\ncomputeSimulatedThrowDescriptor');
{
  const mid = () => 0.5; // jitter() maps 0.5 onto exactly zero deflection

  const brief = computeSimulatedThrowDescriptor(0, mid);
  const long = computeSimulatedThrowDescriptor(5000, mid);

  check('a brief tap gives minimum power', near(brief.power, SimulatedThrow.minPower));
  check('a long hold saturates at maximum power', near(long.power, SimulatedThrow.maxPower));
  check('hold duration scales power in between',
    computeSimulatedThrowDescriptor(600, mid).power > brief.power &&
    computeSimulatedThrowDescriptor(600, mid).power < long.power);

  check('centred randomness gives a dead-straight throw',
    near(brief.aimDeg, 0) && near(brief.attackAngleDeg, 0));

  // The whole point of the jitter bounds: SkipTuning maps a neutral grip onto the
  // optimal angle with a 12 degree tolerance, so a simulated throw must land inside
  // that window or every test throw sinks and every score is zero.
  const extremes = [computeSimulatedThrowDescriptor(600, () => 0),
                    computeSimulatedThrowDescriptor(600, () => 1)];
  check('attack angle stays inside the skip tolerance window',
    extremes.every((d) => Math.abs(d.attackAngleDeg) <= SimulatedThrow.attackJitterDeg),
    extremes.map((d) => d.attackAngleDeg.toFixed(1)).join(' / '));

  check('is flagged as simulated', brief.simulated === true);
  check('reports zero samples, which is the tell on the TV readout',
    brief.sampleCount === 0 && brief.peakAccel === 0);
}

// ------------------------------------------------- release() fallback selection
console.log('\nThrowController: arm, swing, stop');
{
  // Minimal stand-ins: enough surface to arm and feed samples, no browser needed.
  const makeMotion = (latest, samples) => ({
    latest,
    buffer: { toArray: () => samples, clear() {} },
    calibrate() {},
    onSample: () => {},
  });

  /** Arms, then pushes samples through detection as the sensor would. */
  const swing = (controller, motion, samples) => {
    controller.arm();
    for (const sample of samples) {
      motion.latest = sample;
      motion.onSample(sample);
    }
  };

  // Accelerate hard, then stop dead. Exactly three calm samples, because that is
  // the documented threshold — two must not be enough.
  const hardSwing = [
    makeSample(0, 2, { dAlpha: 5 }),
    makeSample(20, 20, { dAlpha: 30 }),
    makeSample(40, 32, { dAlpha: 60 }),   // peak
    makeSample(60, 3, { dAlpha: 75 }),    // calm 1
    makeSample(80, 2, { dAlpha: 76 }),    // calm 2
    makeSample(100, 1, { dAlpha: 77 }),   // calm 3 — fires here
  ];

  {
    const sent = [];
    const motion = makeMotion(null, hardSwing);
    const c = new ThrowController(motion, { onThrow: (p) => sent.push(p), aimHz: 1000 });
    swing(c, motion, hardSwing);

    check('an abrupt stop fires the throw with no button release', sent.length === 1);
    check('the throw is measured, not simulated', sent.length === 1 && !sent[0].simulated);
    check('firing returns the controller to idle', c.state === ThrowState.IDLE);
  }

  // A swing passes through near-zero acceleration at its own turnaround. Firing
  // there would throw halfway through the arc.
  {
    const sent = [];
    const dip = [
      makeSample(0, 2), makeSample(20, 25), makeSample(40, 1), // momentary dip mid-swing
      makeSample(60, 30), makeSample(80, 28),
    ];
    const motion = makeMotion(null, dip);
    const c = new ThrowController(motion, { onThrow: (p) => sent.push(p), aimHz: 1000 });
    swing(c, motion, dip);

    check('a single calm sample mid-swing does not fire', sent.length === 0);
    check('the controller stays armed through the dip', c.state === ThrowState.ARMED);
    c.cancel();
  }

  // Hand tremor while aiming must never register as a swing.
  {
    const sent = [];
    const still = [makeSample(0, 1), makeSample(20, 2), makeSample(40, 1), makeSample(60, 2)];
    const motion = makeMotion(null, still);
    const c = new ThrowController(motion, { onThrow: (p) => sent.push(p), aimHz: 1000 });
    swing(c, motion, still);

    check('holding still never fires', sent.length === 0 && c.state === ThrowState.ARMED);
    c.cancel();
  }

  // Second tap stands down so the player can re-aim and recalibrate.
  {
    const sent = [];
    const motion = makeMotion(hardSwing[0], hardSwing);
    const c = new ThrowController(motion, { onThrow: (p) => sent.push(p), aimHz: 1000 });

    c.toggle();
    check('first tap arms', c.state === ThrowState.ARMED);
    c.toggle();
    check('second tap disarms without throwing', c.state === ThrowState.IDLE && sent.length === 0);
  }

  // A device that never produced a single motion event has no sensors at all, so
  // the second tap has to throw or a desktop seat deadlocks the match.
  {
    const sent = [];
    const motion = makeMotion(null, []);
    const c = new ThrowController(motion, { onThrow: (p) => sent.push(p), aimHz: 1000 });

    c.toggle();
    const d = c.toggle();
    check('a sensorless device throws on the second tap',
      sent.length === 1 && sent[0].simulated === true && d.simulated === true);
    check('the simulated throw is typed THROW like any other', sent[0].type === 'THROW');
  }

  // Tapping to disarm when never armed must stay a no-op.
  {
    const sent = [];
    const c = new ThrowController(makeMotion(null, []), {
      onThrow: (p) => sent.push(p),
      aimHz: 1000,
    });
    check('disarming without arming sends nothing',
      c.disarm() === null && sent.length === 0);
  }
}

// --------------------------------------------- live spin reaches the aim frame
// The unit checks above prove the envelope math and the payload separately. This
// one proves they are actually wired together, which is the part that can break
// silently: the envelope has to be updated before the swing-detection guards, or
// a player aiming gently never accumulates any spin at all.
console.log('\nThrowController: live spin on the aim stream');
{
  const motion = {
    latest: null,
    buffer: { toArray: () => [], clear() {} },
    calibrate() {},
    onSample: () => {},
  };

  const frames = [];
  const c = new ThrowController(motion, { onAim: (p) => frames.push(p), aimHz: 200 });
  c.arm();

  // A wrist turn well below the swing threshold — this is someone aiming, not
  // throwing, which is exactly when the ghost should be showing spin. The phone is
  // then already still again by the time the aim frame goes out, which is the normal
  // case at 20Hz and the reason the envelope exists: read instantaneously here, this
  // flick would be reported as no spin at all.
  const flick = makeSample(0, 5, { rotationRate: { alpha: 0, beta: 300, gamma: 400 } });
  motion.latest = flick;
  motion.onSample(flick);

  const settledSample = makeSample(40, 5);
  motion.latest = settledSample;
  motion.onSample(settledSample);

  await new Promise((resolve) => setTimeout(resolve, 30));

  const spun = frames[0];
  check('a turn below the swing threshold still reaches the aim frame', spun !== undefined,
    frames.length ? `saw ${frames.length} frame(s)` : 'no frames at all');
  check('the frame reports the held peak, not the still phone it was sampled on',
    spun !== undefined && near(spun.spinRate, 500 * Math.exp(-40 / 250), 1e-9),
    spun && spun.spinRate.toFixed(2));

  c.cancel();
  const settled = frames.length;
  await new Promise((resolve) => setTimeout(resolve, 30));
  check('disarming stops the aim stream', frames.length === settled);
}

console.log(failures === 0
  ? '\nPASS — all controller math checks green'
  : `\nFAIL — ${failures} check(s) failed`);
process.exit(failures === 0 ? 0 : 1);
