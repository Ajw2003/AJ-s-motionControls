'use strict';

/**
 * Watches, live, every TCP connection a hotspot guest opens to this host.
 *
 * `diagnose-hotspot.js` answers "does the relay serve the right thing?" from the host. This
 * answers the question that one structurally cannot: "did the phone's request arrive at all?"
 * Those look identical on the phone — a page that never loads is the same screen whether the
 * request was blocked, was answered wrongly, or was never sent over the Wi-Fi in the first
 * place. Only the host's connection table separates them.
 *
 * The distinction matters most for Android. A network flagged "no internet" is not made the
 * device's default network, so ordinary apps — Chrome included — can route to cellular while
 * the OS's own captive-portal probe stays bound to the Wi-Fi. When that happens the probe
 * shows up here and the browser never does, from the same phone, at the same moment.
 *
 * Read-only: it shells out to `netstat` and prints. It changes nothing and touches no socket.
 *
 * Usage:  node watch-guests.js          (Ctrl+C to stop)
 */

const { execFile } = require('child_process');

const HOTSPOT_PREFIX = (process.env.DNS_RESPOND_IP || '192.168.137.1').replace(/\.\d+$/, '.');
const HOST_IP = process.env.DNS_RESPOND_IP || '192.168.137.1';
const POLL_MS = 1000;

/**
 * What each local port means to a phone, so the output reads as the join funnel rather than as
 * a port list. Anything unlisted is printed with its bare number.
 */
const PORT_MEANING = {
  80: 'captive-portal probe or portal page (plain HTTP)',
  443: 'HTTPS — nothing listens here; an HTTPS-upgraded URL lands on this and hangs',
  8443: 'the game itself (controller page / WebSocket)',
};

/** Connections we have already announced, keyed local->remote, so each is reported once. */
const seen = new Map();

function timestamp() {
  return new Date().toLocaleTimeString();
}

/**
 * Parses `netstat -ano -p TCP` into the guest connections only — remote address on the hotspot
 * subnet, and not the host talking to itself, which is the trap the earlier diagnosis fell into.
 */
function parseGuestConnections(stdout) {
  const rows = [];

  for (const line of stdout.split(/\r?\n/)) {
    const parts = line.trim().split(/\s+/);
    if (parts.length < 4 || parts[0] !== 'TCP') continue;

    const [, local, remote, state] = parts;
    const remoteIp = remote.slice(0, remote.lastIndexOf(':'));
    if (!remoteIp.startsWith(HOTSPOT_PREFIX) || remoteIp === HOST_IP) continue;

    rows.push({
      key: `${local}->${remote}`,
      localPort: Number(local.slice(local.lastIndexOf(':') + 1)),
      remote,
      remoteIp,
      state,
    });
  }

  return rows;
}

function poll() {
  execFile('netstat', ['-ano', '-p', 'TCP'], (err, stdout) => {
    if (err) {
      console.log(`  netstat failed: ${err.message}`);
      return;
    }

    for (const row of parseGuestConnections(stdout)) {
      const previousState = seen.get(row.key);
      if (previousState === row.state) continue;

      seen.set(row.key, row.state);
      if (previousState) {
        console.log(`  ${timestamp()}  ${row.remoteIp} -> :${row.localPort}   ${row.state}`);
        continue;
      }

      const meaning = PORT_MEANING[row.localPort] || `port ${row.localPort}`;
      console.log(`\n  ${timestamp()}  ${row.remoteIp} connected to :${row.localPort}`);
      console.log(`             ${meaning}`);
    }
  });
}

console.log(`Watching for guests on ${HOTSPOT_PREFIX}x connecting to this host. Ctrl+C to stop.\n`);
console.log('  Nothing below means nothing arrived. A phone whose browser times out while this');
console.log('  stays silent did not send the request over the hotspot at all.\n');

poll();
setInterval(poll, POLL_MS);
