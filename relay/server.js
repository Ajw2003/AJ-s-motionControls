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
 *
 * Static file serving is deliberately an allowlist rather than a directory listing
 * (see `isAllowedRelativePath`): a bare prefix check on the resolved path let a
 * phone read anything checked into `controller/` — package.json, eslint config,
 * test scripts, whatever gets added next to `js/` later — with nothing to notice
 * a new file had joined the public surface (C4, docs/Findings-NetworkAndThrowStability.md).
 * Naming exactly what a phone needs (`/`, `index.html`, `js/<name>.js`) keeps that
 * surface from silently growing. Request handling is also per-request try/catch'd
 * rather than guarded by a global `uncaughtException` handler, because a malformed
 * request path used to throw synchronously with nothing catching it and took the
 * whole process — every room on it — down with it (C1, same doc). A handler scoped
 * to one request can never do that; a global one would only paper over the crash.
 */

const { WebSocketServer, WebSocket } = require('ws');
const qrcode = require('qrcode-generator');
const crypto = require('crypto');
const http = require('http');
const fs = require('fs');
const nodePath = require('path');
const { createResponder, makeSubnetTest } = require('./dns');
const { createFunnel, Stage } = require('./funnel');

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

// Application close code for "a newer connection took this same player/host slot".
// Distinct from the ordinary 1000 on purpose: the displaced socket's client-side
// onclose cannot tell 1000 apart from the host itself vanishing, and reconnecting
// in response is exactly the join ping-pong documented in
// docs/Findings-DirectTransportFlapping.md. Exported so net.js can compare against
// the same literal rather than drifting to a second one.
const REPLACED_CLOSE_CODE = 4001;

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
  sendFunnelSnapshot(ws);
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
    previous.close(REPLACED_CLOSE_CODE, 'Replaced by a reconnecting host');
  }

  send(ws, {
    type: 'ROOM_CREATED',
    roomCode: room.code,
    resumed: true,
    players: [...room.players.keys()],
  });

  sendFunnelSnapshot(ws);

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
    existing.close(REPLACED_CLOSE_CODE, 'Replaced by a new connection');
  }

  room.players.set(playerId, ws);
  room.lastActivity = Date.now();

  ws.meta.role = 'player';
  ws.meta.roomCode = code;
  ws.meta.playerId = playerId;

  if (funnel) funnel.record(ws.meta.remoteAddress, Stage.PLAYING);

  send(ws, { type: 'JOINED', roomCode: code, playerId, reconnected: isReconnect });
  send(room.host, { type: 'PLAYER_JOINED', playerId, reconnected: isReconnect });
  log(
    `room ${code}: ${playerId} ${isReconnect ? 'reconnected' : 'joined'} (${room.players.size} players)`,
  );
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

// Only these are ever handed back: `/`, `index.html`, and `js/<name>.js`. This is an
// allowlist rather than "serve anything under controller/" (C4) — the old prefix check
// let a phone read package.json, eslint.config.js, test-sensors.js, anything else
// checked into the folder, since nothing distinguished "the app" from "the repo the
// app happens to live in". Naming exactly what the phone needs keeps that from
// silently growing back the next time a file gets added next to js/.
function isAllowedRelativePath(relative) {
  if (relative === 'index.html') return true;
  if (/^js\/[^/]+\.js$/.test(relative)) return true;
  return false;
}

function serveStatic(req, res) {
  const rawPath = (req.url || '/').split('?')[0];
  let requested;
  try {
    // A malformed escape (e.g. a lone `%`) makes decodeURIComponent throw a URIError.
    // Before this guard that throw was synchronous inside the request handler with
    // nothing catching it anywhere in the process, so Node exited and took every room
    // on the relay down with it (C1) — one phone crashing the whole match. Answering
    // 400 keeps this request-scoped, like every other malformed-input case here.
    requested = decodeURIComponent(rawPath);
  } catch (err) {
    if (err instanceof URIError) {
      res.writeHead(400, { 'Content-Type': 'text/plain' }).end('Bad request');
      return;
    }
    throw err;
  }

  const relative = requested === '/' ? 'index.html' : requested.replace(/^\/+/, '');

  if (!isAllowedRelativePath(relative)) {
    res.writeHead(404, { 'Content-Type': 'text/plain' }).end('Not found');
    return;
  }

  const resolved = nodePath.resolve(STATIC_DIR, relative);

  // Belt and braces alongside the allowlist above: the allowlist's regex already rules
  // out `..` segments, but a resolved path is cheap to double-check and this is the
  // same containment test the rest of the file already reasoned about. `+ path.sep`
  // (not just `STATIC_DIR`) so a sibling directory whose name merely starts with
  // "controller" can never pass (C4).
  if (!resolved.startsWith(STATIC_DIR + nodePath.sep)) {
    res.writeHead(403).end('Forbidden');
    return;
  }

  fs.readFile(resolved, (err, data) => {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain' }).end('Not found');
      return;
    }

    // The controller document itself, not its assets: a phone that fetched only a stylesheet
    // has not opened the game, and counting that as PAGE_OPEN would report a phone as ready
    // to play before it has loaded the page.
    if (funnel && relative === 'index.html') {
      funnel.record(req.socket.remoteAddress, Stage.PAGE_OPEN);
    }

    const type =
      CONTENT_TYPES[nodePath.extname(resolved).toLowerCase()] || 'application/octet-stream';
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
    res
      .writeHead(400, { 'Content-Type': 'application/json' })
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

    res
      .writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-cache' })
      .end(JSON.stringify({ size, rows }));
  } catch (err) {
    res
      .writeHead(400, { 'Content-Type': 'application/json' })
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
// DNS_NAME, not a suffix: the certificate LocalServices hands this process is single-name
// now (see Assets/Scripts/Net/MagicDomain.cs), so the responder answers exactly one name
// rather than any label ending in a wildcard's suffix. The literal default here is a
// genuine fallback, not a second source of truth — LocalServices always passes its own
// MagicDomain.HostName explicitly, so this only matters if the relay is started by hand
// with DNS_RESPOND_IP set and DNS_NAME left unset.
const DNS_NAME = process.env.DNS_NAME || 'play.skiprocking.ca';
const DNS_UPSTREAM = process.env.DNS_UPSTREAM || null;
const useDns = Boolean(DNS_RESPOND_IP);

// Android and iOS each probe a domain of their choosing right after joining a network, to
// decide whether to show "No internet" and whether to keep cellular as the default route.
// Guests are isolated from the host's own internet by design, so these probes always fail on
// their own, and "it just stopped working" reports are very likely a phone that decided this
// network was useless rather than an actual game bug. Answering them ourselves (this list, plus
// /generate_204 and /hotspot-detect.html below) fixes the symptom without re-enabling
// forwarding and undoing the isolation decision — see docs/plans/magic-domain-replacement.md
// Phase 4 and docs/Decision-OfflineHosting.md.
//
// Every name here is one we intend to SERVE: the phone's plain-HTTP probe reaches :80 and gets
// a 204 or "Success". Adding a name in order to have it refused is a different idea and does
// not belong here — `www.google.com`, Android's HTTPS probe host, was added on exactly that
// reasoning and removed again when hardware testing falsified it. Android resolved it, got our
// address, and still issued no HTTP probe.
const CONNECTIVITY_PROBE_DOMAINS = [
  'connectivitycheck.gstatic.com',
  'connectivitycheck.android.com',
  'clients3.google.com',
  'captive.apple.com',
  'www.appleiphonecell.com',
];

let dnsResponder = null;
let http80Server = null;

/**
 * The join funnel (funnel.js), or null outside Hotspot mode.
 *
 * Gated on `useDns` because every stage of it is a statement about a phone on the hotspot
 * subnet: without a hotspot there is no DNS_ALLOW to confine tracking to, every request on
 * a developer's machine arrives from 127.0.0.1, and reporting that as "a phone is waiting
 * to scan" would be worse than reporting nothing.
 */
const funnel =
  useDns && DNS_ALLOW
    ? createFunnel({
        isTracked: makeSubnetTest(DNS_ALLOW),
        onChange: (counts) => broadcastFunnelToHosts(counts),
      })
    : null;

/**
 * Tells every connected host what the funnel currently sees. Sent to hosts rather than to a
 * room, because the funnel is keyed by IP and has no idea which room a device would land
 * in — in practice a local relay serves exactly one host, and a second one is a developer's
 * second Editor rather than a stranger.
 */
function broadcastFunnelToHosts(counts) {
  for (const room of rooms.values()) {
    send(room.host, { type: 'DEVICE_FUNNEL', ...counts });
  }
}

/**
 * Hands one host the funnel as it stands right now. `onChange` only fires on a change, so
 * without this a host that connects (or restarts, which is routine — stopping and starting
 * the Unity Editor) sees nothing at all until the next phone does something, even with
 * three already sitting on the Wi-Fi waiting to be told to scan.
 */
function sendFunnelSnapshot(ws) {
  if (!funnel) return;
  send(ws, { type: 'DEVICE_FUNNEL', ...funnel.counts() });
}

// True only once the UDP :53 bind has actually resolved — the listen() callback below only
// *initiates* the bind, it doesn't wait for it, so a caller that needs to know DNS is really
// listening (HotspotService, before it starts tethering — the responder must win the race
// against ICS for :53) has to check this, not just that /health answers at all.
let dnsBound = false;

function handleRequest(req, res) {
  // Wrapped per-request rather than with a global `process.on('uncaughtException')`
  // (C1): the latter would keep the process alive but in unknown state — rooms half
  // torn down, timers possibly stopped — and Node itself only guarantees it's safe to
  // exit after one, not to keep serving. Catching here means one bad request answers
  // 500 and every other room and every other request keeps working exactly as before.
  try {
    // Base is a formality — only the path and query are ever read.
    const url = new URL(req.url || '/', 'http://localhost');

    if (url.pathname === '/health') {
      let players = 0;
      for (const room of rooms.values()) players += room.players.size;

      const memory = process.memoryUsage();

      res.writeHead(200, { 'Content-Type': 'application/json' }).end(
        JSON.stringify({
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
          dns: { enabled: useDns, bound: dnsBound },
        }),
      );
      return;
    }

    if (url.pathname === '/qr') {
      serveQr(req, res, url.searchParams);
      return;
    }

    if (serveConnectivityProbe(res, url.pathname)) return;

    serveStatic(req, res);
  } catch (err) {
    log('request handler error:', err && err.message ? err.message : err);
    if (!res.headersSent) {
      res.writeHead(500, { 'Content-Type': 'text/plain' }).end('Internal error');
    }
  }
}

/** Where a phone has to end up. Every address in it is a constant — see MagicDomain.cs. */
const GAME_URL = `https://${DNS_NAME}:${PORT}/`;

/**
 * Answers Android's and iOS's connectivity probes, and returns true if it handled the request.
 * Returns false for anything else so the caller keeps routing.
 *
 * Always the "there is internet here" answer, so the phone stops flagging "No internet" and
 * stops preferring cellular — a phone that drops to cellular mid-match is the failure Phase 4
 * was written to prevent (docs/plans/magic-domain-replacement.md).
 */
function serveConnectivityProbe(res, pathname) {
  // Android treats anything other than a bare 204 as "there is a login page here" — so the
  // answer must be an empty body, not JSON, not even an empty object.
  if (pathname === '/generate_204') {
    res.writeHead(204).end();
    return true;
  }

  // Apple looks for this exact literal body (no whitespace, no trailing newline) on a plain
  // page load.
  if (pathname === '/hotspot-detect.html') {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }).end('Success');
    return true;
  }

  return false;
}

// The WebSocket server attaches to either kind of listener unchanged, so wss://
// comes free with the TLS branch and needs no separate handling.
const httpServer = useTls
  ? require('https').createServer(
      { cert: fs.readFileSync(TLS_CERT), key: fs.readFileSync(TLS_KEY) },
      handleRequest,
    )
  : http.createServer(handleRequest);

const wss = new WebSocketServer({ server: httpServer, maxPayload: MAX_MESSAGE_BYTES });

wss.on('connection', (ws, req) => {
  ws.meta = {
    role: null,
    roomCode: null,
    playerId: null,
    isAlive: true,
    rateWindowStart: Date.now(),
    rateCount: 0,
    // Captured here rather than read at JOIN time: by then the socket may be mid-close and
    // remoteAddress reads back undefined. Only the funnel uses it, and only to count devices.
    remoteAddress: req && req.socket ? req.socket.remoteAddress : null,
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
  if (dnsResponder) {
    dnsResponder.stop().catch(() => {});
    dnsResponder = null;
  }
  dnsBound = false;

  if (http80Server) {
    try {
      http80Server.close();
    } catch {
      // Already down, or never bound — nothing to recover from, safe to ignore.
    }
    http80Server = null;
  }

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

  if (useDns) {
    startDns();
    // Connectivity probes are cleartext HTTP on :80, never :8443, so they need their own
    // listener regardless of whether the main server is TLS. Hotspot-only, same as DNS —
    // gated on useDns rather than a separate flag because there is no reason to want one
    // without the other.
    startHttpProbeListener();
  }
});

async function startDns() {
  try {
    dnsResponder = createResponder({
      ip: DNS_RESPOND_IP,
      names: [DNS_NAME, ...CONNECTIVITY_PROBE_DOMAINS],
      allow: DNS_ALLOW,
      upstream: DNS_UPSTREAM,
      // Teed into probe-access.log as well as stdout: which names the phone asks for is
      // the half of the join picture the :80 log cannot show.
      log: logDnsLine,
      onQuery: (address) => {
        if (funnel) funnel.record(address, Stage.ASSOCIATED);
      },
    });
  } catch (err) {
    // Thrown for a missing DNS_ALLOW, which is a configuration mistake rather than a
    // runtime condition. Refusing to start the resolver is correct; killing the relay
    // with it is not, because the game still works on a LAN without one.
    log(`DNS responder NOT started: ${err.message}`);
    dnsResponder = null;
    return;
  }

  try {
    await dnsResponder.start();
    dnsBound = true;
  } catch (err) {
    // EADDRINUSE here almost certainly means ICS got :53 first, which breaks name
    // resolution for every phone on the hotspot. Loud, because the symptom at the phone
    // is an unrelated-looking "server not found".
    log(`DNS responder FAILED to bind: ${err.code || err.message}`);
    log('  Phones on the hotspot will not resolve the game address.');
    log('  The responder must bind :53 before tethering starts.');
    dnsResponder = null;
  }
}

/**
 * What :80 answers. It has its own handler rather than reusing `handleRequest`, and that is
 * a bug fix, not tidying: `handleRequest` ends in `serveStatic`, so this listener used to
 * serve the ENTIRE CONTROLLER over plain HTTP on every interface the host has, despite the
 * comment below claiming it served the probes only. A guest who typed the host name without
 * `https://` got a controller page that loaded, looked correct, and could not read a single
 * sensor — a secure context is required for DeviceMotion and there is none here. Redirecting
 * instead turns that silent dead end into the right page. See
 * docs/plans/join-failure-reduction.md §1.
 */
/**
 * Appends one line to probe-access.log, beside this file.
 *
 * The :80 listener and the DNS responder are the only parts of the relay a phone touches
 * before it has joined, and they are the parts nobody can see: the relay is a child of the
 * Unity Editor, so its stdout reaches the Console only when LocalServices.verboseLogging is
 * on. Three separate diagnoses in this project were blind exactly here — netstat proves a
 * connection arrived but not what it asked for, and "the phone shows No internet" looks
 * identical whether the probe never came, came for a path we do not answer, or was answered
 * correctly and ignored by the platform. A file sidesteps the Unity plumbing entirely, and
 * survives the session that produced it.
 *
 * Best-effort by design: a logging failure must never take down a listener a guest is
 * mid-request on, so every error here is swallowed.
 */
function appendAccessLog(line) {
  try {
    fs.appendFileSync(nodePath.join(__dirname, 'probe-access.log'), `${line}\n`);
  } catch {
    // Diagnostics are never worth failing a request over.
  }
}

/** One line per :80 request: who asked, for what, with which Host, and what we answered. */
function logProbeRequest(req, pathname, outcome) {
  const address = req.socket.remoteAddress || '?';
  const host = req.headers.host || '?';
  appendAccessLog(
    [new Date().toISOString(), address, req.method, pathname, `Host=${host}`, outcome].join(' '),
  );
}

/**
 * The DNS responder's log line, teed to stdout and to the access log.
 *
 * Which names a phone asks for is the other half of the picture the :80 log gives, and the
 * half that distinguishes "the phone never probed" from "the phone probed someone else's
 * resolver" — a distinction that decides whether the fix is ours at all.
 */
function logDnsLine(...args) {
  log(...args);
  const line = args.join(' ');
  if (line.startsWith('dns ')) appendAccessLog(`${new Date().toISOString()} ${line}`);
}

function handleProbeRequest(req, res) {
  const url = new URL(req.url || '/', 'http://localhost');

  if (serveConnectivityProbe(res, url.pathname)) {
    logProbeRequest(req, url.pathname, `answered ${res.statusCode}`);
    return;
  }

  res.writeHead(302, { Location: GAME_URL }).end();
  logProbeRequest(req, url.pathname, `302 -> ${GAME_URL}`);
}

/**
 * Plain-HTTP listener on :80 for the connectivity probes — the game
 * itself is never served here (see handleProbeRequest), only redirected to.
 *
 * Per the plan's resolved open question: some machines already have :80 taken (IIS, Docker
 * Desktop, Skype's old binding), and a failed launch is a worse outcome than a phone that
 * shows "No internet" while the game still works perfectly over :8443. So this DEGRADES
 * QUIETLY — EADDRINUSE and friends are caught and logged, never thrown past this function —
 * and LOGS LOUDLY, because the resulting symptom is easy to misdiagnose as a network
 * problem rather than a cosmetic one.
 */
function startHttpProbeListener() {
  http80Server = http.createServer(handleProbeRequest);

  http80Server.on('error', (err) => {
    log(`Connectivity probe listener on :80 FAILED to bind: ${err.code || err.message}`);
    log('  This is cosmetic: guests may see "No internet" on their phone, but the game');
    log('  itself is unaffected — it is served on :8443, not :80.');
    http80Server = null;
  });

  http80Server.listen(80, BIND_HOST, () => {
    log(`Connectivity probe listener on ${BIND_HOST}:80 (generate_204, hotspot-detect.html)`);
    log(`  everything else on :80 redirects to ${GAME_URL}`);
  });
}
