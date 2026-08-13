'use strict';

/**
 * Smoke test against a DEPLOYED relay, over the real network.
 *
 * test-relay.js covers the routing logic in-process. This covers everything
 * that only breaks once a proxy, TLS and DNS are in the path — chiefly whether
 * the WebSocket upgrade survives, which no amount of curl will tell you.
 *
 *   node test-deployed.js                          (defaults to the Fly app)
 *   node test-deployed.js wss://play.skiprocking.ca
 *
 * The multi-backend check at the end is the one worth keeping: rooms live in a
 * per-process Map, so the app MUST run exactly one machine. Two machines fail
 * intermittently and only under real load, which is the worst way to find out.
 */

const { WebSocket } = require('ws');

const URL = process.argv[2] || 'wss://rockskipping-relay.fly.dev';
const HTTP = URL.replace(/^wss:/, 'https:').replace(/^ws:/, 'http:');
const JOIN_ATTEMPTS = 8;

const results = [];
let failed = false;

function check(name, ok, detail) {
  results.push(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
  if (!ok) failed = true;
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

function open(label) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(URL);
    const timer = setTimeout(() => reject(new Error(`${label}: no open within 15s`)), 15000);
    ws.on('open', () => { clearTimeout(timer); resolve(ws); });
    ws.on('error', (e) => { clearTimeout(timer); reject(new Error(`${label}: ${e.message}`)); });
  });
}

function next(ws, type, ms = 10000) {
  const promise = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`timeout waiting for ${type}`)), ms);
    const onMessage = (raw) => {
      const msg = JSON.parse(raw);
      if (msg.type !== type) return;
      clearTimeout(timer);
      ws.off('message', onMessage);
      resolve(msg);
    };
    ws.on('message', onMessage);
  });
  // Callers start these eagerly to avoid a race; without this a timeout would
  // surface as an unhandled rejection and kill the process before it reports.
  promise.catch(() => {});
  return promise;
}

(async () => {
  console.log(`testing ${URL}\n`);

  const host = await open('host');
  check('wss upgrade survives the proxy', true, URL);

  host.send(JSON.stringify({ type: 'CREATE_ROOM' }));
  const created = await next(host, 'ROOM_CREATED');
  const code = created.roomCode;
  check('CREATE_ROOM returns a usable code', /^[A-HJ-NP-Z]{4}$/.test(code), code);

  const player = await open('player');
  const hostSeesJoin = next(host, 'PLAYER_JOINED');
  player.send(JSON.stringify({ type: 'JOIN_ROOM', roomCode: code, playerId: 'SMOKE' }));

  const joined = await next(player, 'JOINED');
  check('JOIN_ROOM accepted', joined.roomCode === code && joined.playerId === 'SMOKE');
  check('host notified of the join', (await hostSeesJoin).playerId === 'SMOKE');

  const aimAtHost = next(host, 'AIM');
  player.send(JSON.stringify({ type: 'AIM', seq: 1, aimDeg: -8.5, playerId: 'SPOOFED' }));
  const aim = await aimAtHost;
  check('AIM routed player -> host', aim.aimDeg === -8.5);
  check('playerId stamped server-side, spoof ignored', aim.playerId === 'SMOKE', `got ${aim.playerId}`);

  const atPlayer = next(player, 'SPECTATE');
  host.send(JSON.stringify({ type: 'SPECTATE', targetPlayerId: 'SMOKE', liveSkips: 4 }));
  check('host -> single player routing', (await atPlayer).liveSkips === 4);

  player.send('this is not json');
  check('malformed JSON answered, not fatal', (await next(player, 'ERROR')).code === 'BAD_JSON');

  // Every one of these must reach the backend holding the room. A single
  // ROOM_NOT_FOUND means more than one machine is serving the app.
  let notFound = 0;
  for (let i = 0; i < JOIN_ATTEMPTS; i++) {
    const p = await open(`probe${i}`);
    const replied = Promise.race([
      next(p, 'JOINED', 8000).then(() => 'joined'),
      next(p, 'ERROR', 8000).then((e) => e.code),
    ]).catch((e) => e.message);
    p.send(JSON.stringify({ type: 'JOIN_ROOM', roomCode: code, playerId: `PROBE${i}` }));
    const outcome = await replied;
    if (outcome === 'ROOM_NOT_FOUND') notFound++;
    p.close();
  }
  check(`all ${JOIN_ATTEMPTS} joins hit the backend holding the room`, notFound === 0,
        notFound ? `${notFound} got ROOM_NOT_FOUND — run: fly scale count 1` : undefined);

  host.close();
  player.close();

  console.log(results.join('\n'));
  console.log(failed ? '\nFAILED' : '\nALL PASS');
  process.exit(failed ? 1 : 0);
})().catch((e) => {
  console.log(results.join('\n'));
  console.log(`\nFAILED — ${e.message}`);
  process.exit(1);
});
