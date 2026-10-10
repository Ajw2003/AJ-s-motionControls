'use strict';

// Starts the relay over HTTPS with the shared, publicly trusted pad certificate.
// Usage: node start-pad.js   (env PORT, DNS_*, PAD_CERT_CACHE_DIR override the defaults)

const { ensureCertificate, defaultCacheDir } = require('./pad-cert');

async function main() {
  const cert = await ensureCertificate({ cacheDir: process.env.PAD_CERT_CACHE_DIR || defaultCacheDir() });
  if (!cert.ok) {
    console.error(`No usable HTTPS certificate: ${cert.reason}`);
    for (const w of cert.warnings) console.error(`  - ${w}`);
    console.error('To fix this, connect this PC to the internet once and run again.');
    process.exit(1);
  }
  const expiry = cert.notAfter.toISOString().slice(0, 10);
  const warn = cert.warnings.length ? ` Warnings: ${cert.warnings.join(' ')}` : '';
  console.log(`Using the ${cert.source === 'fresh' ? 'newly downloaded' : 'saved'} certificate for ${cert.name} (expires ${expiry}, ${cert.daysLeft} days left).${warn}`);

  const env = process.env;
  env.TLS_CERT = cert.certPath;
  env.TLS_KEY = cert.keyPath;
  env.DNS_NAME = cert.name;
  env.PORT = env.PORT || '8443'; // 8443 is already open in the firewall by setup-offline.ps1
  env.JOIN_PORT = env.JOIN_PORT || '8780'; // the join page, loopback only
  // PAD_NO_DNS=1 is for local tests where nothing should answer DNS.
  if (env.PAD_NO_DNS !== '1') {
    env.DNS_RESPOND_IP = env.DNS_RESPOND_IP || '192.168.137.1'; // the Windows hotspot address
    env.DNS_ALLOW = env.DNS_ALLOW || '192.168.137.0/24';
  }
  require('./server');
}

main().catch((err) => {
  console.error(`Could not start: ${err.message}`);
  process.exit(1);
});
