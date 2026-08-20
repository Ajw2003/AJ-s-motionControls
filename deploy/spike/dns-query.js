#!/usr/bin/env node
/**
 * Tiny DNS client for testing dns-responder.js.
 *
 * Exists because Windows `nslookup` silently ignores `-port=` when a server is given, which
 * looks exactly like the responder being broken. This sends a real query to a chosen
 * host:port and prints what actually came back.
 *
 *   node dns-query.js play.local-ip.sh A 127.0.0.1 5399
 */

'use strict';

const dgram = require('dgram');

const TYPES = { A: 1, NS: 2, CNAME: 5, PTR: 12, TXT: 16, AAAA: 28 };
const RCODES = ['NOERROR', 'FORMERR', 'SERVFAIL', 'NXDOMAIN', 'NOTIMP', 'REFUSED'];

const [, , name, typeName = 'A', host = '127.0.0.1', port = '53'] = process.argv;

if (!name) {
  console.error('usage: node dns-query.js <name> [type] [server] [port]');
  process.exit(1);
}

const qtype = TYPES[typeName.toUpperCase()];
if (!qtype) {
  console.error(`unknown type: ${typeName} (known: ${Object.keys(TYPES).join(', ')})`);
  process.exit(1);
}

function encodeQuery(name, qtype) {
  const labels = name.split('.').filter(Boolean);
  const nameLength = labels.reduce((total, label) => total + 1 + label.length, 0) + 1;
  const buffer = Buffer.alloc(12 + nameLength + 4);

  buffer.writeUInt16BE(0x4242, 0);   // fixed ID, so a stray reply is obvious
  buffer.writeUInt16BE(0x0100, 2);   // standard query, recursion desired
  buffer.writeUInt16BE(1, 4);        // QDCOUNT

  let offset = 12;
  for (const label of labels) {
    buffer.writeUInt8(label.length, offset);
    buffer.write(label, offset + 1, 'ascii');
    offset += 1 + label.length;
  }
  buffer.writeUInt8(0, offset);
  offset += 1;

  buffer.writeUInt16BE(qtype, offset);
  buffer.writeUInt16BE(1, offset + 2);  // IN
  return buffer;
}

function skipName(buffer, offset) {
  while (offset < buffer.length) {
    const length = buffer[offset];
    if (length === 0) return offset + 1;
    if ((length & 0xc0) === 0xc0) return offset + 2;
    offset += 1 + length;
  }
  return offset;
}

function describe(buffer) {
  const flags = buffer.readUInt16BE(2);
  const rcode = flags & 0x0f;
  const ancount = buffer.readUInt16BE(6);

  const lines = [
    `  rcode      : ${RCODES[rcode] || rcode}`,
    `  authoritative: ${(flags & 0x0400) !== 0}`,
    `  answers    : ${ancount}`,
  ];

  let offset = skipName(buffer, 12) + 4;  // past the question

  for (let i = 0; i < ancount; i += 1) {
    offset = skipName(buffer, offset);
    const type = buffer.readUInt16BE(offset);
    const ttl = buffer.readUInt32BE(offset + 4);
    const rdlength = buffer.readUInt16BE(offset + 8);
    const rdata = buffer.subarray(offset + 10, offset + 10 + rdlength);

    const value = type === 1 && rdlength === 4 ? [...rdata].join('.') : `<type ${type}, ${rdlength} bytes>`;
    lines.push(`  answer[${i}]  : ${value}   (ttl ${ttl})`);
    offset += 10 + rdlength;
  }

  return lines.join('\n');
}

const socket = dgram.createSocket('udp4');
const timer = setTimeout(() => {
  console.log(`  NO RESPONSE from ${host}:${port} after 3s`);
  socket.close();
  process.exit(2);
}, 3000);

socket.on('message', (reply) => {
  clearTimeout(timer);
  console.log(describe(reply));
  socket.close();
  process.exit(0);
});

socket.on('error', (err) => {
  clearTimeout(timer);
  console.log(`  ERROR: ${err.message}`);
  process.exit(1);
});

console.log(`\nquery ${name} ${typeName.toUpperCase()} -> ${host}:${port}`);
socket.send(encodeQuery(name, qtype), Number(port), host);
