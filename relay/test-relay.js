'use strict';

/**
 * Integration test for the relay. Spawns server.js on a scratch port and exercises
 * the routing plus each hardening fix. Run with: npm test
 */

const { WebSocket } = require('ws');
const { spawn } = require('child_process');
const path = require('path');

const PORT = 3999;
const URL = `ws://127.0.0.1:${PORT}`;

let failures = 0;

function check(label, ok, detail) {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ` — ${detail}` : ''}`);
  if (!ok) failures++;
}

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(URL);
    ws.inbox = [];
    ws.waiters = [];
    ws.on('message', (raw) => {
      let msg;
      try { msg = JSON.parse(raw); } catch { return; }
      const waiter = ws.waiters.shift();
      if (waiter) waiter(msg);
      else ws.inbox.push(msg);
    });
    ws.on('open', () => resolve(ws));
    ws.on('error', reject);
  });
}

function next(ws, timeoutMs = 2000) {
  if (ws.inbox.length) return Promise.resolve(ws.inbox.shift());
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('timed out waiting for a message')), timeoutMs);
    ws.waiters.push((msg) => { clearTimeout(timer); resolve(msg); });
  });
}

function send(ws, obj) {
  ws.send(JSON.stringify(obj));
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function run() {
  console.log('Relay integration test\n');

  // ---- routing basics -----------------------------------------------------
  const host = await connect();
  send(host, { type: 'CREATE_ROOM' });
  const created = await next(host);

  check('host receives ROOM_CREATED', created.type === 'ROOM_CREATED');
  const code = created.roomCode;
  check('room code is 4 characters', typeof code === 'string' && code.length === 4, code);
  check('room code avoids ambiguous I/O', !/[IO]/.test(code), code);

  // ---- fix: joins to a missing room must be answered, not ignored ----------
  const stranger = await connect();
  send(stranger, { type: 'JOIN_ROOM', roomCode: 'ZZZZ', playerId: 'NOBODY' });
  const rejected = await next(stranger);
  check('join to unknown room is rejected', rejected.type === 'ERROR' && rejected.code === 'ROOM_NOT_FOUND',
    `${rejected.type}/${rejected.code}`);
  stranger.close();

  // ---- normal join --------------------------------------------------------
  const player = await connect();
  send(player, { type: 'JOIN_ROOM', roomCode: code, playerId: 'aj' });
  const joined = await next(player);
  check('player receives JOINED', joined.type === 'JOINED');
  check('player id is normalised to uppercase', joined.playerId === 'AJ', joined.playerId);

  const hostSawJoin = await next(host);
  check('host is told PLAYER_JOINED', hostSawJoin.type === 'PLAYER_JOINED' && hostSawJoin.playerId === 'AJ');

  // ---- player -> host, with server-side identity stamping ------------------
  send(player, { type: 'THROW', playerId: 'SOMEONE_ELSE', power: 0.72 });
  const throwMsg = await next(host);
  check('THROW reaches the host', throwMsg.type === 'THROW' && throwMsg.power === 0.72);
  check('spoofed playerId is overwritten by the server', throwMsg.playerId === 'AJ', throwMsg.playerId);

  // ---- host -> players ----------------------------------------------------
  send(host, { type: 'SPECTATE', liveSkips: 3 });
  const spectate = await next(player);
  check('SPECTATE broadcasts to players', spectate.type === 'SPECTATE' && spectate.liveSkips === 3);

  send(host, { type: 'TURN', targetPlayerId: 'AJ', yourTurn: true });
  const turn = await next(player);
  check('targetPlayerId routes to one player', turn.type === 'TURN' && turn.yourTurn === true);

  // ---- fix: malformed JSON must not kill the process -----------------------
  player.send('this is not json{{{');
  const badJson = await next(player);
  check('malformed JSON returns an error', badJson.type === 'ERROR' && badJson.code === 'BAD_JSON');

  send(player, { type: 'THROW', power: 0.5 });
  const stillAlive = await next(host);
  check('server still routing after malformed input', stillAlive.type === 'THROW' && stillAlive.power === 0.5);

  // ---- reconnect reclaims the same slot -----------------------------------
  const rejoin = await connect();
  send(rejoin, { type: 'JOIN_ROOM', roomCode: code, playerId: 'AJ' });
  const rejoined = await next(rejoin);
  check('reconnect is accepted', rejoined.type === 'JOINED');
  check('reconnect is flagged as such', rejoined.reconnected === true);

  // ---- host disconnect closes the room ------------------------------------
  host.close();
  const closed = await next(rejoin, 3000);
  check('players are told ROOM_CLOSED when the host leaves', closed.type === 'ROOM_CLOSED');

  player.close();
  rejoin.close();
  await sleep(100);
}

const server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
  env: { ...process.env, PORT: String(PORT) },
  stdio: ['ignore', 'ignore', 'inherit'],
});

let exitCode = 0;
server.on('exit', (code, signal) => {
  if (!signal) {
    console.error(`\nServer exited unexpectedly with code ${code} — it should never die.`);
    process.exit(1);
  }
});

sleep(600)
  .then(run)
  .catch((err) => {
    console.error('\nTest run threw:', err.message);
    exitCode = 1;
  })
  .then(() => {
    console.log(failures === 0 ? '\nPASS — all relay checks green' : `\nFAIL — ${failures} check(s) failed`);
    server.kill('SIGKILL');
    process.exit(failures === 0 ? exitCode : 1);
  });
