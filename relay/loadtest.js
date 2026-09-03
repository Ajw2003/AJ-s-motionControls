'use strict';

/**
 * Capacity measurement: simulates concurrent parties and reports what they cost.
 *
 * Each room is 1 host + PLAYERS_PER_ROOM phones. One phone streams AIM at 20Hz —
 * turn-based, so only the active player aims. That is the real rate, from `aimHz`
 * in throw.js, not an invented one.
 *
 * The host sends SPECTATE only on state changes — a join, a turn, a completed throw,
 * the match ending — so it is not modelled as steady traffic here. It used to go out
 * at 4Hz to every player for the whole time a rock was in the air, which dominated
 * this measurement; a match now sends single-digit SPECTATEs per turn instead of
 * dozens. If you reintroduce anything periodic on the host side, model it here or
 * these numbers stop meaning anything.
 *
 *   npm run loadtest                    25 rooms x 4 players, 15s
 *   node loadtest.js 250 8 20           250 rooms x 8 players, 20s
 *   RELAY_URL=ws://127.0.0.1:3100 node loadtest.js 100 6 15
 *
 * Reads the counters that /health exposes, so point it at a relay running this
 * version or newer. Run it against a LOCAL relay for capacity planning; pointing
 * it at production is a denial-of-service against your own players.
 *
 * Measured numbers and what they imply: HostingPlan.md §14.
 */

const { WebSocket } = require('ws');

const URL = process.env.RELAY_URL || 'ws://127.0.0.1:3100';
const HTTP = URL.replace('ws://', 'http://');
const ROOMS = Number(process.argv[2] || 25);
const PLAYERS_PER_ROOM = Number(process.argv[3] || 4);
const SECONDS = Number(process.argv[4] || 15);

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

function open() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(URL);
    ws.on('open', () => resolve(ws));
    ws.on('error', reject);
  });
}

function firstOfType(ws, type) {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error(`timeout ${type}`)), 20000);
    const on = (raw) => {
      const m = JSON.parse(raw);
      if (m.type !== type) return;
      clearTimeout(t);
      ws.off('message', on);
      resolve(m);
    };
    ws.on('message', on);
  });
}

(async () => {
  const sockets = [];
  const timers = [];
  const rooms = [];

  process.stdout.write(`building ${ROOMS} rooms x ${PLAYERS_PER_ROOM} players ... `);

  for (let r = 0; r < ROOMS; r++) {
    const host = await open();
    sockets.push(host);
    host.on('message', () => {});
    host.send(JSON.stringify({ type: 'CREATE_ROOM' }));
    const { roomCode } = await firstOfType(host, 'ROOM_CREATED');

    const players = [];
    for (let p = 0; p < PLAYERS_PER_ROOM; p++) {
      const ws = await open();
      sockets.push(ws);
      ws.on('message', () => {});
      ws.send(JSON.stringify({ type: 'JOIN_ROOM', roomCode, playerId: `P${p}` }));
      players.push(ws);
    }
    rooms.push({ host, players, roomCode });
  }

  console.log('done');
  await wait(1000);

  const beforeResponse = await fetch(`${HTTP}/health`);
  const before = await beforeResponse.json();
  const t0 = Date.now();

  // Only the active player aims — the game is turn-based.
  let seq = 0;
  for (const room of rooms) {
    const aimer = room.players[0];
    timers.push(
      setInterval(() => {
        aimer.send(
          JSON.stringify({
            type: 'AIM',
            roomCode: room.roomCode,
            seq: seq++,
            aimDeg: -8.5,
            attackAngleDeg: 22.0,
            rollDeg: 4.0,
            windup: 0.35,
            spinRate: 412.6,
          }),
        );
      }, 50),
    );
  }

  await wait(SECONDS * 1000);
  for (const t of timers) clearInterval(t);

  const afterResponse = await fetch(`${HTTP}/health`);
  const after = await afterResponse.json();
  const elapsed = (Date.now() - t0) / 1000;

  const inRate = (after.messagesIn - before.messagesIn) / elapsed;
  const outRate = (after.messagesOut - before.messagesOut) / elapsed;

  console.log(`
rooms          ${after.rooms}
players        ${after.players}
connections    ${after.connections}
rss            ${after.rssMb} MB   (heap ${after.heapMb} MB)
msg in         ${inRate.toFixed(0)}/s
msg out        ${outRate.toFixed(0)}/s
per room       ${(inRate / after.rooms).toFixed(1)} in, ${(outRate / after.rooms).toFixed(1)} out
rss per room   ${((after.rssMb / after.rooms) * 1024).toFixed(0)} KB`);

  for (const s of sockets) s.close();
  await wait(500);
  process.exit(0);
})().catch((e) => {
  console.error('FAILED', e.message);
  process.exit(1);
});
