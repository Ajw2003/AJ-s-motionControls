'use strict';

// Tests for the controller path (#2): message validation, slots, drop counting, the
// rate cap, and the real server routing `/pad` sockets and `/api/pads` end to end.

const { test, describe, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('events');
const { WebSocket } = require('ws');
const { spawn } = require('child_process');
const http = require('http');
const path = require('path');
const {
  createPadHub,
  parseMotionMessage,
  MAX_PADS,
  MAX_MESSAGES_PER_SECOND,
} = require('./pad-hub');

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function motion(seq, overrides = {}) {
  return {
    type: 'MOTION',
    seq,
    ts: 1_000_000 + seq * 16_667,
    accel: [0, 0, -1],
    gyro: [1, 2, 3],
    ...overrides,
  };
}

// A stand-in socket: enough of ws's surface for the hub, with sent frames recorded.
function fakeSocket() {
  const ws = new EventEmitter();
  ws.sent = [];
  ws.closed = null;
  ws.send = (text) => ws.sent.push(JSON.parse(text));
  ws.close = (code, reason) => {
    ws.closed = { code, reason };
  };
  ws.deliver = (obj) =>
    ws.emit('message', Buffer.from(typeof obj === 'string' ? obj : JSON.stringify(obj)));
  return ws;
}

describe('parseMotionMessage', () => {
  test('accepts a well-formed sample', () => {
    assert.deepEqual(parseMotionMessage(motion(7)), {
      seq: 7,
      ts: 1_000_000 + 7 * 16_667,
      accel: [0, 0, -1],
      gyro: [1, 2, 3],
    });
  });

  for (const [label, bad] of [
    ['wrong type', motion(1, { type: 'AIM' })],
    ['negative seq', motion(-1)],
    ['fractional ts', motion(1, { ts: 1.5 })],
    ['two-axis accel', motion(1, { accel: [0, 0] })],
    ['NaN in gyro', motion(1, { gyro: [0, NaN, 0] })],
    ['string in accel', motion(1, { accel: ['0', 0, 0] })],
    ['null', null],
  ]) {
    test(`rejects ${label}`, () => assert.equal(parseMotionMessage(bad), null));
  }
});

describe('pad hub', () => {
  test('gives the first four phones slots 0-3 and turns the fifth away', () => {
    const hub = createPadHub();
    const sockets = Array.from({ length: MAX_PADS + 1 }, fakeSocket);
    const slots = sockets.map((ws, i) => hub.attach(ws, `10.0.0.${i}`));
    assert.deepEqual(slots, [0, 1, 2, 3, null]);
    assert.equal(sockets[4].sent[0].type, 'PAD_REJECTED');
    assert.equal(sockets[4].closed.code, 1013);
  });

  test('frees a slot on disconnect and reuses it', () => {
    const hub = createPadHub();
    const a = fakeSocket();
    const b = fakeSocket();
    hub.attach(a, 'a');
    hub.attach(b, 'b');
    a.emit('close');
    assert.equal(hub.snapshot()[0], null);
    assert.equal(hub.attach(fakeSocket(), 'c'), 0);
  });

  test('publishes samples, counts gaps in seq as lost, keeps the newest as latest', () => {
    const hub = createPadHub();
    const ws = fakeSocket();
    const seen = [];
    hub.events.on('sample', (slot, sample) => seen.push([slot, sample.seq]));
    hub.attach(ws, 'x');
    for (const seq of [0, 1, 2, 5, 4]) ws.deliver(motion(seq));
    const pad = hub.snapshot()[0];
    assert.deepEqual(seen, [
      [0, 0],
      [0, 1],
      [0, 2],
      [0, 5],
      [0, 4],
    ]);
    assert.equal(pad.received, 5);
    assert.equal(pad.dropped, 2); // 3 and 4 were missing when 5 arrived
    assert.equal(pad.latest.seq, 5); // the late 4 does not replace it
  });

  test('counts malformed messages as rejected without publishing them', () => {
    const hub = createPadHub();
    const ws = fakeSocket();
    let published = 0;
    hub.events.on('sample', () => published++);
    hub.attach(ws, 'x');
    ws.deliver('not json');
    ws.deliver({ type: 'MOTION', seq: 'one' });
    assert.equal(published, 0);
    assert.equal(hub.snapshot()[0].rejected, 2);
  });

  test('answers PING with PONG echoing the phone time', () => {
    const hub = createPadHub();
    const ws = fakeSocket();
    hub.attach(ws, 'x');
    ws.deliver({ type: 'PING', t: 123.5 });
    assert.deepEqual(ws.sent.at(-1), { type: 'PONG', t: 123.5 });
  });

  test('drops messages past the per-second cap, then recovers in the next window', () => {
    let clock = 0;
    const hub = createPadHub({ now: () => clock });
    const ws = fakeSocket();
    hub.attach(ws, 'x');
    for (let seq = 0; seq < MAX_MESSAGES_PER_SECOND + 50; seq++) ws.deliver(motion(seq));
    let pad = hub.snapshot()[0];
    assert.equal(pad.received, MAX_MESSAGES_PER_SECOND);
    assert.equal(pad.rejected, 50);
    clock = 1000;
    ws.deliver(motion(10_000));
    pad = hub.snapshot()[0];
    assert.equal(pad.received, MAX_MESSAGES_PER_SECOND + 1);
  });

  test('reports the arrival rate over the last second', () => {
    let clock = 0;
    const hub = createPadHub({ now: () => clock });
    const ws = fakeSocket();
    hub.attach(ws, 'x');
    for (let seq = 0; seq < 120; seq++) {
      clock = seq * (1000 / 60);
      ws.deliver(motion(seq));
    }
    const rate = hub.snapshot()[0].rateHz;
    assert.ok(rate >= 59 && rate <= 61, `expected about 60 Hz, got ${rate}`);
  });
});

describe('server: /pad and /api/pads', () => {
  const PORT = 3997;
  let server;
  const open = [];

  function connectPad() {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(`ws://127.0.0.1:${PORT}/pad`);
      ws.inbox = [];
      ws.on('message', (raw) => ws.inbox.push(JSON.parse(raw)));
      ws.on('open', () => {
        open.push(ws);
        resolve(ws);
      });
      ws.on('error', reject);
    });
  }

  function get(pathname) {
    return new Promise((resolve, reject) => {
      http
        .get({ host: '127.0.0.1', port: PORT, path: pathname }, (res) => {
          let body = '';
          res.on('data', (chunk) => (body += chunk));
          res.on('end', () => resolve({ status: res.statusCode, body }));
        })
        .on('error', reject);
    });
  }

  before(async () => {
    server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
      env: { ...process.env, PORT: String(PORT) },
      stdio: ['ignore', 'ignore', 'inherit'],
    });
    await sleep(600);
  });

  after(() => {
    for (const ws of open) ws.close();
    server.kill('SIGKILL');
  });

  test('a phone on /pad gets a slot and its samples show up in /api/pads', async () => {
    const ws = await connectPad();
    await sleep(100);
    assert.deepEqual(ws.inbox[0], { type: 'PAD_ACCEPTED', slot: 0 });

    for (let seq = 0; seq < 30; seq++) ws.send(JSON.stringify(motion(seq, { gyro: [10, 20, 30] })));
    await sleep(200);

    const { status, body } = await get('/api/pads');
    assert.equal(status, 200);
    const { pads } = JSON.parse(body);
    assert.equal(pads.length, 4);
    assert.equal(pads[0].received, 30);
    assert.equal(pads[0].latest.seq, 29);
    assert.deepEqual(pads[0].latest.gyro, [10, 20, 30]);
    assert.equal(pads[1], null);
  });

  test('a /pad socket is not a game client: game messages get no room reply', async () => {
    const ws = await connectPad();
    await sleep(100);
    ws.inbox.length = 0;
    ws.send(JSON.stringify({ type: 'JOIN_ROOM', roomCode: 'ABCD', playerId: 'p1' }));
    await sleep(200);
    assert.deepEqual(ws.inbox, []);
  });

  test('serves the controller and monitor pages', async () => {
    for (const page of ['/pad.html', '/monitor.html', '/js/pad.js', '/js/motion-sample.js']) {
      const { status } = await get(page);
      assert.equal(status, 200, page);
    }
  });
});
