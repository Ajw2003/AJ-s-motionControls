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

import { computeThrowDescriptor, buildAimPayload, ThrowTuning } from './js/throw.js';

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

  // Clamping at release.
  const wild = [makeSample(0, 5), makeSample(10, 30, { dAlpha: 90, dBeta: -120, dGamma: 17 })];
  const dWild = computeThrowDescriptor(wild);
  check('aim clamps to +/-45', dWild.aimDeg === ThrowTuning.maxAimDeg, `${dWild.aimDeg}`);
  check('attack angle clamps to its floor', dWild.attackAngleDeg === ThrowTuning.minAttackDeg);
  check('roll is passed through unclamped', dWild.rollDeg === 17);

  const spun = [makeSample(0, 5), makeSample(10, 30, { rotationRate: { alpha: 3, beta: 4, gamma: 0 } })];
  check('spin rate is the rotation magnitude', near(computeThrowDescriptor(spun).spinRate, 5));
}

// ---------------------------------------------------------------- aim payload
console.log('\nbuildAimPayload');
{
  const payload = buildAimPayload(makeSample(0, 20, { dAlpha: 60, dBeta: 22, dGamma: 3 }), 42);
  check('carries a sequence number', payload.seq === 42);
  check('is typed AIM', payload.type === 'AIM');
  check('aim is clamped like a throw', payload.aimDeg === ThrowTuning.maxAimDeg);
  check('attack angle survives', payload.attackAngleDeg === 22);
  check('windup is normalised 0..1', payload.windup > 0 && payload.windup <= 1, payload.windup.toFixed(3));
  check('null sample yields null payload', buildAimPayload(null, 1) === null);
}

console.log(failures === 0
  ? '\nPASS — all controller math checks green'
  : `\nFAIL — ${failures} check(s) failed`);
process.exit(failures === 0 ? 0 : 1);
