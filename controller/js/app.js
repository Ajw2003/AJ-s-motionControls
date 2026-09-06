/**
 * Controller app wiring: network + sensors + gesture + UI.
 *
 * TEST STATUS: verified on Android (Chrome). iOS paths are written from spec and
 * tagged `UNVERIFIED (iOS)` — see PLAN.md "Test device policy".
 */

import { MotionSource, requestMotionPermission, isSecureContextOk } from './sensors.js';
import { ThrowController, ThrowState, buildAimPayload } from './throw.js';
import { RelayConnection, ConnectionStatus } from './net.js';
import { PeerLink, Transport } from './peer.js';
import { loadIdentity, ensureDeviceId, updateIdentity } from './identity.js';

const el = (id) => document.getElementById(id);

const ui = {
  status: el('status'),
  joinScreen: el('joinScreen'),
  chooseScreen: el('chooseScreen'),
  waitScreen: el('waitScreen'),
  throwScreen: el('throwScreen'),
  confirmBtn: el('confirmBtn'),
  advanceBtn: el('advanceBtn'),
  againBtn: el('againBtn'),
  recentreBtn: el('recentreBtn'),
  nameBtn: el('nameBtn'),
  nameEditInput: el('nameEditInput'),
  chooseRock: el('chooseRock'),
  joinError: el('joinError'),
  nameInput: el('nameInput'),
  codeInput: el('codeInput'),
  joinBtn: el('joinBtn'),
  gripBtn: el('gripBtn'),
  waitTitle: el('waitTitle'),
  waitDetail: el('waitDetail'),
  lastThrow: el('lastThrow'),
  scores: el('scores'),
  colourPicker: el('colourPicker'),
  colourSwatches: el('colourSwatches'),
  colourDefaultBtn: el('colourDefaultBtn'),
  colourDefaultDot: el('colourDefaultDot'),
  colourInput: el('colourInput'),
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

// Sent as a SET_NAME the moment JOINED comes back — see handleMessage. Set from whichever
// path joined (typed in the form, or read back from the stored identity on a silent
// rejoin), so identity.js's stored name and the relay's copy of it never diverge.
let myName = '';

// True from the moment this phone has ever actually held a seat this page-load, onward.
// Distinguishes an ERROR/ROOM_CLOSED worth retrying forever (a seat that exists somewhere
// and is worth getting back into — a mid-match Wi-Fi blip, a host restart) from one that
// means exactly what it says: a room code that was never valid, typed wrong on the very
// first join attempt, which must still show the form rather than retry a room that never
// existed. See handleMessage's ERROR/ROOM_CLOSED cases and
// docs/plans/seamless-rejoin.md "the repeated tap to play" §2-3.
let hasJoinedOnce = false;

// The phone's own belief about whose turn it is, sourced only from TURN — the message
// TurnManager sends personalised per-player and only while Phase is genuinely Playing (see
// TurnManager.StartTurn/SendTurnStates). SPECTATE also carries a phase field, but it is
// broadcast state, not personalised, and renderSpectate must never let a LOBBY-phase SPECTATE
// override what TURN already told this specific phone — see renderSpectate and startLobbyAim.
let myTurn = false;

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
  ({
    join: ui.joinScreen,
    choose: ui.chooseScreen,
    wait: ui.waitScreen,
    throw: ui.throwScreen,
  })[name].classList.add('active');
}

function setStatus(text, kind = '') {
  ui.status.textContent = text;
  ui.status.className = kind;
}

// ---------------------------------------------------------------- gesture

const thrower = new ThrowController(motion, {
  // Through sendAim, not sendInput: the seq the TV orders frames by is stamped there,
  // once, for every producer. See sendAim.
  onAim: (payload) => sendAim(payload),
  onThrow: (payload) => {
    sendInput(payload);
    lastThrow = payload;
    ui.throwResult.textContent =
      `Speed ${payload.swingSpeed.toFixed(2)} m/s` +
      (payload.simulated ? ' · simulated (no motion sensors)' : '');
  },
  onStateChange: (state) => {
    renderGripButton(state);

    // Hand the AIM stream over rather than running both. Arming replaces the flat
    // preview frames with ThrowController's own, which carry live spin and windup;
    // standing down again (a second tap, a throw, a cancelled turn) hands it back so
    // the ghost survives until TURN says the turn is over.
    if (state === ThrowState.ARMED) stopPreviewAim();
    else startTurnAim();
  },
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

// ---------------------------------------------------------------- aim streams

/**
 * One AIM sequence number for the whole phone, stamped on the way out.
 *
 * Three things produce AIM frames now — the lobby stream, the turn-idle stream and
 * ThrowController while armed — and AimPreview drops any frame whose seq is not
 * greater than the last one it accepted from that player. Counting separately per
 * producer meant handing over mid-turn could go backwards: after ten seconds of
 * lobby pointing the lobby counter is past 200 while ThrowController's is in single
 * figures, so every armed frame was dropped until it climbed back past the lobby's,
 * and the ghost stayed missing for the first several seconds of a turn.
 *
 * Stamped here rather than inside each producer so a fourth one cannot reintroduce
 * the problem: `sendAim` is the only route onto the wire.
 */
let aimSeq = 0;

function sendAim(payload) {
  if (!payload) return;
  sendInput({ ...payload, seq: ++aimSeq });
}

/**
 * The cosmetic 20Hz AIM stream, deliberately independent of ThrowController's
 * arm/swing state machine rather than reusing `thrower.arm()`.
 *
 * That independence is the whole safety property: ThrowController's release
 * detection (`_onSample` -> `_fire()`) only runs while `state === ARMED`, so a
 * stream that never arms it can never fire a throw no matter how the phone is
 * swung. Suppressing `_fire()` after the fact would have meant trusting every
 * future change to the swing detector to remember these streams exist; not arming
 * it at all needs no such promise.
 *
 * Two callers, one timer: the lobby's shore-pointing picker, and a player whose turn
 * has started but who has not tapped to arm yet. Sharing the timer is what stops the
 * two ever running at once and double-feeding the TV.
 */
let previewAimTimer = null;

function startPreviewAim() {
  if (previewAimTimer !== null) return; // idempotent — SPECTATE and TURN both repeat

  // Never while armed: ThrowController is streaming its own frames then, with live
  // spin and windup in them, and this one would interleave zeros.
  if (thrower.state === ThrowState.ARMED) return;

  motion.calibrate();
  previewAimTimer = setInterval(() => {
    sendAim(buildAimPayload(motion.latest, 0, 0, 0));
  }, 50); // 20Hz, matching ThrowController's aimHz default
}

function stopPreviewAim() {
  if (previewAimTimer === null) return;
  clearInterval(previewAimTimer);
  previewAimTimer = null;
}

/**
 * Shore-pointing, in the lobby and in a mid-match re-pick pause.
 *
 * The `myTurn` guard is here rather than only at the call sites, deliberately: this
 * is the lobby's entry point, so gating it here means no future caller can start the
 * shore-pointing stream on top of a live turn.
 */
function startLobbyAim() {
  if (myTurn) return;
  startPreviewAim();
}

function stopLobbyAim() {
  stopPreviewAim();
}

/**
 * Keeps the TV's ghost stone alive between the moment a turn starts and the moment
 * the player taps to arm.
 *
 * Without this the ghost vanishes half a second into every turn. The lobby stream is
 * stopped by TURN, ThrowController's stream does not exist until `arm()`, and
 * AimPreview hides the ghost after `staleAfterSeconds` with no frames — so a player
 * looking up at the TV to line a shot up saw empty water until they tapped, which
 * reads as the preview being broken. See docs/Findings-AimGhostVanishesOnNewRound.md.
 *
 * Not an auto-arm, and that distinction is the point: arming would hand the release
 * detector a live phone before the player has looked up, and a swing at turn start
 * would throw.
 */
function startTurnAim() {
  if (!myTurn) return;
  startPreviewAim();
}

// ---------------------------------------------------------------- advancing

/**
 * Move the match on from a phone: start it from the lobby, resume from a re-pick,
 * begin the next one from final scores.
 *
 * The TV's Space key still does all three and remains the way to get the very first
 * match going — this is what stops a couch session needing a keyboard between every
 * match. TurnManager runs one switch for both, so they cannot disagree about where
 * "on" is; the phone deliberately names no destination (see AdvanceMessage).
 *
 * Buttons are hidden until a SPECTATE says the current phase can be advanced, so
 * neither one is ever offered mid-turn.
 */
function bindAdvance() {
  for (const button of [ui.advanceBtn, ui.againBtn]) {
    button.addEventListener('click', () => {
      // Hidden immediately rather than on the reply. The phase change comes back as a
      // SPECTATE a network round trip later, and a button left live through that gap
      // invites a second press that lands in the next phase — from Results that is
      // Lobby, and the second press would start the match before anyone has picked.
      button.hidden = true;
      sendInput({ type: 'ADVANCE' });
    });
  }
}

/**
 * Show whichever advance button belongs to this phase, and label it for what it
 * actually does.
 *
 * `choosing` is the only thing the phone knows about the mid-match re-pick pause —
 * the wire phase for it is LOBBY, because a choose screen behaves identically either
 * way (see TurnManager.BroadcastSpectate). It exists purely so this button does not
 * offer to "start" a match that is already three rounds old.
 */
function renderAdvance(phase, message) {
  const canChoose = phase === 'LOBBY';
  ui.advanceBtn.hidden = !canChoose;
  if (canChoose) ui.advanceBtn.textContent = message.choosing ? 'RESUME MATCH' : 'START MATCH';

  ui.againBtn.hidden = phase !== 'RESULTS';
}

// ---------------------------------------------------------------- renaming

/**
 * Renaming from the wait screen (Fix B, docs/plans/seamless-rejoin-round2.md). No rejoin,
 * no reconnect: the seat is keyed by device id, so this is a cosmetic SET_NAME on the live
 * connection, exactly like the one JOINED already sends — see handleMessage's JOINED case.
 */
function bindRename() {
  ui.nameBtn.addEventListener('click', () => {
    ui.nameEditInput.value = myName;
    ui.nameBtn.hidden = true;
    ui.nameEditInput.hidden = false;
    ui.nameEditInput.focus();
  });

  const commit = () => {
    if (ui.nameEditInput.hidden) return; // already committed by the other event

    const name = ui.nameEditInput.value.trim();
    ui.nameEditInput.hidden = true;
    ui.nameBtn.hidden = false;

    if (!name || name === myName) {
      renderNameBtn();
      return;
    }

    myName = name;
    updateIdentity({ name });
    renderNameBtn();
    connection?.send({ type: 'SET_NAME', name: myName });
  };

  ui.nameEditInput.addEventListener('blur', commit);
  ui.nameEditInput.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') ui.nameEditInput.blur(); // triggers commit via blur
  });
}

function renderNameBtn() {
  ui.nameBtn.textContent = myName;
}

function bindChoose() {
  ui.confirmBtn.addEventListener('click', () => sendInput({ type: 'PICK_ROCK' }));
  ui.recentreBtn.addEventListener('click', () => motion.calibrate());
  bindColour();
}

// ---------------------------------------------------------------- colour picking

// The colour this phone last asked for. Not the same thing as the colour it HAS: the
// host may nudge a pick that is too dark or too close to another player (see
// PlayerColours.Pick), and what comes back in SPECTATE is the truth. Kept only so the
// swatch row can show a selection before the round trip completes.
let requestedColour = null;

// The colour SPECTATE last said is ours, so re-rendering the swatches on every
// broadcast does not fight a picker the player is mid-drag on.
let myColour = null;

function pickColour(colour) {
  requestedColour = colour;
  applyMyColour(colour);
  sendInput({ type: 'PICK_COLOUR', colour });
}

function bindColour() {
  // 'input' rather than 'change' so dragging around the OS colour wheel previews live.
  // Cheap: PICK_COLOUR is tiny, it is not on the throw path, and the relay's rate limit
  // is 60/s against an AIM stream that only runs at 20.
  ui.colourInput.addEventListener('input', () => pickColour(ui.colourInput.value));

  ui.colourDefaultBtn.addEventListener('click', () => setSwatchesOpen(ui.colourSwatches.hidden));
}

/**
 * Opens or closes the palette sub-menu.
 *
 * One function so the row's visibility and the button's ring can never disagree —
 * aria-expanded is what the CSS keys the ring off, so a stray `hidden` assignment
 * elsewhere would leave a closed menu wearing an open button.
 */
function setSwatchesOpen(open) {
  ui.colourSwatches.hidden = !open;
  ui.colourDefaultBtn.setAttribute('aria-expanded', String(open));
}

/** Paints this phone's own accent, so the player sees their colour on their device. */
function applyMyColour(colour) {
  if (!colour) return;
  document.documentElement.style.setProperty('--accent', colour);
  ui.colourInput.value = colour;

  for (const swatch of ui.colourSwatches.children) {
    swatch.classList.toggle('selected', swatch.dataset.colour === colour.toLowerCase());
  }
}

/**
 * Rebuilds the swatch row from the host's palette.
 *
 * The palette comes down the wire rather than being duplicated here: it is authored on
 * the PlayerColourPalette asset, and a second copy in this file would be the third place
 * player colours live. Idempotent — SPECTATE repeats constantly, so this returns early
 * unless the palette actually changed.
 */
let renderedPalette = '';

function renderSwatches(palette) {
  if (!Array.isArray(palette) || palette.length === 0) return;

  const key = palette.join(',');
  if (key === renderedPalette) return;
  renderedPalette = key;

  ui.colourSwatches.replaceChildren();
  for (const colour of palette) {
    const swatch = document.createElement('button');
    swatch.type = 'button';
    swatch.className = 'colour-swatch';
    swatch.dataset.colour = String(colour).toLowerCase();
    swatch.style.background = colour;
    swatch.setAttribute('aria-label', `Colour ${colour}`);
    // Closing on the pick, not on pickColour itself: the OS wheel fires pickColour on
    // every frame of a drag, and a menu that shut mid-drag would be its own bug.
    swatch.addEventListener('click', () => {
      pickColour(colour);
      setSwatchesOpen(false);
    });
    ui.colourSwatches.appendChild(swatch);
  }

  // The Default button previews what is behind it — a slice per palette colour, so it
  // reads as "the presets" rather than as a second copy of the Custom swatch.
  ui.colourDefaultDot.style.background = paletteGradient(palette);

  applyMyColour(myColour ?? requestedColour);
}

/** Equal conic slices, one per palette colour, as a CSS background value. */
function paletteGradient(palette) {
  const slice = 360 / palette.length;
  const stops = palette.map((colour, i) => `${colour} ${i * slice}deg ${(i + 1) * slice}deg`);
  return `conic-gradient(${stops.join(', ')})`;
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
      myTurn = false; // a fresh join/rejoin has no standing TURN yet — wait for one
      stopPreviewAim(); // and no standing stream either; the next SPECTATE/TURN starts one
      hasJoinedOnce = true; // a later ERROR/ROOM_CLOSED now means "get back in", not "unknown player"
      setStatus(`Connected as ${myPlayerId}`, 'ok');
      renderNameBtn();
      showScreen('wait');
      motion.start();
      requestWakeLock();

      // playerId is now the device id, not the typed name (docs/plans/seamless-rejoin.md
      // Workstream C) — the relay has no idea what to display until this arrives. Sent on
      // every JOINED, not just a first-time join, so a rename mid-match (myName updated,
      // then this same path re-runs on a reconnect) reaches the host too.
      if (myName) connection.send({ type: 'SET_NAME', name: myName });

      // Only after JOINED: the relay routes by room membership, so an offer sent any
      // earlier has nowhere to go. Failure here is silent by design — the throw path
      // already works over the relay, and this is only ever an upgrade.
      if (PEER_ENABLED && peer) peer.start().catch(() => {});
      break;

    case 'ERROR':
      // Once this phone has ever actually held a seat, an ERROR means "get back in", not
      // "unknown player" — retry rather than bouncing to a form that would mint a second
      // seat under a fresh id. Before the first successful JOINED (a bad/mistyped room
      // code, say), hasJoinedOnce is still false and the form is exactly the right
      // response. See docs/plans/seamless-rejoin.md "the repeated tap to play" §2.
      if (hasJoinedOnce && connection?.credentials) {
        connection.retryJoin();
        break;
      }
      ui.joinError.textContent = message.message ?? 'Something went wrong.';
      showScreen('join');
      ui.joinBtn.disabled = false;
      break;

    case 'TURN':
      // Any TURN means the match has left the lobby — stop here rather than waiting
      // for the next SPECTATE, so the phone is never streaming AIM into a turn it
      // no longer applies to.
      stopLobbyAim();
      myTurn = message.yourTurn;

      if (message.yourTurn) {
        ui.throwResult.textContent = '';
        showScreen('throw');

        // After myTurn is set, which startTurnAim gates on. This is what keeps the
        // TV's ghost stone up from the instant the turn starts instead of only once
        // the player taps to arm — see startTurnAim.
        startTurnAim();
      } else {
        thrower.cancel();

        // cancel() only stops ThrowController's own stream. The turn-idle one is this
        // module's, and a phone left streaming into someone else's turn is exactly the
        // frame AimPreview would have to reject.
        stopPreviewAim();
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

    case 'ROOM_CLOSED':
      myTurn = false; // before cancel(), whose IDLE transition would otherwise re-start the stream
      thrower.cancel();
      stopPreviewAim();

      // Workstream B's stable room code means a host restart hands back the same code, so
      // a phone that already held a seat should retry rather than eject — it looks
      // identical to the host never having left. The join form stays the response to an
      // actual, permanent end of match for a phone that never held a seat here.
      if (hasJoinedOnce && connection?.credentials) {
        setStatus('Host restarting…', 'bad');
        peer?.close();
        connection.retryJoin();
        break;
      }

      setStatus('Game ended', 'bad');
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

  // Comparison stays against the id; only the rendered label changes. message.names is the
  // id -> display-name map TurnManager sends alongside scores/rocks — see
  // docs/plans/seamless-rejoin.md Workstream C. Falls back to the raw id so a build without
  // PlayerNames running (or a name that hasn't arrived yet) still shows something.
  const who = message.lastPlayer === myPlayerId ? 'You' : (message.names?.[message.lastPlayer] ?? message.lastPlayer);
  const parts = [`${who}: ${message.lastSkips} skip${message.lastSkips === 1 ? '' : 's'}`];
  if (typeof message.lastDistance === 'number') parts.push(`${message.lastDistance.toFixed(1)} m`);
  return parts.join(' · ');
}

function renderSpectate(message) {
  if (thrower.state === ThrowState.ARMED) return; // don't yank the screen mid-throw

  // Without the phase, every idle moment reads "Waiting for the next turn" — including
  // the lobby before anything has started and the results screen after it has ended.
  const phase = message.phase ?? 'PLAYING';

  // TURN is the authoritative, personalised signal for "is it my turn" — SPECTATE's phase
  // is broadcast state and must never override it. Without this, a LOBBY-phase SPECTATE
  // landing in the gap between TURN saying yourTurn:true and the player actually tapping
  // to arm would silently swap the phone back to the choose screen and restart the
  // shore-pointing AIM timer underneath a turn the phone has already been told is live.
  if (phase === 'LOBBY' && myTurn) return;

  // Before the LOBBY branch's early return, so the lobby gets its button too.
  renderAdvance(phase, message);

  // Likewise before the early return: the picker lives on the choose screen, and the
  // colour the host settled on is what this phone should be showing — not the one it
  // asked for, which PlayerColours may have nudged for legibility or to keep two
  // players apart.
  renderSwatches(message.palette);
  const settled = message.colours?.[myPlayerId];
  if (settled && settled !== myColour) {
    myColour = settled;
    applyMyColour(settled);
  }

  if (phase === 'LOBBY') {
    // SPECTATE repeats throughout the lobby (a player joining or leaving, for
    // instance) — showScreen/startLobbyAim are both idempotent, so re-entering
    // this branch on every one of those pings is harmless.
    showScreen('choose');
    startLobbyAim();
    ui.colourPicker.hidden = false;

    // Only present once ShoreRocks has resolved a highlight for this phone and
    // TurnManager has broadcast it — blank until then, which the CSS collapses.
    const mine = message.rocks?.[myPlayerId];
    ui.chooseRock.textContent = mine?.rockName ? `On: ${mine.rockName}` : '';
    return;
  }

  // Not while this phone is the one aiming. SPECTATE is broadcast state and goes out on
  // every turn start and throw completion, so an unconditional stop here killed the
  // turn-idle stream a moment after TURN started it — and with it the TV's ghost. TURN
  // is the personalised message, and it already stops the stream for everyone else.
  if (!myTurn) stopPreviewAim();

  // Off the choose screen the picker has nowhere to be — a mid-throw colour change is
  // allowed by the host but there is no screen real estate for it here. Collapsed as
  // well as hidden, so a mid-match re-pick opens on the compact two-button row rather
  // than restoring a sub-menu left open several minutes ago.
  ui.colourPicker.hidden = true;
  setSwatchesOpen(false);

  if (phase === 'RESULTS') {
    // leader() still operates on the id-keyed scores dict — the comparison below stays
    // against the id, and only the rendered label swaps in the display name.
    const winner = leader(message.scores);
    ui.waitTitle.textContent = 'Final scores';
    ui.waitDetail.textContent = winner
      ? winner === myPlayerId
        ? 'You win!'
        : `${message.names?.[winner] ?? winner} wins`
      : 'It is a tie.';
  } else {
    ui.waitTitle.textContent = message.activePlayer
      ? `${message.names?.[message.activePlayer] ?? message.activePlayer} is throwing`
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

    // textContent, never innerHTML. A display name is attacker-controlled text that every
    // other phone renders, exactly as the typed name used to be before Workstream C moved
    // it off playerId and into SET_NAME/message.names — the relay's sanitizePlayerId
    // uppercase+16-char cap no longer applies to it. Interpolating it as markup let a name
    // like <H1>OWNED</H1> render as markup on everyone else's screen; textContent is what
    // still rules that out.
    const name = document.createElement('span');
    name.className = 'name';

    // Their colour, matching their dot on the TV. A span with a background, never
    // interpolated markup — the colour string is host-generated, but the name beside
    // it is not, and keeping both on the textContent path means neither can ever
    // become markup.
    const colour = message.colours?.[player];
    if (colour) {
      const dot = document.createElement('span');
      dot.className = 'score-dot';
      dot.style.background = colour;
      name.appendChild(dot);
    }

    // `player` is still the dict key — an opaque device id, kept that way so two players
    // with the same typed name never collide as one scores entry (see BuildSpectate's
    // remarks on playerNames). Only the rendered label swaps in the display name.
    const label = document.createElement('span');
    label.textContent = message.names?.[player] ?? player;
    name.appendChild(label);

    const points = document.createElement('span');
    points.textContent = score;

    row.append(name, points);
    ui.scores.appendChild(row);
  }
}

// ---------------------------------------------------------------- join

/** Builds the RelayConnection/PeerLink pair the first time either join path needs one. */
function ensureConnection() {
  if (connection) return;

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

/** Sends JOIN_ROOM the moment the socket is actually open — shared by both join paths. */
function joinOnceConnected(code, deviceId) {
  const waitForOpen = setInterval(() => {
    if (
      connection.status === ConnectionStatus.CONNECTED ||
      connection.status === ConnectionStatus.JOINED
    ) {
      clearInterval(waitForOpen);
      connection.joinRoom(code, deviceId);
    }
  }, 100);
}

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

  // The wire playerId is the device id, not the typed name (docs/plans/seamless-rejoin.md
  // Workstream C) — the name travels separately as SET_NAME once JOINED comes back, so
  // renaming later never looks like a new player. ensureDeviceId mints one on this
  // device's very first join and returns the same one on every join after that.
  const deviceId = ensureDeviceId();
  myName = name;
  updateIdentity({ name, room: code });

  ensureConnection();
  joinOnceConnected(code, deviceId);
}

// ---------------------------------------------------------------- misc

async function requestWakeLock() {
  // Best effort. Keeps the screen alive between turns so players aren't unlocking
  // their phone every round. Unsupported on some browsers; harmless when it fails.
  try {
    if ('wakeLock' in navigator) await navigator.wakeLock.request('screen');
  } catch {
    /* ignore */
  }
}

function tryLockPortrait() {
  // Best effort only — most browsers refuse outside fullscreen. sensors.js
  // compensates for screen rotation regardless, which is the real fix.
  try {
    screen.orientation?.lock?.('portrait').catch(() => {});
  } catch {
    /* ignore */
  }
}

function updateDebug() {
  if (!ui.debug.classList.contains('visible')) return;
  const s = motion.latest;
  ui.debug.textContent = [
    `state    ${thrower.state}   seq ${thrower.seq}   windup ${thrower._windSpeed.toFixed(2)} m/s`,
    `socket   ${connection?.status ?? 'not connected'}`,
    `input    ${peer ? peer.transport : 'relay'}${PEER_ENABLED ? '' : '  (?direct=0)'}`,
    `calib    ${motion.hasReference() ? 'set' : 'none'}`,
    // The delta quaternion itself. Four numbers rather than three named angles because
    // that is genuinely what is on the wire now — printing a decomposition here would
    // be inventing a representation the rest of the system does not use. All-zeros with
    // qw at 1 means the phone is exactly where it was at the tap.
    s
      ? `attitude w ${s.attitude.w.toFixed(3).padStart(6)}  x ${s.attitude.x.toFixed(3).padStart(6)}` +
        `  y ${s.attitude.y.toFixed(3).padStart(6)}  z ${s.attitude.z.toFixed(3).padStart(6)}`
      : 'no samples yet — is this page on HTTPS?',
    // How stale the last real orientation reading is. A number that keeps climbing
    // means the aim is running on gyro integration alone, which is the tell for a
    // device whose deviceorientation stream is slower than its devicemotion one.
    s
      ? `orient   ${s.orientationAgeMs === null ? 'none' : `${s.orientationAgeMs.toFixed(0)}ms old`}`
      : '',
    // gravitySource is the one to read before trusting any power tuning: 'hardware'
    // and 'exact' are clean, 'filter' still has the rotation artefact in it.
    s
      ? `accel ${s.power.toFixed(2).padStart(6)} m/s²  via ${s.gravitySource}  samples ${motion.buffer.length}`
      : '',
    lastThrow
      ? `throw  speed ${lastThrow.swingSpeed.toFixed(2)} m/s  spin ${lastThrow.spinRate.toFixed(0)}  ` +
        `q ${lastThrow.qw.toFixed(2)} ${lastThrow.qx.toFixed(2)} ${lastThrow.qy.toFixed(2)} ${lastThrow.qz.toFixed(2)}`
      : 'no throw yet',
  ].join('\n');
}

function init() {
  const params = new URLSearchParams(location.search);
  const prefill = params.get('room');
  const roomFromUrl = prefill ? prefill.toUpperCase() : '';
  if (prefill) ui.codeInput.value = roomFromUrl;

  ui.joinBtn.addEventListener('click', join);
  ui.debugToggle.addEventListener('click', () => ui.debug.classList.toggle('visible'));
  bindChoose();
  bindAdvance();
  bindGrip();
  bindRename();
  renderGripButton(thrower.state); // see renderGripButton — nothing else paints the initial label
  tryLockPortrait();

  if (!isSecureContextOk()) {
    setStatus('Insecure context — sensors will not work', 'bad');
  } else {
    setStatus('Ready to join');
  }

  // Prefill the join form from a stored identity rather than skipping the form entirely —
  // a skipped form is a dead end for a phone that can never see the naming/renaming UI
  // again (docs/plans/seamless-rejoin-round2.md Fix A). Same on both platforms:
  // requestMotionPermission still runs inside the JOIN tap's own gesture either way, so
  // there is nothing iOS needs that Android doesn't also get here.
  //
  // The name comes back whenever one is stored, deliberately NOT gated on the room
  // matching. A player who reaches the page from a bookmark, a stale QR, or a bare URL
  // with no ?room= at all has the same name they always had, and re-typing it is exactly
  // the friction this work exists to remove. The room code is prefilled from the URL
  // above, and stays the URL's business.
  const identity = loadIdentity();
  if (identity?.name) ui.nameInput.value = identity.name;
  if (!ui.codeInput.value && identity?.room) ui.codeInput.value = identity.room;

  setInterval(updateDebug, 100);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') requestWakeLock();
  });
}

init();
