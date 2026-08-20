#!/usr/bin/env node
/**
 * Minimal authoritative-ish DNS responder for the offline-hotspot spike.
 *
 * The question it exists to answer: when phones are on a PC-hosted hotspot, can the PC
 * answer `<something>.local-ip.sh` itself, so the borrowed certificate keeps working with
 * no internet at all? Certificates can be cached; DNS cannot, because the cache that matters
 * lives on each phone. Answering locally is the only way round that.
 *
 * It logs every query it receives, which is the actual output of the spike — seeing a
 * phone's address in that log is the proof that the phone asked *us* rather than a router.
 *
 * Deliberately hand-rolled: no dependencies, so it can be dropped into the relay process
 * later without adding one. Only what a phone actually asks for is implemented.
 *
 *   node dns-responder.js --ip 192.168.137.1 --name play.local-ip.sh
 *   node dns-responder.js --ip 10.0.0.213 --suffix .local-ip.sh --port 5399
 *   node dns-responder.js --ip 192.168.137.1 --suffix .local-ip.sh --upstream 1.1.1.1
 */

'use strict';

const dgram = require('dgram');

// ---------------------------------------------------------------- arguments

function parseArgs(argv) {
  const args = {
    ip: null,
    name: null,        // exact match, case-insensitive
    suffix: null,      // or match anything ending in this
    port: 53,
    bind: '0.0.0.0',
    upstream: null,    // forward non-matching queries here, if set
    ttl: 60,
  };

  for (let i = 2; i < argv.length; i += 2) {
    const key = argv[i].replace(/^--/, '');
    const value = argv[i + 1];
    if (!(key in args)) fail(`Unknown option: ${argv[i]}`);
    args[key] = key === 'port' || key === 'ttl' ? Number(value) : value;
  }

  if (!args.ip) fail('--ip is required (the address to answer with).');
  if (!args.name && !args.suffix) fail('One of --name or --suffix is required.');
  if (!/^(\d{1,3}\.){3}\d{1,3}$/.test(args.ip)) fail(`--ip is not an IPv4 address: ${args.ip}`);

  return args;
}

function fail(message) {
  console.error(`error: ${message}`);
  process.exit(1);
}

// ------------------------------------------------------------ wire decoding

const TYPE_A = 1;
const TYPE_AAAA = 28;
const CLASS_IN = 1;

const TYPE_NAMES = { 1: 'A', 2: 'NS', 5: 'CNAME', 12: 'PTR', 15: 'MX', 16: 'TXT', 28: 'AAAA', 33: 'SRV', 65: 'HTTPS' };

/**
 * Reads a DNS name starting at `offset`. Handles compression pointers, which a question
 * section should never use but a malformed or hostile packet certainly can.
 * Returns the name plus the offset just past it (pointers do not advance the caller).
 */
function readName(buffer, offset) {
  const labels = [];
  let jumped = false;
  let end = offset;
  let guard = 0;

  while (offset < buffer.length) {
    if (guard++ > 128) throw new Error('name too long or looping');
    const length = buffer[offset];

    if (length === 0) {
      if (!jumped) end = offset + 1;
      break;
    }

    if ((length & 0xc0) === 0xc0) {
      if (offset + 1 >= buffer.length) throw new Error('truncated pointer');
      const pointer = ((length & 0x3f) << 8) | buffer[offset + 1];
      if (!jumped) end = offset + 2;
      if (pointer >= buffer.length) throw new Error('pointer out of range');
      offset = pointer;
      jumped = true;
      continue;
    }

    offset += 1;
    if (offset + length > buffer.length) throw new Error('truncated label');
    labels.push(buffer.toString('ascii', offset, offset + length));
    offset += length;
  }

  return { name: labels.join('.'), end };
}

function decodeQuery(buffer) {
  if (buffer.length < 12) throw new Error('shorter than a DNS header');

  const id = buffer.readUInt16BE(0);
  const flags = buffer.readUInt16BE(2);
  const qdcount = buffer.readUInt16BE(4);

  if ((flags & 0x8000) !== 0) throw new Error('is a response, not a query');
  if (qdcount < 1) throw new Error('no question section');

  const { name, end } = readName(buffer, 12);
  if (end + 4 > buffer.length) throw new Error('truncated question');

  return {
    id,
    recursionDesired: (flags & 0x0100) !== 0,
    name,
    type: buffer.readUInt16BE(end),
    class: buffer.readUInt16BE(end + 2),
    questionStart: 12,
    questionEnd: end + 4,
  };
}

// ------------------------------------------------------------ wire encoding

const RCODE_NOERROR = 0;
const RCODE_NXDOMAIN = 3;
const RCODE_REFUSED = 5;

/**
 * Builds a reply reusing the question verbatim, which keeps the answer's name pointer
 * (0xC00C) valid without re-encoding the name.
 */
function buildReply(query, original, { rcode = RCODE_NOERROR, answerIp = null, ttl = 60 }) {
  const question = original.subarray(query.questionStart, query.questionEnd);
  const answer = answerIp ? Buffer.alloc(16) : Buffer.alloc(0);

  if (answerIp) {
    answer.writeUInt16BE(0xc00c, 0);          // pointer to the question's name
    answer.writeUInt16BE(TYPE_A, 2);
    answer.writeUInt16BE(CLASS_IN, 4);
    answer.writeUInt32BE(ttl, 6);
    answer.writeUInt16BE(4, 10);              // RDLENGTH
    for (const [i, octet] of answerIp.split('.').entries()) {
      answer.writeUInt8(Number(octet), 12 + i);
    }
  }

  const header = Buffer.alloc(12);
  header.writeUInt16BE(query.id, 0);
  // QR=1, AA=1, plus RA so a client that asked for recursion does not report failure.
  header.writeUInt16BE(0x8400 | (query.recursionDesired ? 0x0180 : 0x0000) | rcode, 2);
  header.writeUInt16BE(1, 4);                 // QDCOUNT
  header.writeUInt16BE(answerIp ? 1 : 0, 6);  // ANCOUNT
  header.writeUInt16BE(0, 8);
  header.writeUInt16BE(0, 10);

  return Buffer.concat([header, question, answer]);
}

// -------------------------------------------------------------------- serve

function matches(name, args) {
  const lower = name.toLowerCase();
  if (args.name) return lower === args.name.toLowerCase();
  return lower.endsWith(args.suffix.toLowerCase());
}

function main() {
  const args = parseArgs(process.argv);
  const socket = dgram.createSocket('udp4');
  const seen = new Set();
  let queries = 0;

  socket.on('error', (err) => {
    if (err.code === 'EADDRINUSE') {
      console.error(`\nCannot bind ${args.bind}:${args.port} — something already holds it.`);
      console.error('If the hotspot is running, that is almost certainly ICS (SharedAccess).');
      console.error('Stop tethering, start this first, then start tethering again.\n');
    } else if (err.code === 'EACCES') {
      console.error(`\nPermission denied binding ${args.bind}:${args.port}.\n`);
    } else {
      console.error(`\nSocket error: ${err.message}\n`);
    }
    process.exit(1);
  });

  socket.on('message', (message, remote) => {
    queries += 1;

    let query;
    try {
      query = decodeQuery(message);
    } catch (e) {
      console.log(`  ${remote.address.padEnd(15)} <malformed: ${e.message}>`);
      return;
    }

    const typeName = TYPE_NAMES[query.type] || `TYPE${query.type}`;

    if (!seen.has(remote.address)) {
      seen.add(remote.address);
      console.log(`\n*** first query from ${remote.address} — ${seen.size} client(s) so far ***`);
    }

    if (query.class !== CLASS_IN) {
      socket.send(buildReply(query, message, { rcode: RCODE_REFUSED }), remote.port, remote.address);
      console.log(`  ${remote.address.padEnd(15)} ${typeName.padEnd(5)} ${query.name}  -> REFUSED (class ${query.class})`);
      return;
    }

    if (matches(query.name, args)) {
      // Answer A. For AAAA on a name we own, return an empty NOERROR rather than
      // NXDOMAIN: NXDOMAIN means "this name does not exist" for every type, and a phone
      // that believes that will not go on to try A.
      if (query.type === TYPE_A) {
        socket.send(buildReply(query, message, { answerIp: args.ip, ttl: args.ttl }), remote.port, remote.address);
        console.log(`  ${remote.address.padEnd(15)} ${typeName.padEnd(5)} ${query.name}  -> ${args.ip}  [MATCH]`);
      } else {
        socket.send(buildReply(query, message, {}), remote.port, remote.address);
        console.log(`  ${remote.address.padEnd(15)} ${typeName.padEnd(5)} ${query.name}  -> no ${typeName} (empty NOERROR)  [MATCH]`);
      }
      return;
    }

    if (args.upstream) {
      forward(message, remote, args, socket, typeName, query.name);
      return;
    }

    socket.send(buildReply(query, message, { rcode: RCODE_NXDOMAIN }), remote.port, remote.address);
    console.log(`  ${remote.address.padEnd(15)} ${typeName.padEnd(5)} ${query.name}  -> NXDOMAIN (no upstream)`);
  });

  socket.on('listening', () => {
    const target = args.name || `*${args.suffix}`;
    console.log('');
    console.log(`  DNS responder on ${args.bind}:${args.port}`);
    console.log(`  Answering  ${target}  ->  ${args.ip}   (TTL ${args.ttl}s)`);
    console.log(`  Other names: ${args.upstream ? `forwarded to ${args.upstream}` : 'NXDOMAIN'}`);
    console.log('');
    console.log('  Every query is logged below. A phone address appearing here is the');
    console.log('  result the spike is looking for.');
    console.log('');
  });

  process.on('SIGINT', () => {
    console.log(`\n\n  ${queries} quer${queries === 1 ? 'y' : 'ies'} from ${seen.size} client(s): ${[...seen].join(', ') || 'none'}\n`);
    socket.close(() => process.exit(0));
  });

  socket.bind(args.port, args.bind);
}

/** Forwards verbatim and relays the reply back, so non-game names keep working. */
function forward(message, remote, args, socket, typeName, name) {
  const client = dgram.createSocket('udp4');
  const timer = setTimeout(() => {
    client.close();
    console.log(`  ${remote.address.padEnd(15)} ${typeName.padEnd(5)} ${name}  -> upstream timeout`);
  }, 3000);

  client.on('error', () => {
    clearTimeout(timer);
    client.close();
  });

  client.on('message', (reply) => {
    clearTimeout(timer);
    socket.send(reply, remote.port, remote.address);
    client.close();
    console.log(`  ${remote.address.padEnd(15)} ${typeName.padEnd(5)} ${name}  -> forwarded`);
  });

  client.send(message, 53, args.upstream);
}

main();
