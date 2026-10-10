'use strict';

// Needs internet; deliberately not in the default `npm test`. Run: node --test test-pad-cert-live.js
// (behind a proxy: NODE_USE_ENV_PROXY=1 and NODE_EXTRA_CA_CERTS=<proxy CA bundle>)

const test = require('node:test');
const assert = require('node:assert');
const { BUNDLE_URL, httpGet, inspectBundle, checkRevocation } = require('./pad-cert');

test('the live published bundle is valid and not revoked', async () => {
  const raw = await httpGet(BUNDLE_URL);
  const info = inspectBundle(raw);
  console.log(`bundle: ok=${info.ok} reason=${info.reason} name=${info.name} serial=${info.serial} daysLeft=${info.daysLeft}`);
  console.log(`issuer: ${info.issuer && info.issuer.subject.replace(/\n/g, ', ')}`);
  assert.strictEqual(info.ok, true);
  assert.strictEqual(info.name, 'pad.skiprocking.ca');

  const rev = await checkRevocation(info.leaf, info.issuer);
  console.log(`revocation: ${rev.status} - ${rev.detail}`);
  if (rev.crlInfo) {
    const c = rev.crlInfo;
    console.log(`crl: ${c.url} serials=${c.count} thisUpdate=${c.thisUpdate.toISOString()} nextUpdate=${c.nextUpdate && c.nextUpdate.toISOString()} signatureVerified=${c.signatureVerified}`);
  }
  assert.strictEqual(rev.status, 'good');
  assert.strictEqual(rev.crlInfo.signatureVerified, true);
  assert.ok(rev.crlInfo.count >= 1 && rev.crlInfo.count < 5_000_000, `implausible serial count ${rev.crlInfo.count}`);
});
