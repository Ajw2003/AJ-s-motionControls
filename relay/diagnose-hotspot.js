'use strict';

/**
 * Answers "why does the phone say No internet?" against a LIVE game.
 *
 * Run it with Unity in Play mode and the hotspot up. It touches the relay the same way a
 * phone does — the same ports, the same paths, the same Host headers — and reports each
 * link in the chain separately, because the symptom on the phone ("connected, no internet")
 * is identical whether DNS failed, :80 never bound, or :80 answered the wrong thing.
 *
 * Read-only. It sends requests and prints what came back; it changes nothing.
 *
 * Usage:  node diagnose-hotspot.js
 */

const http = require('http');
const https = require('https');
const dgram = require('dgram');

const HOTSPOT_IP = process.env.DNS_RESPOND_IP || '192.168.137.1';
const RELAY_PORT = Number(process.env.PORT) || 8443;
const HOST_NAME = process.env.DNS_NAME || 'play.skiprocking.ca';

/** The connectivity probe Android actually sends over HTTP. iOS uses captive.apple.com/hotspot-detect.html. */
const ANDROID_PROBE_HOST = 'connectivitycheck.gstatic.com';

const PASS = 'PASS';
const FAIL = 'FAIL';
const WARN = 'WARN';

function report(status, label, detail) {
  console.log(`  ${status}  ${label}${detail ? `\n        ${detail}` : ''}`);
}

function get(agent, options) {
  return new Promise((resolve) => {
    const req = agent.get({ timeout: 4000, ...options }, (res) => {
      let body = '';
      res.on('data', (chunk) => {
        body += chunk;
      });
      res.on('end', () =>
        resolve({ status: res.statusCode, location: res.headers.location, body }),
      );
    });
    req.on('timeout', () => {
      req.destroy();
      resolve({ error: 'TIMEOUT' });
    });
    req.on('error', (err) => resolve({ error: err.code || err.message }));
  });
}

/** One A query, hand-rolled so this script needs no dependency and no resolver config. */
function dnsQuery(name) {
  return new Promise((resolve) => {
    const labels = name.split('.');
    const parts = [Buffer.from([0x2a, 0x2a, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0])];
    for (const label of labels) {
      parts.push(Buffer.from([label.length]), Buffer.from(label, 'ascii'));
    }
    parts.push(Buffer.from([0, 0, 1, 0, 1]));
    const query = Buffer.concat(parts);

    const socket = dgram.createSocket('udp4');
    const timer = setTimeout(() => {
      socket.close();
      resolve({ error: 'TIMEOUT' });
    }, 3000);

    socket.on('message', (msg) => {
      clearTimeout(timer);
      socket.close();
      const answers = msg.readUInt16BE(6);
      if (answers === 0) {
        resolve({ answers: 0 });
        return;
      }
      // The answer's rdata is the last four bytes of a single-A reply.
      const ip = Array.from(msg.subarray(msg.length - 4)).join('.');
      resolve({ answers, ip });
    });
    socket.on('error', (err) => {
      clearTimeout(timer);
      resolve({ error: err.code });
    });
    socket.send(query, 53, HOTSPOT_IP);
  });
}

async function main() {
  console.log(
    `\nDiagnosing hotspot at ${HOTSPOT_IP} (relay port ${RELAY_PORT}, host ${HOST_NAME})\n`,
  );

  console.log('1. Is the relay up at all?');
  // The game always runs this port with TLS, but a relay started by hand for testing has no
  // certificate and answers plain HTTP on the same port. Trying both means the script reports
  // "the relay is down" only when it genuinely is, rather than when it is merely not TLS.
  let health = await get(https, {
    host: HOTSPOT_IP,
    port: RELAY_PORT,
    path: '/health',
    rejectUnauthorized: false,
  });
  if (health.error === 'EPROTO') {
    health = await get(http, { host: HOTSPOT_IP, port: RELAY_PORT, path: '/health' });
    if (!health.error) {
      report(
        WARN,
        'the relay is serving plain HTTP, not HTTPS',
        'Expected for a hand-started relay; a phone needs TLS for sensors.',
      );
    }
  }
  if (health.error) {
    report(
      FAIL,
      `/health did not answer (${health.error})`,
      'The relay is not running or not reachable. Start Play in Unity, then re-run.',
    );
    console.log('\nNothing below can be trusted while the relay is down. Stopping.\n');
    return;
  }
  let dnsBound = null;
  try {
    dnsBound = JSON.parse(health.body).dns;
    report(PASS, '/health answered', `dns: ${JSON.stringify(dnsBound)}`);
  } catch {
    report(WARN, '/health answered but was not JSON', health.body.slice(0, 120));
  }

  console.log('\n2. Does DNS answer the probe domain a phone asks for?');
  if (dnsBound && dnsBound.enabled === false) {
    report(
      FAIL,
      'DNS is disabled in this relay',
      'DNS_RESPOND_IP was not set — the game is not in Hotspot mode.',
    );
  } else if (dnsBound && dnsBound.bound === false) {
    report(
      FAIL,
      'The DNS responder never bound :53',
      'Something else holds UDP :53. The phone cannot resolve anything on this network.',
    );
  }
  const answer = await dnsQuery(ANDROID_PROBE_HOST);
  if (answer.error) {
    report(
      FAIL,
      `no DNS answer for ${ANDROID_PROBE_HOST} (${answer.error})`,
      'Check the "RockSkipping hotspot DNS" firewall rule and that :53 bound.',
    );
  } else if (answer.answers === 0) {
    report(
      FAIL,
      `${ANDROID_PROBE_HOST} resolved to nothing`,
      'It must be in CONNECTIVITY_PROBE_DOMAINS in server.js.',
    );
  } else {
    report(
      answer.ip === HOTSPOT_IP ? PASS : WARN,
      `${ANDROID_PROBE_HOST} -> ${answer.ip}`,
      answer.ip === HOTSPOT_IP ? null : `expected ${HOTSPOT_IP}`,
    );
  }

  console.log('\n3. Is the :80 probe listener bound and reachable?');
  console.log(
    '   (this is the step that decides whether a phone can EVER be told there is internet)',
  );
  const loopback80 = await get(http, { host: '127.0.0.1', port: 80, path: '/generate_204' });
  if (loopback80.error === 'ECONNREFUSED') {
    report(
      FAIL,
      ':80 is not bound on this machine',
      'startHttpProbeListener could not bind and degraded quietly. Something else holds :80 —\n' +
        '        check with: Get-NetTCPConnection -LocalPort 80 -State Listen',
    );
  } else if (loopback80.error) {
    report(FAIL, `:80 on loopback failed (${loopback80.error})`);
  } else {
    report(PASS, ':80 is bound and answering the probe on loopback');
  }

  const hotspot80 = await get(http, { host: HOTSPOT_IP, port: 80, path: '/generate_204' });
  if (hotspot80.error) {
    report(
      FAIL,
      `:80 unreachable on ${HOTSPOT_IP} (${hotspot80.error})`,
      'Bound but blocked. Check the "RockSkipping connectivity probe" firewall rule.',
    );
  } else {
    // Deliberately hedged. This request originates on the host and is destined for the host's
    // own address, so Windows does not evaluate it against the inbound firewall rules the way
    // it evaluates a packet arriving over the wireless interface from a phone. A PASS here
    // proves the socket is listening and answering; it does NOT prove a guest can reach it.
    //
    // Nor does opening the URL on the phone's browser, which is what this used to advise. On
    // Android a Wi-Fi network flagged "no internet" is not the default network, so the
    // browser can route to cellular and time out while the platform's own probe — bound to
    // the Wi-Fi — reaches :80 fine. Only the host's connection table settles it, which is
    // what watch-guests.js prints.
    report(
      PASS,
      `:80 answers on ${HOTSPOT_IP} (from this machine)`,
      'This does NOT prove a phone can reach it — host-local traffic skips the inbound' +
        '\n        firewall, and a phone browser timing out does not disprove it either.' +
        '\n        Run  node watch-guests.js  and join with the phone to see what arrives.',
    );
  }

  console.log('\n4. What does the Android probe actually get back?');
  const probe = await get(http, {
    host: HOTSPOT_IP,
    port: 80,
    path: '/generate_204',
    headers: { Host: ANDROID_PROBE_HOST },
  });
  if (probe.error) {
    report(
      FAIL,
      `the probe got no answer (${probe.error})`,
      'This is exactly what makes Android say "connected, no internet" with no sign-in prompt.',
    );
  } else if (probe.status === 204) {
    report(PASS, '204 — the phone is told there is internet here');
  } else {
    report(FAIL, `unexpected status ${probe.status}`, probe.body.slice(0, 120));
  }

  console.log('\nDone.\n');
}

main();
