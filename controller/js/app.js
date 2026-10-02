/**
 * Controller app wiring: network + sensors + gesture + UI.
 *
 * TEST STATUS: verified on real hardware across multiple iPhone generations (Safari)
 * and multiple Samsung and Pixel generations (Chrome) — see docs/DeviceMatrix.md for
 * per-device results and for what "verified" requires.
 */

import {
  MotionSource,
  requestMotionPermission,
  isSecureContextOk,
  tiltAngleDeg,
} from './sensors.js';
import { ThrowController, ThrowState, ThrowTuning, buildAimPayload } from './throw.js';
import { rankScores } from './ranking.js';
import { RelayConnection, ConnectionStatus } from './net.js';
import { ROCK_ART } from './rock-art.js';
import { loadIdentity, ensureDeviceId, updateIdentity, clearIdentity } from './identity.js';

const el = (id) => document.getElementById(id);

const ui = {
  status: el('status'),
  joinScreen: el('joinScreen'),
  chooseScreen: el('chooseScreen'),
  waitScreen: el('waitScreen'),
  throwScreen: el('throwScreen'),
  tutorialScreen: el('tutorialScreen'),
  tutorialDots: el('tutorialDots'),
  tutorialVisual: el('tutorialVisual'),
  tutorialTitle: el('tutorialTitle'),
  tutorialBody: el('tutorialBody'),
  tutorialSkipBtn: el('tutorialSkipBtn'),
  tutorialBackBtn: el('tutorialBackBtn'),
  tutorialNextBtn: el('tutorialNextBtn'),
  howToPlayBtn: el('howToPlayBtn'),
  confirmBtn: el('confirmBtn'),
  advanceBtn: el('advanceBtn'),
  againBtn: el('againBtn'),
  recentreBtn: el('recentreBtn'),
  nameBtn: el('nameBtn'),
  nameEditInput: el('nameEditInput'),
  chooseRock: el('chooseRock'),
  chooseRockArt: el('chooseRockArt'),
  chooseRockName: el('chooseRockName'),
  joinError: el('joinError'),
  nameInput: el('nameInput'),
  joinBtn: el('joinBtn'),
  gripBtn: el('gripBtn'),
  gripWrap: el('gripWrap'),
  armRing: el('armRing'),
  calBadge: el('calBadge'),
  tiltBox: el('tiltBox'),
  tiltBubble: el('tiltBubble'),
  tiltLockRing: el('tiltLockRing'),
  waitTitle: el('waitTitle'),
  waitDetail: el('waitDetail'),
  lastThrow: el('lastThrow'),
  scores: el('scores'),
  colourPicker: el('colourPicker'),
  colourSwatches: el('colourSwatches'),
  colourBackdrop: el('colourBackdrop'),
  colourDoneBtn: el('colourDoneBtn'),
  colourCustom: el('colourCustom'),
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

// A5/A6 accidental-throw guard tunables (docs/Findings-NetworkAndThrowStability.md),
// overridable the same way as GRAVITY_K so Ayden can dial them in on a real phone
// without a redeploy. A non-numeric or missing value keeps ThrowTuning's default —
// `Number('')` is 0, which is falsy, so `|| default` behaves the same way here.
const throwParams = new URLSearchParams(location.search);
const THROW_TUNING = {
  ...ThrowTuning,
  armGraceMs: Number(throwParams.get('armGraceMs')) || ThrowTuning.armGraceMs,
  swingOnsetSamples: Number(throwParams.get('onsetSamples')) || ThrowTuning.swingOnsetSamples,
  minThrowSwingSpeed: Number(throwParams.get('minThrowSpeed')) || ThrowTuning.minThrowSwingSpeed,
};

/**
 * D3/D4: the two-state badge and lock haptic/pulse, driven off MotionSource's
 * Settling/Aiming edge. Deliberately two states, not three — ThrowController exposes
 * no hook for its internal _swinging sub-state beyond the existing IDLE/ARMED
 * onStateChange, and the grip button's own "SWING!" label already covers that
 * moment, which typically lasts under a second anyway.
 */
const motion = new MotionSource({
  gravityK: GRAVITY_K,
  onCalibrationStateChange: (state) => {
    renderCalBadge(state);

    const aiming = state === 'aiming';
    ui.tiltBubble?.classList.toggle('locked', aiming);

    if (aiming) {
      // Android only — see bindGrip's own guard comment; iOS support for
      // navigator.vibrate is unreliable, so this is a bonus, not something the feel
      // depends on.
      if (navigator.vibrate) navigator.vibrate(10);

      // D2's lock-tick pulse: a burst-and-fade at the instant of the lock, distinct
      // from D7's fill-ring below, which is a progressive fill over a duration.
      if (ui.tiltLockRing) {
        ui.tiltLockRing.classList.remove('show');
        void ui.tiltLockRing.offsetWidth; // restart the animation
        ui.tiltLockRing.classList.add('show');
      }
    }
  },
});

/** D3: text/colour for the Settling/Aiming badge. */
function renderCalBadge(state) {
  if (!ui.calBadge) return;
  const aiming = state === 'aiming';
  ui.calBadge.textContent = aiming ? 'Aim locked' : 'Settling…';
  ui.calBadge.classList.toggle('aiming', aiming);
}

/**
 * D7: the fill-ring around the grip button for the existing (invisible) armGraceMs
 * hold requirement. animationDuration is set here, from the same ThrowTuning
 * constant the real guard reads, so the visual and the real grace window can never
 * drift out of sync as two separate magic numbers.
 */
function renderArmRing(state) {
  if (!ui.armRing) return;
  const circle = ui.armRing.querySelector('circle');
  if (state === ThrowState.ARMED) {
    if (circle) circle.style.animationDuration = `${THROW_TUNING.armGraceMs}ms`;
    ui.armRing.classList.remove('filling');
    void ui.armRing.offsetWidth; // restart the animation
    ui.armRing.classList.add('filling');
  } else {
    ui.armRing.classList.remove('filling');
  }
}

/** D8: a short qualitative line from the throw descriptor's own fields. */
function describeThrow(payload) {
  const { pitchDeg } = tiltAngleDeg({ w: payload.qw, x: payload.qx, y: payload.qy, z: payload.qz });

  const speedLabel =
    payload.swingSpeed < 2 ? 'soft swing' : payload.swingSpeed < 4 ? 'firm swing' : 'fast swing';

  const spinMag = Math.abs(payload.spinRate ?? 0);
  const spinLabel = spinMag < 100 ? 'no spin' : spinMag < 400 ? 'light spin' : 'heavy spin';

  const angleLabel =
    Math.abs(pitchDeg) < 3
      ? 'level release'
      : `${Math.abs(pitchDeg).toFixed(0)}° ${pitchDeg < 0 ? 'low' : 'high'}`;

  return `${speedLabel} · ${angleLabel} · ${spinLabel}`;
}
let connection = null;
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

// Which of the five screens is showing right now. Tracked so the "?" button can send
// openTutorial() back to wherever it was pressed from, without every call site having
// to name its own screen.
let activeScreenName = 'join';

// Whether the tutorial is the screen actually on display. showScreen is the single place
// this flips, in either direction: leaving the tutorial by any path — SKIP, LET'S PLAY, a
// live TURN, even an ERROR bouncing the phone back to the join form — always routes through
// showScreen(something-else), so there is no separate path that could leave this stuck true.
let tutorialOpen = false;

function sendInput(payload) {
  connection?.send(payload);
}

// True from this phone's THROW until the next TURN (#55). TURN only moves once the stone
// has settled, so without this the grip came straight back to TAP TO AIM mid-flight, and
// a second throw there was dropped by the TV or launched a second stone. Nothing from the
// TV acknowledges a THROW yet, so a THROW lost on the way would leave the grip locked
// forever; STONE_OUT_FALLBACK_MS is the bound on that.
let stoneOut = false;
let stoneOutTimer = null;
const STONE_OUT_FALLBACK_MS = 30_000;

function setStoneOut(out) {
  clearTimeout(stoneOutTimer);
  stoneOutTimer = null;
  stoneOut = out;

  if (out) {
    stopPreviewAim(); // no AIM through the flight; the TV hides the ghost then anyway
    stoneOutTimer = setTimeout(() => {
      setStoneOut(false);
      startTurnAim();
    }, STONE_OUT_FALLBACK_MS);
  }
  renderGripButton(thrower.state);
}

function showScreen(name) {
  for (const screen of [
    ui.joinScreen,
    ui.chooseScreen,
    ui.waitScreen,
    ui.throwScreen,
    ui.tutorialScreen,
  ]) {
    screen.classList.remove('active');
  }
  ({
    join: ui.joinScreen,
    choose: ui.chooseScreen,
    wait: ui.waitScreen,
    throw: ui.throwScreen,
    tutorial: ui.tutorialScreen,
  })[name].classList.add('active');

  // Only offered from the lobby and between-turn screens: not on the join form (nothing
  // to return to yet), not mid-throw (your own turn outranks reading about the game),
  // and not on the tutorial itself.
  ui.howToPlayBtn.hidden = name !== 'wait' && name !== 'choose';

  activeScreenName = name;
  tutorialOpen = name === 'tutorial';

  // D2: the live tilt bubble runs continuously whenever #throwScreen is up, not only
  // while Aiming — showScreen is already the one place that owns screen-transition
  // side effects, so this is a second lifecycle owner only in the sense of "off
  // everywhere else", not a second place that decides when the throw screen is shown.
  if (name === 'throw') startTiltIndicator();
  else stopTiltIndicator();
}

// ---------------------------------------------------------------- tilt indicator

// D2 on-phone live tilt indicator. Degrees of tilt past this read as fully deflected
// against the edge of the level box — a generous swing range for the wrist tilts
// this game is actually played with, not the phone's full range of motion.
const TILT_INDICATOR_MAX_DEG = 35;

// Pixel radius the bubble is allowed to travel from centre, sized to the level-box
// markup in index.html (see .tilt-box).
const TILT_INDICATOR_RADIUS_PX = 48;

let tiltRafId = null;

function updateTiltIndicator() {
  const attitude = motion.latest?.attitude;
  if (attitude && ui.tiltBubble) {
    const { pitchDeg, rollDeg } = tiltAngleDeg(attitude);
    const nx = clampTilt(rollDeg / TILT_INDICATOR_MAX_DEG);
    // Screen Y grows downward; a phone pitched back (nose up) should move the bubble
    // up on the level box, hence the sign flip here rather than in tiltAngleDeg,
    // which stays a plain geometric helper with no UI convention baked in.
    const ny = clampTilt(-pitchDeg / TILT_INDICATOR_MAX_DEG);
    const x = (nx * TILT_INDICATOR_RADIUS_PX).toFixed(1);
    const y = (ny * TILT_INDICATOR_RADIUS_PX).toFixed(1);
    ui.tiltBubble.style.transform = `translate(calc(-50% + ${x}px), calc(-50% + ${y}px))`;
  }
  tiltRafId = requestAnimationFrame(updateTiltIndicator);
}

function clampTilt(v) {
  return v < -1 ? -1 : v > 1 ? 1 : v;
}

function startTiltIndicator() {
  if (tiltRafId !== null) return;
  tiltRafId = requestAnimationFrame(updateTiltIndicator);
}

function stopTiltIndicator() {
  if (tiltRafId === null) return;
  cancelAnimationFrame(tiltRafId);
  tiltRafId = null;
}

function setStatus(text, kind = '') {
  ui.status.textContent = text;
  ui.status.className = kind;
}

// ---------------------------------------------------------------- gesture

const thrower = new ThrowController(motion, {
  tuning: THROW_TUNING,
  // Through sendAim, not sendInput: the seq the TV orders frames by is stamped there,
  // once, for every producer. See sendAim.
  onAim: (payload) => sendAim(payload),
  onThrow: (payload) => {
    sendInput(payload);
    lastThrow = payload;
    setStoneOut(true);

    // D4: a short pulse on release, distinct from the Settling→Aiming lock tick.
    if (navigator.vibrate) navigator.vibrate(20);

    ui.throwResult.textContent =
      `Speed ${payload.swingSpeed.toFixed(2)} m/s` +
      (payload.simulated ? ' · simulated (no motion sensors)' : '') +
      ` · ${describeThrow(payload)}`;
  },
  onStateChange: (state) => {
    renderGripButton(state);
    renderArmRing(state);

    // Hand the AIM stream over rather than running both. Arming replaces the flat
    // preview frames with ThrowController's own, which carry live spin and windup;
    // standing down again (a second tap, a throw, a cancelled turn) hands it back so
    // the ghost survives until TURN says the turn is over.
    // _fire() reaches here (IDLE) just before onThrow, so the stream this starts is
    // stopped again by setStoneOut a moment later.
    if (state === ThrowState.ARMED) stopPreviewAim();
    else if (!stoneOut) startTurnAim();
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
  ui.gripBtn.classList.toggle('out', stoneOut);
  ui.gripBtn.textContent = stoneOut ? 'STONE IN THE AIR' : armed ? 'SWING!' : 'TAP TO AIM';
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
  // Latest-wins (#57): skipped while bytes are still queued, so a Wi-Fi stall cannot
  // pile up stale frames for the TV to replay, with the THROW stuck behind them.
  connection?.sendIfClear({ ...payload, seq: ++aimSeq });
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

  motion.recentre();
  previewAimTimer = setInterval(() => {
    sendAim(buildAimPayload(motion.latest, 0, 0, 0));
  }, 50); // 20Hz, matching ThrowController's aimHz default
}

function stopPreviewAim() {
  // Before the early return: whatever stream was or was not running, leaving it means
  // leaving the lobby's no-tracking mode (startLobbyAim), so the next arm or turn-start
  // stream gets the normal Settling tracking back.
  motion.trackingEnabled = true;
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

  // Pointing at a rock and holding still is exactly what Settling reads as "the phone
  // is at rest, move zero here" — so the pointer slid back to centre on its own (issue
  // #23). The lobby's zero is the recentre() in startPreviewAim and the RE-CENTRE
  // button, nothing else. stopPreviewAim turns tracking back on.
  motion.trackingEnabled = false;
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

// #49: once this phone has confirmed a stone, START MATCH becomes the primary button and
// CONFIRM steps down. Local flag because SPECTATE carries no confirmed state — "rocks"
// is filled for every player with the catalog default until they pick.
let stoneConfirmed = false;

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
  // A new choose round starts unconfirmed: the flag lives only while we sit in LOBBY.
  if (!canChoose) stoneConfirmed = false;
  ui.chooseScreen.classList.toggle('stone-confirmed', stoneConfirmed);
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
    if (myPlayerId) setConnectedStatus();
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
  ui.confirmBtn.addEventListener('click', () => {
    sendInput({ type: 'PICK_ROCK' });
    stoneConfirmed = true;
    ui.chooseScreen.classList.add('stone-confirmed');
  });
  ui.recentreBtn.addEventListener('click', () => motion.recentre());
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

  // Tapping the stone opens the panel (#51). A pick leaves it open on purpose, so the
  // player watches the stone change; only the backdrop or DONE closes it.
  ui.chooseRock.addEventListener('click', () => setColourSheetOpen(true));
  ui.colourBackdrop.addEventListener('click', () => setColourSheetOpen(false));
  ui.colourDoneBtn.addEventListener('click', () => setColourSheetOpen(false));
}

/**
 * Opens or closes the colour panel. One function so every close path (backdrop, DONE,
 * leaving the choose screen, a TURN arriving) goes through the same `hidden` write.
 */
function setColourSheetOpen(open) {
  ui.colourPicker.hidden = !open;
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
 * Shows this player's actual stone on the choose screen (#51), tinted by --accent.
 *
 * The SVG strings in ROCK_ART are constants generated by tools/art/rocks/make_phone_art.py;
 * no player-supplied text ever reaches innerHTML, which is why assigning it is safe here.
 * The name goes in via textContent. Colour needs no work here: the art fills with
 * currentColor and the button's CSS colour is var(--accent), which applyMyColour sets, so a
 * colour change recolours the stone on its own. Idempotent, because SPECTATE repeats.
 */
function renderMyStone(mine) {
  const art = (mine?.rockId && ROCK_ART[mine.rockId]) || (mine?.rockName && ROCK_ART[mine.rockName]);
  if (!art) {
    ui.chooseRock.hidden = true;
    return;
  }
  if (ui.chooseRockArt.dataset.rock !== mine.rockName) {
    ui.chooseRockArt.innerHTML = art;
    ui.chooseRockArt.dataset.rock = mine.rockName;
  }
  ui.chooseRockName.textContent = mine.rockName ?? '';
  ui.chooseRock.hidden = false;
}

/**
 * Rebuilds the palette circles from the host's palette, marking the ones other players hold.
 *
 * The palette comes down the wire rather than being duplicated here: it is authored on
 * the PlayerColourPalette asset, and a second copy in this file would be the third place
 * player colours live. Idempotent — SPECTATE repeats constantly, so this returns early
 * unless the palette or who holds what actually changed (#51). Held-by-others comes from
 * message.colours, excluding this phone, matched on lower-cased hex.
 */
let renderedPalette = '';

function renderSwatches(palette, colours, names) {
  if (!Array.isArray(palette) || palette.length === 0) return;

  // Lower-cased hex -> initial of the player holding it. textContent only, never markup:
  // the name is player-supplied.
  const taken = new Map();
  for (const [id, colour] of Object.entries(colours ?? {})) {
    if (id === myPlayerId || !colour) continue;
    const initial = Array.from(String(names?.[id] ?? '').trim())[0] ?? '';
    taken.set(String(colour).toLowerCase(), initial.toUpperCase());
  }

  const key = palette.join(',') + '|' + [...taken].join(',');
  if (key === renderedPalette) return;
  renderedPalette = key;

  for (const old of ui.colourSwatches.querySelectorAll('.colour-swatch')) old.remove();
  for (const colour of palette) {
    const swatch = document.createElement('button');
    swatch.type = 'button';
    swatch.className = 'colour-swatch';
    swatch.dataset.colour = String(colour).toLowerCase();
    swatch.style.background = colour;
    swatch.setAttribute('aria-label', `Colour ${colour}`);
    if (taken.has(swatch.dataset.colour)) {
      swatch.classList.add('taken');
      swatch.textContent = taken.get(swatch.dataset.colour);
    }
    // No close here: the player picks to watch the stone change, and the OS wheel fires
    // pickColour on every frame of a drag, so a panel that shut on a pick would be a bug.
    swatch.addEventListener('click', () => pickColour(colour));
    ui.colourSwatches.insertBefore(swatch, ui.colourCustom);
  }

  applyMyColour(myColour ?? requestedColour);
}

function bindGrip() {
  // A tap, not a hold. The finger has to be off the phone during the swing, and
  // holding a button through a throwing motion is both awkward and a good way to
  // launch the phone. touchend rather than touchstart so a drag off the button
  // does not count.
  const tap = (event) => {
    event.preventDefault(); // stop the tap turning into a scroll, zoom or click
    if (stoneOut) return; // your stone is still out; the next TURN re-opens this (#55)
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

// ---------------------------------------------------------------- tutorial

/**
 * One inline SVG `<g>` for a phone held in a hand — the shared piece every tutorial page
 * draws its scene from, rather than each page hand-authoring its own phone shape. This is
 * literal on purpose (a rounded phone body with a camera dot, gripping fingers, a thumb)
 * rather than an abstract glyph: the walkthrough exists to show what to physically do with
 * the phone, so the phone needs to actually look like a phone in a hand, at a glance.
 *
 * `dim` renders a faint echo (an earlier position in a motion, not the current one) and
 * `tapRipple` draws the ripple that marks the exact instant a tap is captured.
 */
function phoneGlyph({ x = 0, y = 0, rotate = 0, scale = 1, dim = false, tapRipple = false } = {}) {
  return `<g transform="translate(${x},${y}) rotate(${rotate}) scale(${scale})" opacity="${dim ? 0.3 : 1}">
    <path d="M -20 4 Q -25 30 -6 35 L 16 35 Q 27 32 25 12 Q 23 -1 10 -1 L -12 -1 Q -20 -1 -20 4 Z" fill="var(--muted)"/>
    <rect x="-15" y="-32" width="30" height="64" rx="7" fill="var(--surface)" stroke="var(--text)" stroke-width="2.5"/>
    <rect x="-11" y="-27" width="22" height="47" rx="3" fill="var(--text)" opacity="0.16"/>
    <circle cx="0" cy="-27" r="1.6" fill="var(--text)" opacity="0.5"/>
    <ellipse cx="-17" cy="6" rx="4.2" ry="3" fill="var(--muted)"/>
    <ellipse cx="-18" cy="14" rx="4.6" ry="3.3" fill="var(--muted)"/>
    <ellipse cx="-17" cy="22" rx="4.2" ry="3" fill="var(--muted)"/>
    <path d="M 6 19 Q 17 15 19 25 Q 15 33 4 29 Z" fill="var(--muted)"/>
    ${
      tapRipple
        ? `<circle cx="0" cy="-8" r="4" fill="none" stroke="var(--accent)" stroke-width="1.6"/>
           <circle cx="0" cy="-8" r="9" fill="none" stroke="var(--accent)" stroke-width="1" opacity="0.5"/>`
        : ''
    }
  </g>`;
}

/**
 * The pre-round walkthrough: four short pages mirroring docs/SkipSchool.html, cut down to
 * what fits a phone screen at a glance. Each visual is a small illustrated scene built from
 * phoneGlyph — a real hand-and-phone pose, a motion arrow, an angle or a distance — rather
 * than an abstract icon, so the point reads before the caption underneath it is even read.
 */
const TUTORIAL_PAGES = [
  {
    title: 'Tap · Swing · Stop',
    body: 'Tap the stone to aim, swing your arm like a real throw, then stop dead. The stop is the release — there is no button to let go of.',
    visual: `<svg viewBox="0 0 200 150" fill="none">
      <defs><marker id="tutArrowGo" markerWidth="7" markerHeight="7" refX="5" refY="3.5" orient="auto"><path d="M0,0 L7,3.5 L0,7 Z" fill="var(--accent)"/></marker></defs>
      ${phoneGlyph({ x: 38, y: 105, tapRipple: true })}
      <path d="M62 92 Q95 45 138 40" stroke="var(--accent)" stroke-width="2.5" fill="none" stroke-dasharray="1 7" stroke-linecap="round" marker-end="url(#tutArrowGo)"/>
      ${phoneGlyph({ x: 158, y: 48, rotate: -58 })}
      <path d="M180 18 L186 30 M195 22 L198 35 M170 12 L172 25" stroke="var(--bad)" stroke-width="2.5" stroke-linecap="round"/>
    </svg>`,
  },
  {
    title: 'Hold still, then aim',
    body: 'While you hold the phone steady, "flat, facing the TV" quietly follows however you’re gripping it. The moment you start to tilt on purpose, that line locks — every degree from there is your aim.',
    visual: `<svg viewBox="0 0 200 150" fill="none">
      ${phoneGlyph({ x: 42, y: 78, tapRipple: true })}
      <line x1="8" y1="78" x2="192" y2="78" stroke="var(--muted)" stroke-width="1.5" stroke-dasharray="2 5" opacity="0.8"/>
      ${phoneGlyph({ x: 148, y: 78, rotate: -32 })}
      <path d="M180 78 A 32 32 0 0 0 168 49" stroke="var(--accent)" stroke-width="2.5" fill="none" stroke-linecap="round"/>
      <path d="M166 52 L168 49 L172 55" stroke="var(--accent)" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
    </svg>`,
  },
  {
    title: 'Swing speed = distance',
    body: 'The harder and faster you swing, the farther the stone flies. That is the only thing that sets distance — a gentle swing stays short no matter how you release it.',
    visual: `<svg viewBox="0 0 200 150" fill="none">
      ${phoneGlyph({ x: 30, y: 100, rotate: -12, scale: 0.8 })}
      <path d="M18 78 Q25 62 38 66" stroke="var(--muted)" stroke-width="2" fill="none" stroke-linecap="round"/>
      <path d="M48 98 Q56 112 68 114" stroke="var(--muted)" stroke-width="2" fill="none" stroke-dasharray="1 5" stroke-linecap="round"/>
      <circle cx="68" cy="114" r="3" fill="var(--muted)"/>
      <line x1="100" y1="20" x2="100" y2="132" stroke="var(--text)" stroke-width="1" opacity="0.14"/>
      ${phoneGlyph({ x: 148, y: 105, rotate: -58, scale: 1.05 })}
      <path d="M104 35 Q118 8 145 16 M100 48 Q113 25 135 30 M96 60 Q107 42 124 45" stroke="var(--accent)" stroke-width="2" fill="none" stroke-linecap="round"/>
      <path d="M165 82 Q186 105 196 138" stroke="var(--accent)" stroke-width="2.3" fill="none" stroke-dasharray="1 5" stroke-linecap="round"/>
      <circle cx="196" cy="138" r="3.8" fill="var(--accent)"/>
    </svg>`,
  },
  {
    title: 'Angle + spin = skip or sink',
    body: 'Release roughly level with a flick of the wrist and it skips clean across the water. Get the angle wrong, or forget the flick, and it sinks fast — even off a hard swing.',
    visual: `<svg viewBox="0 0 200 150" fill="none">
      <defs><marker id="tutArrowBad" markerWidth="8" markerHeight="8" refX="4" refY="6" orient="auto"><path d="M0,0 L8,0 L4,8 Z" fill="var(--bad)"/></marker></defs>
      <circle cx="26" cy="18" r="11" fill="none" stroke="var(--good)" stroke-width="2.5"/>
      <path d="M21 18 l4 4 l7 -8" stroke="var(--good)" stroke-width="2.5" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
      <line x1="4" y1="105" x2="90" y2="105" stroke="var(--muted)" stroke-width="2"/>
      <g transform="translate(24,60) rotate(-14)">
        <rect x="-17" y="-7" width="34" height="14" rx="4.5" fill="var(--surface)" stroke="var(--good)" stroke-width="2.5"/>
      </g>
      <path d="M17 56 a11 11 0 1 1 -3 9" stroke="var(--good)" stroke-width="2" fill="none" stroke-linecap="round"/>
      <path d="M9 44 l3 -9 l9 3" stroke="var(--good)" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
      <path d="M2 105 Q18 94 34 105 T66 105 T90 100" stroke="var(--good)" stroke-width="2.5" fill="none"/>
      <circle cx="34" cy="105" r="2.4" fill="var(--good)"/>
      <circle cx="66" cy="105" r="2" fill="var(--good)"/>
      <circle cx="174" cy="18" r="11" fill="none" stroke="var(--bad)" stroke-width="2.5"/>
      <path d="M170 14 l8 8 M178 14 l-8 8" stroke="var(--bad)" stroke-width="2.5" stroke-linecap="round"/>
      <line x1="110" y1="105" x2="196" y2="105" stroke="var(--muted)" stroke-width="2"/>
      <g transform="translate(155,50) rotate(62)">
        <rect x="-17" y="-7" width="34" height="14" rx="4.5" fill="var(--surface)" stroke="var(--bad)" stroke-width="2.5"/>
      </g>
      <path d="M148 66 L153 100" stroke="var(--bad)" stroke-width="2.5" stroke-linecap="round" marker-end="url(#tutArrowBad)"/>
      <path d="M140 106 Q153 98 166 106 M144 112 Q153 106 162 112" stroke="var(--bad)" stroke-width="2" fill="none" stroke-linecap="round"/>
    </svg>`,
  },
];

let tutorialIndex = 0;
let tutorialReturnScreen = 'wait';

function renderTutorialPage() {
  const page = TUTORIAL_PAGES[tutorialIndex];
  ui.tutorialVisual.innerHTML = page.visual;
  ui.tutorialTitle.textContent = page.title;
  ui.tutorialBody.textContent = page.body;

  ui.tutorialDots.replaceChildren();
  TUTORIAL_PAGES.forEach((_, i) => {
    const dot = document.createElement('span');
    dot.className = 'tutorial-dot' + (i === tutorialIndex ? ' active' : '');
    ui.tutorialDots.appendChild(dot);
  });

  ui.tutorialBackBtn.hidden = tutorialIndex === 0;
  const last = tutorialIndex === TUTORIAL_PAGES.length - 1;
  ui.tutorialNextBtn.textContent = last ? "LET'S PLAY" : 'NEXT';
}

/**
 * Opens the walkthrough. `returnTo` is which screen to restore on close — defaults to
 * whatever was showing, so the "?" button needs no per-screen wiring of its own.
 */
function openTutorial(returnTo = activeScreenName) {
  tutorialReturnScreen = returnTo === 'tutorial' ? 'wait' : returnTo;
  tutorialIndex = 0;
  renderTutorialPage();
  showScreen('tutorial');
}

function closeTutorial() {
  showScreen(tutorialReturnScreen);
}

function bindTutorial() {
  ui.howToPlayBtn.addEventListener('click', () => openTutorial());
  ui.tutorialSkipBtn.addEventListener('click', () => closeTutorial());
  ui.tutorialBackBtn.addEventListener('click', () => {
    if (tutorialIndex > 0) {
      tutorialIndex--;
      renderTutorialPage();
    }
  });
  ui.tutorialNextBtn.addEventListener('click', () => {
    if (tutorialIndex < TUTORIAL_PAGES.length - 1) {
      tutorialIndex++;
      renderTutorialPage();
    } else {
      closeTutorial();
    }
  });
}

// ---------------------------------------------------------------- messages

function handleMessage(message) {
  switch (message.type) {
    case 'JOINED':
      myPlayerId = message.playerId;
      myTurn = false; // a fresh join/rejoin has no standing TURN yet — wait for one
      setStoneOut(false); // the TURN that follows a join says where things stand
      stopPreviewAim(); // and no standing stream either; the next SPECTATE/TURN starts one
      hasJoinedOnce = true; // a later ERROR/ROOM_CLOSED now means "get back in", not "unknown player"
      setConnectedStatus();
      renderNameBtn();
      showScreen('wait');
      motion.start();
      requestWakeLock();

      // Auto-shown once per device, right after the first successful join. Marked seen
      // immediately rather than on dismissal, so a player who gets interrupted by a live
      // TURN mid-walkthrough isn't shown it again on their next join — it stays reachable
      // afterward through the "?" button either way.
      if (!loadIdentity()?.tutorialSeen) {
        updateIdentity({ tutorialSeen: true });
        openTutorial('wait');
      }

      // playerId is now the device id, not the typed name (docs/plans/seamless-rejoin.md
      // Workstream C) — the relay has no idea what to display until this arrives. Sent on
      // every JOINED, not just a first-time join, so a rename mid-match (myName updated,
      // then this same path re-runs on a reconnect) reaches the host too.
      if (myName) connection.send({ type: 'SET_NAME', name: myName });
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
      // A live turn always outranks the tutorial, whether it's this phone's turn or not —
      // reopen it later from the "?" button. No explicit close needed: the branches below
      // call showScreen('throw') or showScreen('wait') either way, which is what actually
      // clears tutorialOpen.

      // Any TURN means the match has left the lobby — stop here rather than waiting
      // for the next SPECTATE, so the phone is never streaming AIM into a turn it
      // no longer applies to.
      stopLobbyAim();
      setColourSheetOpen(false); // a turn has begun; the panel has no place over it (#51)
      myTurn = message.yourTurn;
      setStoneOut(false); // any TURN means the last stone has settled (#55)

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
      break;

    case 'HOST_RESUMED':
      setConnectedStatus();
      break;

    case 'ROOM_CLOSED':
      myTurn = false; // before cancel(), whose IDLE transition would otherwise re-start the stream
      setStoneOut(false);
      thrower.cancel();
      stopPreviewAim();

      // Workstream B's stable room code means a host restart hands back the same code, so
      // a phone that already held a seat should retry rather than eject — it looks
      // identical to the host never having left. The join form stays the response to an
      // actual, permanent end of match for a phone that never held a seat here.
      if (hasJoinedOnce && connection?.credentials) {
        setStatus('Host restarting…', 'bad');
        connection.retryJoin();
        break;
      }

      setStatus('Game ended', 'bad');
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
/**
 * The status bar once connected. The display name, not myPlayerId: that is the relay's
 * device id (e.g. 4XO7R9DH4R33), meaningless to the person holding the phone (#45).
 */
function setConnectedStatus() {
  setStatus(myName ? `Connected as ${myName}` : 'Connected', 'ok');
}

function renderLastThrow(message, phase) {
  if (phase !== 'PLAYING') return '';
  if (!message.lastPlayer || typeof message.lastSkips !== 'number') return '';

  // Comparison stays against the id; only the rendered label changes. message.names is the
  // id -> display-name map TurnManager sends alongside scores/rocks — see
  // docs/plans/seamless-rejoin.md Workstream C. Falls back to the raw id so a build without
  // PlayerNames running (or a name that hasn't arrived yet) still shows something.
  const who = message.lastPlayer === myPlayerId ? 'You' : (message.names?.[message.lastPlayer] ?? message.lastPlayer);
  // The TV's format, MatchHud.FormatThrow ("143m   9sk"), so one throw reads the same on
  // the phone as on the TV (#47). Rounded to whole metres there too.
  const skips = `${message.lastSkips}sk`;
  if (typeof message.lastDistance !== 'number') return `${who}: ${skips}`;
  return `${who}: ${Math.round(message.lastDistance)}m\u2003${skips}`;
}

function renderSpectate(message) {
  if (thrower.state === ThrowState.ARMED) return; // don't yank the screen mid-throw
  if (tutorialOpen) return; // don't yank it out from under a player reading the tutorial either

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
  renderSwatches(message.palette, message.colours, message.names);
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

    // Only present once ShoreRocks has resolved a highlight for this phone and
    // TurnManager has broadcast it — blank until then, which the CSS collapses.
    renderMyStone(message.rocks?.[myPlayerId]);
    return;
  }

  // Not while this phone is the one aiming. SPECTATE is broadcast state and goes out on
  // every turn start and throw completion, so an unconditional stop here killed the
  // turn-idle stream a moment after TURN started it — and with it the TV's ghost. TURN
  // is the personalised message, and it already stops the stream for everyone else.
  if (!myTurn) stopPreviewAim();

  // Off the choose screen the picker has nowhere to be — a mid-throw colour change is
  // allowed by the host but there is no screen real estate for it here. Closed so a
  // mid-match re-pick does not reopen a panel left up from before.
  setColourSheetOpen(false);

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
  // Sorted and numbered (#46). `.me` marks this phone's own row, `.active` whose turn
  // it is — separate things that can land on the same row.
  for (const { player, score, rank } of rankScores(message.scores)) {
    const row = document.createElement('div');
    row.className = 'score-row'
      + (player === message.activePlayer ? ' active' : '')
      + (player === myPlayerId ? ' me' : '');

    // textContent, never innerHTML. A display name is attacker-controlled text that every
    // other phone renders, exactly as the typed name used to be before Workstream C moved
    // it off playerId and into SET_NAME/message.names — the relay's sanitizePlayerId
    // uppercase+16-char cap no longer applies to it. Interpolating it as markup let a name
    // like <H1>OWNED</H1> render as markup on everyone else's screen; textContent is what
    // still rules that out.
    const name = document.createElement('span');
    name.className = 'name';

    const rankEl = document.createElement('span');
    rankEl.className = 'score-rank';
    rankEl.textContent = rank;
    name.appendChild(rankEl);

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

    if (player === myPlayerId) {
      const you = document.createElement('span');
      you.className = 'you-tag';
      you.textContent = 'YOU';
      name.appendChild(you);
    }

    const points = document.createElement('span');
    points.textContent = score;

    row.append(name, points);
    ui.scores.appendChild(row);
  }
}

// ---------------------------------------------------------------- join

/** Builds the RelayConnection the first time either join path needs one. */
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

// #44: the room code the join screen used to show, now just a remembered value. Filled from
// ?room= (a QR scan) or the stored identity, empty otherwise; the relay accepts empty or stale.
let hiddenRoomCode = '';

async function join() {
  const name = ui.nameInput.value.trim();
  // #44: there is no code box any more. Send whatever came from ?room= or storage (the relay
  // falls back to its only open room if it is stale), or an empty code if there is none.
  const code = hiddenRoomCode;

  if (!name) {
    ui.joinError.textContent = 'Enter your name.';
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

  // Confirmed on device: must be called inside the user gesture that is this tap.
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
    // A5/A6 guard state — onset run toward swingOnsetSamples, and whether the arm
    // grace window (armGraceMs) is still blocking a swing from starting. Both are
    // otherwise invisible from outside ThrowController, which is exactly why a
    // real throw that gets eaten on hardware is hard to tune blind.
    // The grace countdown reads the same clock the guard does — sample time since
    // _graceStartT — not performance.now() since arm(). Those two clocks need not
    // share an origin, and a readout that disagrees with the rule it describes is
    // worse than none while tuning armGraceMs on a phone.
    `onset    ${thrower._onsetRun}/${thrower.tuning.swingOnsetSamples}   grace ${
      thrower._graceStartT === null || !s
        ? 'n/a'
        : `${Math.max(0, thrower.tuning.armGraceMs - (s.t - thrower._graceStartT)).toFixed(0)}ms`
    }`,
    `socket   ${connection?.status ?? 'not connected'}`,
    `calib    ${motion.calState}`,
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

  // ?resetIdentity=1 wipes this device's stored identity before anything below reads it, so
  // a phone that has already joined once can be put back into its first-run state (new
  // deviceId, empty name, tutorial unseen) without digging into dev tools. Testing-only —
  // never set by the app itself.
  if (params.get('resetIdentity')) clearIdentity();

  const prefill = params.get('room');
  if (prefill) hiddenRoomCode = prefill.trim().toUpperCase();

  ui.joinBtn.addEventListener('click', join);
  // A sensor readout for testing, not for players: shown only with ?debug=1 (#48).
  ui.debugToggle.hidden = params.get('debug') !== '1';
  ui.debugToggle.addEventListener('click', () => ui.debug.classList.toggle('visible'));
  bindChoose();
  bindAdvance();
  bindGrip();
  bindRename();
  bindTutorial();
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
  if (!hiddenRoomCode && identity?.room) hiddenRoomCode = identity.room;

  setInterval(updateDebug, 100);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') requestWakeLock();
  });
}

init();
