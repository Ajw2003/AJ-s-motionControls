// A minimal DSU client: asks the PC server for every slot, like an emulator would, and
// prints what arrives. Checks the DSU side of issue #3 without Dolphin.
//
// Run from the repo root, with the relay running:
//   node tools/dsu-client.js            (port 26760, prints for 10 seconds)
//   node tools/dsu-client.js 26760 30   (port, seconds)
// Exits 1 if nothing valid arrived.

const dgram = require('dgram');
const path = require('path');
const { buildPacket, parsePacket, parsePadData, MessageType, SlotState } = require(
  path.join(__dirname, '..', 'relay', 'dsu.js'),
);

// DualShock 4 names, as an emulator's DSU input view shows them.
const DSU_BUTTON_NAMES = [
  ['buttons1', 0x80, 'Left'],
  ['buttons1', 0x40, 'Down'],
  ['buttons1', 0x20, 'Right'],
  ['buttons1', 0x10, 'Up'],
  ['buttons1', 0x08, 'Options'],
  ['buttons1', 0x04, 'R3'],
  ['buttons1', 0x02, 'L3'],
  ['buttons1', 0x01, 'Share'],
  ['buttons2', 0x80, 'Square'],
  ['buttons2', 0x40, 'Cross'],
  ['buttons2', 0x20, 'Circle'],
  ['buttons2', 0x10, 'Triangle'],
  ['buttons2', 0x08, 'R1'],
  ['buttons2', 0x04, 'L1'],
  ['buttons2', 0x02, 'R2'],
  ['buttons2', 0x01, 'L2'],
];

const port = Number(process.argv[2]) || 26760;
const seconds = Number(process.argv[3]) || 10;
const CLIENT_ID = 0x41a3c11e;
// Emulators renew their subscription about once a second; the server drops a client
// after five quiet seconds.
const RESUBSCRIBE_MS = 1000;

const socket = dgram.createSocket('udp4');
const perSlot = new Map(); // slot -> { count, last }
let malformed = 0;
let versionSeen = null;

function request(type, body) {
  const payload = Buffer.alloc(4 + body.length);
  payload.writeUInt32LE(type, 0);
  body.copy(payload, 4);
  socket.send(buildPacket('DSUC', CLIENT_ID, payload), port, '127.0.0.1');
}

socket.on('message', (packet) => {
  const parsed = parsePacket(packet, 'DSUS');
  if (!parsed) {
    malformed++;
    return;
  }
  if (parsed.type === MessageType.VERSION) versionSeen = parsed.body.readUInt16LE(0);
  if (parsed.type === MessageType.PORT_INFO && parsed.body.readUInt8(1) === SlotState.CONNECTED) {
    console.log(`slot ${parsed.body.readUInt8(0) + 1}: connected`);
  }
  if (parsed.type === MessageType.PAD_DATA) {
    const pad = parsePadData(parsed.body);
    const entry = perSlot.get(pad.slot) || { count: 0, last: null };
    entry.count++;
    entry.last = pad;
    perSlot.set(pad.slot, entry);
  }
});

socket.bind(0, '127.0.0.1', () => {
  console.log(`DSU client asking 127.0.0.1:${port} for ${seconds}s`);
  request(MessageType.VERSION, Buffer.alloc(0));
  request(MessageType.PORT_INFO, Buffer.from([4, 0, 0, 0, 0, 1, 2, 3]));
  const subscribe = () => request(MessageType.PAD_DATA, Buffer.alloc(8)); // every slot
  subscribe();
  const renew = setInterval(subscribe, RESUBSCRIBE_MS);

  const report = setInterval(() => {
    for (const [slot, { count, last }] of perSlot) {
      const a = last.accel.map((n) => n.toFixed(2)).join(' ');
      const g = last.gyro.map((n) => n.toFixed(0)).join(' ');
      const held = DSU_BUTTON_NAMES.filter(([byte, bit]) => last[byte] & bit).map(([, , n]) => n);
      if (last.home) held.push('PS/Home');
      console.log(
        `slot ${slot + 1}: ${count} packets  accel(g) ${a}  gyro(deg/s) ${g}` +
          `  buttons [${held.join(' ')}]  sticks ${last.sticks.join(' ')}`,
      );
    }
  }, 1000);

  setTimeout(() => {
    clearInterval(renew);
    clearInterval(report);
    socket.close();
    const total = [...perSlot.values()].reduce((sum, e) => sum + e.count, 0);
    console.log(
      `done: server protocol ${versionSeen ?? 'no reply'}, ${total} pad packets over ${seconds}s, ${malformed} malformed`,
    );
    process.exit(total > 0 && malformed === 0 ? 0 : 1);
  }, seconds * 1000);
});
