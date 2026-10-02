'use strict';

/**
 * Tests for the local DNS responder. Run with: npm test
 *
 * Two halves, deliberately. The pure wire encoding and the guards are exercised directly,
 * because that is where the subtle mistakes live and none of it needs a socket. Then a real
 * responder is bound on a high port to prove the pieces work together.
 *
 * The AAAA and source-guard cases are not padding. Both were wrong in the first draft, and
 * both fail in ways that look like something else entirely: NXDOMAIN on AAAA makes a phone
 * abandon a name it could have resolved over IPv4, and a missing source guard turns this
 * into a DDoS amplifier without changing anything a local test would notice.
 */

const { test, describe } = require('node:test');
const assert = require('node:assert/strict');
const dgram = require('dgram');
const dns = require('./dns');

const PORT = 5390;

function check(label, ok, detail) {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ` — ${detail}` : ''}`);
  assert.ok(ok, `${label}${detail ? ` — ${detail}` : ''}`);
}

// --------------------------------------------------------------- query encoding

function encodeQuery(name, qtype = 1, id = 0x4242) {
  const labels = name.split('.').filter(Boolean);
  const nameLength = labels.reduce((total, l) => total + 1 + l.length, 0) + 1;
  const buffer = Buffer.alloc(12 + nameLength + 4);

  buffer.writeUInt16BE(id, 0);
  buffer.writeUInt16BE(0x0100, 2);
  buffer.writeUInt16BE(1, 4);

  let offset = 12;
  for (const label of labels) {
    buffer.writeUInt8(label.length, offset);
    buffer.write(label, offset + 1, 'ascii');
    offset += 1 + label.length;
  }
  buffer.writeUInt8(0, offset);
  offset += 1;

  buffer.writeUInt16BE(qtype, offset);
  buffer.writeUInt16BE(1, offset + 2);
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

function parseReply(buffer) {
  const flags = buffer.readUInt16BE(2);
  const ancount = buffer.readUInt16BE(6);
  const answers = [];

  let offset = skipName(buffer, 12) + 4;
  for (let i = 0; i < ancount; i += 1) {
    offset = skipName(buffer, offset);
    const type = buffer.readUInt16BE(offset);
    const rdlength = buffer.readUInt16BE(offset + 8);
    const rdata = buffer.subarray(offset + 10, offset + 10 + rdlength);
    if (type === 1 && rdlength === 4) answers.push([...rdata].join('.'));
    offset += 10 + rdlength;
  }

  return { rcode: flags & 0x0f, authoritative: (flags & 0x0400) !== 0, ancount, answers };
}

/** Sends one query and resolves with the reply, or null if nothing came back. */
function ask(name, qtype = 1, timeoutMs = 700) {
  return new Promise((resolve) => {
    const socket = dgram.createSocket('udp4');
    const timer = setTimeout(() => {
      try {
        socket.close();
      } catch {
        // Socket may already be closing/closed; nothing to recover from, safe to ignore.
      }
      resolve(null);
    }, timeoutMs);

    socket.on('message', (reply) => {
      clearTimeout(timer);
      try {
        socket.close();
      } catch {
        // Socket may already be closing/closed; nothing to recover from, safe to ignore.
      }
      resolve(parseReply(reply));
    });
    socket.on('error', () => {
      clearTimeout(timer);
      resolve(null);
    });
    socket.send(encodeQuery(name, qtype), PORT, '127.0.0.1');
  });
}

// ------------------------------------------------------------------------ units

describe('Source guard', () => {
  test('subnet matching', () => {
    const inSubnet = dns.makeSubnetTest('192.168.137.0/24');
    check('address inside the subnet is allowed', inSubnet('192.168.137.52'));
    check('address outside the subnet is not', !inSubnet('10.0.0.213'));
    check('adjacent subnet is not', !inSubnet('192.168.138.1'));
    check('loopback is always allowed', inSubnet('127.0.0.1'));
    check('IPv4-mapped IPv6 is unwrapped', inSubnet('::ffff:192.168.137.9'));
    check('garbage is rejected rather than thrown on', !inSubnet('not-an-address'));
  });

  test('an invalid CIDR throws rather than silently allowing everything', () => {
    let threw = false;
    try {
      dns.makeSubnetTest('nonsense');
    } catch {
      threw = true;
    }
    check('an invalid CIDR throws rather than silently allowing everything', threw);
  });
});

describe('Rate limiter', () => {
  test('caps requests per source and rolls over the window', () => {
    let now = 1000;
    const limited = dns.makeRateLimiter(3, () => now);
    const verdicts = [limited('a'), limited('a'), limited('a'), limited('a'), limited('a')];
    check(
      'allows up to the cap',
      verdicts.slice(0, 3).every((v) => v === false),
    );
    check(
      'blocks past the cap',
      verdicts.slice(3).every((v) => v === true),
    );
    check('a different source has its own budget', limited('b') === false);

    now += 1100;
    check('the window rolls over', limited('a') === false);
  });
});

describe('Refusing to start unguarded', () => {
  test('createResponder refuses without an allow range', () => {
    let noAllow = false;
    try {
      dns.createResponder({ ip: '192.168.137.1', suffix: '.local-ip.sh' });
    } catch (err) {
      noAllow = /allow is required/.test(err.message);
    }
    check(
      'createResponder refuses without an allow range',
      noAllow,
      'an unguarded resolver is a DDoS amplifier, not merely untidy',
    );
  });
});

// ------------------------------------------------------------------ integration

// The game's fixed hostname, now that the certificate is single-name rather than an
// IP-encoded wildcard label under local-ip.sh — see
// docs/plans/magic-domain-replacement.md Phase 1/2. `names` covers this plus the
// connectivity probe domains added in Phase 4; server.js wires the exact same list.
const GAME_NAME = 'play.skiprocking.ca';
const PROBE_NAMES = [
  'connectivitycheck.gstatic.com',
  'connectivitycheck.android.com',
  'clients3.google.com',
  'captive.apple.com',
  'www.appleiphonecell.com',
];

describe('Responder integration', () => {
  let responder;

  test('Answering', async () => {
    responder = dns.createResponder({
      ip: '192.168.137.1',
      names: [GAME_NAME, ...PROBE_NAMES],
      port: PORT,
      bind: '127.0.0.1',
      allow: '127.0.0.0/8',
      rate: 100,
    });
    await responder.start();

    const a = await ask(GAME_NAME);
    check(
      'A for a matching name is answered',
      a && a.answers[0] === '192.168.137.1',
      a ? `got ${a.answers[0]}` : 'no reply',
    );
    check('the answer is authoritative', a && a.authoritative);

    const upper = await ask(GAME_NAME.toUpperCase());
    check('matching is case-insensitive', upper && upper.answers[0] === '192.168.137.1');
  });

  test('Connectivity probe domains are answered too', async () => {
    // Android and iOS each probe a domain of their own choosing right after joining a
    // network to decide whether to show "No internet". setup-offline.ps1 disables IP
    // forwarding every session, so these probes would otherwise always fail on a genuinely
    // isolated hotspot — answering them here is what keeps a phone from flagging the
    // network as broken. See docs/plans/magic-domain-replacement.md Phase 4.
    for (const probeName of PROBE_NAMES) {
      const answer = await ask(probeName);
      check(
        `probe domain ${probeName} resolves to the hotspot address`,
        answer && answer.answers[0] === '192.168.137.1',
        answer ? `got ${answer.answers[0]}` : 'no reply',
      );
    }
  });

  test('AAAA must not be NXDOMAIN', async () => {
    const aaaa = await ask(GAME_NAME, 28);
    check(
      'AAAA returns NOERROR',
      aaaa && aaaa.rcode === dns.RCODE_NOERROR,
      aaaa ? `rcode ${aaaa.rcode}` : 'no reply',
    );
    check('AAAA returns no answers', aaaa && aaaa.ancount === 0);
    check(
      'AAAA is not NXDOMAIN',
      aaaa && aaaa.rcode !== dns.RCODE_NXDOMAIN,
      'NXDOMAIN means the name does not exist for ANY type, so a phone would never try A',
    );
  });

  test('Non-matching names', async () => {
    const other = await ask('example.com');
    check(
      'a name we do not own is NXDOMAIN with no upstream',
      other && other.rcode === dns.RCODE_NXDOMAIN,
    );
  });

  test('Malformed input', async () => {
    const truncated = await new Promise((resolve) => {
      const socket = dgram.createSocket('udp4');
      const timer = setTimeout(() => {
        try {
          socket.close();
        } catch {
          // Socket may already be closing/closed; nothing to recover from, safe to ignore.
        }
        resolve('no reply');
      }, 500);
      socket.on('message', () => {
        clearTimeout(timer);
        try {
          socket.close();
        } catch {
          // Socket may already be closing/closed; nothing to recover from, safe to ignore.
        }
        resolve('replied');
      });
      socket.on('error', () => {
        clearTimeout(timer);
        resolve('error');
      });
      socket.send(Buffer.from([0x42, 0x42, 0x01]), PORT, '127.0.0.1');
    });
    check(
      'a truncated packet is dropped rather than crashing the process',
      truncated === 'no reply',
      `responder ${truncated}`,
    );

    const stillAlive = await ask(GAME_NAME);
    check(
      'the responder still answers afterwards',
      stillAlive && stillAlive.answers[0] === '192.168.137.1',
    );

    await responder.stop();
  });
});

/**
 * Exercises the guard for real, which needs a source that is not loopback.
 *
 * Loopback is unconditionally exempt so local testing works, so querying 127.0.0.1 proves
 * nothing about the guard — it only proves the exemption. Binding the *client* to this
 * machine's LAN address makes the source address a real one, and that is the path an
 * attacker would use.
 *
 * Skipped rather than faked when there is no LAN address to bind, because a test that
 * silently downgrades to testing nothing is worse than one that says it did not run.
 */
describe('Out-of-range sources are dropped, not refused', () => {
  function findLocalAddress() {
    const os = require('os');
    for (const entries of Object.values(os.networkInterfaces())) {
      for (const entry of entries || []) {
        if (entry.family === 'IPv4' && !entry.internal) return entry.address;
      }
    }
    return null;
  }

  /**
   * Shared body for both cases below: send `queryName` from a real (non-loopback) LAN
   * source at a responder whose `allow` range certainly excludes it, and confirm silence.
   */
  async function expectDropped(queryName, port) {
    const localAddress = findLocalAddress();
    if (!localAddress) {
      console.log('  SKIP  no non-loopback address on this machine to send from');
      return;
    }

    // Allow a range this machine is certainly not in: TEST-NET-3, reserved for documentation.
    const responder = dns.createResponder({
      ip: '192.168.137.1',
      names: [GAME_NAME, ...PROBE_NAMES],
      port,
      bind: '0.0.0.0',
      allow: '203.0.113.0/24',
      rate: 100,
    });

    await responder.start();

    try {
      const reply = await new Promise((resolve) => {
        const socket = dgram.createSocket('udp4');
        const timer = setTimeout(() => {
          try {
            socket.close();
          } catch {
            // Socket may already be closing/closed; nothing to recover from, safe to ignore.
          }
          resolve(null);
        }, 800);
        socket.on('message', (m) => {
          clearTimeout(timer);
          try {
            socket.close();
          } catch {
            // Socket may already be closing/closed; nothing to recover from, safe to ignore.
          }
          resolve(m);
        });
        socket.on('error', () => {
          clearTimeout(timer);
          resolve(null);
        });
        // Binding the client pins the source address the responder will see.
        socket.bind(0, localAddress, () => {
          socket.send(encodeQuery(queryName), port, localAddress);
        });
      });

      check(
        `a query for ${queryName} from ${localAddress} gets NO reply at all`,
        reply === null,
        'silence, not REFUSED — replying to a possibly-forged source is the amplification',
      );

      const stats = responder.stats();
      check('and it was counted as dropped', stats.dropped >= 1, `dropped ${stats.dropped}`);
      check('nothing was answered', stats.answered === 0, `answered ${stats.answered}`);
    } finally {
      await responder.stop();
    }
  }

  test('a query for the game hostname from a real LAN source gets no reply', async () => {
    await expectDropped(GAME_NAME, PORT + 1);
  });

  test('a connectivity probe query from outside the hotspot CIDR is also dropped', async () => {
    // The probe domains are matched by the same exact-name set as the game hostname
    // (dns.js's `exactNames`), so the source guard runs identically for both — this proves
    // it rather than assuming it from the game-hostname case above. A source outside the
    // hotspot subnet must never learn the hotspot answers for connectivitycheck.gstatic.com
    // either; the whole point of the guard is that it applies before *any* name is matched.
    await expectDropped(PROBE_NAMES[0], PORT + 2);
  });
});

// ------------------------------------------------------------ fallback names (sharing)

/** Fake upstream on a high port. `behaviour(msg)` returns a Buffer to reply with, or null. */
function startFakeUpstream(port, behaviour) {
  return new Promise((resolve) => {
    const sock = dgram.createSocket('udp4');
    const seen = [];
    sock.on('message', (msg, rinfo) => {
      seen.push(msg);
      const out = behaviour(msg);
      if (out) sock.send(out, rinfo.port, rinfo.address);
    });
    sock.bind(port, '127.0.0.1', () => resolve({ sock, seen }));
  });
}

/** An upstream answer that is visibly not ours: an A record for 9.9.9.9. */
function realAnswer(msg) {
  return dns.buildReply(dns.decodeQuery(msg), msg, { answerIp: '9.9.9.9' });
}

describe('Fallback names', () => {
  const FB_PORT = PORT + 10;
  const UP_PORT = PORT + 11;

  async function withResponder(extra, fn) {
    const responder = dns.createResponder({
      ip: '192.168.137.1',
      names: [GAME_NAME],
      fallbackNames: [PROBE_NAMES[0]],
      fallbackMs: 300,
      port: FB_PORT,
      bind: '127.0.0.1',
      allow: '127.0.0.0/8',
      rate: 100,
      ...extra,
    });
    await responder.start();
    try {
      await fn();
    } finally {
      await responder.stop();
    }
  }

  function askFallback(name) {
    return new Promise((resolve) => {
      const socket = dgram.createSocket('udp4');
      const timer = setTimeout(() => {
        socket.close();
        resolve(null);
      }, 1500);
      socket.on('message', (m) => {
        clearTimeout(timer);
        socket.close();
        resolve(parseReply(m));
      });
      socket.send(encodeQuery(name), FB_PORT, '127.0.0.1');
    });
  }

  test('fallback name is forwarded and the upstream reply returned', async () => {
    const up = await startFakeUpstream(UP_PORT, realAnswer);
    try {
      await withResponder({ upstream: '127.0.0.1', upstreamPort: UP_PORT }, async () => {
        const r = await askFallback(PROBE_NAMES[0]);
        check(
          'upstream answer is returned',
          r && r.answers[0] === '9.9.9.9',
          r ? `got ${r.answers[0]}` : 'no reply',
        );
        check('upstream was asked', up.seen.length === 1);
      });
    } finally {
      up.sock.close();
    }
  });

  test('fallback name is answered locally when upstream is silent', async () => {
    const up = await startFakeUpstream(UP_PORT, () => null);
    try {
      await withResponder({ upstream: '127.0.0.1', upstreamPort: UP_PORT }, async () => {
        const r = await askFallback(PROBE_NAMES[0]);
        check(
          'local answer after fallbackMs',
          r && r.answers[0] === '192.168.137.1',
          r ? `got ${r.answers[0]}` : 'no reply',
        );
        check('upstream was asked first', up.seen.length === 1);
      });
    } finally {
      up.sock.close();
    }
  });

  test('exact names stay local and other names are forwarded', async () => {
    const up = await startFakeUpstream(UP_PORT, realAnswer);
    try {
      await withResponder({ upstream: '127.0.0.1', upstreamPort: UP_PORT }, async () => {
        const other = await askFallback('example.com');
        check('non-matching name forwarded', other && other.answers[0] === '9.9.9.9');
        const game = await askFallback(GAME_NAME);
        check('exact name answered locally', game && game.answers[0] === '192.168.137.1');
        check('exact name never reached upstream', up.seen.length === 1, `saw ${up.seen.length}`);
      });
    } finally {
      up.sock.close();
    }
  });

  test('with no upstream a fallback name is answered locally', async () => {
    await withResponder({}, async () => {
      const r = await askFallback(PROBE_NAMES[0]);
      check(
        'answered locally',
        r && r.answers[0] === '192.168.137.1',
        r ? `got ${r.answers[0]}` : 'no reply',
      );
    });
  });
});
