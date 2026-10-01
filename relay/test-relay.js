'use strict';

/**
 * Integration test for the relay. Spawns server.js on a scratch port and exercises
 * the routing plus each hardening fix. Run with: npm test
 */

const { test, describe, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { WebSocket } = require('ws');
const { spawn } = require('child_process');
const http = require('http');
const path = require('path');

const PORT = 3999;
const URL = `ws://127.0.0.1:${PORT}`;

function check(label, ok, detail) {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ` — ${detail}` : ''}`);
  assert.ok(ok, `${label}${detail ? ` — ${detail}` : ''}`);
}

function connect(url = URL) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
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

describe('Relay integration', () => {
  let server;

  // Shared connections/state threaded across the tests below, in the same order
  // the original single run() function used them.
  let host, code, stranger, player, rejoin, returning, heldCode, playerCloseCode;

  before(async () => {
    server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
      // A short grace period so the host-disconnect cases do not take a minute each.
      env: { ...process.env, PORT: String(PORT), HOST_GRACE_MS: '1500' },
      stdio: ['ignore', 'ignore', 'inherit'],
    });
    server.on('exit', (code, signal) => {
      if (!signal) {
        console.error(`\nServer exited unexpectedly with code ${code} — it should never die.`);
      }
    });
    await sleep(600);
  });

  after(async () => {
    server.kill('SIGKILL');
  });

  test('routing basics', async () => {
    host = await connect();
    send(host, { type: 'CREATE_ROOM' });
    const created = await next(host);

    check('host receives ROOM_CREATED', created.type === 'ROOM_CREATED');
    code = created.roomCode;
    check('room code is 4 characters', typeof code === 'string' && code.length === 4, code);
    check('room code avoids ambiguous I/O', !/[IO]/.test(code), code);
  });

  // The old "join to an unknown room is rejected" case lived here. Since #44 a stale code
  // with exactly one room open falls back to that room, so the rejection cases moved to the
  // 'join without a room code' suite at the bottom, which controls the room count.

  test('normal join', async () => {
    player = await connect();
    playerCloseCode = null;
    player.on('close', (closeCode) => {
      playerCloseCode = closeCode;
    });
    send(player, { type: 'JOIN_ROOM', roomCode: code, playerId: 'aj' });
    const joined = await next(player);
    check('player receives JOINED', joined.type === 'JOINED');
    check('player id is normalised to uppercase', joined.playerId === 'AJ', joined.playerId);

    const hostSawJoin = await next(host);
    check(
      'host is told PLAYER_JOINED',
      hostSawJoin.type === 'PLAYER_JOINED' && hostSawJoin.playerId === 'AJ',
    );
  });

  test('player -> host, with server-side identity stamping', async () => {
    send(player, { type: 'THROW', playerId: 'SOMEONE_ELSE', power: 0.72 });
    const throwMsg = await next(host);
    check('THROW reaches the host', throwMsg.type === 'THROW' && throwMsg.power === 0.72);
    check(
      'spoofed playerId is overwritten by the server',
      throwMsg.playerId === 'AJ',
      throwMsg.playerId,
    );
  });

  test('host -> players', async () => {
    send(host, { type: 'SPECTATE', liveSkips: 3 });
    const spectate = await next(player);
    check('SPECTATE broadcasts to players', spectate.type === 'SPECTATE' && spectate.liveSkips === 3);

    send(host, { type: 'TURN', targetPlayerId: 'AJ', yourTurn: true });
    const turn = await next(player);
    check('targetPlayerId routes to one player', turn.type === 'TURN' && turn.yourTurn === true);
  });

  test('fix: malformed JSON must not kill the process', async () => {
    player.send('this is not json{{{');
    const badJson = await next(player);
    check('malformed JSON returns an error', badJson.type === 'ERROR' && badJson.code === 'BAD_JSON');

    send(player, { type: 'THROW', power: 0.5 });
    const stillAlive = await next(host);
    check(
      'server still routing after malformed input',
      stillAlive.type === 'THROW' && stillAlive.power === 0.5,
    );
  });

  test('reconnect reclaims the same slot', async () => {
    rejoin = await connect();
    send(rejoin, { type: 'JOIN_ROOM', roomCode: code, playerId: 'AJ' });
    const rejoined = await next(rejoin);
    check('reconnect is accepted', rejoined.type === 'JOINED');
    check('reconnect is flagged as such', rejoined.reconnected === true);
  });

  // Regression test for the join ping-pong: the displaced socket (`player`, the
  // original AJ connection just evicted by `rejoin` above) must receive a close
  // code the client can tell apart from an ordinary server close, and must never
  // send another JOIN_ROOM in response. See docs/Findings-DirectTransportFlapping.md.
  test('fix: a displaced player socket is closed with 4001 and does not rejoin', async () => {
    // The close event (listened for since 'normal join', above) may already have
    // fired by the time this test body runs; poll briefly rather than racing it.
    for (let i = 0; i < 20 && playerCloseCode === null; i++) await sleep(50);
    check(
      'the displaced socket is closed with the REPLACED_CLOSE_CODE (4001), not 1000',
      playerCloseCode === 4001,
      String(playerCloseCode),
    );

    // If the old client code were still in place, the displaced socket would treat
    // this as an ordinary close and reconnect, sending a fresh JOIN_ROOM — which
    // would evict `rejoin` in turn. Prove that never happens: `rejoin` must still
    // hold the seat, and no further PLAYER_JOINED for AJ should reach the host.
    await sleep(300);
    send(host, { type: 'SPECTATE', liveSkips: 7, targetPlayerId: 'AJ' });
    const stillOnRejoin = await next(rejoin, 2000);
    check(
      'the socket that displaced it still holds the seat',
      stillOnRejoin.type === 'SPECTATE' && stillOnRejoin.liveSkips === 7,
      JSON.stringify(stillOnRejoin),
    );
  });

  test('QR endpoint: the join URL players actually scan', async () => {
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
  });

  // ---- C1: a malformed request path must never crash the process -----------
  // Before the fix, decodeURIComponent threw a URIError with nothing to catch it,
  // which took the whole process (and every room on it) down with it. See
  // docs/Findings-NetworkAndThrowStability.md, C1.
  test('fix: a malformed percent-encoded path answers 400, not a crash', async () => {
    const bad = await httpGet('/%');
    check('GET /% answers 400', bad.status === 400, String(bad.status));

    const health = await httpGet('/health');
    check(
      '/health still answers after a malformed path',
      health.status === 200 && JSON.parse(health.body).ok === true,
      String(health.status),
    );
  });

  // ---- C4: static serving is an allowlist, not the whole controller/ tree ---
  test('fix: static serving is an allowlist, not a directory', async () => {
    const pkg = await httpGet('/package.json');
    check('/package.json is not served', pkg.status === 404, String(pkg.status));

    const eslintCfg = await httpGet('/eslint.config.js');
    check('/eslint.config.js is not served', eslintCfg.status === 404, String(eslintCfg.status));

    const testSensors = await httpGet('/test-sensors.js');
    check('/test-sensors.js is not served', testSensors.status === 404, String(testSensors.status));

    const appJs = await httpGet('/js/app.js');
    check('/js/app.js is served', appJs.status === 200, String(appJs.status));

    const index = await httpGet('/');
    check('/ is served', index.status === 200, String(index.status));

    const traversal = await httpGet('/..%2fpackage.json');
    check(
      'path traversal never serves package.json',
      traversal.status === 404 || traversal.status === 400,
      String(traversal.status),
    );
  });

  // ---- a host that comes back keeps its room and its players ---------------
  // This is the party-survival case: a redeploy or a Wi-Fi blip must not eject
  // everyone and force them to retype a code that no longer exists.
  test('a host that comes back keeps its room and its players', async () => {
    heldCode = code;
    host.close();

    const hostLeft = await next(rejoin, 3000);
    check(
      'players are told HOST_LEFT, not ROOM_CLOSED, on a host drop',
      hostLeft.type === 'HOST_LEFT',
      hostLeft.type,
    );

    returning = await connect();
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
  });

  // ---- a host that never comes back does close the room --------------------
  // The grace period holds the room, it does not keep it forever: players must not
  // be stranded on a dead room either.
  test('a host that never comes back does close the room', async () => {
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
  });
});

// #44: phones no longer type a room code. A JOIN with no code, or a stale one, falls back
// to the relay's only open room. Own server and port so the room count is under control
// (the suite above keeps one room open throughout, which would mask the zero/two cases).
describe('Relay join without a room code (#44)', () => {
  const PORT2 = 3998;
  const URL2 = `ws://127.0.0.1:${PORT2}`;
  let server2;
  const open = [];
  const conn = async () => {
    const ws = await connect(URL2);
    open.push(ws);
    return ws;
  };
  const createRoom = async () => {
    const h = await conn();
    send(h, { type: 'CREATE_ROOM' });
    const created = await next(h);
    return { h, code: created.roomCode };
  };

  before(async () => {
    server2 = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
      env: { ...process.env, PORT: String(PORT2), HOST_GRACE_MS: '1500' },
      stdio: ['ignore', 'ignore', 'inherit'],
    });
    await sleep(600);
  });

  after(async () => {
    for (const ws of open) ws.close();
    server2.kill('SIGKILL');
  });

  test('no code and zero rooms is an error', async () => {
    const p = await conn();
    send(p, { type: 'JOIN_ROOM', playerId: 'NOROOM' });
    const r = await next(p);
    check('rejected with ROOM_NOT_FOUND', r.type === 'ERROR' && r.code === 'ROOM_NOT_FOUND', `${r.type}/${r.code}`);
  });

  let first;
  test('no code and one room joins that room', async () => {
    first = await createRoom();
    const p = await conn();
    send(p, { type: 'JOIN_ROOM', playerId: 'ONE' });
    const r = await next(p);
    check('JOINED the only room', r.type === 'JOINED' && r.roomCode === first.code, `${r.type}/${r.roomCode}`);
  });

  test('stale code and one room joins that room', async () => {
    const p = await conn();
    send(p, { type: 'JOIN_ROOM', roomCode: 'ZZZZ', playerId: 'STALE' });
    const r = await next(p);
    check('JOINED the only room', r.type === 'JOINED' && r.roomCode === first.code, `${r.type}/${r.roomCode}`);
  });

  test('no code and two rooms is ROOM_AMBIGUOUS', async () => {
    await createRoom();
    const p = await conn();
    send(p, { type: 'JOIN_ROOM', playerId: 'TWO' });
    const r = await next(p);
    check('rejected with ROOM_AMBIGUOUS', r.type === 'ERROR' && r.code === 'ROOM_AMBIGUOUS', `${r.type}/${r.code}`);
  });
});
