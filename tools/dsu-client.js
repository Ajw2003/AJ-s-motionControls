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
      console.log(`slot ${slot + 1}: ${count} packets  accel(g) ${a}  gyro(deg/s) ${g}`);
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
