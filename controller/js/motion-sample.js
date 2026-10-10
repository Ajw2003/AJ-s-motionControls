/**
 * Raw motion samples for emulator input (issue #2).
 *
 * Rock Skipping sends a processed attitude at 20 Hz; emulators need the raw readings
 * at the phone's own rate. This module turns one DeviceMotionEvent's readings into
 * the wire message, and nothing else, so it can be tested in Node with no device.
 *
 * Units on the wire are the ones the DSU protocol uses, so the PC never converts:
 * acceleration in g (gravity included), rotation rate in deg/s, timestamp in whole
 * microseconds. Axes are the device's own: x to the right of the screen, y up the
 * screen, z out of the screen.
 */

import { GRAVITY_MS2 } from './sensors.js';

export const MOTION_MESSAGE_TYPE = 'MOTION';

function roundTo(value, places) {
  const scale = 10 ** places;
  return Math.round(value * scale) / scale;
}

/**
 * Build one MOTION message.
 *
 * @param {{x:number,y:number,z:number}} accelerationMs2 from readAcceleration (m/s², gravity included)
 * @param {{alpha:number,beta:number,gamma:number}} rotationRate from readRotationRate (deg/s)
 * @param {number} timestampMs absolute time of the reading in ms (performance.timeOrigin + event.timeStamp)
 * @param {number} seq increasing per-phone counter, so the PC can see drops
 * @returns {object|null} null when there is no acceleration reading to send
 */
export function makeMotionMessage(accelerationMs2, rotationRate, timestampMs, seq) {
  if (!accelerationMs2) return null;
  // Browsers give alpha = x, beta = y, gamma = z rotation rates (right-hand rule), as the
  // current W3C spec (§6.3.2), Chromium and WebKit all do. The older spec text (beta about x,
  // gamma about y, alpha about z) is wrong for every browser; trusting it crossed the axes (#30).
  return {
    type: MOTION_MESSAGE_TYPE,
    seq,
    ts: Math.round(timestampMs * 1000),
    accel: [
      roundTo(accelerationMs2.x / GRAVITY_MS2, 4),
      roundTo(accelerationMs2.y / GRAVITY_MS2, 4),
      roundTo(accelerationMs2.z / GRAVITY_MS2, 4),
    ],
    gyro: [
      roundTo(rotationRate.alpha, 2),
      roundTo(rotationRate.beta, 2),
      roundTo(rotationRate.gamma, 2),
    ],
  };
}

/**
 * Which way up `accelerationIncludingGravity` is on this browser (#34): +1 as the spec and
 * Android give it, -1 when mirrored, as Safari on iPhone gives it (WebKit passes CoreMotion
 * through; W3C public-geolocation, 2014). As in Rock Skipping, the orientation angles are the
 * ground truth for "up" (`gravityInDeviceFrame`, sensors.js), so no browser is named here.
 * Only near-still readings vote, and the decision needs a steady run, so a shake cannot flip it.
 */
export class GravitySign {
  constructor() {
    this.sign = 1;
    this._votes = 0;
  }

  /**
   * @param {{x:number,y:number,z:number}} accel raw accelerationIncludingGravity, m/s²
   * @param {{x:number,y:number,z:number}|null} up gravity in the device frame from the
   *   orientation angles, m/s² (null before the first orientation reading)
   */
  update(accel, up) {
    if (!up) return this.sign;
    const a = Math.hypot(accel.x, accel.y, accel.z);
    const u = Math.hypot(up.x, up.y, up.z);
    if (u === 0 || a < 0.8 * GRAVITY_MS2 || a > 1.2 * GRAVITY_MS2) return this.sign;
    const cos = (accel.x * up.x + accel.y * up.y + accel.z * up.z) / (a * u);
    if (cos > 0.8) this._votes = Math.min(this._votes + 1, 30);
    else if (cos < -0.8) this._votes = Math.max(this._votes - 1, -30);
    if (this._votes <= -20) this.sign = -1;
    else if (this._votes >= 20) this.sign = 1;
    return this.sign;
  }
}

/**
 * Samples per second over a sliding window, for the on-phone readout.
 */
export class RateMeter {
  constructor(windowMs = 1000) {
    this.windowMs = windowMs;
    this._times = [];
  }

  record(nowMs) {
    this._times.push(nowMs);
    this._trim(nowMs);
  }

  ratePerSecond(nowMs) {
    this._trim(nowMs);
    return (this._times.length * 1000) / this.windowMs;
  }

  _trim(nowMs) {
    const cutoff = nowMs - this.windowMs;
    let drop = 0;
    while (drop < this._times.length && this._times[drop] <= cutoff) drop++;
    if (drop > 0) this._times.splice(0, drop);
  }
}
