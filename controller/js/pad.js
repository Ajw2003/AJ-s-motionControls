/**
 * The controller page (issue #2): streams raw motion to the PC over the `/pad` socket.
 * The on-screen gamepad (#4) builds on this page.
 */

import {
  requestMotionPermission,
  isSecureContextOk,
  readAcceleration,
  readRotationRate,
} from './sensors.js';
import { makeMotionMessage, RateMeter } from './motion-sample.js';

const RECONNECT_DELAY_MS = 1000;
const READOUT_INTERVAL_MS = 250;

const el = (id) => document.getElementById(id);
const startButton = el('start');

let socket = null;
let seq = 0;
let sent = 0;
let latest = null;
const rate = new RateMeter();

function setStatus(text, kind) {
  const status = el('status');
  status.textContent = text;
  status.className = kind || '';
}

function connect() {
  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  socket = new WebSocket(`${scheme}://${location.host}/pad`);
  setStatus('Connecting…');

  socket.addEventListener('message', (event) => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch {
      return; // the PC only sends JSON; anything else is noise
    }
    if (message.type === 'PAD_ACCEPTED') {
      el('slot').textContent = String(message.slot + 1);
      setStatus('Connected', 'ok');
    } else if (message.type === 'PAD_REJECTED') {
      setStatus(message.reason, 'bad');
    }
  });

  socket.addEventListener('close', () => {
    el('slot').textContent = '–';
    setStatus('Disconnected — retrying', 'bad');
    setTimeout(connect, RECONNECT_DELAY_MS);
  });
}

function onMotion(event) {
  const message = makeMotionMessage(
    readAcceleration(event),
    readRotationRate(event),
    performance.timeOrigin + event.timeStamp,
    seq,
  );
  if (!message) return;
  seq++;
  latest = message;
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify(message));
    sent++;
    rate.record(performance.now());
  }
}

function renderReadout() {
  el('rate').textContent = rate.ratePerSecond(performance.now()).toFixed(0);
  el('sent').textContent = String(sent);
  if (latest) {
    el('accel').textContent = latest.accel.map((n) => n.toFixed(2)).join('  ');
    el('gyro').textContent = latest.gyro.map((n) => n.toFixed(0)).join('  ');
  }
}

startButton.addEventListener('click', async () => {
  if (!isSecureContextOk()) {
    setStatus('Needs HTTPS for motion sensors', 'bad');
    return;
  }
  // Never rejects: failures come back as { granted: false, error }.
  const permission = await requestMotionPermission();
  if (!permission.granted) {
    setStatus(
      permission.error
        ? `Motion permission failed: ${permission.error}`
        : 'Motion permission denied',
      'bad',
    );
    return;
  }
  startButton.hidden = true;
  window.addEventListener('devicemotion', onMotion);
  connect();
  setInterval(renderReadout, READOUT_INTERVAL_MS);
});
