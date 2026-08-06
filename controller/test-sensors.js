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
} from './js/sensors.js';

import {
  computeThrowDescriptor,
  computeSimulatedThrowDescriptor,
  buildAimPayload,
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

console.log(failures === 0
  ? '\nPASS — all controller math checks green'
  : `\nFAIL — ${failures} check(s) failed`);
process.exit(failures === 0 ? 0 : 1);
