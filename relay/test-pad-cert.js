'use strict';

// Fixtures are committed (fixtures/pad-cert, made by make-fixtures.sh) so this runs on
// Windows without openssl. CRLs are handed in through the injected `fetch`.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { inspectBundle, checkRevocation, ensureCertificate, parseCrl, crlUrlFromCert } = require('./pad-cert');

const FIX = path.join(__dirname, 'fixtures', 'pad-cert');
const read = (f) => fs.readFileSync(path.join(FIX, f));
const text = (f) => read(f).toString('utf8');

function bundle({ leaf = 'leaf.pem', ca = 'ca.pem', key = 'leaf-key.pem', name = 'pad.skiprocking.ca', chain } = {}) {
  return {
    name,
    serial: 'ignored',
    not_after: 'ignored',
    issued_at: 'ignored',
    chain: chain === undefined ? text(leaf) + text(ca) : chain,
    key: text(key),
  };
}

const crlFetch = (file) => async (url) => {
  assert.strictEqual(url, 'http://crl.test/ca.crl');
  return read(file);
};
const mkdtemp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'pad-cert-test-'));
const noNudge = () => {
  const calls = [];
  const fn = async (r) => calls.push(r);
  fn.calls = calls;
  return fn;
};

test('a good bundle is accepted, as object or JSON text', () => {
  for (const input of [bundle(), JSON.stringify(bundle())]) {
    const r = inspectBundle(input);
    assert.strictEqual(r.ok, true);
    assert.strictEqual(r.reason, 'ok');
    assert.strictEqual(r.name, 'pad.skiprocking.ca');
    assert.strictEqual(r.serial, '1000');
    assert.ok(r.daysLeft > 1000);
    assert.ok(r.notAfter instanceof Date);
  }
});

test('each way a bundle can be wrong gets its own reason', () => {
  const cases = {
    malformed: ['{not json', { ...bundle(), key: undefined }, bundle({ chain: 'nonsense' })],
    'bare-leaf': [bundle({ chain: text('leaf.pem') })],
    'key-mismatch': [bundle({ key: 'key-other.pem' })],
    'name-mismatch': [bundle({ leaf: 'leaf-wrongname.pem' })],
    'bad-issuer': [bundle({ ca: 'other-ca.pem' })],
    expired: [bundle({ leaf: 'leaf-expired.pem' })],
  };
  for (const [reason, inputs] of Object.entries(cases)) {
    for (const input of inputs) {
      const r = inspectBundle(input);
      assert.strictEqual(r.ok, false, reason);
      assert.strictEqual(r.reason, reason);
    }
  }
});

test('the same leaf is expired only once the clock passes its end date', () => {
  const b = bundle({ leaf: 'leaf-expired.pem' });
  assert.strictEqual(inspectBundle(b, { now: new Date('2020-01-15T00:00:00Z') }).ok, true);
  assert.strictEqual(inspectBundle(b, { now: new Date('2020-03-01T00:00:00Z') }).reason, 'expired');
  assert.strictEqual(inspectBundle(b, { now: new Date('2019-01-01T00:00:00Z') }).reason, 'not-yet-valid');
});

test('the CRL address is read from the certificate', () => {
  const { leaf } = inspectBundle(bundle());
  assert.strictEqual(crlUrlFromCert(leaf), 'http://crl.test/ca.crl');
});

test('CRLs parse from DER and PEM', () => {
  const der = parseCrl(read('crl-revoked.der'));
  assert.deepStrictEqual([...der.revoked], ['1001']);
  assert.ok(der.thisUpdate < new Date() && der.nextUpdate > new Date());
  assert.deepStrictEqual([...parseCrl(read('crl-empty.pem')).revoked], []);
});

test('revocation: not listed means good', async () => {
  const { leaf, issuer } = inspectBundle(bundle());
  const r = await checkRevocation(leaf, issuer, { fetch: crlFetch('crl-revoked.der') });
  assert.strictEqual(r.status, 'good');
  assert.strictEqual(r.crlInfo.signatureVerified, true);
  assert.strictEqual((await checkRevocation(leaf, issuer, { fetch: crlFetch('crl-empty.pem') })).status, 'good');
});

test('revocation: listed means revoked', async () => {
  const { leaf, issuer } = inspectBundle(bundle({ leaf: 'leaf-revoked.pem' }));
  const r = await checkRevocation(leaf, issuer, { fetch: crlFetch('crl-revoked.der') });
  assert.strictEqual(r.status, 'revoked');
});

test('revocation: RSA-signed CRLs work too', async () => {
  const fetch = async () => read('rsa-crl.der');
  const good = inspectBundle(bundle({ leaf: 'rsa-leaf.pem', ca: 'rsa-ca.pem', key: 'rsa-key.pem' }));
  const bad = inspectBundle(bundle({ leaf: 'rsa-leaf-revoked.pem', ca: 'rsa-ca.pem', key: 'rsa-key.pem' }));
  assert.strictEqual(good.ok, true);
  assert.strictEqual((await checkRevocation(good.leaf, good.issuer, { fetch })).status, 'good');
  assert.strictEqual((await checkRevocation(bad.leaf, bad.issuer, { fetch })).status, 'revoked');
});

test('revocation: a CRL signed by someone else is unknown, even if it lists the serial', async () => {
  const { leaf, issuer } = inspectBundle(bundle());
  assert.deepStrictEqual([...parseCrl(read('crl-other-ca.pem')).revoked], ['1000']);
  const r = await checkRevocation(leaf, issuer, { fetch: crlFetch('crl-other-ca.pem') });
  assert.strictEqual(r.status, 'unknown');
});

test('revocation: fetch errors, garbage and a stale CRL are unknown, never good', async () => {
  const { leaf, issuer } = inspectBundle(bundle());
  const boom = async () => {
    throw new Error('no network');
  };
  assert.strictEqual((await checkRevocation(leaf, issuer, { fetch: boom })).status, 'unknown');
  assert.strictEqual((await checkRevocation(leaf, issuer, { fetch: async () => Buffer.from('garbage') })).status, 'unknown');
  const far = new Date('2300-01-01T00:00:00Z');
  assert.strictEqual((await checkRevocation(leaf, issuer, { fetch: crlFetch('crl-empty.pem'), now: far })).status, 'unknown');
});

test('ensureCertificate: a fresh download is checked and cached atomically', async () => {
  const cacheDir = path.join(mkdtemp(), 'nested', 'cert');
  const body = JSON.stringify(bundle());
  const r = await ensureCertificate({ cacheDir, download: async () => body, fetch: crlFetch('crl-empty.pem'), nudge: noNudge() });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.source, 'fresh');
  assert.strictEqual(r.status, 'good');
  assert.strictEqual(r.name, 'pad.skiprocking.ca');
  assert.deepStrictEqual(r.warnings, []);
  assert.strictEqual(fs.readFileSync(r.certPath, 'utf8'), text('leaf.pem') + text('ca.pem'));
  assert.strictEqual(fs.readFileSync(r.keyPath, 'utf8'), text('leaf-key.pem'));
  assert.strictEqual(fs.readFileSync(path.join(cacheDir, 'pad-cert.json'), 'utf8'), body);
  assert.deepStrictEqual(fs.readdirSync(cacheDir).sort(), ['chain.pem', 'key.pem', 'pad-cert.json']);
});

test('ensureCertificate: unknown revocation still uses the fresh bundle, with a warning', async () => {
  const r = await ensureCertificate({
    cacheDir: mkdtemp(),
    download: async () => JSON.stringify(bundle()),
    fetch: async () => {
      throw new Error('blocked');
    },
    nudge: noNudge(),
  });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.source, 'fresh');
  assert.strictEqual(r.status, 'unknown');
  assert.strictEqual(r.warnings.length, 1);
});

test('ensureCertificate: offline falls back to the cache with a warning', async () => {
  const cacheDir = mkdtemp();
  await ensureCertificate({ cacheDir, download: async () => JSON.stringify(bundle()), fetch: crlFetch('crl-empty.pem') });
  fs.rmSync(path.join(cacheDir, 'key.pem'));
  const offline = async () => {
    throw new Error('getaddrinfo ENOTFOUND');
  };
  const r = await ensureCertificate({ cacheDir, download: offline, nudge: noNudge() });
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.source, 'cache');
  assert.strictEqual(r.status, 'unchecked');
  assert.ok(r.warnings.length >= 2);
  assert.strictEqual(fs.readFileSync(r.keyPath, 'utf8'), text('leaf-key.pem')); // rewritten
});

test('ensureCertificate: a revoked fresh bundle nudges and falls back to a different cached one', async () => {
  const cacheDir = mkdtemp();
  await ensureCertificate({ cacheDir, download: async () => JSON.stringify(bundle()), fetch: crlFetch('crl-empty.pem') });
  const nudge = noNudge();
  const r = await ensureCertificate({
    cacheDir,
    download: async () => JSON.stringify(bundle({ leaf: 'leaf-revoked.pem' })),
    fetch: crlFetch('crl-revoked.der'),
    nudge,
  });
  assert.deepStrictEqual(nudge.calls, ['revoked']);
  assert.strictEqual(r.ok, true);
  assert.strictEqual(r.source, 'cache');
  assert.strictEqual(fs.readFileSync(r.certPath, 'utf8'), text('leaf.pem') + text('ca.pem'));
});

test('ensureCertificate: a cached copy of the revoked certificate is not reused', async () => {
  const cacheDir = mkdtemp();
  const revoked = JSON.stringify(bundle({ leaf: 'leaf-revoked.pem' }));
  await ensureCertificate({ cacheDir, download: async () => revoked, fetch: crlFetch('crl-empty.pem') });
  const nudge = noNudge();
  const r = await ensureCertificate({ cacheDir, download: async () => revoked, fetch: crlFetch('crl-revoked.der'), nudge });
  assert.deepStrictEqual(nudge.calls, ['revoked']);
  assert.strictEqual(r.ok, false);
  assert.match(r.reason, /revoked/);
});

test('ensureCertificate: an expired fresh bundle nudges; a failing nudge is only a warning', async () => {
  const r = await ensureCertificate({
    cacheDir: mkdtemp(),
    download: async () => JSON.stringify(bundle({ leaf: 'leaf-expired.pem' })),
    nudge: async () => {
      throw new Error('nudge down');
    },
  });
  assert.strictEqual(r.ok, false);
  assert.ok(r.warnings.some((w) => /nudge down/.test(w)));
  assert.match(r.reason, /expired/);
});

test('ensureCertificate: offline with nothing saved says so in plain English', async () => {
  const r = await ensureCertificate({
    cacheDir: mkdtemp(),
    download: async () => {
      throw new Error('offline');
    },
  });
  assert.strictEqual(r.ok, false);
  assert.match(r.reason, /none is saved/);
});
