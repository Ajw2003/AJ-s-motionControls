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
  ThrowTuning,
  SimulatedThrow,
  toGameYaw,
  setYawSign,
  getYawSign,
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

  // Clamping at release. Note the aim sign: device yaw is negated into game yaw,
  // so a positive dAlpha (phone turned left) clamps to the NEGATIVE limit.
  const wild = [makeSample(0, 5), makeSample(10, 30, { dAlpha: 90, dBeta: -120, dGamma: 17 })];
  const dWild = computeThrowDescriptor(wild);
  check('aim clamps to +/-45', dWild.aimDeg === -ThrowTuning.maxAimDeg, `${dWild.aimDeg}`);
  check('attack angle clamps to its floor', dWild.attackAngleDeg === ThrowTuning.minAttackDeg);
  check('roll is passed through unclamped', dWild.rollDeg === 17);

  // Peak selection must not reach back past the swing. A device reporting at half
  // the assumed rate fills the fixed-size ring buffer with twice as much history,
  // and without the time bound this "peak" from two seconds ago would win, making
  // releaseDelayMs enormous and timingQuality zero.
  const stale = [makeSample(0, 30), makeSample(2000, 8), makeSample(2010, 9)];
  const dStale = computeThrowDescriptor(stale);
  check('a peak older than the swing window is ignored',
    near(dStale.peakAccel, 9), `peakAccel ${dStale.peakAccel}`);
  check('ignoring it keeps timing meaningful',
    dStale.timingQuality > 0.9, dStale.timingQuality.toFixed(3));

  const allStale = [makeSample(0, 30), makeSample(5000, 0.5)];
  check('a window containing only a fumble still returns null',
    computeThrowDescriptor(allStale) === null);

  const spun = [makeSample(0, 5), makeSample(10, 30, { rotationRate: { alpha: 3, beta: 4, gamma: 0 } })];
  check('spin rate is the rotation magnitude', near(computeThrowDescriptor(spun).spinRate, 5));
}

// ---------------------------------------------------------------- aim payload
console.log('\nbuildAimPayload');
{
  const payload = buildAimPayload(makeSample(0, 20, { dAlpha: 60, dBeta: 22, dGamma: 3 }), 42);
  check('carries a sequence number', payload.seq === 42);
  check('is typed AIM', payload.type === 'AIM');
  check('aim is clamped like a throw', payload.aimDeg === -ThrowTuning.maxAimDeg);
  check('attack angle survives', payload.attackAngleDeg === 22);
  check('windup is normalised 0..1', payload.windup > 0 && payload.windup <= 1, payload.windup.toFixed(3));
  check('null sample yields null payload', buildAimPayload(null, 1) === null);
}

// ---------------------------------------------------------------- aim yaw sign
console.log('\ndevice yaw -> game yaw');
{
  // The bug this guards: DeviceOrientation alpha counts counter-clockwise viewed
  // from above, Unity's AngleAxis(deg, up) counts clockwise. Passing the delta
  // through unchanged made the rock steer the opposite way to the phone.
  check('default sign inverts device yaw', getYawSign() === -1);
  check('turning the phone left aims left, not right', toGameYaw(30) === -30);
  check('and the mirror case holds', toGameYaw(-30) === 30);
  check('zero stays zero', toGameYaw(0) === 0);

  const straight = [makeSample(0, 5), makeSample(10, 30, { dAlpha: 20 })];
  check('a throw inherits the same convention',
    computeThrowDescriptor(straight).aimDeg === -20);

  setYawSign(1);
  check('the override flips it for a platform that disagrees', toGameYaw(30) === 30);
  check('an aim frame follows the override too',
    buildAimPayload(makeSample(0, 10, { dAlpha: 20 }), 1).aimDeg === 20);

  setYawSign(-1); // restore, later tests assume the default
  check('restoring the default works', getYawSign() === -1);
}

// ------------------------------------------------- sample-rate independence
console.log('\nGravityFilter sample-rate independence');
{
  // At the 60Hz reference the filter must behave exactly as it did when the
  // coefficient was a fixed per-sample 0.8, or Android regresses.
  const reference = new GravityFilter(0.8);
  reference.update({ x: 0, y: 0, z: 9.8 });
  const atReference = reference.update({ x: 0, y: 0, z: 19.6 });
  check('60Hz reproduces the original fixed-k behaviour',
    near(atReference.z, 19.6 - (0.8 * 9.8 + 0.2 * 19.6), 1e-9),
    atReference.z.toFixed(6));

  // Half the rate means twice the elapsed time per sample, so the gravity estimate
  // must move further per sample to keep the same wall-clock time constant.
  const slow = new GravityFilter(0.8);
  slow.update({ x: 0, y: 0, z: 9.8 });
  const atHalfRate = slow.update({ x: 0, y: 0, z: 19.6 }, 1000 / 30);
  check('a 30Hz device adapts further per sample',
    atHalfRate.z < atReference.z,
    `${atHalfRate.z.toFixed(3)} vs ${atReference.z.toFixed(3)}`);

  const fast = new GravityFilter(0.8);
  fast.update({ x: 0, y: 0, z: 9.8 });
  const atDoubleRate = fast.update({ x: 0, y: 0, z: 19.6 }, 1000 / 120);
  check('a 120Hz device adapts less per sample', atDoubleRate.z > atReference.z,
    atDoubleRate.z.toFixed(3));
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
console.log('\nThrowController.release fallback');
{
  // Minimal stand-ins: enough surface for grip()/release(), no browser needed.
  const makeMotion = (latest, samples) => ({
    latest,
    buffer: { toArray: () => samples, clear() {} },
    calibrate() {},
  });

  const realSwing = [];
  for (let i = 0; i <= 10; i++) realSwing.push(makeSample(i * 10, i * 3));

  // A device with sensors and a genuine swing: the measured throw must win.
  {
    const sent = [];
    const c = new ThrowController(makeMotion(realSwing[10], realSwing), {
      onThrow: (p) => sent.push(p),
      aimHz: 1000,
    });
    c.grip();
    const d = c.release();
    check('a real swing produces a measured throw, not a simulated one',
      sent.length === 1 && !sent[0].simulated && d.sampleCount === realSwing.length);
  }

  // A device with sensors but a limp gesture: still a fumble. Falling back here
  // would turn every pocket bump on a real phone into a throw.
  {
    const sent = [];
    const fumble = [makeSample(0, 1), makeSample(10, 2)];
    const c = new ThrowController(makeMotion(fumble[1], fumble), {
      onThrow: (p) => sent.push(p),
      aimHz: 1000,
    });
    c.grip();
    check('a fumble on a real device stays a fumble',
      c.release() === null && sent.length === 0);
  }

  // A device that never produced a single motion event has no sensors at all.
  {
    const sent = [];
    const c = new ThrowController(makeMotion(null, []), {
      onThrow: (p) => sent.push(p),
      aimHz: 1000,
    });
    c.grip();
    const d = c.release();
    check('a sensorless device falls back to a simulated throw',
      sent.length === 1 && sent[0].simulated === true && d.simulated === true);
    check('the simulated throw is typed THROW like any other', sent[0].type === 'THROW');
  }

  // Releasing without gripping must stay a no-op rather than firing a simulated throw.
  {
    const sent = [];
    const c = new ThrowController(makeMotion(null, []), {
      onThrow: (p) => sent.push(p),
      aimHz: 1000,
    });
    check('release without grip sends nothing',
      c.release() === null && sent.length === 0);
  }
}

console.log(failures === 0
  ? '\nPASS — all controller math checks green'
  : `\nFAIL — ${failures} check(s) failed`);
process.exit(failures === 0 ? 0 : 1);
