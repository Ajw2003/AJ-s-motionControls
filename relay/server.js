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
const qrcode = require('qrcode-generator');
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
// AIM streams at the sensor rate (60Hz) while a player is gripping, so the ceiling
// has to sit well clear of that. At exactly 60 the limiter starts discarding aim
// frames the moment a throw or a heartbeat shares the same second — silently, since
// a flooding client deliberately gets no feedback — and the arc stutters for reasons
// that look nothing like rate limiting from the game side.
const RATE_MAX_MESSAGES = 180;

// No I and no O — these are read aloud off a TV and typed on a phone.
const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ';
const CODE_LENGTH = 4;
const MAX_PLAYER_ID_LENGTH = 16;

// A join URL is well under this. The endpoint is reachable through the public
// tunnel, so it takes a bound rather than trusting the caller.
const MAX_QR_DATA_LENGTH = 512;

// Latest-value-wins streams: when the destination socket is backed up, delivering
// these late is strictly worse than not delivering them at all, because a fresher
// frame is at most a sensor tick behind. TCP would otherwise queue every stale
// frame and replay the whole backlog in order. The relay stays game-agnostic
// apart from this two-entry drop list.
const LATEST_WINS_TYPES = new Set(['AIM', 'SPECTATE']);
const BACKPRESSURE_BYTES = 4096;

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

/** Pre-serialized send, so a broadcast stringifies once, not once per recipient. */
function sendRaw(ws, json, dropWhenCongested = false) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  if (dropWhenCongested && ws.bufferedAmount > BACKPRESSURE_BYTES) return;
  ws.send(json);
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

  // Tell the host too, and clear its registration so a re-sent CREATE_ROOM
  // succeeds. Without this an idle-swept room left the Unity host alive but
  // permanently bricked: every broadcast hit NOT_IN_ROOM and CREATE_ROOM bounced
  // off ALREADY_REGISTERED, with no way back short of restarting the game.
  // When the host itself disconnected, the send is a no-op on a closed socket.
  send(room.host, { type: 'ROOM_CLOSED', reason });
  if (room.host.meta) {
    room.host.meta.role = null;
    room.host.meta.roomCode = null;
  }

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

  // A connection already registered as a player that joins again under a
  // different name or room would otherwise leave its old slot pointing at this
  // socket forever — a ghost that counts toward the room cap, never gets a
  // PLAYER_LEFT, and shows a stale name on the TV.
  if (ws.meta.role === 'player' && ws.meta.roomCode &&
      (ws.meta.roomCode !== code || ws.meta.playerId !== playerId)) {
    const oldRoom = rooms.get(ws.meta.roomCode);
    if (oldRoom && oldRoom.players.get(ws.meta.playerId) === ws) {
      oldRoom.players.delete(ws.meta.playerId);
      send(oldRoom.host, { type: 'PLAYER_LEFT', playerId: ws.meta.playerId });
      log(`room ${oldRoom.code}: ${ws.meta.playerId} left (rejoined as someone else)`);
    }
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

  // Latest-wins streams are dropped rather than queued behind a backed-up host
  // socket — a queued AIM frame is already obsolete by the time it would drain.
  if (LATEST_WINS_TYPES.has(data.type) && room.host.bufferedAmount > BACKPRESSURE_BYTES) return;

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

  const dropWhenCongested = LATEST_WINS_TYPES.has(data.type);
  const json = JSON.stringify(data);

  if (data.targetPlayerId) {
    // Sanitized with the same rules used when the key was stored, or a long name
    // can never match its truncated slot.
    const target = room.players.get(sanitizePlayerId(data.targetPlayerId) ?? '');
    if (target) sendRaw(target, json, dropWhenCongested);
    return;
  }

  for (const [, playerWs] of room.players) {
    sendRaw(playerWs, json, dropWhenCongested);
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

  // Refuse anything that escapes the static root. The path-separator check
  // matters: a bare prefix test also accepts sibling directories whose names
  // merely start with "controller".
  if (resolved !== STATIC_DIR && !resolved.startsWith(STATIC_DIR + nodePath.sep)) {
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

/**
 * QR module matrix for an arbitrary string, as JSON.
 *
 * Returns the raw black/white grid rather than a PNG so the caller renders it at
 * whatever size it needs — Unity draws this on a TV, where a fixed-resolution
 * image would either band or blur. Rows are strings of '0'/'1' because Unity's
 * JsonUtility cannot deserialize nested arrays.
 */
function serveQr(req, res, query) {
  const data = query.get('data');

  if (!data || data.length > MAX_QR_DATA_LENGTH) {
    res.writeHead(400, { 'Content-Type': 'application/json' })
      .end(JSON.stringify({ error: `data must be 1..${MAX_QR_DATA_LENGTH} characters` }));
    return;
  }

  try {
    // Type 0 picks the smallest version that fits. Level M survives the glare and
    // off-angle scanning you get pointing a phone at a television.
    const qr = qrcode(0, 'M');
    qr.addData(data);
    qr.make();

    const size = qr.getModuleCount();
    const rows = [];
    for (let row = 0; row < size; row++) {
      let bits = '';
      for (let col = 0; col < size; col++) bits += qr.isDark(row, col) ? '1' : '0';
      rows.push(bits);
    }

    res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-cache' })
      .end(JSON.stringify({ size, rows }));
  } catch (err) {
    res.writeHead(400, { 'Content-Type': 'application/json' })
      .end(JSON.stringify({ error: String(err.message || err) }));
  }
}

const httpServer = http.createServer((req, res) => {
  // Base is a formality — only the path and query are ever read.
  const url = new URL(req.url || '/', 'http://localhost');

  if (url.pathname === '/health') {
    res.writeHead(200, { 'Content-Type': 'application/json' })
      .end(JSON.stringify({ ok: true, rooms: rooms.size }));
    return;
  }

  if (url.pathname === '/qr') {
    serveQr(req, res, url.searchParams);
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
        // Echo the client's timestamp so it can measure round-trip time — the
        // number that separates tunnel latency from everything the code owns.
        send(ws, { type: 'PONG', t: typeof data.t === 'number' ? data.t : undefined });
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
