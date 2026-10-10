// Captures every DSU pad-data packet as an emulator receives it, one JSON line each:
// { arrivalUs, timestampUs, packet, slot, accel, gyro }. arrivalUs is this PC's monotonic
// clock; timestampUs is the phone's own sample time carried in the packet. Feed the file to
// tools/sim-sudachi-motion.js to see how arrival jitter turns into heading drift.
//
// Run from the repo root, with the relay running:
//   node tools/dsu-capture.js capture.jsonl 60     (file, seconds)

const dgram = require('dgram');
const fs = require('fs');
const path = require('path');
const { buildPacket, parsePacket, parsePadData, MessageType } = require(
  path.join(__dirname, '..', 'relay', 'dsu.js'),
);

const file = process.argv[2] || 'capture.jsonl';
const seconds = Number(process.argv[3]) || 60;
const port = 26760;
const CLIENT_ID = 0x41a3c11f;

const out = fs.createWriteStream(file);
const socket = dgram.createSocket('udp4');
let count = 0;

function request(type, body) {
  const payload = Buffer.alloc(4 + body.length);
  payload.writeUInt32LE(type, 0);
  body.copy(payload, 4);
  socket.send(buildPacket('DSUC', CLIENT_ID, payload), port, '127.0.0.1');
}

socket.on('message', (packet) => {
  const arrivalUs = process.hrtime.bigint() / 1000n;
  const parsed = parsePacket(packet, 'DSUS');
  if (!parsed || parsed.type !== MessageType.PAD_DATA) return;
  const pad = parsePadData(parsed.body);
  if (!pad) return;
  count++;
  out.write(
    JSON.stringify({
      arrivalUs: Number(arrivalUs),
      timestampUs: Number(pad.timestampUs),
      packet: pad.packetNumber,
      slot: pad.slot,
      accel: pad.accel,
      gyro: pad.gyro,
    }) + '\n',
  );
});

socket.bind(0, '127.0.0.1', () => {
  console.log(`Capturing DSU pad data from 127.0.0.1:${port} into ${file} for ${seconds}s`);
  const subscribe = () => request(MessageType.PAD_DATA, Buffer.alloc(8)); // every slot
  subscribe();
  const renew = setInterval(subscribe, 1000);
  setTimeout(() => {
    clearInterval(renew);
    socket.close();
    out.end(() => {
      console.log(`done: ${count} packets`);
      process.exit(count > 0 ? 0 : 1);
    });
  }, seconds * 1000);
});
