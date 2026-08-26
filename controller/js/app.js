/**
 * Controller app wiring: network + sensors + gesture + UI.
 *
 * TEST STATUS: verified on Android (Chrome). iOS paths are written from spec and
 * tagged `UNVERIFIED (iOS)` — see PLAN.md "Test device policy".
 */

import { MotionSource, requestMotionPermission, isSecureContextOk } from './sensors.js';
import { ThrowController, ThrowState, ThrowTuning, buildAimPayload } from './throw.js';
import { RelayConnection, ConnectionStatus } from './net.js';
import { PeerLink, Transport } from './peer.js';

const el = (id) => document.getElementById(id);

const ui = {
  status: el('status'),
  joinScreen: el('joinScreen'),
  chooseScreen: el('chooseScreen'),
  waitScreen: el('waitScreen'),
  throwScreen: el('throwScreen'),
  confirmBtn: el('confirmBtn'),
  recentreBtn: el('recentreBtn'),
  joinError: el('joinError'),
  nameInput: el('nameInput'),
  codeInput: el('codeInput'),
  joinBtn: el('joinBtn'),
  gripBtn: el('gripBtn'),
  waitTitle: el('waitTitle'),
  waitDetail: el('waitDetail'),
  lastThrow: el('lastThrow'),
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
  for (const screen of [ui.joinScreen, ui.chooseScreen, ui.waitScreen, ui.throwScreen]) {
    screen.classList.remove('active');
  }
  ({ join: ui.joinScreen, choose: ui.chooseScreen, wait: ui.waitScreen, throw: ui.throwScreen })[name]
    .classList.add('active');
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
      `Power ${(payload.power * 100).toFixed(0)}%` +
      (payload.simulated ? ' · simulated (no motion sensors)' : '');
  },
  onStateChange: (state) => renderGripButton(state),
});

/**
 * Paint the grip button for a given state.
 *
 * Split out and called once at startup because onStateChange only fires on a
 * *transition* — ThrowController seeds this.state directly in its constructor, so
 * nothing fired until the first arm and the button sat on whatever the markup said.
 */
function renderGripButton(state) {
  const armed = state === ThrowState.ARMED;
  ui.gripBtn.classList.toggle('held', armed);
  ui.gripBtn.textContent = armed ? 'SWING!' : 'TAP TO AIM';
}

// ---------------------------------------------------------------- lobby pointing

/**
 * AIM streaming for the shore-pointing rock picker, deliberately independent of
 * ThrowController's arm/swing state machine rather than reusing `thrower.arm()`.
 *
 * That independence is the whole safety property: ThrowController's release
 * detection (`_onSample` -> `_fire()`) only runs while `state === ARMED`, so a
 * lobby that never arms it can never fire a throw no matter how the phone is
 * swung while choosing a stone. Suppressing `_fire()` after the fact would have
 * meant trusting every future change to the swing detector to remember the lobby
 * exists; not arming it at all needs no such promise.
 */
let lobbyAimTimer = null;
let lobbySeq = 0;

function startLobbyAim() {
  if (lobbyAimTimer !== null) return; // idempotent — SPECTATE repeats while in LOBBY
  motion.calibrate();
  lobbyAimTimer = setInterval(() => {
    const payload = buildAimPayload(motion.latest, ++lobbySeq, 0, 0);
    if (payload) sendInput(payload);
  }, 50); // 20Hz, matching ThrowController's aimHz default
}

function stopLobbyAim() {
  if (lobbyAimTimer === null) return;
  clearInterval(lobbyAimTimer);
  lobbyAimTimer = null;
}

function bindChoose() {
  ui.confirmBtn.addEventListener('click', () => sendInput({ type: 'PICK_ROCK' }));
  ui.recentreBtn.addEventListener('click', () => motion.calibrate());
}

function bindGrip() {
  // A tap, not a hold. The finger has to be off the phone during the swing, and
  // holding a button through a throwing motion is both awkward and a good way to
  // launch the phone. touchend rather than touchstart so a drag off the button
  // does not count.
  const tap = (event) => {
    event.preventDefault(); // stop the tap turning into a scroll, zoom or click
    const descriptor = thrower.toggle();

    // Android only — iOS support for navigator.vibrate is unreliable, so this is
    // a bonus rather than something the feel depends on.
    if (navigator.vibrate) navigator.vibrate(descriptor ? 30 : 15);
  };

  ui.gripBtn.addEventListener('touchend', tap, { passive: false });

  // Mouse fallback so the gesture can be exercised on a desktop browser, where the
  // second tap is what produces a simulated throw.
  ui.gripBtn.addEventListener('click', (event) => {
    if (event.detail === 0) return; // keyboard-activated click; ignore
    tap(event);
  });
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
      // Any TURN means the match has left the lobby — stop here rather than waiting
      // for the next SPECTATE, so the phone is never streaming AIM into a turn it
      // no longer applies to.
      stopLobbyAim();

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

    // The host dropped. RelayConnection is already trying to get back into the same
    // room, so hold the screen rather than ejecting the player — a redeploy or a
    // Wi-Fi blip is seconds, and the relay holds the room open across it.
    case 'HOST_LEFT':
      setStatus('Host reconnecting…', 'bad');
      thrower.cancel();
      peer?.close();
      break;

    case 'HOST_RESUMED':
      setStatus(`Connected as ${myPlayerId}`, 'ok');
      if (PEER_ENABLED && peer) peer.start().catch(() => {});
      break;

    // Only reaches here once the rejoin window has expired — RelayConnection
    // swallows this while it still has a chance of getting back in.
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

/**
 * One line describing the throw that just finished, or '' when there isn't one.
 *
 * Blank in the lobby and on the results screen: the first has no throw behind it, and
 * the second is already showing final standings, where one player's last throw is noise.
 */
function renderLastThrow(message, phase) {
  if (phase !== 'PLAYING') return '';
  if (!message.lastPlayer || typeof message.lastSkips !== 'number') return '';

  const who = message.lastPlayer === myPlayerId ? 'You' : message.lastPlayer;
  const parts = [`${who}: ${message.lastSkips} skip${message.lastSkips === 1 ? '' : 's'}`];
  if (typeof message.lastDistance === 'number') parts.push(`${message.lastDistance.toFixed(1)} m`);
  return parts.join(' · ');
}

function renderSpectate(message) {
  if (thrower.state === ThrowState.ARMED) return; // don't yank the screen mid-throw

  // Without the phase, every idle moment reads "Waiting for the next turn" — including
  // the lobby before anything has started and the results screen after it has ended.
  const phase = message.phase ?? 'PLAYING';

  if (phase === 'LOBBY') {
    // SPECTATE repeats throughout the lobby (a player joining or leaving, for
    // instance) — showScreen/startLobbyAim are both idempotent, so re-entering
    // this branch on every one of those pings is harmless.
    showScreen('choose');
    startLobbyAim();
    return;
  }

  stopLobbyAim();

  if (phase === 'RESULTS') {
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
    ui.waitDetail.textContent = parts.join(' · ') || 'Watch the TV.';
  }

  // The throw that just finished, on its own line. Attributed to lastPlayer rather
  // than activePlayer: the turn has already moved on by the time this arrives, so
  // reading it off the caption would credit every throw to the next player up.
  ui.lastThrow.textContent = renderLastThrow(message, phase);

  ui.scores.replaceChildren();
  for (const [player, score] of Object.entries(message.scores ?? {})) {
    const row = document.createElement('div');
    row.className = 'score-row' + (player === message.activePlayer ? ' active' : '');

    // textContent, never innerHTML. A player name is attacker-controlled text that every
    // other phone renders, and the relay only trims/uppercases it - uppercasing is not
    // sanitisation, because HTML tags are case-insensitive. Interpolating it as markup
    // let a name like <H1>OWNED</H1> render as markup on everyone else's screen, held
    // back from worse only by the 16-character cap in sanitizePlayerId.
    const name = document.createElement('span');
    name.textContent = player;

    const points = document.createElement('span');
    points.textContent = score;

    row.append(name, points);
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
    `state    ${thrower.state}   seq ${thrower.seq}   windup ${thrower.windup.toFixed(2)}`,
    `socket   ${connection?.status ?? 'not connected'}`,
    `input    ${peer ? peer.transport : 'relay'}${PEER_ENABLED ? '' : '  (?direct=0)'}`,
    `calib    ${motion.hasReference() ? 'set' : 'none'}`,
    // The delta quaternion itself. Four numbers rather than three named angles because
    // that is genuinely what is on the wire now — printing a decomposition here would
    // be inventing a representation the rest of the system does not use. All-zeros with
    // qw at 1 means the phone is exactly where it was at the tap.
    s ? `attitude w ${s.attitude.w.toFixed(3).padStart(6)}  x ${s.attitude.x.toFixed(3).padStart(6)}` +
        `  y ${s.attitude.y.toFixed(3).padStart(6)}  z ${s.attitude.z.toFixed(3).padStart(6)}`
      : 'no samples yet — is this page on HTTPS?',
    // How stale the last real orientation reading is. A number that keeps climbing
    // means the aim is running on gyro integration alone, which is the tell for a
    // device whose deviceorientation stream is slower than its devicemotion one.
    s ? `orient   ${s.orientationAgeMs === null ? 'none' : `${s.orientationAgeMs.toFixed(0)}ms old`}` : '',
    // gravitySource is the one to read before trusting any power tuning: 'hardware'
    // and 'exact' are clean, 'filter' still has the rotation artefact in it.
    s ? `accel ${s.power.toFixed(2).padStart(6)} m/s²  via ${s.gravitySource}  samples ${motion.buffer.length}` : '',
    lastThrow
      ? `throw  pow ${lastThrow.power.toFixed(2)}  spin ${lastThrow.spinRate.toFixed(0)}  ` +
        `q ${lastThrow.qw.toFixed(2)} ${lastThrow.qx.toFixed(2)} ${lastThrow.qy.toFixed(2)} ${lastThrow.qz.toFixed(2)}`
      : 'no throw yet',
    // Both metrics, so light/medium/hard swings can be compared directly. If these
    // three do not separate, the metric is wrong — see ThrowTuning.powerMetric.
    lastThrow
      ? `metric speed ${(lastThrow.swingSpeed ?? 0).toFixed(2)} m/s   peak ${lastThrow.peakAccel.toFixed(1)} m/s²   using ${ThrowTuning.powerMetric}`
      : '',
  ].join('\n');
}

function init() {
  const params = new URLSearchParams(location.search);
  const prefill = params.get('room');
  if (prefill) ui.codeInput.value = prefill.toUpperCase();

  ui.joinBtn.addEventListener('click', join);
  ui.debugToggle.addEventListener('click', () => ui.debug.classList.toggle('visible'));
  bindChoose();
  bindGrip();
  renderGripButton(thrower.state); // see renderGripButton — nothing else paints the initial label
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
