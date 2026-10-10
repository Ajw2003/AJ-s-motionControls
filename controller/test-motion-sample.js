/**
 * Unit tests for the raw MOTION message (issue #2). No browser, no device.
 *
 * Run with: npm test    (from controller/)
 */

import assert from 'node:assert/strict';
import { makeMotionMessage, RateMeter, MOTION_MESSAGE_TYPE, GravitySign } from './js/motion-sample.js';
import { GRAVITY_MS2 } from './js/sensors.js';

let failures = 0;
function check(label, fn) {
  try {
    fn();
    console.log(`  PASS  ${label}`);
  } catch (err) {
    failures++;
    console.log(`  FAIL  ${label} — ${err.message}`);
  }
}

console.log('motion-sample');

check('phone lying flat reads 1 g on z', () => {
  const m = makeMotionMessage(
    { x: 0, y: 0, z: GRAVITY_MS2 },
    { alpha: 0, beta: 0, gamma: 0 },
    1000,
    0,
  );
  assert.deepEqual(m.accel, [0, 0, 1]);
});

// W3C Device Orientation and Motion §6.3.2, Chromium's device_motion_event_pump.cc and WebKit's
// DeviceMotionClientIOS.mm all give alpha = x, beta = y, gamma = z (the older spec text said
// otherwise). aj's Android phone agreed against gravity, 2026-10-10 (#30).
check('rotation rates map alpha→x, beta→y, gamma→z, as browsers implement them', () => {
  const m = makeMotionMessage({ x: 0, y: 0, z: 0 }, { alpha: 10, beta: 20, gamma: 30 }, 0, 0);
  assert.deepEqual(m.gyro, [10, 20, 30]);
});

check('timestamp is whole microseconds', () => {
  const m = makeMotionMessage(
    { x: 0, y: 0, z: 0 },
    { alpha: 0, beta: 0, gamma: 0 },
    1712345678901.25,
    0,
  );
  assert.equal(m.ts, 1712345678901250);
  assert.ok(Number.isSafeInteger(m.ts));
});

check('carries type and seq', () => {
  const m = makeMotionMessage({ x: 1, y: 2, z: 3 }, { alpha: 0, beta: 0, gamma: 0 }, 0, 42);
  assert.equal(m.type, MOTION_MESSAGE_TYPE);
  assert.equal(m.seq, 42);
});

check('rounds to 4 places for g and 2 for deg/s, keeping messages small', () => {
  const m = makeMotionMessage(
    { x: 1.23456789, y: 0, z: 0 },
    { alpha: 12.34567, beta: 0, gamma: 0 },
    0,
    0,
  );
  assert.equal(m.accel[0], 0.1259);
  assert.equal(m.gyro[0], 12.35);
});

check('no acceleration reading means nothing to send', () => {
  assert.equal(makeMotionMessage(null, { alpha: 0, beta: 0, gamma: 0 }, 0, 0), null);
});

check('a realistic message stays well under the relay 4096-byte limit', () => {
  const m = makeMotionMessage(
    { x: -12.3456, y: 19.6133, z: -0.0001 },
    { alpha: -1999.99, beta: 1999.99, gamma: -0.01 },
    1712345678901.25,
    9_999_999,
  );
  assert.ok(JSON.stringify(m).length < 200, `${JSON.stringify(m).length} bytes`);
});

check('RateMeter reports about 60 Hz for a 60 Hz stream', () => {
  const meter = new RateMeter();
  for (let i = 0; i < 180; i++) meter.record(i * (1000 / 60));
  const rate = meter.ratePerSecond(179 * (1000 / 60));
  assert.ok(rate >= 59 && rate <= 61, `got ${rate}`);
});

check('RateMeter falls to 0 once samples stop', () => {
  const meter = new RateMeter();
  for (let i = 0; i < 60; i++) meter.record(i * 16);
  assert.equal(meter.ratePerSecond(5000), 0);
});

// iPhone Safari reports accelerationIncludingGravity mirrored (WebKit passes CoreMotion through;
// W3C public-geolocation, 2014). As in Rock Skipping, the orientation angles are the ground truth
// for "up"; the reading is flipped when it keeps pointing the other way (#34).
const G = GRAVITY_MS2;
check('gravity agreeing with orientation is left alone', () => {
  const s = new GravitySign();
  for (let i = 0; i < 60; i++) s.update({ x: 0, y: 0, z: G }, { x: 0, y: 0, z: G });
  assert.equal(s.sign, 1);
});

check('gravity opposite to orientation is flipped after a steady run (iPhone)', () => {
  const s = new GravitySign();
  for (let i = 0; i < 60; i++) s.update({ x: 0, y: 0, z: -G }, { x: 0, y: 0, z: G });
  assert.equal(s.sign, -1);
});

check('a few shaken or opposite readings do not flip it', () => {
  const s = new GravitySign();
  for (let i = 0; i < 60; i++) s.update({ x: 0, y: 0, z: G }, { x: 0, y: 0, z: G });
  for (let i = 0; i < 5; i++) s.update({ x: 0, y: 0, z: -G }, { x: 0, y: 0, z: G });
  for (let i = 0; i < 20; i++) s.update({ x: 3 * G, y: 0, z: -G }, { x: 0, y: 0, z: G }); // a hard shake
  assert.equal(s.sign, 1);
});

check('no orientation reading yet means no decision', () => {
  const s = new GravitySign();
  for (let i = 0; i < 60; i++) s.update({ x: 0, y: 0, z: -G }, null);
  assert.equal(s.sign, 1);
});

if (failures) {
  console.log(`\nFAIL — ${failures} motion-sample check(s) failed`);
  process.exit(1);
}
console.log('\nPASS — all motion-sample checks green');
