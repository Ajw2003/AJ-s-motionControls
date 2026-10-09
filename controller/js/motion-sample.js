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
  // The spec names rotation rates for the Euler angle each drives: beta turns about x,
  // gamma about y, alpha about z. Mapping them by name order instead is the classic slip.
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
      roundTo(rotationRate.beta, 2),
      roundTo(rotationRate.gamma, 2),
      roundTo(rotationRate.alpha, 2),
    ],
  };
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
