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
  pointingHeading,
  MIN_POINTING_CONFIDENCE,
  MotionSource,
  headingRateFromGyro,
  FAST_ROTATION_DEG_PER_SEC,
} from './js/sensors.js';

import {
  computeThrowDescriptor,
  computeSimulatedThrowDescriptor,
  buildAimPayload,
  ThrowController,
  ThrowGesture,
  ThrowState,
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
    dYaw: extra.dYaw ?? 0,
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

  // Clamping at release. Aim is deliberately NOT narrowed: a 45 degree ceiling made
  // every fast swing report the same aim, because anything past it flattened to the
  // limit and the rock left on a line the player had not chosen.
  const wild = [makeSample(0, 5), makeSample(10, 30, { dYaw: 90, dBeta: -120, dGamma: 17 })];
  const dWild = computeThrowDescriptor(wild);
  check('a wide aim survives instead of being clipped to 45', dWild.aimDeg === 90,
    `${dWild.aimDeg}`);
  check('aim allows the full rotation', ThrowTuning.maxAimDeg === 180);
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
  const payload = buildAimPayload(makeSample(0, 20, { dYaw: 60, dBeta: 22, dGamma: 3 }), 42);
  check('carries a sequence number', payload.seq === 42);
  check('is typed AIM', payload.type === 'AIM');
  check('aim is passed through like a throw', payload.aimDeg === 60);
  check('attack angle survives', payload.attackAngleDeg === 22);
  check('windup is normalised 0..1', payload.windup > 0 && payload.windup <= 1, payload.windup.toFixed(3));
  check('null sample yields null payload', buildAimPayload(null, 1) === null);
}

// ------------------------------------------------------------- pointing heading
console.log('\npointingHeading (the gimbal fix)');
{
  // Flat phone, top pointing at magnetic north.
  const north = pointingHeading(0, 0);
  check('a level phone at alpha 0 heads due north',
    near(north.headingDeg, 0) && near(north.confidence, 1));

  // alpha counts counter-clockwise, so the resulting bearing is its negation.
  check('turning the phone left gives a left (negative) bearing',
    near(pointingHeading(30, 0).headingDeg, -30));
  check('turning the phone right gives a right bearing',
    near(pointingHeading(-30, 0).headingDeg, 30));

  // Roll cannot move where the top of the phone points, and the function does not
  // even accept gamma — this documents that as intent rather than omission.
  check('bearing is unaffected by pitch while the phone still points forward',
    near(pointingHeading(30, 45).headingDeg, -30));

  // THE BUG. Two degrees of pitch either side of vertical used to flip the aim by
  // 180 degrees, because Euler alpha is degenerate there. Confidence is what lets
  // the caller refuse to believe the heading through that region.
  const justUnder = pointingHeading(30, 89);
  const justOver = pointingHeading(30, 91);
  check('heading really does flip 180 degrees across vertical',
    Math.abs(shortestAngleDelta(justOver.headingDeg, justUnder.headingDeg)) > 179,
    `${justUnder.headingDeg.toFixed(1)} -> ${justOver.headingDeg.toFixed(1)}`);
  check('but confidence collapses there, so both are rejected',
    justUnder.confidence < MIN_POINTING_CONFIDENCE &&
    justOver.confidence < MIN_POINTING_CONFIDENCE,
    `${justUnder.confidence.toFixed(3)} / ${justOver.confidence.toFixed(3)}`);

  check('a phone aimed at the sky is rejected',
    pointingHeading(0, 90).confidence < MIN_POINTING_CONFIDENCE);
  check('a phone pointing backwards over the shoulder is rejected',
    pointingHeading(0, 150).confidence < MIN_POINTING_CONFIDENCE);
  check('a normally-held phone is accepted',
    pointingHeading(0, 20).confidence >= MIN_POINTING_CONFIDENCE);
}

// ---------------------------------------------------------------- aim yaw sign
console.log('\ndevice heading -> game yaw');
{
  // Bearings are clockwise-positive and so is Unity's yaw, so this passes through.
  check('default sign passes the bearing through', getYawSign() === 1);
  check('a right bearing aims right', toGameYaw(30) === 30);
  check('and the mirror case holds', toGameYaw(-30) === -30);
  check('zero stays zero', toGameYaw(0) === 0);

  const straight = [makeSample(0, 5), makeSample(10, 30, { dYaw: 20 })];
  check('a throw inherits the same convention',
    computeThrowDescriptor(straight).aimDeg === 20);

  setYawSign(-1);
  check('the override flips it for a platform that disagrees', toGameYaw(30) === -30);
  check('an aim frame follows the override too',
    buildAimPayload(makeSample(0, 10, { dYaw: 20 }), 1).aimDeg === -20);

  setYawSign(1); // restore, later tests assume the default
  check('restoring the default works', getYawSign() === 1);
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

// -------------------------------------------------------------- gyro heading rate
console.log('\nheadingRateFromGyro');
{
  const rate = (alpha, beta = 0, gamma = 0) => ({ alpha, beta, gamma });

  // Level phone spun about the screen normal: alpha counts counter-clockwise and the
  // bearing is clockwise, so they are negatives of each other.
  check('a level phone turning left reads as a left bearing rate',
    near(headingRateFromGyro(0, 0, rate(200)), -200, 1e-9));
  check('and the mirror case holds',
    near(headingRateFromGyro(0, 0, rate(-200)), 200, 1e-9));

  // THE CASE THAT KILLED THE FIRST ATTEMPT. Rolling a tilted phone about its own long
  // axis sweeps a large world-vertical rotation, but the top of the phone does not
  // change heading at all. Anything using the world-vertical component alone reports a
  // couple of hundred deg/s of aim drift here that the player never performed.
  check('rolling a tilted phone produces no heading rate',
    near(headingRateFromGyro(55, 0, rate(0, 0, 250)), 0, 1e-9),
    `${headingRateFromGyro(55, 0, rate(0, 0, 250)).toFixed(3)}`);

  check('pitching produces no heading rate either',
    near(headingRateFromGyro(0, 0, rate(0, 200, 0)), 0, 1e-9));

  // Tilt divides through cos(beta), so the same body rate turns the bearing faster
  // the more the phone is pitched.
  check('tilt scales the bearing rate',
    headingRateFromGyro(60, 0, rate(100)) < headingRateFromGyro(0, 0, rate(100)),
    `${headingRateFromGyro(60, 0, rate(100)).toFixed(1)} vs -100`);

  check('a near-vertical phone reports no rate rather than exploding',
    headingRateFromGyro(90, 0, rate(300)) === 0);
}

// ------------------------------------------------ aim tracks live, right to release
console.log('\nlive aim tracking');
{
  // MotionSource reads window.screen for rotation compensation. Nothing else in the
  // path touches the DOM, so a stub is enough to drive it in Node.
  const hadWindow = 'window' in globalThis;
  if (!hadWindow) globalThis.window = { screen: {} };

  const motion = new MotionSource({ gravityK: 0.8 });
  const orient = (alpha, beta = 0) => motion._onOrientation({ alpha, beta, gamma: 0 });
  const move = (extraZ, alphaRate = 0) => motion._onMotion({
    accelerationIncludingGravity: { x: 0, y: 0, z: 9.8 + extraZ },
    rotationRate: { alpha: alphaRate, beta: 0, gamma: 0 },
  });

  // Turn the phone and let it come to rest. The fused reading only reasserts itself
  // once the gyro says the phone is slow, so settling is what a real aiming pause is.
  const aimAt = (alpha) => {
    move(0);                                  // report the phone as still first, or
    for (let i = 0; i < 60; i++) orient(alpha); // the correction stays gated off
    move(0);                                  // sample carries the settled heading
  };

  // Grip: point somewhere and calibrate.
  orient(0);
  move(0);
  motion.calibrate();
  move(0);          // re-seed the gravity filter that calibrate() reset

  aimAt(20);
  check('aim follows the phone while aiming',
    near(motion.latest.dYaw, -20, 0.5), `${motion.latest.dYaw.toFixed(2)}`);

  // The arc has to keep tracking through the swing, not freeze at its start — and the
  // throw is taken from the last sample before release, so this value IS the throw.
  // Anything that held it steady here would discard the aim the player released on.
  const beforeSwing = motion.latest.dYaw;
  for (let i = 0; i < 4; i++) {
    await new Promise((r) => setTimeout(r, 15));
    move(18, 300);
  }
  check('aim keeps tracking once the swing is under way',
    motion.latest.dYaw < beforeSwing - 5,
    `${beforeSwing.toFixed(1)} -> ${motion.latest.dYaw.toFixed(1)}`);

  // The release frame is what the descriptor reads.
  const atRelease = motion.latest.dYaw;
  const descriptor = computeThrowDescriptor(motion.buffer.toArray());
  check('the throw is computed from the release frame, not an earlier one',
    near(descriptor.aimDeg, toGameYaw(atRelease), 1e-6),
    `${descriptor.aimDeg.toFixed(2)} vs ${toGameYaw(atRelease).toFixed(2)}`);

  // A hard swing turns well past the old 45 degree ceiling. Clamping there made every
  // fast throw report the same aim regardless of where the phone actually pointed.
  aimAt(120);
  const wide = computeThrowDescriptor(motion.buffer.toArray());
  check('a wide turn is not flattened to a 45 degree ceiling',
    Math.abs(wide.aimDeg) > 45, `${wide.aimDeg.toFixed(1)}`);

  if (!hadWindow) delete globalThis.window;
}

// ------------------------------------------- gyro carries heading between updates
console.log('\ngyro integration');
{
  const hadWindow = 'window' in globalThis;
  if (!hadWindow) globalThis.window = { screen: {} };

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  const motion = new MotionSource({ gravityK: 0.8 });
  const orient = (alpha, beta = 0) => motion._onOrientation({ alpha, beta, gamma: 0 });
  const spin = (alphaRate) => motion._onMotion({
    accelerationIncludingGravity: { x: 0, y: 0, z: 9.8 },
    rotationRate: { alpha: alphaRate, beta: 0, gamma: 0 },
  });

  orient(0);
  spin(0);
  const start = motion.headingDeg;

  // THE BUG: deviceorientation fires on its own slower schedule, so between its
  // updates the heading used to sit frozen — a fast swing simply did not register,
  // and the release frame reported an angle from tens of milliseconds earlier.
  // No orientation events at all here: only the gyro is feeding it.
  const t0 = performance.now();
  for (let i = 0; i < 6; i++) {
    await sleep(15);
    spin(300);            // turning left hard
  }
  const elapsedSec = (performance.now() - t0) / 1000;
  const moved = motion.headingDeg - start;

  check('heading advances with no orientation events at all', Math.abs(moved) > 1,
    `${moved.toFixed(1)} deg over ${elapsedSec.toFixed(2)}s`);
  check('it advances in the direction the gyro reported', moved < 0, `${moved.toFixed(1)}`);
  check('and by roughly the integral of the turn rate',
    Math.abs(moved) > 300 * elapsedSec * 0.5 && Math.abs(moved) < 300 * elapsedSec * 1.5,
    `${Math.abs(moved).toFixed(0)} vs ~${(300 * elapsedSec).toFixed(0)}`);

  // While turning fast the fused reading must not drag the estimate back to its own
  // stale value — that would hand back exactly the lag this exists to remove.
  const beforeCorrection = motion.headingDeg;
  orient(0);                       // fused sensor still insisting on the old bearing
  const pulled = Math.abs(motion.headingDeg - beforeCorrection);
  check('a stale orientation reading barely moves it mid-swing', pulled < 1,
    `pulled ${pulled.toFixed(2)} deg`);

  // Once the phone settles, the drift-free reading is allowed to reassert itself.
  motion.headingRateDegPerSec = 0;
  const beforeSettled = motion.headingDeg;
  orient(0);
  check('but it does correct once the phone is still',
    Math.abs(motion.headingDeg - beforeSettled) > 1,
    `${Math.abs(motion.headingDeg - beforeSettled).toFixed(2)} deg`);

  check('the fast-rotation threshold is well below a real swing',
    FAST_ROTATION_DEG_PER_SEC < 300);

  if (!hadWindow) delete globalThis.window;
}

// --------------------------------------------------------- tap-swing-tap gesture
console.log('\nTAP gesture');
{
  const makeMotion = (latest, samples) => ({
    latest,
    buffer: { toArray: () => samples, clear() {} },
    calibrate() {},
  });

  const swing = [];
  for (let i = 0; i <= 10; i++) swing.push(makeSample(i * 10, i * 3));

  const tapController = (options = {}) => new ThrowController(makeMotion(swing[10], swing), {
    aimHz: 1000,
    gesture: ThrowGesture.TAP,
    minArmMs: 0,
    ...options,
  });

  {
    const sent = [];
    const c = tapController({ onThrow: (p) => sent.push(p) });

    check('the first tap arms without throwing',
      c.tap() === null && c.state === ThrowState.GRIPPED && sent.length === 0);
    check('the second tap throws', c.tap() !== null && sent.length === 1);
    check('and the gesture returns to idle', c.state === ThrowState.IDLE);
  }

  {
    // Without this floor, one bouncy tap arms and fires in the same motion and
    // launches a rock with no swing behind it.
    const sent = [];
    const c = tapController({ onThrow: (p) => sent.push(p), minArmMs: 5000 });
    c.tap();

    check('a second tap inside the minimum arm time is ignored',
      c.tap() === null && sent.length === 0 && c.state === ThrowState.GRIPPED);
    c.cancel();
  }

  {
    // HOLD mode must be untouched by any of this — it is the fallback.
    const sent = [];
    const c = new ThrowController(makeMotion(swing[10], swing), {
      onThrow: (p) => sent.push(p), aimHz: 1000, gesture: ThrowGesture.HOLD,
    });
    c.grip();
    check('hold mode still throws on release', c.release() !== null && sent.length === 1);
  }

  {
    // In tap mode nothing is on the glass, so an armed-and-forgotten throw is
    // invisible — it would stream aim frames forever and stall the match.
    const sent = [];
    let expired = false;
    const c = tapController({
      onThrow: (p) => sent.push(p),
      onExpire: () => { expired = true; },
      aimHz: 200,
      maxArmMs: 20,
    });
    c.tap();

    await new Promise((resolve) => setTimeout(resolve, 100));

    check('an armed throw nobody releases expires', expired && c.state === ThrowState.IDLE);
    check('and expiring does not throw a rock', sent.length === 0);
  }

  {
    // HOLD mode has a finger physically down, which is unambiguous intent, so the
    // timeout must not apply there.
    let expired = false;
    const c = new ThrowController(makeMotion(swing[10], swing), {
      onExpire: () => { expired = true; },
      aimHz: 200, gesture: ThrowGesture.HOLD, maxArmMs: 20,
    });
    c.grip();

    await new Promise((resolve) => setTimeout(resolve, 100));

    check('a held gesture never times out', !expired && c.state === ThrowState.GRIPPED);
    c.cancel();
  }
}

console.log(failures === 0
  ? '\nPASS — all controller math checks green'
  : `\nFAIL — ${failures} check(s) failed`);
process.exit(failures === 0 ? 0 : 1);
