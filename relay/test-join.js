'use strict';
// The join page's data and stop endpoints, on the loopback-only listener (#14).
const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('child_process');
const http = require('http');
const path = require('path');

const PORT = 39181;
const JOIN_PORT = 39182;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function request(port, method, urlPath) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port, method, path: urlPath }, (res) => {
      let body = '';
      res.on('data', (c) => (body += c));
      res.on('end', () => resolve({ status: res.statusCode, body }));
    });
    req.on('error', reject);
    req.end();
  });
}

let server;
let exitCode = null;
before(async () => {
  server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
    env: {
      ...process.env,
      PORT: String(PORT),
      JOIN_PORT: String(JOIN_PORT),
      DSU_PORT: '0',
      JOIN_SSID: 'AJ Motion 1A2B',
      JOIN_PASS: 'p;ss,word',
      DNS_NAME: 'pad.skiprocking.ca',
      JOIN_EMULATORS: JSON.stringify([{ emulator: 'Sudachi', status: 'changed', detail: 'x' }]),
    },
    stdio: ['ignore', 'ignore', 'inherit'],
  });
  server.on('exit', (code) => (exitCode = code));
  await sleep(600);
});
after(() => {
  if (exitCode === null) server.kill('SIGKILL');
});

test('the join listener serves /api/join with the hotspot details', async () => {
  const { status, body } = await request(JOIN_PORT, 'GET', '/api/join');
  assert.equal(status, 200);
  assert.deepEqual(JSON.parse(body), {
    ssid: 'AJ Motion 1A2B',
    passphrase: 'p;ss,word',
    padUrl: `https://pad.skiprocking.ca:${PORT}/pad.html`,
    emulators: [{ emulator: 'Sudachi', status: 'changed', detail: 'x' }],
  });
});

test('the join listener serves join.html', async () => {
  const { status, body } = await request(JOIN_PORT, 'GET', '/join.html');
  assert.equal(status, 200);
  assert.match(body, /<title>/);
});

test('POST /api/stop ends the relay with code 0', async () => {
  const { status } = await request(JOIN_PORT, 'POST', '/api/stop');
  assert.equal(status, 204);
  await sleep(400);
  assert.equal(exitCode, 0);
});
