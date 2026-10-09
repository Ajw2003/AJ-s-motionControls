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
import { GamepadState, makePadMessage } from './gamepad-state.js';
import { LAYOUTS, DEFAULT_LAYOUT } from './layouts.js';
import { mountGamepad } from './gamepad-view.js';

const RECONNECT_DELAY_MS = 1000;
const READOUT_INTERVAL_MS = 250;
// Changes are sent the moment they happen; this repeat only refreshes a PC that missed one.
const PAD_REPEAT_MS = 250;
const LAYOUT_STORAGE_KEY = 'ajmc.layout';

const el = (id) => document.getElementById(id);
const startButton = el('start');

let socket = null;
let seq = 0;
let sent = 0;
let latest = null;
const rate = new RateMeter();
const gamepad = new GamepadState();
let padSeq = 0;
let slotNumber = null;
let layoutId = loadLayoutChoice();
let unmountGamepad = null;

function setStatus(text, kind) {
  const status = el('status');
  status.textContent = text;
  status.className = kind || '';
  el('chip-status').textContent =
    slotNumber && kind === 'ok' ? `Slot ${slotNumber} · connected` : text;
  el('chip-status').className = kind || '';
}

// A remembered layout is a convenience: storage can be missing or throw (private mode).
function loadLayoutChoice() {
  try {
    const saved = localStorage.getItem(LAYOUT_STORAGE_KEY);
    if (saved && LAYOUTS[saved]) return saved;
  } catch {
    // no storage: fall back to the default below
  }
  return DEFAULT_LAYOUT;
}

function saveLayoutChoice(id) {
  try {
    localStorage.setItem(LAYOUT_STORAGE_KEY, id);
  } catch {
    // not saved; the choice still applies for this visit
  }
}

function renderLayoutPicker() {
  const list = el('layouts');
  list.replaceChildren();
  for (const [id, layout] of Object.entries(LAYOUTS)) {
    const choice = document.createElement('button');
    choice.type = 'button';
    choice.className = 'layout-choice';
    choice.dataset.layout = id;
    choice.setAttribute('aria-pressed', String(id === layoutId));
    choice.innerHTML = `<strong></strong><span></span>`;
    choice.querySelector('strong').textContent = layout.name;
    choice.querySelector('span').textContent = layout.hint;
    choice.addEventListener('click', () => {
      layoutId = id;
      saveLayoutChoice(id);
      renderLayoutPicker();
    });
    list.appendChild(choice);
  }
}

function sendPad() {
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify(makePadMessage(gamepad, padSeq++)));
  }
}

function showGamepad() {
  if (unmountGamepad) unmountGamepad();
  unmountGamepad = mountGamepad(el('controls'), LAYOUTS[layoutId], gamepad, sendPad);
  document.body.classList.add('playing');
}

renderLayoutPicker();

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
      slotNumber = message.slot + 1;
      el('slot').textContent = String(slotNumber);
      setStatus('Connected', 'ok');
      sendPad(); // the PC starts from this phone's real state, not "nothing pressed"
    } else if (message.type === 'PAD_REJECTED') {
      setStatus(message.reason, 'bad');
    }
  });

  socket.addEventListener('close', () => {
    slotNumber = null;
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
  if (!socket) {
    window.addEventListener('devicemotion', onMotion);
    connect();
    setInterval(renderReadout, READOUT_INTERVAL_MS);
    setInterval(sendPad, PAD_REPEAT_MS);
  }
  startButton.textContent = 'Back to the controller';
  showGamepad();
});

el('chip').addEventListener('click', () => {
  // Back to the layout picker. Motion keeps streaming; the pad releases everything.
  if (unmountGamepad) unmountGamepad();
  unmountGamepad = null;
  document.body.classList.remove('playing');
});
