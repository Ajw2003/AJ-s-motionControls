'use strict';

/**
 * A deliberately small authoritative DNS responder, for hotspot mode only.
 *
 * WHY THIS EXISTS
 * Certificates can be cached, so a host only needs the internet once a quarter for those.
 * DNS cannot be cached, because the cache that matters lives on *each phone* — a guest
 * joining for the first time has nothing. Answering locally is the only way round that, and
 * it is what makes playing with no internet possible at all.
 *
 * When the PC is the access point it is already its clients' DNS server, so it can answer
 * authoritatively with no upstream. Proven with a real phone resolving `play.local-ip.sh`,
 * a name no public resolver answers.
 *
 * WHERE IT MUST NOT RUN
 * LAN mode. There, phones use the router's DNS, which is none of our business — and running
 * a resolver a phone did not ask for would be both useless and rude. Env-gated accordingly.
 *
 * SECURITY - READ BEFORE CHANGING ANY DEFAULT
 * An earlier cut of this bound every interface and forwarded for anyone, which is the
 * textbook definition of an open recursive resolver: free for any host on any attached
 * network, and a DNS amplification reflector for spoofed-source DDoS.
 *
 * The source guard is therefore LOAD-BEARING, not defence in depth. It cannot be replaced by
 * binding narrowly, because the hotspot address does not exist until the hotspot is running
 * and :53 must be held *before* that to win the race with ICS. See SecurityReview.md §4.
 */

const dgram = require('dgram');

const TYPE_A = 1;
const CLASS_IN = 1;

const RCODE_NOERROR = 0;
const RCODE_NXDOMAIN = 3;
const RCODE_REFUSED = 5;

const TYPE_NAMES = { 1: 'A', 2: 'NS', 5: 'CNAME', 12: 'PTR', 16: 'TXT', 28: 'AAAA', 65: 'HTTPS' };

// ------------------------------------------------------------------ wire decoding

/**
 * Reads a DNS name at `offset`, following compression pointers. A question section should
 * never use one; a malformed or hostile packet certainly can.
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

  const flags = buffer.readUInt16BE(2);
  if ((flags & 0x8000) !== 0) throw new Error('is a response, not a query');
  if (buffer.readUInt16BE(4) < 1) throw new Error('no question section');

  const { name, end } = readName(buffer, 12);
  if (end + 4 > buffer.length) throw new Error('truncated question');

  return {
    id: buffer.readUInt16BE(0),
    recursionDesired: (flags & 0x0100) !== 0,
    name,
    type: buffer.readUInt16BE(end),
    class: buffer.readUInt16BE(end + 2),
    questionStart: 12,
    questionEnd: end + 4,
  };
}

// ------------------------------------------------------------------ wire encoding

/**
 * Builds a reply reusing the question bytes verbatim, which keeps the answer's name pointer
 * (0xC00C) valid without re-encoding the name.
 */
function buildReply(query, original, { rcode = RCODE_NOERROR, answerIp = null, ttl = 60 } = {}) {
  const question = original.subarray(query.questionStart, query.questionEnd);
  const answer = answerIp ? Buffer.alloc(16) : Buffer.alloc(0);

  if (answerIp) {
    answer.writeUInt16BE(0xc00c, 0);
    answer.writeUInt16BE(TYPE_A, 2);
    answer.writeUInt16BE(CLASS_IN, 4);
    answer.writeUInt32BE(ttl, 6);
    answer.writeUInt16BE(4, 10);
    const octets = answerIp.split('.');
    for (let i = 0; i < 4; i += 1) answer.writeUInt8(Number(octets[i]), 12 + i);
  }

  const header = Buffer.alloc(12);
  header.writeUInt16BE(query.id, 0);
  // QR=1, AA=1, plus RA so a client that asked for recursion does not read this as failure.
  header.writeUInt16BE(0x8400 | (query.recursionDesired ? 0x0180 : 0x0000) | rcode, 2);
  header.writeUInt16BE(1, 4);
  header.writeUInt16BE(answerIp ? 1 : 0, 6);

  return Buffer.concat([header, question, answer]);
}

// ------------------------------------------------------------------ source guards

function ipToInt(ip) {
  const parts = ip.split('.');
  if (parts.length !== 4) return null;

  let value = 0;
  for (const part of parts) {
    if (!/^\d{1,3}$/.test(part)) return null;
    const octet = Number(part);
    if (octet > 255) return null;
    value = (value * 256) + octet;
  }
  return value;
}

/** Returns a predicate for "is this source allowed to ask us anything at all". */
function makeSubnetTest(cidr) {
  const [network, bitsRaw] = String(cidr).split('/');
  const bits = bitsRaw === undefined ? 32 : Number(bitsRaw);
  const base = ipToInt(network);

  if (base === null || !Number.isInteger(bits) || bits < 0 || bits > 32) {
    throw new Error(`not a valid CIDR: ${cidr}`);
  }

  const mask = bits === 0 ? 0 : (2 ** 32 - 2 ** (32 - bits));
  const masked = (base & mask) >>> 0;

  return (address) => {
    if (address === '127.0.0.1') return true;   // local testing, never a remote source
    const value = ipToInt(String(address).replace(/^::ffff:/, ''));
    if (value === null) return false;
    return ((value & mask) >>> 0) === masked;
  };
}

/**
 * Per-source rolling window. Amplification needs volume, so a cap per source removes most of
 * the value of using this as a reflector even if the range guard is somehow wrong.
 */
function makeRateLimiter(perSecond, now = () => Date.now()) {
  const windows = new Map();

  return (address) => {
    const at = now();
    let entry = windows.get(address);

    if (!entry || at - entry.start >= 1000) {
      entry = { start: at, count: 0 };
      windows.set(address, entry);
    }

    entry.count += 1;

    // Bound the map so a spoofed-source flood cannot grow it without limit.
    if (windows.size > 1024) {
      for (const [key, value] of windows) {
        if (at - value.start >= 1000) windows.delete(key);
      }
    }

    return entry.count > perSecond;
  };
}

// ----------------------------------------------------------------------- responder

/**
 * @param {object}   options
 * @param {string}   options.ip        address to answer with
 * @param {string}  [options.suffix]   answer anything ending in this
 * @param {string}  [options.name]     or answer exactly this name
 * @param {string}  [options.allow]    CIDR permitted to query. REQUIRED in practice.
 * @param {number}  [options.rate]     max queries per second per source
 * @param {string}  [options.upstream] forward non-matching names here; omitted means NXDOMAIN
 */
function createResponder(options) {
  const {
    ip,
    suffix = null,
    name = null,
    port = 53,
    bind = '0.0.0.0',
    allow,
    rate = 50,
    ttl = 60,
    upstream = null,
    log = () => {},
  } = options;

  if (!ip) throw new Error('ip is required');
  if (!suffix && !name) throw new Error('one of suffix or name is required');
  if (!allow) throw new Error('allow is required — see SecurityReview.md §4');

  const isAllowed = makeSubnetTest(allow);
  const isRateLimited = makeRateLimiter(rate);
  const stats = { answered: 0, forwarded: 0, refused: 0, dropped: 0 };

  let socket = null;

  const matches = (queried) => {
    const lower = queried.toLowerCase();
    return name ? lower === name.toLowerCase() : lower.endsWith(suffix.toLowerCase());
  };

  function handle(message, remote) {
    // Both guards DROP rather than reply. Answering a source outside the hotspot — even
    // with REFUSED — still sends a packet to a possibly-forged address, which is the
    // amplification behaviour being prevented. Silence is the correct answer.
    if (!isAllowed(remote.address)) { stats.dropped += 1; return; }
    if (isRateLimited(remote.address)) { stats.dropped += 1; return; }

    let query;
    try {
      query = decodeQuery(message);
    } catch {
      stats.dropped += 1;
      return;
    }

    if (query.class !== CLASS_IN) {
      stats.refused += 1;
      socket.send(buildReply(query, message, { rcode: RCODE_REFUSED }), remote.port, remote.address);
      return;
    }

    if (matches(query.name)) {
      if (query.type === TYPE_A) {
        stats.answered += 1;
        socket.send(buildReply(query, message, { answerIp: ip, ttl }), remote.port, remote.address);
        log(`dns ${remote.address} ${TYPE_NAMES[query.type] || query.type} ${query.name} -> ${ip}`);
      } else {
        // Empty NOERROR, never NXDOMAIN. NXDOMAIN means "this name does not exist" for
        // EVERY record type, so a dual-stack phone that believes it never goes on to try
        // A — the difference between working and failing silently on some devices.
        stats.answered += 1;
        socket.send(buildReply(query, message, {}), remote.port, remote.address);
      }
      return;
    }

    if (upstream) { forward(message, remote); return; }

    stats.refused += 1;
    socket.send(buildReply(query, message, { rcode: RCODE_NXDOMAIN }), remote.port, remote.address);
  }

  function forward(message, remote) {
    const client = dgram.createSocket('udp4');
    const timer = setTimeout(() => { try { client.close(); } catch {} }, 3000);

    client.on('error', () => { clearTimeout(timer); try { client.close(); } catch {} });
    client.on('message', (reply) => {
      clearTimeout(timer);
      stats.forwarded += 1;
      socket.send(reply, remote.port, remote.address);
      try { client.close(); } catch {}
    });

    client.send(message, 53, upstream);
  }

  return {
    start() {
      return new Promise((resolve, reject) => {
        socket = dgram.createSocket('udp4');
        socket.on('error', (err) => { socket = null; reject(err); });
        socket.on('message', handle);
        socket.bind(port, bind, () => {
          log(`dns responder on ${bind}:${port}, answering ${name || `*${suffix}`} -> ${ip}, ` +
              `accepting ${allow} only, max ${rate}/s per source`);
          resolve();
        });
      });
    },

    stop() {
      return new Promise((resolve) => {
        if (!socket) return resolve();
        const s = socket;
        socket = null;
        s.close(resolve);
      });
    },

    stats: () => ({ ...stats }),
  };
}

module.exports = {
  createResponder,
  // Exported for tests: the wire handling is where the subtle mistakes live, and it is
  // testable without binding a privileged port.
  decodeQuery,
  buildReply,
  makeSubnetTest,
  makeRateLimiter,
  RCODE_NOERROR,
  RCODE_NXDOMAIN,
  RCODE_REFUSED,
};
