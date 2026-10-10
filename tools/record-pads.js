'use strict';
// Records what the relay receives from each phone, ten times a second, so input lag and drops
// can be lined up against other events (the relay's DNS log lines carry the same ISO times).
// Usage: node tools/record-pads.js [seconds=120] > pads.log    (relay must be running)
// Each line: ISO time, then per connected slot: samples received in the last 100 ms, total
// sequence gaps (lost samples), the relay's rate, and the newest accel reading in g.

const http = require('http');

const JOIN_PORT = Number(process.env.JOIN_PORT || 8780);
const seconds = Number(process.argv[2] || 120);
const last = new Map();

function poll() {
  http
    .get({ host: '127.0.0.1', port: JOIN_PORT, path: '/api/pads', timeout: 500 }, (res) => {
      let body = '';
      res.on('data', (c) => (body += c));
      res.on('end', () => {
        let pads;
        try {
          ({ pads } = JSON.parse(body));
        } catch (err) {
          console.log(`${new Date().toISOString()} bad reply: ${err.message}`);
          return;
        }
        const parts = pads.map((p, slot) => {
          if (!p) return null;
          const prev = last.get(slot) || p.received;
          last.set(slot, p.received);
          const a = p.latest ? p.latest.accel.map((v) => v.toFixed(2)).join(',') : '-';
          return `slot${slot} +${p.received - prev} dropped=${p.dropped} rate=${p.rateHz} accel=${a}`;
        });
        console.log(`${new Date().toISOString()} ${parts.filter(Boolean).join(' | ') || 'no phone'}`);
      });
    })
    .on('error', (err) => console.log(`${new Date().toISOString()} relay unreachable: ${err.message}`));
}

const timer = setInterval(poll, 100);
setTimeout(() => clearInterval(timer), seconds * 1000);
