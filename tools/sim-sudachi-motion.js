// Replays a DSU capture (tools/dsu-capture.js) through a port of the Yuzu-family motion
// maths that Sudachi uses, and reports how far the heading ends from where it started.
// Run three ways so the causes separate:
//   arrival   - integration time = gap between packet arrivals (what Sudachi does)
//   phone     - integration time = gap between the phone's own sample timestamps
//   phone-raw - phone timing and no 0.007 turn/s dead zone (the emulator's floor)
//
// Usage: node tools/sim-sudachi-motion.js capture.jsonl
// Ported from Eden (a Yuzu fork, like Sudachi), read 2026-10-10:
//   src/input_common/drivers/udp_client.cpp       DSU -> BasicMotion
//   src/hid_core/frontend/motion_input.cpp        MotionInput (Mahony AHRS)
//   src/hid_core/frontend/emulated_controller.cpp SetMotion call order

const fs = require('fs');

const ThresholdStandard = 0.007;
const IsAtRestRelaxed = 0.05;
const GyroMax = 5;
const AccelMax = 7;
const v3 = (x = 0, y = 0, z = 0) => [x, y, z];
const add = (a, b) => a.map((v, i) => v + b[i]);
const sub = (a, b) => a.map((v, i) => v - b[i]);
const mul = (a, k) => a.map((v) => v * k);
const len = (a) => Math.hypot(...a);
const norm = (a) => {
  const l = len(a);
  return l ? mul(a, 1 / l) : a;
};
const clamp = (v, m) => Math.max(-m, Math.min(m, v));

class MotionInput {
  constructor(threshold) {
    this.kp = 0.3;
    this.ki = 0.005;
    this.kd = 0;
    this.threshold = threshold;
    this.quat = [0, 0, -1, 0]; // x, y, z, w as stored by yuzu (quat[3] is w)
    this.accel = v3();
    this.gyro = v3();
    this.bias = v3();
    this.realError = v3();
    this.integralError = v3();
    this.derivativeError = v3();
    this.onlyAccel = true;
    this.resetCounter = 0;
  }
  isMoving(s) {
    return len(this.gyro) >= s || len(this.accel) <= 0.9 || len(this.accel) >= 1.1;
  }
  setAcceleration(a) {
    this.accel = a.map((v) => clamp(v, AccelMax));
  }
  setGyroscope(g) {
    this.gyro = sub(g, this.bias).map((v) => clamp(v, GyroMax));
    if (!this.isMoving(IsAtRestRelaxed)) this.bias = add(mul(this.bias, 0.9999), mul(g, 0.0001));
    if (len(this.gyro) < this.threshold) this.gyro = v3();
    else this.onlyAccel = false;
  }
  resetOrientation() {
    if (this.onlyAccel) return;
    if (!this.isMoving(IsAtRestRelaxed) && this.accel[2] <= -0.9) {
      if (++this.resetCounter > 900) {
        this.quat = [0, 0, -1, 0];
        this.integralError = v3();
        this.resetCounter = 0;
        this.resets = (this.resets || 0) + 1;
      }
    } else this.resetCounter = 0;
  }
  updateOrientation(elapsedUs) {
    if (len(this.realError) >= 0.1) this.resetOrientation();
    let [q2, q3, q4, q1] = this.quat;
    const dt = elapsedUs / 1e6;
    if (dt > 0.1 || dt <= 0) return;
    const na = norm(this.accel);
    let g = mul(this.gyro, Math.PI * 2);
    g = [g[1], -g[0], -g[2]];
    if (this.onlyAccel) g = v3();
    const al = len(this.accel);
    if (al >= 0.75 && al <= 1.25) {
      const ax = -na[0];
      const ay = na[1];
      const az = -na[2];
      const vx = 2 * (q2 * q4 - q1 * q3);
      const vy = 2 * (q1 * q2 + q3 * q4);
      const vz = q1 * q1 - q2 * q2 - q3 * q3 + q4 * q4;
      const err = [az * vx - ax * vz, ay * vz - az * vy, ax * vy - ay * vx];
      this.derivativeError = sub(err, this.realError);
      this.realError = err;
      if (this.ki !== 0 && len(this.realError) >= 0.05) this.integralError = add(this.integralError, err);
      else this.integralError = v3();
      if (!this.onlyAccel) {
        g = add(g, mul(this.realError, this.kp));
        g = add(g, mul(this.integralError, this.ki));
        g = add(g, mul(this.derivativeError, this.kd));
      }
    }
    const gx = g[1];
    const gy = g[0];
    const gz = g[2];
    const pa = q2;
    const pb = q3;
    const pc = q4;
    q1 = q1 + (-q2 * gx - q3 * gy - q4 * gz) * (0.5 * dt);
    q2 = pa + (q1 * gx + pb * gz - pc * gy) * (0.5 * dt);
    q3 = pb + (q1 * gy - pa * gz + pc * gx) * (0.5 * dt);
    q4 = pc + (q1 * gz + pa * gy - pb * gx) * (0.5 * dt);
    const q = norm([q2, q3, q4, q1]);
    this.quat = q;
  }
  yawDeg() {
    const [x, y, z, w] = this.quat;
    return (Math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z)) * 180) / Math.PI;
  }
}

function run(samples, timing, threshold) {
  const m = new MotionInput(threshold);
  const yaws = [];
  for (let i = 1; i < samples.length; i++) {
    const s = samples[i];
    const p = samples[i - 1];
    const dt = timing === 'arrival' ? s.arrivalUs - p.arrivalUs : s.timestampUs - p.timestampUs;
    // DSU -> BasicMotion, exactly as udp_client.cpp: gyro is [pitch, yaw, roll] in deg/s.
    const scale = 1 / 312;
    m.setAcceleration([s.accel[0], -s.accel[2], s.accel[1]]);
    m.setGyroscope([s.gyro[0] * scale, s.gyro[2] * scale, -s.gyro[1] * scale]);
    m.updateOrientation(dt);
    yaws.push(m.yawDeg());
  }
  return { yaws, resets: m.resets || 0 };
}

const wrap = (d) => ((((d + 180) % 360) + 360) % 360) - 180;

if (require.main === module) {
  const file = process.argv[2];
  const samples = fs
    .readFileSync(file, 'utf8')
    .split('\n')
    .filter(Boolean)
    .map((l) => JSON.parse(l))
    .filter((s) => s.slot === 0);
  const gaps = samples.slice(1).map((s, i) => (s.arrivalUs - samples[i].arrivalUs) / 1000);
  const sorted = [...gaps].sort((a, b) => a - b);
  const pct = (p) => sorted[Math.floor((sorted.length - 1) * p)].toFixed(1);
  console.log(`${samples.length} packets over ${((samples.at(-1).arrivalUs - samples[0].arrivalUs) / 1e6).toFixed(1)} s`);
  console.log(`arrival gap ms: median ${pct(0.5)}, 95% ${pct(0.95)}, 99% ${pct(0.99)}, max ${pct(1)}`);
  // Heading after the first 2 s (settled) against the last second (phone back where it began).
  const settle = 120;
  for (const [name, timing, threshold] of [
    ['arrival  ', 'arrival', ThresholdStandard],
    ['phone    ', 'phone', ThresholdStandard],
    ['phone-raw', 'phone', 0],
  ]) {
    const { yaws, resets } = run(samples, timing, threshold);
    const start = yaws[Math.min(settle, yaws.length - 1)];
    const end = yaws.at(-1);
    console.log(`${name}: heading start ${start.toFixed(1)} deg, end ${end.toFixed(1)} deg, drift ${wrap(end - start).toFixed(1)} deg, auto-resets ${resets}`);
  }
}

module.exports = { MotionInput, run };
