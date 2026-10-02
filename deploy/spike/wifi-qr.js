#!/usr/bin/env node
/**
 * Renders a Wi-Fi join QR as terminal ASCII, so the two-scan flow can be tested with a real
 * phone before any of it is built into the game.
 *
 * Payload is the de-facto standard both Android and iOS cameras understand:
 *   WIFI:T:WPA;S:<ssid>;P:<passphrase>;H:false;;
 *
 * Reuses qrcode-generator from relay/node_modules — already a dependency of the relay, so
 * this adds nothing new to bundle.
 *
 *   node wifi-qr.js --ssid "RockSkip ABCD" --pass "hunter2000"
 *   node wifi-qr.js --ssid "RockSkip ABCD" --pass "hunter2000" --url https://play.local-ip.sh:8443
 *   node wifi-qr.js --ssid "RockSkip ABCD" --pass "hunter2000" --svg C:/path/join.svg
 *
 * --svg writes the Wi-Fi QR as an image instead, for consoles that garble the block characters.
 */

'use strict';

const path = require('path');

let qrcode;
try {
  qrcode = require(path.join(__dirname, '..', '..', 'relay', 'node_modules', 'qrcode-generator'));
} catch {
  console.error('Could not load qrcode-generator from relay/node_modules.');
  console.error('Run `npm install` in relay/ first.');
  process.exit(1);
}

const args = { ssid: null, pass: null, auth: 'WPA', hidden: 'false', url: null, svg: null };
for (let i = 2; i < process.argv.length; i += 2) {
  const key = process.argv[i].replace(/^--/, '');
  if (!(key in args)) {
    console.error(`Unknown option: ${process.argv[i]}`);
    process.exit(1);
  }
  args[key] = process.argv[i + 1];
}

if (!args.ssid) {
  console.error('usage: node wifi-qr.js --ssid <ssid> --pass <passphrase> [--url <join url>]');
  process.exit(1);
}

/** Escapes the four characters that are structural in a WIFI: payload. */
function escapeValue(value) {
  return String(value).replace(/([\\;,:"])/g, '\\$1');
}

function render(text, label) {
  // Type 0 = auto-size, 'L' = most error correction headroom traded for the smallest
  // symbol, which matters when the target is an 80-column terminal.
  const qr = qrcode(0, 'L');
  qr.addData(text);
  qr.make();

  const count = qr.getModuleCount();
  const pad = 2;
  const lines = [];

  // Two vertical modules per character cell via half-blocks, so the symbol stays square
  // in a terminal where cells are roughly twice as tall as they are wide.
  for (let row = -pad; row < count + pad; row += 2) {
    let line = '';
    for (let col = -pad; col < count + pad; col += 1) {
      const top = row >= 0 && row < count && col >= 0 && col < count && qr.isDark(row, col);
      const bottom = row + 1 >= 0 && row + 1 < count && col >= 0 && col < count && qr.isDark(row + 1, col);
      if (top && bottom) line += '█';
      else if (top) line += '▀';
      else if (bottom) line += '▄';
      else line += ' ';
    }
    lines.push(line);
  }

  console.log('');
  console.log(`  ${label}`);
  console.log('');
  for (const line of lines) console.log('  ' + line);
  console.log('');
  console.log(`  payload: ${text}`);
  console.log('');
}

const payload = `WIFI:T:${args.auth};S:${escapeValue(args.ssid)};P:${escapeValue(args.pass || '')};H:${args.hidden};;`;

if (args.svg) {
  const qr = qrcode(0, 'M');
  qr.addData(payload);
  qr.make();
  const svg = qr.createSvgTag({ cellSize: 10, margin: 4, scalable: true })
    .replace('<svg', '<svg width="480" height="480" style="background:#fff"');
  require('fs').writeFileSync(args.svg, svg);
  console.log(`Wrote ${args.svg}`);
  process.exit(0);
}
render(payload, `SCAN 1 of 2 — join the network "${args.ssid}"`);

if (args.url) {
  render(args.url, 'SCAN 2 of 2 — open the game');
  console.log('  Scan them in order. The first joins the hotspot, the second opens the');
  console.log('  controller. They are separate payload types and cannot be combined.');
  console.log('');
}
