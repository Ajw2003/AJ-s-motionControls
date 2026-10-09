'use strict';

// Tests for the DSU server (#3): packet framing and CRC, the axis mapping, and the real
// relay answering a scripted DSU client with a phone's motion end to end.

const { test, describe, before, after } = require('node:test');
const assert = require('node:assert/strict');
const dgram = require('dgram');
const { WebSocket } = require('ws');
const { spawn } = require('child_process');
const path = require('path');
const {
  buildPacket,
  parsePacket,
  crc32,
  phoneToDsuAxes,
  padDataPayload,
  parsePadData,
  MessageType,
  SlotState,
  PROTOCOL_VERSION,
} = require('./dsu');

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

describe('packets', () => {
  test('CRC-32 matches the standard check value', () => {
    assert.equal(crc32(Buffer.from('123456789')), 0xcbf43926);
  });

  test('a built packet has the header fields and a CRC that verifies', () => {
    const payload = Buffer.alloc(6);
    payload.writeUInt32LE(MessageType.VERSION, 0);
    const packet = buildPacket('DSUS', 0xdeadbeef, payload);
    assert.equal(packet.toString('ascii', 0, 4), 'DSUS');
    assert.equal(packet.readUInt16LE(4), PROTOCOL_VERSION);
    assert.equal(packet.readUInt16LE(6), 6);
    assert.equal(packet.readUInt32LE(12), 0xdeadbeef);
    const parsed = parsePacket(packet, 'DSUS');
    assert.equal(parsed.type, MessageType.VERSION);
    assert.equal(parsed.senderId, 0xdeadbeef);
  });

  test('rejects a flipped bit, the wrong magic, the wrong version and a short packet', () => {
    const payload = Buffer.alloc(4);
    payload.writeUInt32LE(MessageType.VERSION, 0);
    const good = buildPacket('DSUC', 1, payload);

    const flipped = Buffer.from(good);
    flipped[good.length - 1] ^= 0x01;
    assert.equal(parsePacket(flipped, 'DSUC'), null);

    assert.equal(parsePacket(good, 'DSUS'), null);

    const oldVersion = Buffer.from(good);
    oldVersion.writeUInt16LE(1000, 4);
    assert.equal(parsePacket(oldVersion, 'DSUC'), null);

    assert.equal(parsePacket(good.subarray(0, 10), 'DSUC'), null);
  });

  test('pad data is the 100-byte packet emulators expect, and reads back', () => {
    const sample = {
      seq: 0,
      ts: 1_791_510_336_526_200,
      accel: [0.1, 0.2, 0.97],
      gyro: [10, 20, 30],
    };
    const packet = buildPacket('DSUS', 1, padDataPayload(2, 41, sample));
    assert.equal(packet.length, 100);
    const pad = parsePadData(parsePacket(packet, 'DSUS').body);
    assert.equal(pad.slot, 2);
    assert.equal(pad.slotState, SlotState.CONNECTED);
    assert.equal(pad.connected, true);
    assert.equal(pad.packetNumber, 41);
    assert.deepEqual(pad.sticks, [128, 128, 128, 128]);
    assert.equal(pad.buttons1, 0);
    assert.equal(pad.timestampUs, BigInt(sample.ts));
    const expected = phoneToDsuAxes(sample.accel, sample.gyro);
    pad.accel.forEach((v, i) => assert.ok(Math.abs(v - expected.accel[i]) < 1e-6));
    pad.gyro.forEach((v, i) => assert.ok(Math.abs(v - expected.gyro[i]) < 1e-6));
  });
});

describe('axes', () => {
  test('phone flat on its back reads as a gamepad held level: gravity on DSU y', () => {
    const { accel } = phoneToDsuAxes([0, 0, 1], [0, 0, 0]);
    assert.deepEqual(accel, [0, 1, -0]);
  });

  test('gyro follows the same axes as acceleration', () => {
    const { gyro } = phoneToDsuAxes([0, 0, 0], [1, 2, 3]);
    assert.deepEqual(gyro, [1, 3, -2]);
  });
});

describe('relay with a scripted DSU client', () => {
  const PORT = 3995;
  const DSU_PORT = 26999;
  let server;
  let udp;
  let pad;
  const received = [];

  function request(type, body = Buffer.alloc(0)) {
    const payload = Buffer.alloc(4 + body.length);
    payload.writeUInt32LE(type, 0);
    body.copy(payload, 4);
    udp.send(buildPacket('DSUC', 0x1234, payload), DSU_PORT, '127.0.0.1');
  }

  async function waitFor(predicate, timeoutMs = 2000) {
    const start = Date.now();
    while (Date.now() - start < timeoutMs) {
      const hit = received.find(predicate);
      if (hit) return hit;
      await sleep(20);
    }
    throw new Error('timed out waiting for a DSU reply');
  }

  before(async () => {
    server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
      env: { ...process.env, PORT: String(PORT), DSU_PORT: String(DSU_PORT) },
      stdio: ['ignore', 'ignore', 'inherit'],
    });
    await sleep(600);
    udp = dgram.createSocket('udp4');
    udp.on('message', (packet) => {
      const parsed = parsePacket(packet, 'DSUS');
      if (parsed) received.push(parsed);
      else received.push({ bad: true });
    });
    await new Promise((resolve) => udp.bind(0, '127.0.0.1', resolve));
  });

  after(() => {
    if (pad) pad.close();
    udp.close();
    server.kill('SIGKILL');
  });

  test('answers the version request', async () => {
    request(MessageType.VERSION);
    const reply = await waitFor((p) => p.type === MessageType.VERSION);
    assert.equal(reply.body.readUInt16LE(0), PROTOCOL_VERSION);
  });

  test('reports all four slots empty before a phone joins', async () => {
    received.length = 0;
    request(MessageType.PORT_INFO, Buffer.from([4, 0, 0, 0, 0, 1, 2, 3]));
    await sleep(200);
    const infos = received.filter((p) => p.type === MessageType.PORT_INFO);
    assert.equal(infos.length, 4);
    for (const info of infos) assert.equal(info.body.readUInt8(1), SlotState.DISCONNECTED);
  });

  test("streams a phone's motion to a subscribed client, axes mapped, nothing malformed", async () => {
    pad = new WebSocket(`ws://127.0.0.1:${PORT}/pad`);
    await new Promise((resolve, reject) => {
      pad.on('open', resolve);
      pad.on('error', reject);
    });
    await sleep(100);

    received.length = 0;
    request(MessageType.PAD_DATA, Buffer.from([0, 0, 0, 0, 0, 0, 0, 0])); // subscribe to every slot
    await sleep(100);
    for (let seq = 0; seq < 60; seq++) {
      pad.send(
        JSON.stringify({
          type: 'MOTION',
          seq,
          ts: 5_000_000 + seq * 16_667,
          accel: [0, 0, 1],
          gyro: [90, 0, 0],
        }),
      );
    }
    await sleep(300);

    const data = received
      .filter((p) => p.type === MessageType.PAD_DATA)
      .map((p) => parsePadData(p.body));
    assert.equal(received.filter((p) => p.bad).length, 0);
    assert.equal(data.length, 60);
    assert.deepEqual(
      data.map((d) => d.packetNumber),
      [...Array(60).keys()],
    );
    const last = data.at(-1);
    assert.equal(last.slot, 0);
    assert.equal(last.timestampUs, BigInt(5_000_000 + 59 * 16_667));
    assert.ok(Math.abs(last.accel[1] - 1) < 1e-6, `gravity on DSU y, got ${last.accel}`);
    assert.ok(Math.abs(last.gyro[0] - 90) < 1e-6, `pitch 90 deg/s, got ${last.gyro}`);

    request(MessageType.PORT_INFO, Buffer.from([1, 0, 0, 0, 0]));
    const info = await waitFor(
      (p) => p.type === MessageType.PORT_INFO && p.body.readUInt8(1) === SlotState.CONNECTED,
    );
    assert.equal(info.body.readUInt8(0), 0);
  });

  test('ignores garbage without replying or crashing', async () => {
    received.length = 0;
    udp.send(Buffer.from('not a dsu packet at all'), DSU_PORT, '127.0.0.1');
    request(MessageType.VERSION);
    await waitFor((p) => p.type === MessageType.VERSION);
    assert.equal(received.length, 1);
  });
});
