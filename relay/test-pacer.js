'use strict';
// The DSU pacer (#29): samples go out at the phone's own spacing, a fixed buffer after the
// fastest observed transit, however bunched they arrive. Driven with a fake clock.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createPacer } = require('./pacer');

function harness(bufferUs) {
  let clock = 0;
  const sent = [];
  const pacer = createPacer({
    bufferUs,
    nowUs: () => clock,
    send: (slot, sample) => sent.push({ at: clock, slot, seq: sample.seq }),
    schedule: () => {}, // the test calls pump() itself
  });
  const advanceTo = (t) => {
    // Step in 0.1 ms, as a busy-wait would, pumping at each step.
    while (clock < t) {
      clock = Math.min(t, clock + 100);
      pacer.pump();
    }
  };
  return { pacer, sent, advanceTo, at: (t) => (clock = t) };
}

const sample = (seq, ts) => ({ seq, ts, accel: [0, 0, 1], gyro: [0, 0, 0] });

test('bunched arrivals go out at the phone spacing, one buffer after the fastest transit', () => {
  const h = harness(17000);
  // Phone samples every 16.667 ms; transit 5 ms, but samples 1-3 are held up and arrive together.
  const ts = [0, 16667, 33333, 50000, 66667];
  const arrive = [5000, 60000, 60200, 60400, 71667];
  ts.forEach((t, i) => {
    h.advanceTo(arrive[i]);
    h.pacer.push(0, sample(i, 1_000_000 + t));
  });
  h.advanceTo(200000);
  const due = ts.map((t) => t + 5000 + 17000);
  assert.deepEqual(
    h.sent.map((s) => s.seq),
    [0, 1, 2, 3, 4],
  );
  // Sample 0 and 4 on time; 1 and 2 were already late when they arrived (go out at once, in
  // order); 3 arrived early enough to wait for its slot.
  // On-time sends land within 0.1 ms: the pump steps 0.1 ms, and the transit estimate creeps
  // up 20 us a sample until a fast sample pulls it back.
  const near = (actual, expected) => assert.ok(Math.abs(actual - expected) <= 100, `${actual} vs ${expected}`);
  near(h.sent[0].at, due[0]);
  assert.equal(h.sent[1].at, 60000);
  assert.equal(h.sent[2].at, 60200);
  near(h.sent[3].at, due[3]);
  near(h.sent[4].at, due[4]);
});

test('a phone clock that runs slow is followed, not left to pile up delay', () => {
  const h = harness(17000);
  // Transit 5 ms, but the phone's timestamps fall 1 ms further behind every 100 samples
  // (a 600 ppm slow clock, worse than real phones), for 3000 samples.
  let worstWait = 0;
  for (let i = 0; i < 3000; i++) {
    const t = i * 16667;
    const arrival = 5000 + t;
    const phoneTs = t - Math.floor(i / 100) * 1000;
    h.advanceTo(arrival);
    h.pacer.push(0, sample(i, phoneTs));
    const waiting = h.pacer.queued();
    worstWait = Math.max(worstWait, waiting);
  }
  h.advanceTo(3000 * 16667 + 100000);
  assert.equal(h.sent.length, 3000);
  const last = h.sent.at(-1);
  const lastArrival = 5000 + 2999 * 16667;
  assert.ok(last.at - lastArrival <= 17000 + 1000, `delay grew to ${(last.at - lastArrival) / 1000} ms`);
  assert.ok(worstWait <= 3, `queue grew to ${worstWait}`);
});

test('slots are paced independently and a disconnect clears the slot', () => {
  const h = harness(17000);
  h.at(5000);
  h.pacer.push(0, sample(0, 0));
  h.pacer.push(1, sample(100, 500000)); // a second phone with its own clock
  h.pacer.clear(0);
  h.advanceTo(100000);
  assert.deepEqual(
    h.sent.map((s) => [s.slot, s.seq]),
    [[1, 100]],
  );
});

test('buffer 0 sends at once, as before pacing', () => {
  const h = harness(0);
  h.at(5000);
  h.pacer.push(0, sample(0, 0));
  h.at(5050);
  h.pacer.push(0, sample(1, 16667));
  assert.deepEqual(
    h.sent.map((s) => [s.at, s.seq]),
    [
      [5000, 0],
      [5050, 1],
    ],
  );
});

test('through the real DSU server: a burst from the phone reaches the emulator evenly spaced', async () => {
  const dgram = require('dgram');
  const { EventEmitter } = require('events');
  const { createDsuServer, buildPacket, parsePacket, parsePadData, MessageType } = require('./dsu');
  const events = new EventEmitter();
  const padHub = { events, snapshot: () => [{ gamepad: null }, null, null, null] };
  const port = 26987;
  const server = createDsuServer({ padHub, port, paceBufferMs: 17 });
  const client = dgram.createSocket('udp4');
  const arrivals = [];
  client.on('message', (packet) => {
    const parsed = parsePacket(packet, 'DSUS');
    if (parsed && parsed.type === MessageType.PAD_DATA && parsePadData(parsed.body)) {
      arrivals.push(Number(process.hrtime.bigint() / 1000n));
    }
  });
  await new Promise((resolve) => client.bind(0, '127.0.0.1', resolve));
  const body = Buffer.alloc(12);
  body.writeUInt32LE(MessageType.PAD_DATA, 0);
  client.send(buildPacket('DSUC', 1, body), port, '127.0.0.1');
  await new Promise((r) => setTimeout(r, 200));

  // 60 samples 16.667 ms apart on the phone's clock with 3 ms transit, except every fifth is
  // held up and arrives together with the next one, 16.7 ms late: the Wi-Fi pattern measured
  // on aj's PC, smaller than the 17 ms buffer.
  const nowUs = () => Number(process.hrtime.bigint() / 1000n);
  const phoneStart = nowUs();
  const sampleAt = (i) => ({ seq: i, ts: phoneStart + i * 16667, accel: [0, 0, 1], gyro: [0, 0, 0] });
  for (let i = 0; i < 60; i++) {
    const arriveAt = phoneStart + i * 16667 + 3000;
    while (nowUs() < arriveAt) await new Promise((r) => setImmediate(r));
    if (i % 5 === 3) continue; // held up...
    if (i % 5 === 4) events.emit('sample', 0, sampleAt(i - 1)); // ...and arrives with the next
    events.emit('sample', 0, sampleAt(i));
  }
  await new Promise((r) => setTimeout(r, 300));
  client.close();
  server.close();

  assert.equal(arrivals.length, 60);
  const gaps = arrivals.slice(1).map((t, i) => (t - arrivals[i]) / 1000);
  const sorted = [...gaps].sort((a, b) => a - b);
  const pct = (p) => sorted[Math.floor((sorted.length - 1) * p)];
  // Without pacing the held-up pairs would land ~0 ms apart after a ~33 ms gap.
  assert.ok(pct(0.05) > 15.5 && pct(0.95) < 18, `gaps ms 5%..95%: ${pct(0.05).toFixed(2)}..${pct(0.95).toFixed(2)}`);
});
