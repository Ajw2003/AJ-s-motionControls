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
  rotateXY,
  magnitude,
  clamp,
  normalize01,
  GravityFilter,
  RingBuffer,
  readAcceleration,
  readRotationRate,
  readLinearAcceleration,
  gravityInDeviceFrame,
  GRAVITY_MS2,
  screenSpinRate,
  orientationToQuaternion,
  quaternionMultiply,
  quaternionConjugate,
  quaternionNormalize,
  quaternionFromRotationVector,
  orientationDelta,
  MotionSource,
} from './js/sensors.js';

import {
  computeThrowDescriptor,
  computeSimulatedThrowDescriptor,
  buildAimPayload,
  decaySpinEnvelope,
  peakSpinEnvelope,
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

const IDENTITY = { w: 1, x: 0, y: 0, z: 0 };

function makeSample(t, power, extra = {}) {
  return {
    t,
    linear: { x: power, y: 0, z: 0 },
    power,
    attitude: extra.attitude ?? IDENTITY,
    rotationRate: extra.rotationRate ?? { alpha: 0, beta: 0, gamma: 0 },
  };
}

/**
 * How far a rotation turns, in degrees, regardless of the axis.
 *
 * Frame-agnostic on purpose. Assertions about the delta quaternion are written in
 * terms of this and of equality-with-a-known-rotation, never by decomposing it into
 * angles — a decomposition here would be a second implementation of the thing under
 * test, and it would have the same pole the old code did.
 */
function rotationAngleDeg(q) {
  const { w } = quaternionNormalize(q);
  return 2 * Math.acos(Math.min(1, Math.abs(w))) * (180 / Math.PI);
}

/** Same rotation? q and -q describe the same one, so compare through the sign. */
function sameRotation(a, b, tolerance = 1e-6) {
  const p = quaternionNormalize(a);
  const q = quaternionNormalize(b);
  const flip = (p.w * q.w + p.x * q.x + p.y * q.y + p.z * q.z) < 0 ? -1 : 1;
  return near(p.w, flip * q.w, tolerance) && near(p.x, flip * q.x, tolerance) &&
         near(p.y, flip * q.y, tolerance) && near(p.z, flip * q.z, tolerance);
}

/** A rotation of `deg` about one of the device's own axes. */
const aboutX = (deg) => quaternionFromRotationVector((deg * Math.PI) / 180, 0, 0);
const aboutY = (deg) => quaternionFromRotationVector(0, (deg * Math.PI) / 180, 0);

console.log('Controller sensor math\n');

// ---------------------------------------------------------------- quaternions
console.log('\nquaternion math');
{
  const q0 = orientationToQuaternion(0, 0, 0);
  check('flat and level is identity', near(q0.w, 1) && near(q0.x, 0) && near(q0.y, 0) && near(q0.z, 0));

  // Each Euler angle must land on its own axis. Getting the Z-X'-Y'' composition
  // order wrong still passes these individually but fails the round trip below.
  const h = Math.SQRT1_2;
  const qA = orientationToQuaternion(90, 0, 0);
  check('alpha turns about Z', near(qA.w, h) && near(qA.z, h) && near(qA.x, 0) && near(qA.y, 0));
  const qB = orientationToQuaternion(0, 90, 0);
  check('beta turns about X', near(qB.w, h) && near(qB.x, h) && near(qB.y, 0) && near(qB.z, 0));
  const qG = orientationToQuaternion(0, 0, 90);
  check('gamma turns about Y', near(qG.w, h) && near(qG.y, h) && near(qG.x, 0) && near(qG.z, 0));

  // Composition order. Z(alpha) then X'(beta) then Y''(gamma) is what the spec
  // defines; the wrong order is correct for small angles and subtly wrong for large
  // ones, which is the kind of bug that survives desk testing and shows up mid-throw.
  const composed = orientationToQuaternion(30, 20, -15);
  const byHand = quaternionMultiply(
    quaternionMultiply(orientationToQuaternion(30, 0, 0), orientationToQuaternion(0, 20, 0)),
    orientationToQuaternion(0, 0, -15));
  check('composes Z then X\' then Y\'\'', sameRotation(composed, byHand));

  const q = orientationToQuaternion(35, -20, 55);
  const id = quaternionMultiply(quaternionConjugate(q), q);
  check('conjugate times self is identity', near(id.w, 1) && near(id.x, 0) && near(id.y, 0) && near(id.z, 0));

  const rot = quaternionFromRotationVector(0, 0, 90 * Math.PI / 180);
  check('rotation vector about z matches an alpha turn',
    near(rot.w, h) && near(rot.z, h));
  const none = quaternionFromRotationVector(0, 0, 0);
  check('a zero rotation vector is identity', near(none.w, 1) && near(none.z, 0));

  const norm = quaternionNormalize({ w: 2, x: 0, y: 0, z: 0 });
  check('normalize scales to unit length', near(norm.w, 1));
  const degenerate = quaternionNormalize({ w: 0, x: 0, y: 0, z: 0 });
  check('a degenerate quaternion falls back to identity', near(degenerate.w, 1));
}

// ------------------------------------------------------------ orientationDelta
console.log('\norientationDelta');
{
  const grip = orientationToQuaternion(200, 90, -30); // an awkward, arbitrary pose

  check('no movement from the reference is identity',
    sameRotation(orientationDelta(grip, grip), IDENTITY));

  // The property the whole control scheme rests on: the delta is the rotation the
  // player physically applied, in the phone's own frame, whatever pose they started
  // from. Ten degrees of wrist is ten degrees of stone.
  for (const pose of [orientationToQuaternion(0, 0, 0),
                      orientationToQuaternion(200, 90, -30),
                      orientationToQuaternion(-45, 12, 80)]) {
    const turned = quaternionMultiply(pose, aboutX(10));
    check('a 10 degree pitch from any grip reads as 10 degrees about x',
      sameRotation(orientationDelta(pose, turned), aboutX(10)));
  }

  // The singularity, stated as the thing it actually breaks. Held upright (beta 90),
  // alpha and gamma are the SAME physical rotation and the browser's split between
  // them is decided by sensor noise. Any three-angle representation has to pick one;
  // the delta quaternion does not have to pick, so both must come out identical.
  const upright = orientationToQuaternion(0, 90, 0);
  const viaAlpha = orientationDelta(upright, orientationToQuaternion(20, 90, 0));
  const viaGamma = orientationDelta(upright, orientationToQuaternion(0, 90, 20));
  check('at the play pose alpha and gamma produce the same rotation',
    sameRotation(viaAlpha, viaGamma),
    `${rotationAngleDeg(viaAlpha).toFixed(3)} vs ${rotationAngleDeg(viaGamma).toFixed(3)} deg`);

  // Crossing the alpha 0/360 seam must not produce a ~360 degree turn. There is no
  // wrap handling anywhere any more — quaternions simply have no seam.
  const wrapped = orientationDelta(
    orientationToQuaternion(355, 90, 0), orientationToQuaternion(5, 90, 0));
  check('crossing the 0/360 seam is a 10 degree turn',
    near(rotationAngleDeg(wrapped), 10, 1e-6), `${rotationAngleDeg(wrapped).toFixed(3)} deg`);

  check('the result is a unit quaternion',
    near(Math.hypot(wrapped.w, wrapped.x, wrapped.y, wrapped.z), 1, 1e-12));
}

// ------------------------------------------------------------ gravity removal
console.log('\ngravity removal');
{
  const G = GRAVITY_MS2;

  // An accelerometer at rest reads the reaction force: +g along whichever device
  // axis points up. Flat on a table that is +z; upright it is +y.
  const flat = gravityInDeviceFrame(orientationToQuaternion(0, 0, 0));
  check('flat on a table reads +g on z', near(flat.z, G, 1e-9) && near(flat.x, 0, 1e-9) && near(flat.y, 0, 1e-9),
    `(${flat.x.toFixed(3)}, ${flat.y.toFixed(3)}, ${flat.z.toFixed(3)})`);

  const upright = gravityInDeviceFrame(orientationToQuaternion(0, 90, 0));
  check('upright reads +g on y', near(upright.y, G, 1e-6) && near(upright.z, 0, 1e-6),
    `(${upright.x.toFixed(3)}, ${upright.y.toFixed(3)}, ${upright.z.toFixed(3)})`);

  // Magnitude is invariant: it is the same gravity whichever way the phone is held.
  for (const [a, b, g] of [[0, 0, 0], [45, 30, -20], [200, 90, 0], [10, -60, 75]]) {
    const v = gravityInDeviceFrame(orientationToQuaternion(a, b, g));
    check(`gravity magnitude holds at ${a}/${b}/${g}`, near(magnitude(v), G, 1e-9));
  }

  // The bug this replaced. GravityFilter estimates gravity in DEVICE coordinates, so
  // a fast turn sweeps the gravity vector through that frame faster than the filter
  // tracks — and the lag reads as user acceleration that has nothing to do with how
  // hard the phone was thrown. Rotating a stationary phone must measure as ~zero.
  // A wrist snap at roughly the speed a real throw produces: 180 degrees in ~190ms,
  // about 900 deg/s. The phone is only turning — it is not accelerating through
  // space at all — so an honest measurement reads zero.
  const filter = new GravityFilter(0.8);
  let filterWorst = 0;
  let exactWorst = 0;
  for (let i = 0; i <= 12; i++) {
    const q = orientationToQuaternion(0, i * 15, 0);
    const reading = gravityInDeviceFrame(q);  // at rest: gravity and nothing else

    filterWorst = Math.max(filterWorst, magnitude(filter.update(reading, i * 16)));

    const exact = gravityInDeviceFrame(q);
    exactWorst = Math.max(exactWorst, magnitude({
      x: reading.x - exact.x, y: reading.y - exact.y, z: reading.z - exact.z,
    }));
  }
  check('exact removal reports no motion while merely turning', exactWorst < 1e-9,
    `${exactWorst.toExponential(2)} m/s²`);

  // This is the number that was compressing every throw into the top of the power
  // window: a large offset present in every gesture, proportional to how fast the
  // wrist turned rather than to how hard the stone was thrown.
  check('the filter fabricates motion while merely turning', filterWorst > 5,
    `${filterWorst.toFixed(2)} m/s² of phantom acceleration`);
}

console.log('\nreadLinearAcceleration');
{
  check('reads the hardware value when present',
    readLinearAcceleration({ acceleration: { x: 1, y: 2, z: 3 } })?.y === 2);
  check('absent on iOS gives null', readLinearAcceleration({}) === null);
  check('all-null axes give null',
    readLinearAcceleration({ acceleration: { x: null, y: null, z: null } }) === null);
  check('a partial reading is still usable',
    readLinearAcceleration({ acceleration: { x: 1, y: null, z: null } })?.x === 1);
}

// ------------------------------------------------- MotionSource orientation
//
// This class had zero coverage before the quaternion rewrite — it was not even
// imported here — which is exactly why the gimbal bug survived so long.
console.log('\nMotionSource orientation');
{
  // Enough of a DeviceMotionEvent for _onMotion. Gravity is left out: the filter
  // seeds from the first sample, so a constant vector reads as zero motion.
  const motionEvent = (rate = { alpha: 0, beta: 0, gamma: 0 }) => ({
    accelerationIncludingGravity: { x: 0, y: 0, z: 9.81 },
    rotationRate: rate,
  });

  const drive = (source, alpha, beta, gamma, rate) => {
    source._onOrientation({ alpha, beta, gamma });
    source._onMotion(motionEvent(rate));
    return source.latest;
  };

  {
    const m = new MotionSource();
    const s = drive(m, 12, 34, 56);
    check('uncalibrated reports no deflection', sameRotation(s.attitude, IDENTITY, 1e-9),
      `${s.attitude.w}/${s.attitude.x}/${s.attitude.y}/${s.attitude.z}`);
  }

  {
    // Flat, screen up — the pose the game is described in. Tapping here must make
    // this exact pose mean "flat stone, facing forward", i.e. identity.
    const m = new MotionSource();
    m._onOrientation({ alpha: 0, beta: 0, gamma: 0 });
    m._onMotion(motionEvent());
    m.calibrate();

    check('the tap pose itself is identity',
      sameRotation(drive(m, 0, 0, 0).attitude, IDENTITY));

    check('pitching the top edge up 10 degrees is 10 degrees about device x',
      sameRotation(drive(m, 0, 10, 0).attitude, aboutX(10)));

    check('rolling the phone 10 degrees is 10 degrees about device y',
      sameRotation(drive(m, 0, 0, 10).attitude, aboutY(10)));
  }

  {
    // Gripped upright — beta 90 is "phone facing you", and precisely where the Euler
    // version came apart. Calibrating here has to work exactly as well as calibrating
    // flat: the reference is whatever pose the tap happened in.
    const m = new MotionSource();
    m._onOrientation({ alpha: 0, beta: 90, gamma: 0 });
    m._onMotion(motionEvent());
    m.calibrate();

    check('no movement from an upright grip reads as identity',
      sameRotation(drive(m, 0, 90, 0).attitude, IDENTITY));

    check('pitching 10 degrees forward of an upright grip is a 10 degree turn',
      sameRotation(drive(m, 0, 80, 0).attitude, aboutX(-10)));

    // The headline case. Sweeping through vertical used to flip alpha and fold gamma,
    // so aim and roll jumped by up to 180 degrees while the phone moved one. Every
    // step must stay small, and the turn from the grip must grow monotonically as the
    // phone keeps pitching the same way.
    let prev = null;
    let prevAngle = null;
    let worstJump = 0;
    let monotonic = true;
    for (let beta = 100; beta >= 80; beta -= 1) {
      const s = drive(m, 0, beta, 0);
      const angle = rotationAngleDeg(s.attitude);

      if (prev !== null) {
        worstJump = Math.max(worstJump,
          rotationAngleDeg(orientationDelta(prev.attitude, s.attitude)));
        // Past the grip pose the turn shrinks to zero and grows again the other way,
        // so only the leg on each side has to be monotonic.
        if (beta > 90 && angle > prevAngle + 1e-9) monotonic = false;
        if (beta < 90 && angle < prevAngle - 1e-9) monotonic = false;
      }

      prev = s;
      prevAngle = angle;
    }
    check('sweeping through vertical never jumps', worstJump < 1.5,
      `worst step ${worstJump.toFixed(3)} deg`);
    check('the turn from the grip changes monotonically across vertical', monotonic);
  }

  {
    // The reference is the tap, not the compass: an arbitrary heading nowhere near
    // north must behave the same as any other.
    const m = new MotionSource();
    m._onOrientation({ alpha: 200, beta: 90, gamma: 0 });
    m._onMotion(motionEvent());
    m.calibrate();

    const yawed = drive(m, 215, 90, 0);
    check('aim is measured from the tap, not from north',
      near(rotationAngleDeg(yawed.attitude), 15, 1e-6),
      `${rotationAngleDeg(yawed.attitude).toFixed(3)} deg`);
  }

  {
    // Gyro carry-forward. deviceorientation and devicemotion are separate streams, so
    // on a device where orientation lags the old code re-stamped a stale triple onto
    // every sample — a staircase in the aim stream, worst while the phone moves fastest.
    //
    // Gripped upright, and turning about the device's Y axis, which at that pose is
    // world up: this is the player swinging the phone across to aim.
    let clock = 0;
    const m = new MotionSource({ now: () => clock });
    m._onOrientation({ alpha: 0, beta: 90, gamma: 0 });
    m._onMotion(motionEvent());
    m.calibrate();

    for (let i = 0; i < 6; i++) {
      clock += 16; // one frame of motion with no orientation event
      m._onMotion(motionEvent({ alpha: 0, beta: 0, gamma: 60 }));
    }

    // 6 frames x 16ms x 60 deg/s = 5.76 degrees, about the device's own y axis.
    check('the attitude keeps moving while orientation is stale',
      sameRotation(m.latest.attitude, aboutY(5.76), 1e-3),
      `${rotationAngleDeg(m.latest.attitude).toFixed(3)} deg`);
    check('staleness is reported', m.latest.orientationAgeMs === 96, `${m.latest.orientationAgeMs}`);

    // A real reading must win, or integration drift accumulates forever.
    clock += 16;
    m._onOrientation({ alpha: 0, beta: 90, gamma: 0 });
    m._onMotion(motionEvent());
    check('a fresh orientation reading overrides the gyro estimate',
      sameRotation(m.latest.attitude, IDENTITY),
      `${rotationAngleDeg(m.latest.attitude).toFixed(3)} deg`);

    // A gap long enough to be a backgrounded tab must not be integrated across.
    clock += 5000;
    m._onMotion(motionEvent({ alpha: 0, beta: 0, gamma: 60 }));
    check('a long gap is not integrated across',
      sameRotation(m.latest.attitude, IDENTITY),
      `${rotationAngleDeg(m.latest.attitude).toFixed(3)} deg`);
  }
}

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
// Spin is the twist about the phone's own screen normal — the frisbee flick — and
// nothing else. alpha is the rate about device Z.
check('screenSpinRate reads the twist about the screen normal',
  screenSpinRate({ alpha: 300, beta: 0, gamma: 0 }) === 300);
check('screenSpinRate keeps the direction of the twist',
  screenSpinRate({ alpha: -300, beta: 0, gamma: 0 }) === -300);

// The bug this replaced. Swinging your arm turns the phone about its other two axes far
// harder than any wrist twist, so a 3-axis magnitude reported arm motion as spin.
check('swinging the arm is not spin',
  screenSpinRate({ alpha: 0, beta: 900, gamma: 700 }) === 0);
check('screenSpinRate treats a missing rate as still', screenSpinRate(null) === 0);
check('screenSpinRate tolerates null axes', screenSpinRate({ alpha: null }) === 0);

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
  check('swing speed is reported alongside it', d.swingSpeed > 0, d.swingSpeed.toFixed(3));
  check('power maps swing speed onto 0..1',
    near(d.power, (d.swingSpeed - ThrowTuning.minSwingSpeed) /
                  (ThrowTuning.maxSwingSpeed - ThrowTuning.minSwingSpeed), 1e-9),
    d.power.toFixed(3));

  // The other metric stays available and correct — see ThrowTuning.powerMetric.
  const byPeak = computeThrowDescriptor(perfect, { ...ThrowTuning, powerMetric: 'peakAccel' });
  check('peakAccel remains selectable',
    near(byPeak.power, (30 - ThrowTuning.minPeakAccel) / (ThrowTuning.maxPeakAccel - ThrowTuning.minPeakAccel), 1e-9),
    byPeak.power.toFixed(3));

  // Nothing scales power down for a slow stop any more. There used to be a
  // timingQuality here cutting launch speed by up to 60% based on how long the phone
  // took to settle after its peak — but a hard swing takes longer to settle, so the
  // penalty fell hardest on the throws that deserved it least. Effort now decides
  // distance on its own, and a trailing stop costs nothing.
  const late = [...perfect];
  for (let i = 1; i <= 25; i++) late.push(makeSample(100 + i * 10, 1));
  const dLate = computeThrowDescriptor(late);
  check('a trailing stop does not weaken the throw', near(dLate.power, d.power),
    `${dLate.power.toFixed(3)} vs ${d.power.toFixed(3)}`);
  check('timing is not a wire field any more', dLate.timingQuality === undefined);

  // Ramp to `peakAccel` over `steps` samples, then release.
  const swing = (peakAccel, steps = 10) => {
    const out = [];
    for (let i = 0; i <= steps; i++) out.push(makeSample(i * 10, (peakAccel * i) / steps));
    out.push(makeSample((steps + 1) * 10, 1)); // released
    return out;
  };

  const harder = computeThrowDescriptor(swing(36));
  const softer = computeThrowDescriptor(swing(14)); // ~2.5x less effort
  check('a much softer throw measures much softer', softer.power < harder.power * 0.55,
    `${softer.power.toFixed(3)} vs ${harder.power.toFixed(3)}`);

  // The property that actually motivated the change of metric, and the one peak
  // acceleration is blind to: a short sharp jerk and a long powerful pull reach the
  // same peak, but the pull puts far more speed into the stone. Peak reports them as
  // the same throw. Velocity, being the integral, does not.
  const jerk = computeThrowDescriptor(swing(30, 10));   // 100ms
  const pull = computeThrowDescriptor(swing(30, 25));   // 250ms, same peak
  check('a jerk and a pull peak identically', near(jerk.peakAccel, pull.peakAccel),
    `${jerk.peakAccel} vs ${pull.peakAccel}`);
  check('peak accel cannot tell them apart',
    near(normalize01(jerk.peakAccel, ThrowTuning.minPeakAccel, ThrowTuning.maxPeakAccel),
         normalize01(pull.peakAccel, ThrowTuning.minPeakAccel, ThrowTuning.maxPeakAccel)));
  check('swing speed does', pull.power > jerk.power * 1.5,
    `jerk ${jerk.power.toFixed(3)} vs pull ${pull.power.toFixed(3)}`);

  // A limp gesture should not register as a throw at all.
  const fumble = [makeSample(0, 1), makeSample(10, 2), makeSample(20, 1)];
  check('weak gesture returns null', computeThrowDescriptor(fumble) === null);

  check('empty input returns null', computeThrowDescriptor([]) === null);
  check('null input returns null', computeThrowDescriptor(null) === null);

  // Orientation is raw and read at the stop. Nothing is clamped: a player who wants
  // to throw backwards is allowed to, and rewriting their input into something
  // "usable" was worse than letting them miss.
  const backwards = aboutX(190);
  const wild = [makeSample(0, 5), makeSample(10, 30, { attitude: backwards })];
  const dWild = computeThrowDescriptor(wild);
  check('attitude is not clamped — backwards throws are allowed',
    sameRotation({ w: dWild.qw, x: dWild.qx, y: dWild.qy, z: dWild.qz }, backwards),
    `${rotationAngleDeg(backwards).toFixed(1)} deg`);

  // The stop sample is the aim: a throwing motion ends pointing where it was thrown.
  const swung = [
    makeSample(0, 1, { attitude: aboutY(20) }),    // lining the shot up
    makeSample(120, 25, { attitude: aboutY(70) }), // swinging
    makeSample(180, 30, { attitude: aboutY(95) }), // peak
    makeSample(200, 2, { attitude: aboutY(110) }), // stopped, pointing at the target
  ];
  const dSwung = computeThrowDescriptor(swung);
  check('attitude comes from the stop, raw',
    sameRotation({ w: dSwung.qw, x: dSwung.qx, y: dSwung.qy, z: dSwung.qz }, aboutY(110)));
  check('power still comes from the peak', near(dSwung.peakAccel, 30));

  // Only the twist about the screen normal counts, and it keeps its sign — see
  // screenSpinRate. The arm-swing components alongside it are not spin.
  const spun = [makeSample(0, 5), makeSample(10, 30, { rotationRate: { alpha: -420, beta: 900, gamma: 700 } })];
  check('spin is the signed twist about the screen normal, not the arm swing',
    near(computeThrowDescriptor(spun).spinRate, -420),
    `${computeThrowDescriptor(spun).spinRate}`);

  // Spin is the one field not read from the stop sample, and this is why: the wrist
  // flick peaks mid-swing and is spent by the time the phone halts — and the throw
  // fires precisely *because* it halted. Reading spin at the stop measured the one
  // instant the wrist was no longer turning, so hard flicks and limp ones landed
  // the same near-zero value.
  const flicked = [
    makeSample(0, 1),
    makeSample(120, 25, { rotationRate: { alpha: 1000, beta: 0, gamma: 0 } }), // the flick
    makeSample(180, 30, { rotationRate: { alpha: 500, beta: 0, gamma: 0 } }),  // peak accel, spin fading
    makeSample(200, 2),                                                         // stopped, not turning
  ];
  const dFlicked = computeThrowDescriptor(flicked);
  check('spin comes from the swing, not the calm stop sample',
    dFlicked.spinRate > 500, `${dFlicked.spinRate.toFixed(1)}`);

  // The device path hands in the envelope ThrowController tracked live, so the stone
  // leaves at the rate the ghost was showing. Anything passed wins over the replay.
  check('an explicit envelope overrides the buffer',
    computeThrowDescriptor(flicked, ThrowTuning, 1234).spinRate === 1234);
  check('a zero envelope is honoured rather than treated as absent',
    computeThrowDescriptor(flicked, ThrowTuning, 0).spinRate === 0);
}

// ---------------------------------------------------------------- aim payload
console.log('\nbuildAimPayload');
{
  const aimed = aboutY(60);
  const payload = buildAimPayload(makeSample(0, 20, { attitude: aimed }), 42);
  check('carries a sequence number', payload.seq === 42);
  check('is typed AIM', payload.type === 'AIM');

  // The preview and the throw must show the same rotation, so both flatten the
  // attitude through the same helper. If these ever spelled it differently the ghost
  // would aim somewhere the stone did not go.
  check('attitude is raw, matching the throw descriptor',
    sameRotation({ w: payload.qw, x: payload.qx, y: payload.qy, z: payload.qz }, aimed));
  check('a phone at the tap pose sends identity',
    buildAimPayload(makeSample(0, 20), 1).qw === 1);
  check('null sample yields null payload', buildAimPayload(null, 1) === null);

  // Spin is what drives the ghost's visual spin on the TV. It has to be the envelope
  // when one is supplied, because the instantaneous value at a 20Hz tick mostly misses
  // the flick that produced it.
  const spinning = makeSample(0, 20, { rotationRate: { alpha: 50, beta: 30, gamma: 40 } });
  check('falls back to this instant\'s spin when no envelope is given',
    near(buildAimPayload(spinning, 1).spinRate, 50));
  check('carries the supplied envelope in preference to the instant',
    near(buildAimPayload(spinning, 1, 320).spinRate, 320));
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

  // Signed, because spin is now a signed rate about the screen normal. The peak is
  // chosen by magnitude and keeps its direction.
  check('a negative peak is held like a positive one',
    near(decaySpinEnvelope(-100, -900, 16), -900));
  check('a decaying positive peak does not beat a real negative one',
    near(decaySpinEnvelope(900, -950, 16), -950),
    `${decaySpinEnvelope(900, -950, 16).toFixed(1)}`);

  // A plain max() would have returned the stale +900 here, so a wrist flicked back the
  // other way would have spun the stone the way the previous throw went.
  check('a decaying peak still wins while it is the larger magnitude',
    decaySpinEnvelope(900, -100, 16) > 800,
    `${decaySpinEnvelope(900, -100, 16).toFixed(1)}`);
}

console.log('\npeakSpinEnvelope');
{
  check('no samples means no spin', peakSpinEnvelope([]) === 0);
  check('null input means no spin', peakSpinEnvelope(null) === 0);

  // Must agree with the live tracker in ThrowController, which is the whole reason
  // it reuses decaySpinEnvelope rather than defining a second notion of peak spin.
  const swing = [
    makeSample(0, 1, { rotationRate: { alpha: 100, beta: 0, gamma: 0 } }),
    makeSample(50, 20, { rotationRate: { alpha: 900, beta: 0, gamma: 0 } }),
    makeSample(100, 5, { rotationRate: { alpha: 0, beta: 0, gamma: 0 } }),
  ];
  let live = 0;
  live = decaySpinEnvelope(live, 100, 0);
  live = decaySpinEnvelope(live, 900, 50);
  live = decaySpinEnvelope(live, 0, 50);
  check('matches the envelope tracked sample by sample',
    near(peakSpinEnvelope(swing), live, 1e-9), `${peakSpinEnvelope(swing).toFixed(6)}`);

  // The tau is the window: spin from before the swing decays away on its own, so
  // there is no separate cutoff constant that could drift out of sync with it.
  const stale = [
    makeSample(0, 1, { rotationRate: { alpha: 900, beta: 0, gamma: 0 } }), // lining up
    makeSample(2000, 20, { rotationRate: { alpha: 40, beta: 0, gamma: 0 } }),
  ];
  check('spin from before the swing has decayed away', near(peakSpinEnvelope(stale), 40, 1e-3),
    `${peakSpinEnvelope(stale).toFixed(3)}`);
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
    sameRotation({ w: brief.qw, x: brief.qx, y: brief.qy, z: brief.qz }, IDENTITY));

  // The whole point of the jitter bounds: identity is already a good throw, and the
  // jitter has to stay inside SkipTuning's tolerance window or every simulated throw
  // sinks and every test score is zero.
  const extremes = [computeSimulatedThrowDescriptor(600, () => 0),
                    computeSimulatedThrowDescriptor(600, () => 1)];
  const jitterBound = Math.hypot(SimulatedThrow.pitchJitterDeg, SimulatedThrow.yawJitterDeg);
  check('the jittered attitude stays inside the skip tolerance window',
    extremes.every((d) =>
      rotationAngleDeg({ w: d.qw, x: d.qx, y: d.qy, z: d.qz }) <= jitterBound + 1e-6),
    extremes.map((d) =>
      rotationAngleDeg({ w: d.qw, x: d.qx, y: d.qy, z: d.qz }).toFixed(1)).join(' / '));

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
    makeSample(0, 2),
    makeSample(20, 20),
    makeSample(40, 32),   // peak
    makeSample(60, 3),    // calm 1
    makeSample(80, 2),    // calm 2
    makeSample(100, 1),   // calm 3 — fires here
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
  const flick = makeSample(0, 5, { rotationRate: { alpha: 500, beta: 0, gamma: 0 } });
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

// ------------------------------------------- live windup reaches the aim frame
// The TV draws the aim arc at this, so it is what makes the predicted skips and
// distance respond to how hard someone is swinging. It used to be a fixed constant,
// which made that readout mathematically incapable of changing.
console.log('\nThrowController: live windup on the aim stream');
{
  const makeMotion = () => ({
    latest: null,
    buffer: { toArray: () => [], clear() {} },
    calibrate() {},
    onSample: () => {},
  });

  {
    const motion = makeMotion();
    const c = new ThrowController(motion, { aimHz: 1000 });
    c.arm();
    check('a phone held still reports no windup', c.windup === 0, `${c.windup}`);

    // Accelerate for 200ms at 20 m/s^2, which is about 4 m/s of hand speed — a firm
    // swing. maxSwingSpeed is 6.5, so this should land high but not saturated.
    for (let i = 1; i <= 10; i++) motion.onSample(makeSample(i * 20, 20));
    check('swinging raises the windup', c.windup > 0.4 && c.windup < 1,
      `${c.windup.toFixed(3)}`);
    c.cancel();
  }

  // The property that matters most: the live estimate and the release descriptor are
  // the same quantity on the same scale. They used to be peak-acceleration and
  // swing-speed respectively, so the arc and the stone could not agree by construction.
  {
    const motion = makeMotion();
    const c = new ThrowController(motion, { aimHz: 1000 });
    c.arm();

    const swing = [];
    for (let i = 1; i <= 10; i++) swing.push(makeSample(i * 20, 20));
    for (const sample of swing) motion.onSample(sample);

    const live = c.windup;
    const released = computeThrowDescriptor(swing).power;
    check('live windup is on the same scale as the throw\'s power',
      Math.abs(live - released) < 0.12,
      `live ${live.toFixed(3)} vs released ${released.toFixed(3)}`);
    c.cancel();
  }

  // Bias in the accelerometer would otherwise integrate into phantom speed the whole
  // time a player is lining a shot up, so the integrator resets whenever the phone is
  // calm — the same reason peakSwingSpeed starts from the last calm sample.
  {
    const motion = makeMotion();
    const c = new ThrowController(motion, { aimHz: 1000 });
    c.arm();
    for (let i = 1; i <= 60; i++) motion.onSample(makeSample(i * 20, 1));
    check('a long calm hold does not accumulate phantom windup', c.windup < 0.05,
      `${c.windup.toFixed(4)}`);
    c.cancel();
  }

  // And the frame actually carries it.
  {
    const motion = makeMotion();
    const frames = [];
    const c = new ThrowController(motion, { onAim: (p) => frames.push(p), aimHz: 200 });
    c.arm();
    for (let i = 1; i <= 10; i++) {
      const sample = makeSample(i * 20, 20);
      motion.latest = sample;
      motion.onSample(sample);
    }
    await new Promise((resolve) => setTimeout(resolve, 30));
    check('the aim frame carries the windup as power',
      frames.length > 0 && frames[0].power > 0.4,
      frames.length ? `${frames[0].power.toFixed(3)}` : 'no frames');
    c.cancel();
  }
}

console.log(failures === 0
  ? '\nPASS — all controller math checks green'
  : `\nFAIL — ${failures} check(s) failed`);
process.exit(failures === 0 ? 0 : 1);
