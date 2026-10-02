'use strict';

/**
 * A stand-in TV host for exercising a phone against a relay without Unity.
 *
 * It does what TurnManager does for one player at a time. Every phone that joins gets
 * TURN yourTurn:true. Each THROW is logged, and SETTLE_MS later (the stone "settling")
 * TURN is sent again. Once a second it prints how many AIM frames each phone sent, which
 * is how to see the stream stop during a stone's flight, or a stall's backlog land at once.
 *
 *   node relay/fake-host.js                      (relay on ws://127.0.0.1:3000)
 *   RELAY_URL=ws://127.0.0.1:3998 SETTLE_MS=5000 node relay/fake-host.js
 *
 * Stop it with Ctrl+C.
 */

const { WebSocket } = require('ws');

const RELAY_URL = process.env.RELAY_URL || 'ws://127.0.0.1:3000';
const SETTLE_MS = Number(process.env.SETTLE_MS) || 3000;

const aimCounts = new Map();

function log(...args) {
  console.log(new Date().toISOString().slice(11, 23), ...args);
}

const ws = new WebSocket(RELAY_URL);

function send(payload) {
  ws.send(JSON.stringify(payload));
}

function giveTurn(playerId) {
  send({ type: 'SPECTATE', phase: 'PLAYING', activePlayer: playerId, scores: { [playerId]: 0 } });
  send({ type: 'TURN', targetPlayerId: playerId, yourTurn: true });
  log(`TURN -> ${playerId} (yourTurn)`);
}

ws.on('open', () => send({ type: 'CREATE_ROOM' }));
ws.on('error', (err) => {
  console.error(`fake-host: cannot reach ${RELAY_URL}: ${err.message}`);
  process.exit(1);
});
ws.on('close', () => {
  log('relay closed the connection');
  process.exit(1);
});

ws.on('message', (raw) => {
  const message = JSON.parse(raw);
  switch (message.type) {
    case 'ROOM_CREATED':
      log(`room ${message.roomCode} open on ${RELAY_URL}; settle ${SETTLE_MS} ms`);
      break;
    case 'PLAYER_JOINED':
      log(`${message.playerId} joined${message.reconnected ? ' (reconnect)' : ''}`);
      giveTurn(message.playerId);
      break;
    case 'PLAYER_LEFT':
      log(`${message.playerId} left`);
      break;
    case 'AIM':
      aimCounts.set(message.playerId, (aimCounts.get(message.playerId) ?? 0) + 1);
      break;
    case 'THROW':
      log(
        `THROW from ${message.playerId}: swing ${Number(message.swingSpeed).toFixed(2)} m/s` +
          `${message.simulated ? ' (simulated)' : ''}`,
      );
      setTimeout(() => giveTurn(message.playerId), SETTLE_MS);
      break;
    default:
      log(`${message.type}${message.playerId ? ` from ${message.playerId}` : ''}`);
  }
});

setInterval(() => {
  if (aimCounts.size === 0) return;
  log(`AIM/s ${[...aimCounts].map(([id, n]) => `${id}=${n}`).join(' ')}`);
  aimCounts.clear();
}, 1000);
