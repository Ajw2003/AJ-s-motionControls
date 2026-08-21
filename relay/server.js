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
const { createResponder } = require('./dns');

const PORT = Number(process.env.PORT) || 3000;

// Must default to all interfaces. On Fly the proxy reaches this process from
// outside the VM, so binding loopback there would refuse every connection and
// fail the health check on deploy. Behind a reverse proxy on the SAME host —
// the Caddy/VPS route in deploy/README.md — set BIND_HOST=127.0.0.1 so the
// plain-HTTP relay is not reachable from the LAN alongside the TLS one.
const BIND_HOST = process.env.BIND_HOST || '0.0.0.0';
const MAX_PLAYERS_PER_ROOM = 8;
const MAX_MESSAGE_BYTES = 4096;
const HEARTBEAT_MS = 30_000;
const ROOM_IDLE_TIMEOUT_MS = 4 * 60 * 60 * 1000; // 4 hours
const ROOM_SWEEP_MS = 5 * 60 * 1000;

// How long a room outlives its host. A host socket drops for dull reasons — a
// redeploy, a Wi-Fi blip, the machine being restarted underneath it — and closing
// the room the instant that happens ejects every player and forces them to retype
// a code that no longer exists. Holding it briefly lets the host reclaim it and
// everyone carry on. Overridable so the tests do not have to wait a minute.
const HOST_GRACE_MS = Number(process.env.HOST_GRACE_MS) || 60_000;
const HOST_GRACE_SWEEP_MS = 2_000;
const RATE_WINDOW_MS = 1000;
const RATE_MAX_MESSAGES = 60; // AIM streams at 20Hz; leaves headroom for bursts

// No I and no O — these are read aloud off a TV and typed on a phone.
const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ';
const CODE_LENGTH = 4;
const MAX_PLAYER_ID_LENGTH = 16;

// A join URL is well under this. The endpoint is reachable through the public
// tunnel, so it takes a bound rather than trusting the caller.
const MAX_QR_DATA_LENGTH = 512;

/** @type {Map<string, {code: string, host: WebSocket, players: Map<string, WebSocket>, lastActivity: number, hostGoneAt: number|null}>} */
const rooms = new Map();

// Counters for /health. Two integer increments on the hot path, which is cheap
// enough to leave on permanently — and without them there is no way to answer
// "how close is this box to its limit?" except by waiting for it to fall over.
const metrics = { messagesIn: 0, messagesOut: 0, roomsCreated: 0, startedAt: Date.now() };

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
    metrics.messagesOut++;
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

function handleCreateRoom(ws, data) {
  if (ws.meta.role) {
    sendError(ws, 'ALREADY_REGISTERED', 'This connection already has a role.');
    return;
  }

  // A host that dropped asks for its previous code back, so every phone still
  // holding it reconnects instead of the room silently becoming unreachable and
  // everyone having to retype. Honoured only when the code is genuinely free or
  // its host is gone — a live room is never handed to a different connection.
  const requested = normalizeRoomCode(data && data.preferredCode);

  if (requested) {
    const existing = rooms.get(requested);

    if (existing && (existing.hostGoneAt !== null || existing.host.readyState !== WebSocket.OPEN)) {
      resumeRoom(existing, ws);
      return;
    }

    if (!existing) {
      createRoom(ws, requested, true);
      return;
    }
    // Otherwise it is in use by a live host; fall through and mint a fresh one.
  }

  const code = generateRoomCode();
  if (!code) {
    sendError(ws, 'NO_CODES_AVAILABLE', 'Could not allocate a room code.');
    return;
  }

  createRoom(ws, code, false);
}

function createRoom(ws, code, reclaimed) {
  const room = { code, host: ws, players: new Map(), lastActivity: Date.now(), hostGoneAt: null };
  rooms.set(code, room);
  metrics.roomsCreated++;

  ws.meta.role = 'host';
  ws.meta.roomCode = code;

  send(ws, { type: 'ROOM_CREATED', roomCode: code, resumed: false });
  log(`room ${code} created${reclaimed ? ' (code reclaimed after host restart)' : ''}`);
}

/**
 * Hand a room whose host vanished to the reconnecting host, keeping every player
 * in place. The players never learn the socket flapped beyond one HOST_RESUMED.
 */
function resumeRoom(room, ws) {
  const previous = room.host;

  room.host = ws;
  room.hostGoneAt = null;
  room.lastActivity = Date.now();

  ws.meta.role = 'host';
  ws.meta.roomCode = room.code;

  // Detach the old socket first: its close handler checks `room.host === ws` to
  // decide whether the host left, and must not now think it did.
  if (previous && previous.meta) previous.meta.roomCode = null;
  if (previous && previous.readyState === WebSocket.OPEN) {
    previous.close(1000, 'Replaced by a reconnecting host');
  }

  send(ws, {
    type: 'ROOM_CREATED',
    roomCode: room.code,
    resumed: true,
    players: [...room.players.keys()],
  });

  for (const [, playerWs] of room.players) send(playerWs, { type: 'HOST_RESUMED' });

  log(`room ${room.code} resumed by a reconnecting host (${room.players.size} players kept)`);
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

// LOAD-BEARING. This began as a spike and its comment said to delete it; it is now
// the only way the game is played. CertificateProvider hands this process a LAN
// certificate and a key, and the phone will not hand out motion sensors to a page
// that is not on a secure origin — so without this branch there is no input.
//
// Env-gated, so the default path is byte-identical to before: with neither
// variable set, this is the plain http.createServer it has always been.
const TLS_CERT = process.env.TLS_CERT;
const TLS_KEY = process.env.TLS_KEY;
const useTls = Boolean(TLS_CERT && TLS_KEY);

// Local DNS, in the same env-gated shape and for the same reason: with the variable
// unset, nothing about this file changes.
//
// Set in HOTSPOT MODE ONLY. On a LAN the phones use the router's resolver, and running
// one here would be answering questions nobody asked us. DNS_ALLOW is not optional when
// DNS_RESPOND_IP is set — dns.js refuses to start without it, because an unguarded
// resolver is a DDoS amplifier rather than merely untidy. See SecurityReview.md §4.
const DNS_RESPOND_IP = process.env.DNS_RESPOND_IP;
const DNS_ALLOW = process.env.DNS_ALLOW;
const DNS_SUFFIX = process.env.DNS_SUFFIX || '.local-ip.sh';
const DNS_UPSTREAM = process.env.DNS_UPSTREAM || null;
const useDns = Boolean(DNS_RESPOND_IP);

let dnsResponder = null;

function handleRequest(req, res) {
  // Base is a formality — only the path and query are ever read.
  const url = new URL(req.url || '/', 'http://localhost');

  if (url.pathname === '/health') {
    let players = 0;
    for (const room of rooms.values()) players += room.players.size;

    const memory = process.memoryUsage();

    res.writeHead(200, { 'Content-Type': 'application/json' })
      .end(JSON.stringify({
        ok: true,
        rooms: rooms.size,
        players,
        connections: wss.clients.size,
        roomsCreated: metrics.roomsCreated,
        messagesIn: metrics.messagesIn,
        messagesOut: metrics.messagesOut,
        rssMb: Math.round(memory.rss / 1048576),
        heapMb: Math.round(memory.heapUsed / 1048576),
        uptimeSec: Math.round((Date.now() - metrics.startedAt) / 1000),
      }));
    return;
  }

  if (url.pathname === '/qr') {
    serveQr(req, res, url.searchParams);
    return;
  }

  serveStatic(req, res);
}

// The WebSocket server attaches to either kind of listener unchanged, so wss://
// comes free with the TLS branch and needs no separate handling.
const httpServer = useTls
  ? require('https').createServer(
      { cert: fs.readFileSync(TLS_CERT), key: fs.readFileSync(TLS_KEY) },
      handleRequest)
  : http.createServer(handleRequest);

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
    metrics.messagesIn++;
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
        handleCreateRoom(ws, data);
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
      // Held rather than closed. See HOST_GRACE_MS — the host is very often coming
      // straight back, and ejecting everyone the moment its socket flaps is what
      // makes a redeploy or a Wi-Fi blip look like the game crashed.
      room.hostGoneAt = Date.now();
      for (const [, playerWs] of room.players) send(playerWs, { type: 'HOST_LEFT' });
      log(`room ${roomCode}: host disconnected, holding for ${HOST_GRACE_MS}ms`);
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

// Separate from the idle sweep because it runs on a much shorter clock: a host that
// is not coming back should not strand its players for minutes.
const hostGraceSweep = setInterval(() => {
  const now = Date.now();
  for (const room of [...rooms.values()]) {
    if (room.hostGoneAt !== null && now - room.hostGoneAt > HOST_GRACE_MS) {
      closeRoom(room, 'Host disconnected');
    }
  }
}, HOST_GRACE_SWEEP_MS);

wss.on('close', () => {
  clearInterval(heartbeat);
  clearInterval(roomSweep);
  clearInterval(hostGraceSweep);
});

function shutdown() {
  log('shutting down');
  clearInterval(heartbeat);
  clearInterval(roomSweep);
  clearInterval(hostGraceSweep);
  for (const room of [...rooms.values()]) closeRoom(room, 'Server shutting down');

  // Released explicitly so a restart can rebind it. Leaving :53 held by a dying process
  // is exactly how the race against ICS gets lost on the next run.
  if (dnsResponder) { dnsResponder.stop().catch(() => {}); dnsResponder = null; }

  wss.close(() => httpServer.close(() => process.exit(0)));
  setTimeout(() => process.exit(0), 3000).unref();
}

process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);

httpServer.listen(PORT, BIND_HOST, () => {
  const scheme = useTls ? 'https' : 'http';
  const wsScheme = useTls ? 'wss' : 'ws';
  log(`Rock Skipping relay listening on ${BIND_HOST}:${PORT}${useTls ? ' (TLS)' : ''}`);
  log(`  controller page  ${scheme}://localhost:${PORT}/`);
  log(`  websocket        ${wsScheme}://localhost:${PORT}/`);
  log(`  serving static from ${STATIC_DIR}`);

  if (useDns) startDns();
});

function startDns() {
  try {
    dnsResponder = createResponder({
      ip: DNS_RESPOND_IP,
      suffix: DNS_SUFFIX,
      allow: DNS_ALLOW,
      upstream: DNS_UPSTREAM,
      log,
    });
  } catch (err) {
    // Thrown for a missing DNS_ALLOW, which is a configuration mistake rather than a
    // runtime condition. Refusing to start the resolver is correct; killing the relay
    // with it is not, because the game still works on a LAN without one.
    log(`DNS responder NOT started: ${err.message}`);
    dnsResponder = null;
    return;
  }

  dnsResponder.start().catch((err) => {
    // EADDRINUSE here almost certainly means ICS got :53 first, which breaks name
    // resolution for every phone on the hotspot. Loud, because the symptom at the phone
    // is an unrelated-looking "server not found".
    log(`DNS responder FAILED to bind: ${err.code || err.message}`);
    log('  Phones on the hotspot will not resolve the game address.');
    log('  The responder must bind :53 before tethering starts.');
    dnsResponder = null;
  });
}
