'use strict';

/**
 * Rock Skipping relay server.
 *
 * Routes messages between one Unity host (the PC driving the TV) and up to
 * MAX_PLAYERS_PER_ROOM phone controllers, keyed by a short room code.
 *
 * Routing is deliberately generic: the relay knows nothing about AIM, THROW or
 * SPECTATE. Anything a player sends goes to the host; anything the host sends
 * goes to every player, or to one player if it carries `targetPlayerId`. New
 * message types can be added to the game without touching this file.
 */

const { WebSocketServer, WebSocket } = require('ws');
const crypto = require('crypto');
const http = require('http');
const fs = require('fs');
const nodePath = require('path');

const PORT = Number(process.env.PORT) || 3000;
const MAX_PLAYERS_PER_ROOM = 8;
const MAX_MESSAGE_BYTES = 4096;
const HEARTBEAT_MS = 30_000;
const ROOM_IDLE_TIMEOUT_MS = 4 * 60 * 60 * 1000; // 4 hours
const ROOM_SWEEP_MS = 5 * 60 * 1000;
const RATE_WINDOW_MS = 1000;
const RATE_MAX_MESSAGES = 60; // AIM streams at 20Hz; leaves headroom for bursts

// No I and no O — these are read aloud off a TV and typed on a phone.
const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ';
const CODE_LENGTH = 4;
const MAX_PLAYER_ID_LENGTH = 16;

/** @type {Map<string, {code: string, host: WebSocket, players: Map<string, WebSocket>, lastActivity: number}>} */
const rooms = new Map();

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

function generateRoomCode() {
  // 24^4 = 331,776 combinations, so collisions are rare; retry a bounded number
  // of times rather than looping forever if the space somehow fills up.
  for (let attempt = 0; attempt < 100; attempt++) {
    let code = '';
    for (let i = 0; i < CODE_LENGTH; i++) {
      code += CODE_ALPHABET[crypto.randomInt(CODE_ALPHABET.length)];
    }
    if (!rooms.has(code)) return code;
  }
  return null;
}

function send(ws, payload) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(payload));
  }
}

function sendError(ws, code, message) {
  send(ws, { type: 'ERROR', code, message });
}

function log(...args) {
  console.log(new Date().toISOString(), ...args);
}

function sanitizePlayerId(raw) {
  if (typeof raw !== 'string') return null;
  const trimmed = raw.trim().toUpperCase().slice(0, MAX_PLAYER_ID_LENGTH);
  return trimmed.length > 0 ? trimmed : null;
}

function normalizeRoomCode(raw) {
  if (typeof raw !== 'string') return null;
  const trimmed = raw.trim().toUpperCase();
  return trimmed.length === CODE_LENGTH ? trimmed : null;
}

/** Rolling-window rate limit. Returns true if the message should be dropped. */
function isRateLimited(meta) {
  const now = Date.now();
  if (now - meta.rateWindowStart >= RATE_WINDOW_MS) {
    meta.rateWindowStart = now;
    meta.rateCount = 0;
  }
  meta.rateCount++;
  return meta.rateCount > RATE_MAX_MESSAGES;
}

function closeRoom(room, reason) {
  for (const [, playerWs] of room.players) {
    send(playerWs, { type: 'ROOM_CLOSED', reason });
    if (playerWs.meta) playerWs.meta.roomCode = null;
    playerWs.close(1000, reason);
  }
  room.players.clear();
  rooms.delete(room.code);
  log(`room ${room.code} closed (${reason})`);
}

// ---------------------------------------------------------------------------
// message handlers
// ---------------------------------------------------------------------------

function handleCreateRoom(ws) {
  if (ws.meta.role) {
    sendError(ws, 'ALREADY_REGISTERED', 'This connection already has a role.');
    return;
  }

  const code = generateRoomCode();
  if (!code) {
    sendError(ws, 'NO_CODES_AVAILABLE', 'Could not allocate a room code.');
    return;
  }

  const room = { code, host: ws, players: new Map(), lastActivity: Date.now() };
  rooms.set(code, room);

  ws.meta.role = 'host';
  ws.meta.roomCode = code;

  send(ws, { type: 'ROOM_CREATED', roomCode: code });
  log(`room ${code} created`);
}

function handleJoinRoom(ws, data) {
  if (ws.meta.role === 'host') {
    sendError(ws, 'HOST_CANNOT_JOIN', 'A host connection cannot join as a player.');
    return;
  }

  const code = normalizeRoomCode(data.roomCode);
  if (!code) {
    sendError(ws, 'BAD_ROOM_CODE', 'Room codes are 4 letters.');
    return;
  }

  const playerId = sanitizePlayerId(data.playerId);
  if (!playerId) {
    sendError(ws, 'BAD_PLAYER_ID', 'Please enter a name.');
    return;
  }

  const room = rooms.get(code);
  // The original prototype stayed silent here, so the phone optimistically
  // showed the game screen for a room that never existed. Always answer.
  if (!room) {
    sendError(ws, 'ROOM_NOT_FOUND', `No game found with code ${code}.`);
    return;
  }

  const existing = room.players.get(playerId);
  const isReconnect = Boolean(existing);

  if (!isReconnect && room.players.size >= MAX_PLAYERS_PER_ROOM) {
    sendError(ws, 'ROOM_FULL', `This game is full (${MAX_PLAYERS_PER_ROOM} players).`);
    return;
  }

  // Reconnect: a phone that slept or dropped Wi-Fi reclaims its slot by name.
  if (isReconnect && existing !== ws) {
    if (existing.meta) existing.meta.roomCode = null;
    existing.close(1000, 'Replaced by a new connection');
  }

  room.players.set(playerId, ws);
  room.lastActivity = Date.now();

  ws.meta.role = 'player';
  ws.meta.roomCode = code;
  ws.meta.playerId = playerId;

  send(ws, { type: 'JOINED', roomCode: code, playerId, reconnected: isReconnect });
  send(room.host, { type: 'PLAYER_JOINED', playerId, reconnected: isReconnect });
  log(`room ${code}: ${playerId} ${isReconnect ? 'reconnected' : 'joined'} (${room.players.size} players)`);
}

/** Player -> host. */
function relayToHost(ws, data) {
  const room = rooms.get(ws.meta.roomCode);
  if (!room) {
    sendError(ws, 'NOT_IN_ROOM', 'You are not in a game.');
    return;
  }
  room.lastActivity = Date.now();
  // Stamp the sender server-side so a phone cannot spoof another player.
  data.playerId = ws.meta.playerId;
  send(room.host, data);
}

/** Host -> one player (when `targetPlayerId` is set) or all players. */
function relayToPlayers(ws, data) {
  const room = rooms.get(ws.meta.roomCode);
  if (!room) {
    sendError(ws, 'NOT_IN_ROOM', 'This host has no room.');
    return;
  }
  room.lastActivity = Date.now();

  if (data.targetPlayerId) {
    const target = room.players.get(String(data.targetPlayerId).toUpperCase());
    if (target) send(target, data);
    return;
  }

  for (const [, playerWs] of room.players) {
    send(playerWs, data);
  }
}

// ---------------------------------------------------------------------------
// server
// ---------------------------------------------------------------------------

// The controller page is served from this same origin on purpose. Motion sensors
// need a secure context, and an HTTPS page cannot open a plain ws:// socket — so
// splitting the page and the socket across two origins means fighting mixed
// content forever. One origin gives HTTPS and WSS together, and one tunnel
// (or one Fly app) covers both.
const STATIC_DIR = nodePath.resolve(__dirname, '..', 'controller');

const CONTENT_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

function serveStatic(req, res) {
  const requested = decodeURIComponent((req.url || '/').split('?')[0]);
  const relative = requested === '/' ? 'index.html' : requested.replace(/^\/+/, '');
  const resolved = nodePath.resolve(STATIC_DIR, relative);

  // Refuse anything that escapes the static root.
  if (!resolved.startsWith(STATIC_DIR)) {
    res.writeHead(403).end('Forbidden');
    return;
  }

  fs.readFile(resolved, (err, data) => {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain' }).end('Not found');
      return;
    }
    const type = CONTENT_TYPES[nodePath.extname(resolved).toLowerCase()] || 'application/octet-stream';
    res.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'no-cache' }).end(data);
  });
}

const httpServer = http.createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(200, { 'Content-Type': 'application/json' })
      .end(JSON.stringify({ ok: true, rooms: rooms.size }));
    return;
  }
  serveStatic(req, res);
});

const wss = new WebSocketServer({ server: httpServer, maxPayload: MAX_MESSAGE_BYTES });

wss.on('connection', (ws) => {
  ws.meta = {
    role: null,
    roomCode: null,
    playerId: null,
    isAlive: true,
    rateWindowStart: Date.now(),
    rateCount: 0,
  };

  ws.on('pong', () => {
    ws.meta.isAlive = true;
  });

  ws.on('message', (raw) => {
    if (isRateLimited(ws.meta)) return; // drop silently; a flooding client gets no feedback loop

    let data;
    try {
      data = JSON.parse(raw);
    } catch {
      // The prototype let this throw, which killed the whole process and every
      // other room with it. One bad client must never take down the server.
      sendError(ws, 'BAD_JSON', 'Message was not valid JSON.');
      return;
    }

    if (!data || typeof data !== 'object' || typeof data.type !== 'string') {
      sendError(ws, 'BAD_MESSAGE', 'Message must be an object with a "type".');
      return;
    }

    switch (data.type) {
      case 'CREATE_ROOM':
        handleCreateRoom(ws);
        return;
      case 'JOIN_ROOM':
        handleJoinRoom(ws, data);
        return;
      case 'PING':
        send(ws, { type: 'PONG' });
        return;
      default:
        if (ws.meta.role === 'player') relayToHost(ws, data);
        else if (ws.meta.role === 'host') relayToPlayers(ws, data);
        else sendError(ws, 'NOT_REGISTERED', 'Send CREATE_ROOM or JOIN_ROOM first.');
    }
  });

  ws.on('close', () => {
    const { role, roomCode, playerId } = ws.meta;
    if (!roomCode) return;

    const room = rooms.get(roomCode);
    if (!room) return;

    if (role === 'host' && room.host === ws) {
      closeRoom(room, 'Host disconnected');
      return;
    }

    if (role === 'player' && room.players.get(playerId) === ws) {
      room.players.delete(playerId);
      send(room.host, { type: 'PLAYER_LEFT', playerId });
      log(`room ${roomCode}: ${playerId} left (${room.players.size} players)`);
    }
  });

  ws.on('error', (err) => log('socket error:', err.message));
});

// Drop sockets that stopped responding — phones that lose signal often never
// send a clean close frame, which would otherwise leak room slots forever.
const heartbeat = setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.meta) continue;
    if (!ws.meta.isAlive) {
      ws.terminate();
      continue;
    }
    ws.meta.isAlive = false;
    ws.ping();
  }
}, HEARTBEAT_MS);

// Rooms whose host vanished without a close frame would otherwise persist for
// the lifetime of the process.
const roomSweep = setInterval(() => {
  const now = Date.now();
  for (const room of [...rooms.values()]) {
    if (now - room.lastActivity > ROOM_IDLE_TIMEOUT_MS) {
      closeRoom(room, 'Room idle timeout');
    }
  }
}, ROOM_SWEEP_MS);

wss.on('close', () => {
  clearInterval(heartbeat);
  clearInterval(roomSweep);
});

function shutdown() {
  log('shutting down');
  clearInterval(heartbeat);
  clearInterval(roomSweep);
  for (const room of [...rooms.values()]) closeRoom(room, 'Server shutting down');
  wss.close(() => httpServer.close(() => process.exit(0)));
  setTimeout(() => process.exit(0), 3000).unref();
}

process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);

httpServer.listen(PORT, () => {
  log(`Rock Skipping relay listening on :${PORT}`);
  log(`  controller page  http://localhost:${PORT}/`);
  log(`  websocket        ws://localhost:${PORT}/`);
  log(`  serving static from ${STATIC_DIR}`);
});
