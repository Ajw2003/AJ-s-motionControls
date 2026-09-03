'use strict';

/**
 * Integration test for the relay. Spawns server.js on a scratch port and exercises
 * the routing plus each hardening fix. Run with: npm test
 */

const { WebSocket } = require('ws');
const { spawn } = require('child_process');
const http = require('http');
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
      try {
        msg = JSON.parse(raw);
      } catch {
        return;
      }
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
    ws.waiters.push((msg) => {
      clearTimeout(timer);
      resolve(msg);
    });
  });
}

function send(ws, obj) {
  ws.send(JSON.stringify(obj));
}

function httpGet(pathAndQuery) {
  return new Promise((resolve, reject) => {
    http
      .get({ host: '127.0.0.1', port: PORT, path: pathAndQuery }, (res) => {
        let body = '';
        res.on('data', (chunk) => {
          body += chunk;
        });
        res.on('end', () => resolve({ status: res.statusCode, body }));
      })
      .on('error', reject);
  });
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
  check(
    'join to unknown room is rejected',
    rejected.type === 'ERROR' && rejected.code === 'ROOM_NOT_FOUND',
    `${rejected.type}/${rejected.code}`,
  );
  stranger.close();

  // ---- normal join --------------------------------------------------------
  const player = await connect();
  send(player, { type: 'JOIN_ROOM', roomCode: code, playerId: 'aj' });
  const joined = await next(player);
  check('player receives JOINED', joined.type === 'JOINED');
  check('player id is normalised to uppercase', joined.playerId === 'AJ', joined.playerId);

  const hostSawJoin = await next(host);
  check(
    'host is told PLAYER_JOINED',
    hostSawJoin.type === 'PLAYER_JOINED' && hostSawJoin.playerId === 'AJ',
  );

  // ---- player -> host, with server-side identity stamping ------------------
  send(player, { type: 'THROW', playerId: 'SOMEONE_ELSE', power: 0.72 });
  const throwMsg = await next(host);
  check('THROW reaches the host', throwMsg.type === 'THROW' && throwMsg.power === 0.72);
  check(
    'spoofed playerId is overwritten by the server',
    throwMsg.playerId === 'AJ',
    throwMsg.playerId,
  );

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
  check(
    'server still routing after malformed input',
    stillAlive.type === 'THROW' && stillAlive.power === 0.5,
  );

  // ---- reconnect reclaims the same slot -----------------------------------
  const rejoin = await connect();
  send(rejoin, { type: 'JOIN_ROOM', roomCode: code, playerId: 'AJ' });
  const rejoined = await next(rejoin);
  check('reconnect is accepted', rejoined.type === 'JOINED');
  check('reconnect is flagged as such', rejoined.reconnected === true);

  // ---- QR endpoint: the join URL players actually scan ---------------------
  const joinUrl = `https://sample-words-here.trycloudflare.com/?room=${code}`;
  const qr = await httpGet(`/qr?data=${encodeURIComponent(joinUrl)}`);
  check('/qr answers 200', qr.status === 200, String(qr.status));

  const grid = JSON.parse(qr.body);
  check(
    '/qr returns a square matrix',
    Number.isInteger(grid.size) && grid.size >= 21 && grid.rows.length === grid.size,
    `${grid.size}x${grid.size}`,
  );
  check(
    '/qr rows are 0/1 strings of the right width',
    grid.rows.every((r) => r.length === grid.size && /^[01]+$/.test(r)),
  );
  // The finder pattern is what a scanner locks onto; an all-white corner means
  // the matrix came back empty or transposed into nonsense.
  check(
    '/qr has a dark finder pattern in the top-left corner',
    grid.rows[0][0] === '1' && grid.rows[6][6] === '1' && grid.rows[1][1] === '0',
  );

  const tooLong = await httpGet(`/qr?data=${'x'.repeat(600)}`);
  check('/qr rejects oversized data', tooLong.status === 400, String(tooLong.status));

  const missing = await httpGet('/qr');
  check('/qr rejects a missing data parameter', missing.status === 400, String(missing.status));

  const health = await httpGet('/health');
  check('/health still answers', health.status === 200 && JSON.parse(health.body).ok === true);

  // ---- a host that comes back keeps its room and its players ---------------
  // This is the party-survival case: a redeploy or a Wi-Fi blip must not eject
  // everyone and force them to retype a code that no longer exists.
  const heldCode = code;
  host.close();

  const hostLeft = await next(rejoin, 3000);
  check(
    'players are told HOST_LEFT, not ROOM_CLOSED, on a host drop',
    hostLeft.type === 'HOST_LEFT',
    hostLeft.type,
  );

  const returning = await connect();
  returning.send(JSON.stringify({ type: 'CREATE_ROOM', preferredCode: heldCode }));

  const resumed = await next(returning);
  check(
    'a reconnecting host reclaims the same room code',
    resumed.type === 'ROOM_CREATED' && resumed.roomCode === heldCode,
    resumed.roomCode,
  );
  check('the room is resumed rather than recreated', resumed.resumed === true);
  check(
    'players were kept across the drop',
    Array.isArray(resumed.players) && resumed.players.includes('AJ'),
    JSON.stringify(resumed.players),
  );

  const resumeNotice = await next(rejoin, 3000);
  check('players are told HOST_RESUMED', resumeNotice.type === 'HOST_RESUMED', resumeNotice.type);

  // Routing must work again without anyone rejoining.
  rejoin.send(JSON.stringify({ type: 'THROW', power: 0.5 }));
  const afterResume = await next(returning);
  check(
    'input routes to the resumed host',
    afterResume.type === 'THROW' && afterResume.playerId === 'AJ',
  );

  // ---- a host that never comes back does close the room --------------------
  // The grace period holds the room, it does not keep it forever: players must not
  // be stranded on a dead room either.
  returning.close();

  const secondDrop = await next(rejoin, 3000);
  check(
    'a second host drop is announced the same way',
    secondDrop.type === 'HOST_LEFT',
    secondDrop.type,
  );

  const eventuallyClosed = await next(rejoin, 6000);
  check(
    'the room closes once the grace period expires without the host returning',
    eventuallyClosed.type === 'ROOM_CLOSED',
    eventuallyClosed.type,
  );

  player.close();
  rejoin.close();
  await sleep(100);
}

const server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
  // A short grace period so the host-disconnect cases do not take a minute each.
  env: { ...process.env, PORT: String(PORT), HOST_GRACE_MS: '1500' },
  stdio: ['ignore', 'ignore', 'inherit'],
});

let exitCode = 0;
server.on('exit', (code, signal) => {
  if (!signal) {
    console.error(`\nServer exited unexpectedly with code ${code} — it should never die.`);
    process.exit(1);
  }
});

(async () => {
  await sleep(600);
  try {
    await run();
  } catch (err) {
    console.error('\nTest run threw:', err.message);
    exitCode = 1;
  }

  // A run that threw has not passed, however many individual checks did — the
  // checks after the throw never ran. Reporting PASS there is worse than useless.
  console.log(
    exitCode !== 0
      ? '\nFAIL — the run threw before finishing'
      : failures === 0
        ? '\nPASS — all relay checks green'
        : `\nFAIL — ${failures} check(s) failed`,
  );
  server.kill('SIGKILL');
  process.exit(failures === 0 ? exitCode : 1);
})();
