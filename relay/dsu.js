'use strict';

// DSU (CemuHook) server, issue #3: hands each phone's motion to emulators (Dolphin, Cemu,
// Ryujinx, the Citra forks) over UDP, with no driver. Packet layout follows the protocol as
// commonly documented (cemuhook-protocol); the parts an emulator actually checks are tested
// in test-dsu.js. Little-endian throughout.

const dgram = require('dgram');
const crypto = require('crypto');
const { createPacer } = require('./pacer');

const PROTOCOL_VERSION = 1001;
const HEADER_BYTES = 16;
const DEFAULT_PORT = 26760;

const MessageType = Object.freeze({
  VERSION: 0x100000,
  PORT_INFO: 0x100001,
  PAD_DATA: 0x100002,
});

const SlotState = Object.freeze({ DISCONNECTED: 0, RESERVED: 1, CONNECTED: 2 });
const DEVICE_MODEL_FULL_GYRO = 2;
const CONNECTION_BLUETOOTH = 2;
const BATTERY_NOT_APPLICABLE = 0;

// Emulators re-send their pad-data request about once a second; one that has gone quiet
// this long has closed or crashed, so stop sending to it.
const CLIENT_TIMEOUT_MS = 5000;

// ---------------------------------------------------------------------------
// Packets
// ---------------------------------------------------------------------------

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

/** Standard CRC-32 (zlib/IEEE), as DSU uses. Written out because zlib.crc32 needs Node 20.15+. */
function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) crc = CRC_TABLE[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

/**
 * Wrap a payload (message type first) in the 16-byte header and fill in the CRC, which
 * covers the whole packet with its own field zeroed.
 */
function buildPacket(magic, senderId, payload) {
  const packet = Buffer.alloc(HEADER_BYTES + payload.length);
  packet.write(magic, 0, 'ascii');
  packet.writeUInt16LE(PROTOCOL_VERSION, 4);
  packet.writeUInt16LE(payload.length, 6);
  packet.writeUInt32LE(senderId >>> 0, 12);
  payload.copy(packet, HEADER_BYTES);
  packet.writeUInt32LE(crc32(packet), 8);
  return packet;
}

/**
 * Check and unpack a packet. Returns { magic, senderId, type, body } or null for anything
 * malformed: wrong magic, length or CRC. `expectMagic` is 'DSUC' on the server side.
 */
function parsePacket(packet, expectMagic) {
  if (!Buffer.isBuffer(packet) || packet.length < HEADER_BYTES + 4) return null;
  const magic = packet.toString('ascii', 0, 4);
  if (magic !== expectMagic) return null;
  if (packet.readUInt16LE(4) !== PROTOCOL_VERSION) return null;
  const length = packet.readUInt16LE(6);
  if (HEADER_BYTES + length > packet.length) return null;
  const framed = packet.subarray(0, HEADER_BYTES + length);
  const claimed = framed.readUInt32LE(8);
  const zeroed = Buffer.from(framed);
  zeroed.writeUInt32LE(0, 8);
  if (crc32(zeroed) !== claimed) return null;
  return {
    magic,
    senderId: framed.readUInt32LE(12),
    type: framed.readUInt32LE(HEADER_BYTES),
    body: framed.subarray(HEADER_BYTES + 4),
  };
}

/** A made-up but stable MAC per slot: emulators use it to tell controllers apart. */
function slotMac(slot) {
  return Buffer.from([0x02, 0x41, 0x4a, 0x4d, 0x43, slot + 1]); // locally administered
}

/** The 11 bytes every port-info and pad-data message starts with. */
function writeSlotHeader(buffer, offset, slot, connected) {
  buffer.writeUInt8(slot, offset);
  buffer.writeUInt8(connected ? SlotState.CONNECTED : SlotState.DISCONNECTED, offset + 1);
  buffer.writeUInt8(connected ? DEVICE_MODEL_FULL_GYRO : 0, offset + 2);
  buffer.writeUInt8(connected ? CONNECTION_BLUETOOTH : 0, offset + 3);
  if (connected) slotMac(slot).copy(buffer, offset + 4);
  buffer.writeUInt8(BATTERY_NOT_APPLICABLE, offset + 10);
  return offset + 11;
}

function versionPayload() {
  const payload = Buffer.alloc(6);
  payload.writeUInt32LE(MessageType.VERSION, 0);
  payload.writeUInt16LE(PROTOCOL_VERSION, 4);
  return payload;
}

function portInfoPayload(slot, connected) {
  const payload = Buffer.alloc(4 + 11 + 1);
  payload.writeUInt32LE(MessageType.PORT_INFO, 0);
  writeSlotHeader(payload, 4, slot, connected);
  return payload; // trailing byte stays zero
}

/**
 * Phone axes to DSU axes: the one place every axis decision lives. Signs taken from the
 * Yuzu-family and Dolphin DSU clients on 2026-10-10, after Sudachi showed a flat phone tilted.
 * doc-ref 46ec docs/4-systems/DsuServer.md
 */
function phoneToDsuAxes(accel, gyro) {
  return {
    accel: [-accel[0], -accel[2], accel[1]],
    gyro: [gyro[0], -gyro[2], gyro[1]],
  };
}

const PAD_DATA_PAYLOAD_BYTES = 84; // 100-byte packets with the header
const STICK_CENTRE = 128;

// Gamepad bits from controller/js/gamepad-state.js (Xbox positions), mirrored here because
// the relay cannot import the phone's ES module. test-dsu.js checks both lists agree.
const PadBit = Object.freeze({
  south: 1 << 0,
  east: 1 << 1,
  west: 1 << 2,
  north: 1 << 3,
  lb: 1 << 4,
  rb: 1 << 5,
  back: 1 << 6,
  start: 1 << 7,
  home: 1 << 8,
  ls: 1 << 9,
  rs: 1 << 10,
  up: 1 << 11,
  down: 1 << 12,
  left: 1 << 13,
  right: 1 << 14,
  lt: 1 << 15,
  rt: 1 << 16,
});

// DSU uses the DualShock 4's buttons, by position: Square is west, Cross south, Circle east,
// Triangle north. Bit values within each of the two button bytes.
const DSU_BUTTONS_1 = [
  ['left', 0x80],
  ['down', 0x40],
  ['right', 0x20],
  ['up', 0x10],
  ['start', 0x08], // Options
  ['rs', 0x04], // R3
  ['ls', 0x02], // L3
  ['back', 0x01], // Share
];
const DSU_BUTTONS_2 = [
  ['west', 0x80], // Square
  ['south', 0x40], // Cross
  ['east', 0x20], // Circle
  ['north', 0x10], // Triangle
  ['rb', 0x08], // R1
  ['lb', 0x04], // L1
  ['rt', 0x02], // R2
  ['lt', 0x01], // L2
];
// The analog bytes, in packet order. Touch buttons are on or off, so each is 0 or 255.
const DSU_ANALOG_ORDER = [
  'left',
  'down',
  'right',
  'up',
  'west',
  'south',
  'east',
  'north',
  'rb',
  'lb',
  'rt',
  'lt',
];

/** A stick axis in -1..1 to DSU's byte: 128 is centre, x grows rightward, y upward. */
function stickByte(value) {
  return Math.max(0, Math.min(255, Math.round(STICK_CENTRE + value * 127)));
}

/**
 * One pad-data message for a slot: motion from `sample`, buttons and sticks from `gamepad`
 * (a PAD state from pad-hub.js, or null before the phone has sent one, which reads as
 * nothing pressed and sticks centred).
 */
function padDataPayload(slot, packetNumber, sample, gamepad = null) {
  const buttons = gamepad ? gamepad.buttons : 0;
  const sticks = gamepad ? gamepad.sticks : [0, 0, 0, 0];
  const isDown = (name) => (buttons & PadBit[name]) !== 0;
  const payload = Buffer.alloc(PAD_DATA_PAYLOAD_BYTES);
  let o = 0;
  payload.writeUInt32LE(MessageType.PAD_DATA, o);
  o += 4;
  o = writeSlotHeader(payload, o, slot, true);
  payload.writeUInt8(1, o++); // connected
  payload.writeUInt32LE(packetNumber >>> 0, o);
  o += 4;
  let byte1 = 0;
  for (const [name, bit] of DSU_BUTTONS_1) if (isDown(name)) byte1 |= bit;
  let byte2 = 0;
  for (const [name, bit] of DSU_BUTTONS_2) if (isDown(name)) byte2 |= bit;
  payload.writeUInt8(byte1, o++);
  payload.writeUInt8(byte2, o++);
  payload.writeUInt8(isDown('home') ? 1 : 0, o++);
  o += 1; // touchpad button: no touchpad
  for (const value of sticks) payload.writeUInt8(stickByte(value), o++); // LX, LY, RX, RY
  for (const name of DSU_ANALOG_ORDER) payload.writeUInt8(isDown(name) ? 255 : 0, o++);
  o += 12; // two touch points, inactive
  payload.writeBigUInt64LE(BigInt(sample.ts), o);
  o += 8;
  const { accel, gyro } = phoneToDsuAxes(sample.accel, sample.gyro);
  for (const value of accel) {
    payload.writeFloatLE(value, o);
    o += 4;
  }
  for (const value of gyro) {
    payload.writeFloatLE(value, o);
    o += 4;
  }
  return payload;
}

/**
 * Read a pad-data body (after the message type) back into numbers. The server never needs
 * this; the tests and tools/dsu-client.js use it to check what an emulator would receive.
 */
function parsePadData(body) {
  if (body.length < PAD_DATA_PAYLOAD_BYTES - 4) return null;
  const at = (offset) => offset - 4; // offsets below are within the full payload
  return {
    slot: body.readUInt8(at(4)),
    slotState: body.readUInt8(at(5)),
    model: body.readUInt8(at(6)),
    mac: body.subarray(at(8), at(14)).toString('hex'),
    connected: body.readUInt8(at(15)) === 1,
    packetNumber: body.readUInt32LE(at(16)),
    buttons1: body.readUInt8(at(20)),
    buttons2: body.readUInt8(at(21)),
    home: body.readUInt8(at(22)),
    analog: [...body.subarray(at(28), at(40))],
    sticks: [
      body.readUInt8(at(24)),
      body.readUInt8(at(25)),
      body.readUInt8(at(26)),
      body.readUInt8(at(27)),
    ],
    timestampUs: body.readBigUInt64LE(at(52)),
    accel: [body.readFloatLE(at(60)), body.readFloatLE(at(64)), body.readFloatLE(at(68))],
    gyro: [body.readFloatLE(at(72)), body.readFloatLE(at(76)), body.readFloatLE(at(80))],
  };
}

// ---------------------------------------------------------------------------
// Server
// ---------------------------------------------------------------------------

/**
 * Serve pad data from `padHub` to DSU clients.
 *
 * @param {object} options
 * @param {object} options.padHub the hub from pad-hub.js (snapshot() and 'sample' events)
 * @param {number} [options.port] UDP port, 26760 by default
 * @param {string} [options.host] bind address; loopback by default, since the emulator runs
 *   on this PC and nothing on the hotspot should be able to read the controllers
 */
function createDsuServer({
  padHub,
  port = DEFAULT_PORT,
  host = '127.0.0.1',
  now = () => Date.now(),
  log = () => {},
  paceBufferMs = 0,
}) {
  const socket = dgram.createSocket('udp4');
  const serverId = crypto.randomBytes(4).readUInt32LE(0);
  // key "address:port" -> { address, port, lastSeen, allSlots, slots:Set, packetNumber }
  const clients = new Map();
  const stats = {
    listening: false,
    port,
    host,
    packetsIn: 0,
    packetsOut: 0,
    malformed: 0,
    error: null,
  };

  function send(payload, address, clientPort) {
    socket.send(buildPacket('DSUS', serverId, payload), clientPort, address, (err) => {
      if (err) log(`[dsu] send to ${address}:${clientPort} failed: ${err.message}`);
    });
    stats.packetsOut++;
  }

  function isConnected(slot) {
    return padHub.snapshot()[slot] !== null;
  }

  function onPortInfoRequest(body, address, clientPort) {
    if (body.length < 4) return;
    const count = Math.min(body.readInt32LE(0), 4, body.length - 4);
    for (let i = 0; i < count; i++) {
      const slot = body.readUInt8(4 + i);
      if (slot < 4) send(portInfoPayload(slot, isConnected(slot)), address, clientPort);
    }
  }

  function onPadDataRequest(body, address, clientPort) {
    if (body.length < 8) return;
    const flags = body.readUInt8(0);
    const slot = body.readUInt8(1);
    const key = `${address}:${clientPort}`;
    let client = clients.get(key);
    if (!client) {
      client = {
        address,
        port: clientPort,
        lastSeen: 0,
        allSlots: false,
        slots: new Set(),
        packetNumber: 0,
      };
      clients.set(key, client);
      log(`[dsu] client ${key} subscribed`);
    }
    client.lastSeen = now();
    // flags: 0 = every slot, bit 0 = by slot, bit 1 = by MAC. A MAC request is answered
    // as "every slot": each slot's MAC is unique, so matching it adds nothing here.
    if (flags === 0 || flags & 0x02) client.allSlots = true;
    else if (flags & 0x01 && slot < 4) client.slots.add(slot);
  }

  socket.on('message', (packet, rinfo) => {
    stats.packetsIn++;
    const parsed = parsePacket(packet, 'DSUC');
    if (!parsed) {
      stats.malformed++;
      return;
    }
    switch (parsed.type) {
      case MessageType.VERSION:
        send(versionPayload(), rinfo.address, rinfo.port);
        return;
      case MessageType.PORT_INFO:
        onPortInfoRequest(parsed.body, rinfo.address, rinfo.port);
        return;
      case MessageType.PAD_DATA:
        onPadDataRequest(parsed.body, rinfo.address, rinfo.port);
        return;
      default:
        stats.malformed++;
    }
  });

  // The newest motion already sent per slot, so a button press can go out at once with the
  // current paced motion rather than waiting for the next sample (or jumping the pacer's queue).
  const latestSample = new Array(4).fill(null);
  // Motion goes out at the phone's own spacing (#29); buttons are never delayed.
  const pacer = createPacer({
    bufferUs: paceBufferMs * 1000,
    send: (slot, sample) => {
      latestSample[slot] = sample;
      broadcast(slot, sample, currentGamepad(slot));
    },
  });

  function currentGamepad(slot) {
    const pad = padHub.snapshot()[slot];
    return pad ? pad.gamepad : null;
  }

  function onSample(slot, sample) {
    pacer.push(slot, sample);
  }

  function onPad(slot, gamepad) {
    // Before the first motion sample, report the phone at rest: flat, gravity down its back.
    const sample = latestSample[slot] || { ts: now() * 1000, accel: [0, 0, 1], gyro: [0, 0, 0] };
    broadcast(slot, sample, gamepad);
  }

  function onDisconnect(slot) {
    latestSample[slot] = null;
    pacer.clear(slot);
  }

  function broadcast(slot, sample, gamepad) {
    const t = now();
    for (const [key, client] of clients) {
      if (t - client.lastSeen > CLIENT_TIMEOUT_MS) {
        clients.delete(key);
        log(`[dsu] client ${key} timed out`);
        continue;
      }
      if (!client.allSlots && !client.slots.has(slot)) continue;
      send(
        padDataPayload(slot, client.packetNumber++, sample, gamepad),
        client.address,
        client.port,
      );
    }
  }
  padHub.events.on('sample', onSample);
  padHub.events.on('pad', onPad);
  padHub.events.on('disconnect', onDisconnect);

  socket.on('error', (err) => {
    // Never fatal to the relay: a busy port (another DSU server, a second relay) only
    // means no emulator input, which the monitor and the log both say.
    stats.error = err.message;
    stats.listening = false;
    log(`[dsu] disabled: ${err.message}`);
    padHub.events.off('sample', onSample);
    padHub.events.off('pad', onPad);
    padHub.events.off('disconnect', onDisconnect);
    socket.close();
  });

  socket.bind(port, host, () => {
    stats.listening = true;
    stats.port = socket.address().port;
    log(`[dsu] listening on udp ${host}:${stats.port}`);
  });

  /** For the monitor: is it up, and which emulators are reading. */
  function status() {
    const t = now();
    return {
      ...stats,
      clients: [...clients.values()]
        .filter((c) => t - c.lastSeen <= CLIENT_TIMEOUT_MS)
        .map((c) => ({
          address: `${c.address}:${c.port}`,
          slots: c.allSlots ? 'all' : [...c.slots],
          sent: c.packetNumber,
        })),
    };
  }

  function close() {
    padHub.events.off('sample', onSample);
    padHub.events.off('pad', onPad);
    padHub.events.off('disconnect', onDisconnect);
    if (stats.listening) socket.close();
    stats.listening = false;
  }

  return { status, close };
}

module.exports = {
  createDsuServer,
  buildPacket,
  parsePacket,
  crc32,
  phoneToDsuAxes,
  padDataPayload,
  parsePadData,
  PadBit,
  MessageType,
  SlotState,
  PROTOCOL_VERSION,
  DEFAULT_PORT,
  CLIENT_TIMEOUT_MS,
};
