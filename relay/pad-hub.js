'use strict';

// Phones connected as controllers (issue #2): accepts their raw MOTION stream on the
// `/pad` WebSocket path, gives each one of four slots, and publishes samples for
// whatever consumes them (the DSU server, #3). Kept apart from the game's rooms so
// its rate budget is sized for a 60-200 Hz stream rather than the game's 20 Hz AIM.

const { EventEmitter } = require('events');

const MAX_PADS = 4; // DSU has four slots
// A phone delivers devicemotion at most around 200 Hz; anything far above that is a
// misbehaving client, not a fast phone.
const MAX_MESSAGES_PER_SECOND = 400;
const RATE_WINDOW_MS = 1000;

function isFiniteTriple(value) {
  return (
    Array.isArray(value) &&
    value.length === 3 &&
    value.every((n) => typeof n === 'number' && Number.isFinite(n))
  );
}

/**
 * Validate one MOTION message from a phone. Returns a clean sample or null.
 * Boundary check: everything past here trusts the shape.
 */
function parseMotionMessage(data) {
  if (!data || data.type !== 'MOTION') return null;
  if (!Number.isSafeInteger(data.seq) || data.seq < 0) return null;
  if (!Number.isSafeInteger(data.ts) || data.ts < 0) return null;
  if (!isFiniteTriple(data.accel) || !isFiniteTriple(data.gyro)) return null;
  return { seq: data.seq, ts: data.ts, accel: data.accel.slice(), gyro: data.gyro.slice() };
}

// One bit per button in controller/js/gamepad-state.js; anything above is not a button.
const PAD_BUTTON_MASK = (1 << 17) - 1;

/**
 * Validate one PAD message (the on-screen gamepad, #4). Returns a clean state or null.
 * Sticks are clamped to -1..1 rather than rejected: a rounding overshoot is not an attack.
 */
function parsePadMessage(data) {
  if (!data || data.type !== 'PAD') return null;
  if (!Number.isSafeInteger(data.seq) || data.seq < 0) return null;
  if (!Number.isInteger(data.buttons) || data.buttons < 0 || data.buttons > PAD_BUTTON_MASK)
    return null;
  if (
    !Array.isArray(data.sticks) ||
    data.sticks.length !== 4 ||
    !data.sticks.every((n) => typeof n === 'number' && Number.isFinite(n))
  ) {
    return null;
  }
  return {
    seq: data.seq,
    buttons: data.buttons,
    sticks: data.sticks.map((n) => Math.max(-1, Math.min(1, n))),
  };
}

function createPadHub({ now = () => Date.now(), log = () => {} } = {}) {
  const events = new EventEmitter();
  const slots = new Array(MAX_PADS).fill(null);

  function freeSlot() {
    return slots.findIndex((pad) => pad === null);
  }

  function attach(ws, remoteAddress) {
    const slot = freeSlot();
    if (slot === -1) {
      ws.send(
        JSON.stringify({ type: 'PAD_REJECTED', reason: 'All four controller slots are in use.' }),
      );
      ws.close(1013, 'pad slots full');
      log(`[pad] rejected ${remoteAddress}: all slots in use`);
      return null;
    }

    const pad = {
      slot,
      remoteAddress,
      connectedAt: now(),
      latest: null,
      gamepad: null, // latest PAD state; motion and buttons arrive separately
      padMessages: 0,
      received: 0,
      dropped: 0, // gaps in seq: lost or reordered on the way
      rejected: 0, // malformed or over the rate cap
      arrivals: [],
      rateWindowStart: now(),
      rateCount: 0,
    };
    slots[slot] = pad;

    // The server's heartbeat sweep reads ws.meta.isAlive on every client.
    ws.meta = { role: 'pad', isAlive: true, remoteAddress };
    ws.on('pong', () => {
      ws.meta.isAlive = true;
    });

    ws.send(JSON.stringify({ type: 'PAD_ACCEPTED', slot }));
    log(`[pad] slot ${slot} connected from ${remoteAddress}`);
    events.emit('connect', slot);

    ws.on('message', (raw) => {
      const t = now();
      if (t - pad.rateWindowStart >= RATE_WINDOW_MS) {
        pad.rateWindowStart = t;
        pad.rateCount = 0;
      }
      if (++pad.rateCount > MAX_MESSAGES_PER_SECOND) {
        pad.rejected++;
        return;
      }

      let data;
      try {
        data = JSON.parse(raw);
      } catch {
        pad.rejected++;
        return;
      }
      if (data && data.type === 'PING') {
        ws.send(JSON.stringify({ type: 'PONG', t: data.t }));
        return;
      }
      if (data && data.type === 'PAD') {
        const state = parsePadMessage(data);
        if (!state) {
          pad.rejected++;
          return;
        }
        // Each PAD message is the whole state, so a late one is simply stale.
        if (pad.gamepad && state.seq <= pad.gamepad.seq) return;
        pad.gamepad = state;
        pad.padMessages++;
        events.emit('pad', slot, state);
        return;
      }
      const sample = parseMotionMessage(data);
      if (!sample) {
        pad.rejected++;
        return;
      }

      if (pad.latest && sample.seq > pad.latest.seq + 1)
        pad.dropped += sample.seq - pad.latest.seq - 1;
      // An older seq arriving late is stale: keep the newest as `latest`.
      if (!pad.latest || sample.seq > pad.latest.seq) pad.latest = sample;
      pad.received++;
      pad.arrivals.push(t);
      events.emit('sample', slot, sample);
    });

    ws.on('close', () => {
      if (slots[slot] === pad) slots[slot] = null;
      log(`[pad] slot ${slot} disconnected`);
      events.emit('disconnect', slot);
    });

    return slot;
  }

  function rateOf(pad, t) {
    const cutoff = t - RATE_WINDOW_MS;
    while (pad.arrivals.length && pad.arrivals[0] <= cutoff) pad.arrivals.shift();
    return (pad.arrivals.length * 1000) / RATE_WINDOW_MS;
  }

  /** What the debug readout shows: one entry per slot, null when empty. */
  function snapshot() {
    const t = now();
    return slots.map((pad) =>
      pad
        ? {
            slot: pad.slot,
            remoteAddress: pad.remoteAddress,
            connectedSec: Math.round((t - pad.connectedAt) / 1000),
            rateHz: rateOf(pad, t),
            received: pad.received,
            dropped: pad.dropped,
            rejected: pad.rejected,
            gamepad: pad.gamepad,
            padMessages: pad.padMessages,
            latest: pad.latest,
          }
        : null,
    );
  }

  return { attach, snapshot, events };
}

module.exports = {
  createPadHub,
  parseMotionMessage,
  parsePadMessage,
  MAX_PADS,
  MAX_MESSAGES_PER_SECOND,
};
