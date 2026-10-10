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
  quaternionNlerp,
  rotationRateMagnitude,
  tiltAngleDeg,
  orientationDelta,
  MotionSource,
  CalibrationTuning,
} from './js/sensors.js';

let failures = 0;

function check(label, ok, detail) {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ` — ${detail}` : ''}`);
  if (!ok) failures++;
}

function near(actual, expected, tolerance = 1e-6) {
  return Math.abs(actual - expected) <= tolerance;
}

const IDENTITY = { w: 1, x: 0, y: 0, z: 0 };

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
  const flip = p.w * q.w + p.x * q.x + p.y * q.y + p.z * q.z < 0 ? -1 : 1;
  return (
    near(p.w, flip * q.w, tolerance) &&
    near(p.x, flip * q.x, tolerance) &&
    near(p.y, flip * q.y, tolerance) &&
    near(p.z, flip * q.z, tolerance)
  );
}

/** A rotation of `deg` about one of the device's own axes. */
const aboutX = (deg) => quaternionFromRotationVector((deg * Math.PI) / 180, 0, 0);
const aboutY = (deg) => quaternionFromRotationVector(0, (deg * Math.PI) / 180, 0);

console.log('Controller sensor math\n');

// ---------------------------------------------------------------- quaternions
console.log('\nquaternion math');
{
  const q0 = orientationToQuaternion(0, 0, 0);
  check(
    'flat and level is identity',
    near(q0.w, 1) && near(q0.x, 0) && near(q0.y, 0) && near(q0.z, 0),
  );

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
    orientationToQuaternion(0, 0, -15),
  );
  check("composes Z then X' then Y''", sameRotation(composed, byHand));

  const q = orientationToQuaternion(35, -20, 55);
  const id = quaternionMultiply(quaternionConjugate(q), q);
  check(
    'conjugate times self is identity',
    near(id.w, 1) && near(id.x, 0) && near(id.y, 0) && near(id.z, 0),
  );

  const rot = quaternionFromRotationVector(0, 0, (90 * Math.PI) / 180);
  check('rotation vector about z matches an alpha turn', near(rot.w, h) && near(rot.z, h));
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

  check(
    'no movement from the reference is identity',
    sameRotation(orientationDelta(grip, grip), IDENTITY),
  );

  // The property the whole control scheme rests on: the delta is the rotation the
  // player physically applied, in the phone's own frame, whatever pose they started
  // from. Ten degrees of wrist is ten degrees of stone.
  for (const pose of [
    orientationToQuaternion(0, 0, 0),
    orientationToQuaternion(200, 90, -30),
    orientationToQuaternion(-45, 12, 80),
  ]) {
    const turned = quaternionMultiply(pose, aboutX(10));
    check(
      'a 10 degree pitch from any grip reads as 10 degrees about x',
      sameRotation(orientationDelta(pose, turned), aboutX(10)),
    );
  }

  // The singularity, stated as the thing it actually breaks. Held upright (beta 90),
  // alpha and gamma are the SAME physical rotation and the browser's split between
  // them is decided by sensor noise. Any three-angle representation has to pick one;
  // the delta quaternion does not have to pick, so both must come out identical.
  const upright = orientationToQuaternion(0, 90, 0);
  const viaAlpha = orientationDelta(upright, orientationToQuaternion(20, 90, 0));
  const viaGamma = orientationDelta(upright, orientationToQuaternion(0, 90, 20));
  check(
    'at the play pose alpha and gamma produce the same rotation',
    sameRotation(viaAlpha, viaGamma),
    `${rotationAngleDeg(viaAlpha).toFixed(3)} vs ${rotationAngleDeg(viaGamma).toFixed(3)} deg`,
  );

  // Crossing the alpha 0/360 seam must not produce a ~360 degree turn. There is no
  // wrap handling anywhere any more — quaternions simply have no seam.
  const wrapped = orientationDelta(
    orientationToQuaternion(355, 90, 0),
    orientationToQuaternion(5, 90, 0),
  );
  check(
    'crossing the 0/360 seam is a 10 degree turn',
    near(rotationAngleDeg(wrapped), 10, 1e-6),
    `${rotationAngleDeg(wrapped).toFixed(3)} deg`,
  );

  check(
    'the result is a unit quaternion',
    near(Math.hypot(wrapped.w, wrapped.x, wrapped.y, wrapped.z), 1, 1e-12),
  );
}

// ------------------------------------------------------------ gravity removal
console.log('\ngravity removal');
{
  const G = GRAVITY_MS2;

  // An accelerometer at rest reads the reaction force: +g along whichever device
  // axis points up. Flat on a table that is +z; upright it is +y.
  const flat = gravityInDeviceFrame(orientationToQuaternion(0, 0, 0));
  check(
    'flat on a table reads +g on z',
    near(flat.z, G, 1e-9) && near(flat.x, 0, 1e-9) && near(flat.y, 0, 1e-9),
    `(${flat.x.toFixed(3)}, ${flat.y.toFixed(3)}, ${flat.z.toFixed(3)})`,
  );

  const upright = gravityInDeviceFrame(orientationToQuaternion(0, 90, 0));
  check(
    'upright reads +g on y',
    near(upright.y, G, 1e-6) && near(upright.z, 0, 1e-6),
    `(${upright.x.toFixed(3)}, ${upright.y.toFixed(3)}, ${upright.z.toFixed(3)})`,
  );

  // Magnitude is invariant: it is the same gravity whichever way the phone is held.
  for (const [a, b, g] of [
    [0, 0, 0],
    [45, 30, -20],
    [200, 90, 0],
    [10, -60, 75],
  ]) {
    const v = gravityInDeviceFrame(orientationToQuaternion(a, b, g));
    check(`gravity magnitude holds at ${a}/${b}/${g}`, near(magnitude(v), G, 1e-9));
  }

  // Rotating a stationary phone fast must measure as ~zero, the GravityFilter lag this replaced.
  // doc-ref 0d59 docs/4-systems/Verification.md
  const filter = new GravityFilter(0.8);
  let filterWorst = 0;
  let exactWorst = 0;
  for (let i = 0; i <= 12; i++) {
    const q = orientationToQuaternion(0, i * 15, 0);
    const reading = gravityInDeviceFrame(q); // at rest: gravity and nothing else

    filterWorst = Math.max(filterWorst, magnitude(filter.update(reading, i * 16)));

    const exact = gravityInDeviceFrame(q);
    exactWorst = Math.max(
      exactWorst,
      magnitude({
        x: reading.x - exact.x,
        y: reading.y - exact.y,
        z: reading.z - exact.z,
      }),
    );
  }
  check(
    'exact removal reports no motion while merely turning',
    exactWorst < 1e-9,
    `${exactWorst.toExponential(2)} m/s²`,
  );

  // This is the number that was compressing every throw into the top of the power
  // window: a large offset present in every gesture, proportional to how fast the
  // wrist turned rather than to how hard the stone was thrown.
  check(
    'the filter fabricates motion while merely turning',
    filterWorst > 5,
    `${filterWorst.toFixed(2)} m/s² of phantom acceleration`,
  );
}

console.log('\nreadLinearAcceleration');
{
  check(
    'reads the hardware value when present',
    readLinearAcceleration({ acceleration: { x: 1, y: 2, z: 3 } })?.y === 2,
  );
  check('absent on iOS gives null', readLinearAcceleration({}) === null);
  check(
    'all-null axes give null',
    readLinearAcceleration({ acceleration: { x: null, y: null, z: null } }) === null,
  );
  check(
    'a partial reading is still usable',
    readLinearAcceleration({ acceleration: { x: 1, y: null, z: null } })?.x === 1,
  );
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

  // A clock that never advances, for the fixed-pose delta assertions below: with no
  // rotationRate accompanying these direct _onOrientation pokes, the calState stays
  // Settling throughout, and a real clock's nonzero (if tiny) dt between calls would
  // let the continuous tracking nlerp the reference a hair toward each new pose —
  // fine on a real device, but it would turn these single-shot "is the delta exact"
  // assertions into a race against however fast the test happens to run.
  const frozenClock = () => 0;

  {
    const m = new MotionSource({ now: frozenClock });
    const s = drive(m, 12, 34, 56);
    check(
      'uncalibrated reports no deflection',
      sameRotation(s.attitude, IDENTITY, 1e-9),
      `${s.attitude.w}/${s.attitude.x}/${s.attitude.y}/${s.attitude.z}`,
    );
  }

  {
    // Flat, screen up — the pose the game is described in. recentre() here must make
    // this exact pose mean "flat stone, facing forward", i.e. identity, exactly as
    // the old tap-triggered calibrate() did.
    const m = new MotionSource({ now: frozenClock });
    m._onOrientation({ alpha: 0, beta: 0, gamma: 0 });
    m._onMotion(motionEvent());
    m.recentre();

    check('the recentre pose itself is identity', sameRotation(drive(m, 0, 0, 0).attitude, IDENTITY));

    check(
      'pitching the top edge up 10 degrees is 10 degrees about device x',
      sameRotation(drive(m, 0, 10, 0).attitude, aboutX(10)),
    );

    check(
      'rolling the phone 10 degrees is 10 degrees about device y',
      sameRotation(drive(m, 0, 0, 10).attitude, aboutY(10)),
    );
  }

  {
    // Gripped upright — beta 90 is "phone facing you", and precisely where the Euler
    // version came apart. Recentring here has to work exactly as well as recentring
    // flat: the reference is whatever pose it is snapped to.
    const m = new MotionSource({ now: frozenClock });
    m._onOrientation({ alpha: 0, beta: 90, gamma: 0 });
    m._onMotion(motionEvent());
    m.recentre();

    check(
      'no movement from an upright grip reads as identity',
      sameRotation(drive(m, 0, 90, 0).attitude, IDENTITY),
    );

    check(
      'pitching 10 degrees forward of an upright grip is a 10 degree turn',
      sameRotation(drive(m, 0, 80, 0).attitude, aboutX(-10)),
    );
  }

  {
    // The reference is set explicitly, not the compass: an arbitrary heading nowhere
    // near north must behave the same as any other.
    const m = new MotionSource({ now: frozenClock });
    m._onOrientation({ alpha: 200, beta: 90, gamma: 0 });
    m._onMotion(motionEvent());
    m.recentre();

    const yawed = drive(m, 215, 90, 0);
    check(
      'aim is measured from the reference, not from north',
      near(rotationAngleDeg(yawed.attitude), 15, 1e-6),
      `${rotationAngleDeg(yawed.attitude).toFixed(3)} deg`,
    );
  }
}

// ---------------------------------------- MotionSource Settling/Aiming calibration
//
// D1: the tap-triggered snapshot is gone. The reference now tracks continuously
// while the phone is held still, and freezes on a sustained rotation-rate burst —
// the same threshold/hysteresis idiom ThrowTuning's swing detector already uses.
console.log('\nMotionSource calibration (Settling/Aiming)');
{
  const motionEvent = (rate = { alpha: 0, beta: 0, gamma: 0 }) => ({
    accelerationIncludingGravity: { x: 0, y: 0, z: 9.81 },
    rotationRate: rate,
  });

  // Advances a controllable clock by one 16ms frame and feeds a devicemotion sample
  // at the given rotation rate — no deviceorientation event, so the reference and the
  // debounce counters are driven purely by dt and rotationRate, the way a real device
  // reports gyro-only frames between orientation events.
  const driveMotion = (source, clockRef, rate) => {
    clockRef.t += 16;
    source._onMotion(motionEvent(rate));
    return source.latest;
  };

  const stillRate = { alpha: 0, beta: 0, gamma: 0 };
  const aimBurst = { alpha: 0, beta: CalibrationTuning.aimRateThresholdDegS + 40, gamma: 0 };

  {
    const clockRef = { t: 0 };
    const m = new MotionSource({ now: () => clockRef.t });
    for (let i = 0; i < 5; i++) driveMotion(m, clockRef, stillRate);
    check(
      'held still reads near-identity with no explicit call',
      sameRotation(m.latest.attitude, IDENTITY, 1e-6),
      `${rotationAngleDeg(m.latest.attitude).toFixed(4)} deg`,
    );
    check('starts in Settling', m.calState === 'settling');
  }

  {
    const transitions = [];
    const clockRef = { t: 0 };
    const m = new MotionSource({
      now: () => clockRef.t,
      onCalibrationStateChange: (state) => transitions.push(state),
    });

    // A sustained burst past the aim threshold, for aimSamples in a row, freezes the
    // reference and flips to Aiming.
    for (let i = 0; i < CalibrationTuning.aimSamples; i++) driveMotion(m, clockRef, aimBurst);
    check('a sustained burst past the aim threshold locks into Aiming', m.calState === 'aiming');
    check(
      'onCalibrationStateChange fires exactly once for the lock',
      transitions.length === 1 && transitions[0] === 'aiming',
      JSON.stringify(transitions),
    );

    // Continued tilting past the lock must not be re-absorbed into the reference —
    // the turn from the frozen reference has to keep growing.
    const lockedAngle = rotationAngleDeg(m.latest.attitude);
    for (let i = 0; i < 10; i++) driveMotion(m, clockRef, aimBurst);
    check(
      'continued tilting after the freeze is not re-absorbed',
      rotationAngleDeg(m.latest.attitude) > lockedAngle + 1,
      `${lockedAngle.toFixed(2)} -> ${rotationAngleDeg(m.latest.attitude).toFixed(2)} deg`,
    );

    // settleSamples of stillness re-enters Settling.
    for (let i = 0; i < CalibrationTuning.settleSamples; i++) driveMotion(m, clockRef, stillRate);
    check('settleSamples of stillness re-enters Settling', m.calState === 'settling');
    check(
      'onCalibrationStateChange fires exactly once per transition',
      transitions.length === 2 && transitions[1] === 'settling',
      JSON.stringify(transitions),
    );
  }

  {
    // recentre() is the direct replacement for the old tap-triggered calibrate() at
    // the two non-throw call sites (lobby preview aim, RE-CENTRE button): it snaps
    // the reference immediately and forces Settling, regardless of how far into
    // Aiming the state machine had gotten.
    const clockRef = { t: 0 };
    const m = new MotionSource({ now: () => clockRef.t });
    for (let i = 0; i < CalibrationTuning.aimSamples; i++) driveMotion(m, clockRef, aimBurst);
    check('locked into Aiming before recentre', m.calState === 'aiming');

    m.recentre();
    check('recentre forces Settling', m.calState === 'settling');

    const afterRecentre = driveMotion(m, clockRef, stillRate);
    check(
      'recentre snaps the reference to the attitude it was called at',
      sameRotation(afterRecentre.attitude, IDENTITY, 1e-6),
      `${rotationAngleDeg(afterRecentre.attitude).toFixed(4)} deg`,
    );
  }

  {
    // trackingEnabled = false is the lobby's shore-pointing mode (issue #23). Settling
    // tracking quietly pulls "zero" to wherever the phone rests, which is right before a
    // throw but in the lobby dragged the pointer back to centre while a player held it on
    // a rock. Turning the phone 30 deg and holding it still must keep the 30 deg there.
    const turnedAndHeld = (trackingEnabled) => {
      let clock = 0;
      const m = new MotionSource({ now: () => clock });
      m.trackingEnabled = trackingEnabled;
      m._onOrientation({ alpha: 0, beta: 90, gamma: 0 });
      m._onMotion(motionEvent());
      m.recentre();

      m._onOrientation({ alpha: 30, beta: 90, gamma: 0 });
      for (let i = 0; i < 90; i++) {
        clock += 16; // ~1.4 s held still, about eight tracking time constants
        m._onMotion(motionEvent(stillRate));
      }
      return rotationAngleDeg(m.latest.attitude);
    };

    const heldWithTracking = turnedAndHeld(true);
    const heldWithoutTracking = turnedAndHeld(false);
    check(
      'with tracking on, a held pose drifts back to centre (throw behaviour)',
      heldWithTracking < 1,
      `${heldWithTracking.toFixed(3)} deg`,
    );
    check(
      'with tracking off, a held pose keeps its aim (lobby pointing)',
      Math.abs(heldWithoutTracking - 30) < 0.5,
      `${heldWithoutTracking.toFixed(3)} deg`,
    );
  }

  {
    // Gyro carry-forward while Aiming. deviceorientation and devicemotion are
    // separate streams, so on a device where orientation lags, the old code
    // re-stamped a stale triple onto every sample — a staircase in the aim stream,
    // worst while the phone moves fastest. A frozen (Aiming) reference is what makes
    // the gyro-integrated delta from here exact rather than smeared by tracking.
    let clock = 0;
    const m = new MotionSource({ now: () => clock });
    m._onOrientation({ alpha: 0, beta: 90, gamma: 0 });
    m._onMotion(motionEvent());
    m.recentre();

    for (let i = 0; i < CalibrationTuning.aimSamples; i++) {
      clock += 16;
      m._onMotion(motionEvent(aimBurst));
    }
    check('locked into Aiming before the gyro-only swing', m.calState === 'aiming');

    const beforeSwing = m.latest.attitude;
    const orientationAgeAtLock = m.latest.orientationAgeMs;
    for (let i = 0; i < 6; i++) {
      clock += 16; // one frame of motion with no orientation event
      m._onMotion(motionEvent({ alpha: 0, beta: 0, gamma: 60 }));
    }
    check(
      'the attitude keeps moving on gyro alone once Aiming',
      rotationAngleDeg(orientationDelta(beforeSwing, m.latest.attitude)) > 1,
      `${rotationAngleDeg(orientationDelta(beforeSwing, m.latest.attitude)).toFixed(3)} deg`,
    );
    check(
      'staleness is reported and grows while no orientation event lands',
      m.latest.orientationAgeMs > orientationAgeAtLock,
      `${orientationAgeAtLock} -> ${m.latest.orientationAgeMs}`,
    );

    // A gap long enough to be a backgrounded tab must not be integrated across.
    const beforeGap = m.latest.attitude;
    clock += 5000;
    m._onMotion(motionEvent({ alpha: 0, beta: 0, gamma: 60 }));
    check(
      'a long gap is not integrated across',
      sameRotation(m.latest.attitude, beforeGap),
      `${rotationAngleDeg(orientationDelta(beforeGap, m.latest.attitude)).toFixed(3)} deg`,
    );
  }
}

// -------------------------------------------------- pure calibration math helpers
console.log('\nquaternionNlerp');
{
  const a = { w: 1, x: 0, y: 0, z: 0 };
  const b = aboutX(60);

  check('t=0 returns the start value', sameRotation(quaternionNlerp(a, b, 0), a));
  check('t=1 returns the end value', sameRotation(quaternionNlerp(a, b, 1), b));

  const half = quaternionNlerp(a, b, 0.5);
  check('t=0.5 lands strictly between the two turns', rotationAngleDeg(half) > 0 && rotationAngleDeg(half) < 60);
  check('the result is always a unit quaternion', near(magnitude({ x: half.x, y: half.y, z: half.z }) ** 2 + half.w ** 2, 1, 1e-9));

  // -b describes the same rotation as b, so lerping toward it must take the same
  // short path as lerping toward b — a naive lerp without the sign flip takes the
  // long way around and briefly overshoots through a near-180-degree turn instead.
  const negB = { w: -b.w, x: -b.x, y: -b.y, z: -b.z };
  check(
    'takes the shortest path regardless of the sign of the target',
    sameRotation(quaternionNlerp(a, b, 0.5), quaternionNlerp(a, negB, 0.5)),
  );
}

console.log('\nrotationRateMagnitude');
{
  check('a still phone reports zero', rotationRateMagnitude({ alpha: 0, beta: 0, gamma: 0 }) === 0);
  check(
    'combines all three axes',
    near(rotationRateMagnitude({ alpha: 3, beta: 4, gamma: 0 }), 5),
  );
  check('a missing rate reads as zero', rotationRateMagnitude(null) === 0);
  check('missing axes default to zero', near(rotationRateMagnitude({ alpha: 3 }), 3));
}

console.log('\ntiltAngleDeg');
{
  const level = tiltAngleDeg(IDENTITY);
  check('level reads zero on both axes', near(level.pitchDeg, 0) && near(level.rollDeg, 0));

  const pitched = tiltAngleDeg(aboutX(20));
  check(
    'pitching about device x reads on pitchDeg, not rollDeg',
    near(Math.abs(pitched.pitchDeg), 20, 1e-3) && near(pitched.rollDeg, 0, 1e-3),
    `${pitched.pitchDeg.toFixed(2)} / ${pitched.rollDeg.toFixed(2)}`,
  );

  const rolled = tiltAngleDeg(aboutY(20));
  check(
    'rolling about device y reads on rollDeg, not pitchDeg',
    near(Math.abs(rolled.rollDeg), 20, 1e-3) && near(rolled.pitchDeg, 0, 1e-3),
    `${rolled.pitchDeg.toFixed(2)} / ${rolled.rollDeg.toFixed(2)}`,
  );

  check('a null attitude is treated as level', near(tiltAngleDeg(null).pitchDeg, 0));
}

// ---------------------------------------------------------------- rotation
console.log('\nrotateXY');
const r90 = rotateXY(1, 0, 90);
check(
  '90deg maps +X to +Y',
  near(r90.x, 0) && near(r90.y, 1),
  `(${r90.x.toFixed(3)}, ${r90.y.toFixed(3)})`,
);
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
check(
  'screenSpinRate reads the twist about the screen normal',
  screenSpinRate({ alpha: 300, beta: 0, gamma: 0 }) === 300,
);
check(
  'screenSpinRate keeps the direction of the twist',
  screenSpinRate({ alpha: -300, beta: 0, gamma: 0 }) === -300,
);

// The bug this replaced. Swinging your arm turns the phone about its other two axes far
// harder than any wrist twist, so a 3-axis magnitude reported arm motion as spin.
check('swinging the arm is not spin', screenSpinRate({ alpha: 0, beta: 900, gamma: 700 }) === 0);
check('screenSpinRate treats a missing rate as still', screenSpinRate(null) === 0);
check('screenSpinRate tolerates null axes', screenSpinRate({ alpha: null }) === 0);

// ---------------------------------------------------------------- gravity
console.log('\nGravityFilter');
{
  const filter = new GravityFilter(0.8);
  const first = filter.update({ x: 0, y: 0, z: 9.8 });
  check(
    'seeds on first sample instead of spiking',
    near(first.z, 0),
    `linear.z was ${first.z.toFixed(3)}`,
  );

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
  buffer.push(1);
  buffer.push(2);
  check('partial fill returns in order', JSON.stringify(buffer.toArray()) === '[1,2]');

  buffer.push(3);
  buffer.push(4);
  buffer.push(5);
  check(
    'keeps only the newest N',
    JSON.stringify(buffer.toArray()) === '[3,4,5]',
    JSON.stringify(buffer.toArray()),
  );
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
  check(
    'ignores event.acceleration even when present',
    fromAndroid.x === 1 && fromAndroid.y === 2 && fromAndroid.z === 3,
    `got ${JSON.stringify(fromAndroid)}`,
  );

  const iosStyle = { accelerationIncludingGravity: { x: 1, y: 2, z: 3 } };
  check(
    'works with no .acceleration property at all (iOS shape)',
    readAcceleration(iosStyle)?.x === 1,
  );

  check('null when the event carries nothing', readAcceleration({}) === null);
  check(
    'null when all axes are null',
    readAcceleration({ accelerationIncludingGravity: { x: null, y: null, z: null } }) === null,
  );

  const partial = readAcceleration({ accelerationIncludingGravity: { x: 5, y: null, z: null } });
  check(
    'missing axes default to 0 rather than NaN',
    partial.x === 5 && partial.y === 0 && partial.z === 0,
  );

  const noRate = readRotationRate({});
  check('rotationRate defaults to zeros', noRate.alpha === 0 && noRate.beta === 0);
}

console.log(
  failures === 0
    ? '\nPASS — all controller math checks green'
    : `\nFAIL — ${failures} check(s) failed`,
);
process.exit(failures === 0 ? 0 : 1);
