/**
 * Controller app wiring: network + sensors + gesture + UI.
 *
 * TEST STATUS: verified on Android (Chrome). iOS paths are written from spec and
 * tagged `UNVERIFIED (iOS)` — see PLAN.md "Test device policy".
 */

import { MotionSource, requestMotionPermission, isSecureContextOk } from './sensors.js';
import { ThrowController, ThrowState } from './throw.js';
import { RelayConnection, ConnectionStatus } from './net.js';
import { PeerLink, Transport } from './peer.js';

const el = (id) => document.getElementById(id);

const ui = {
  status: el('status'),
  joinScreen: el('joinScreen'),
  waitScreen: el('waitScreen'),
  throwScreen: el('throwScreen'),
  joinError: el('joinError'),
  nameInput: el('nameInput'),
  codeInput: el('codeInput'),
  joinBtn: el('joinBtn'),
  gripBtn: el('gripBtn'),
  waitTitle: el('waitTitle'),
  waitDetail: el('waitDetail'),
  scores: el('scores'),
  throwResult: el('throwResult'),
  debug: el('debug'),
  debugToggle: el('debugToggle'),
};

// Tunable from the URL (?gravityK=0.92) so the filter can be dialled in on a real
// phone without a redeploy. k controls how fast the gravity estimate adapts: too
// low and a slow swing leaks into the estimate and gets subtracted back out,
// flattening peak power; too high and it lags when the phone is re-oriented.
// The right value depends on the device's real sample rate — tune on hardware.
const GRAVITY_K = Number(new URLSearchParams(location.search).get('gravityK')) || 0.8;

const motion = new MotionSource({ gravityK: GRAVITY_K });
let connection = null;
let peer = null;
let lastThrow = null;
let myPlayerId = '';

// ?direct=0 forces everything through the relay. Worth keeping: it is the only way to
// compare the two transports on the same phone and network, and to reproduce a
// relay-only session when a peer connection is suspected of misbehaving.
const PEER_ENABLED = new URLSearchParams(location.search).get('direct') !== '0';

/** Fastest transport available. Falls back to the relay on its own. */
function sendInput(payload) {
  if (peer) return peer.send(payload);
  connection?.send(payload);
  return Transport.RELAY;
}

function showScreen(name) {
  for (const screen of [ui.joinScreen, ui.waitScreen, ui.throwScreen]) {
    screen.classList.remove('active');
  }
  ({ join: ui.joinScreen, wait: ui.waitScreen, throw: ui.throwScreen })[name].classList.add('active');
}

function setStatus(text, kind = '') {
  ui.status.textContent = text;
  ui.status.className = kind;
}

// ---------------------------------------------------------------- gesture

const thrower = new ThrowController(motion, {
  onAim: (payload) => sendInput(payload),
  onThrow: (payload) => {
    sendInput(payload);
    lastThrow = payload;
    ui.throwResult.textContent =
      `Power ${(payload.power * 100).toFixed(0)}% · Timing ${(payload.timingQuality * 100).toFixed(0)}%` +
      (payload.simulated ? ' · simulated (no motion sensors)' : '');
  },
  onStateChange: (state) => {
    ui.gripBtn.classList.toggle('held', state === ThrowState.GRIPPED);
    ui.gripBtn.textContent = state === ThrowState.GRIPPED ? 'SWING!' : 'HOLD';
  },
});

function bindGrip() {
  const start = (event) => {
    event.preventDefault(); // stop the tap turning into a scroll or a zoom
    thrower.grip();
  };
  const end = (event) => {
    event.preventDefault();
    const descriptor = thrower.release();
    // Android only — iOS support for navigator.vibrate is unreliable, so this is
    // a bonus rather than something the feel depends on.
    if (descriptor && navigator.vibrate) navigator.vibrate(30);
  };

  ui.gripBtn.addEventListener('touchstart', start, { passive: false });
  ui.gripBtn.addEventListener('touchend', end, { passive: false });
  ui.gripBtn.addEventListener('touchcancel', () => thrower.cancel());

  // Mouse fallback so the gesture can be exercised on a desktop browser.
  ui.gripBtn.addEventListener('mousedown', start);
  ui.gripBtn.addEventListener('mouseup', end);
}

// ---------------------------------------------------------------- messages

function handleMessage(message) {
  // Signalling is consumed here and never reaches the switch — the peer link owns it.
  if (peer && peer.handleSignal(message)) return;

  switch (message.type) {
    case 'JOINED':
      myPlayerId = message.playerId;
      setStatus(`Connected as ${myPlayerId}`, 'ok');
      showScreen('wait');
      motion.start();
      requestWakeLock();

      // Only after JOINED: the relay routes by room membership, so an offer sent any
      // earlier has nowhere to go. Failure here is silent by design — the throw path
      // already works over the relay, and this is only ever an upgrade.
      if (PEER_ENABLED && peer) peer.start().catch(() => {});
      break;

    case 'ERROR':
      ui.joinError.textContent = message.message ?? 'Something went wrong.';
      showScreen('join');
      ui.joinBtn.disabled = false;
      break;

    case 'TURN':
      if (message.yourTurn) {
        ui.throwResult.textContent = '';
        showScreen('throw');
      } else {
        thrower.cancel();
        showScreen('wait');
      }
      break;

    case 'SPECTATE':
      renderSpectate(message);
      break;

    case 'ROOM_CLOSED':
      setStatus('Game ended', 'bad');
      thrower.cancel();
      peer?.close();
      showScreen('join');
      ui.joinBtn.disabled = false;
      break;
  }
}

/** Highest scorer, or null when nobody leads outright. */
function leader(scores) {
  const entries = Object.entries(scores ?? {});
  if (entries.length === 0) return null;

  entries.sort((a, b) => b[1] - a[1]);
  if (entries.length > 1 && entries[0][1] === entries[1][1]) return null; // tied
  return entries[0][0];
}

function renderSpectate(message) {
  if (thrower.state === ThrowState.GRIPPED) return; // don't yank the screen mid-throw

  // Without the phase, every idle moment reads "Waiting for the next turn" — including
  // the lobby before anything has started and the results screen after it has ended.
  const phase = message.phase ?? 'PLAYING';

  if (phase === 'LOBBY') {
    ui.waitTitle.textContent = 'Waiting for the game to start';
    ui.waitDetail.textContent = 'You are in. Watch the TV.';
  } else if (phase === 'RESULTS') {
    const winner = leader(message.scores);
    ui.waitTitle.textContent = 'Final scores';
    ui.waitDetail.textContent = winner
      ? (winner === myPlayerId ? 'You win!' : `${winner} wins`)
      : 'It is a tie.';
  } else {
    ui.waitTitle.textContent = message.activePlayer
      ? `${message.activePlayer} is throwing`
      : 'Waiting for the next turn';

    const parts = [];
    if (message.round && message.totalRounds) {
      parts.push(`Round ${message.round}/${message.totalRounds}`);
    }
    if (message.rockName) parts.push(message.rockName);
    if (typeof message.liveSkips === 'number') parts.push(`${message.liveSkips} skips`);
    if (typeof message.liveDistance === 'number') parts.push(`${message.liveDistance.toFixed(1)} m`);
    ui.waitDetail.textContent = parts.join(' · ') || 'Watch the TV.';
  }

  ui.scores.innerHTML = '';
  for (const [player, score] of Object.entries(message.scores ?? {})) {
    const row = document.createElement('div');
    row.className = 'score-row' + (player === message.activePlayer ? ' active' : '');
    row.innerHTML = `<span>${player}</span><span>${score}</span>`;
    ui.scores.appendChild(row);
  }
}

// ---------------------------------------------------------------- join

async function join() {
  const name = ui.nameInput.value.trim();
  const code = ui.codeInput.value.trim().toUpperCase();

  if (!name || code.length !== 4) {
    ui.joinError.textContent = 'Enter your name and the 4-letter code.';
    return;
  }

  // Motion sensors are restricted to secure contexts. Over plain HTTP on a LAN IP
  // this fails on every modern phone, so say so plainly instead of appearing to
  // work and then producing dead sensors.
  if (!isSecureContextOk()) {
    ui.joinError.textContent = 'Needs HTTPS. Open this page over https:// or localhost.';
    return;
  }

  ui.joinBtn.disabled = true;
  ui.joinError.textContent = '';

  // UNVERIFIED (iOS): must be called inside the user gesture that is this tap.
  // Deferring it behind an await of anything else loses gesture context and iOS
  // rejects the request. Android has no prompt and returns granted immediately.
  const permission = await requestMotionPermission();
  if (!permission.granted) {
    ui.joinError.textContent = 'Motion access denied. Enable it and reload.';
    ui.joinBtn.disabled = false;
    return;
  }

  if (!connection) {
    connection = new RelayConnection({
      onMessage: handleMessage,
      onStatus: (status) => {
        if (status === ConnectionStatus.RECONNECTING) setStatus('Reconnecting…', 'bad');
        else if (status === ConnectionStatus.CONNECTING) setStatus('Connecting…');
      },
    });
    connection.connect();

    if (PEER_ENABLED && 'RTCPeerConnection' in window) {
      peer = new PeerLink({
        connection,
        onTransportChange: (transport) => {
          setStatus(
            transport === Transport.DIRECT
              ? `Connected as ${myPlayerId} · direct`
              : `Connected as ${myPlayerId}`,
            'ok',
          );
        },
      });
    }
  }

  const waitForOpen = setInterval(() => {
    if (connection.status === ConnectionStatus.CONNECTED || connection.status === ConnectionStatus.JOINED) {
      clearInterval(waitForOpen);
      connection.joinRoom(code, name);
    }
  }, 100);
}

// ---------------------------------------------------------------- misc

async function requestWakeLock() {
  // Best effort. Keeps the screen alive between turns so players aren't unlocking
  // their phone every round. Unsupported on some browsers; harmless when it fails.
  try {
    if ('wakeLock' in navigator) await navigator.wakeLock.request('screen');
  } catch { /* ignore */ }
}

function tryLockPortrait() {
  // Best effort only — most browsers refuse outside fullscreen. sensors.js
  // compensates for screen rotation regardless, which is the real fix.
  try { screen.orientation?.lock?.('portrait').catch(() => {}); } catch { /* ignore */ }
}

function updateDebug() {
  if (!ui.debug.classList.contains('visible')) return;
  const s = motion.latest;
  ui.debug.textContent = [
    `state    ${thrower.state}   seq ${thrower.seq}`,
    `socket   ${connection?.status ?? 'not connected'}`,
    `input    ${peer ? peer.transport : 'relay'}${PEER_ENABLED ? '' : '  (?direct=0)'}`,
    `calib    ${motion.hasReference() ? 'set' : 'none'}`,
    s ? `dA ${s.dAlpha.toFixed(1).padStart(7)}  dB ${s.dBeta.toFixed(1).padStart(7)}  dG ${s.dGamma.toFixed(1).padStart(7)}`
      : 'no samples yet — is this page on HTTPS?',
    s ? `power ${s.power.toFixed(2).padStart(6)} m/s²   samples ${motion.buffer.length}   k=${GRAVITY_K}` : '',
    lastThrow
      ? `throw  pow ${lastThrow.power.toFixed(2)} tim ${lastThrow.timingQuality.toFixed(2)} aim ${lastThrow.aimDeg.toFixed(0)} atk ${lastThrow.attackAngleDeg.toFixed(0)} spin ${lastThrow.spinRate.toFixed(0)}`
      : 'no throw yet',
  ].join('\n');
}

function init() {
  const params = new URLSearchParams(location.search);
  const prefill = params.get('room');
  if (prefill) ui.codeInput.value = prefill.toUpperCase();

  ui.joinBtn.addEventListener('click', join);
  ui.debugToggle.addEventListener('click', () => ui.debug.classList.toggle('visible'));
  bindGrip();
  tryLockPortrait();

  if (!isSecureContextOk()) {
    setStatus('Insecure context — sensors will not work', 'bad');
  } else {
    setStatus('Ready to join');
  }

  setInterval(updateDebug, 100);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') requestWakeLock();
  });
}

init();
