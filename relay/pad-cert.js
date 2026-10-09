'use strict';

// The shared HTTPS certificate for the phone pad (see issue #20). One publicly trusted
// certificate is published as a single JSON file; this module fetches it, checks it is
// really usable and not revoked, and keeps a copy so the game still starts offline.
// Node built-ins only: the PC is Windows and has no openssl.

const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const https = require('node:https');
const os = require('node:os');
const path = require('node:path');

const RELEASE_BASE = 'https://github.com/Ajw2003/rockskipping-cert/releases/download/pad-current/';
const BUNDLE_URL = `${RELEASE_BASE}pad-cert.json`;
const NUDGE_REASONS = ['revoked', 'expired', 'phone-rejected'];
const DAY_MS = 86_400_000;

// What a person reads when a certificate is turned down.
const REASON_TEXT = {
  malformed: 'the certificate file is damaged or incomplete',
  'bare-leaf': 'the certificate file has no issuer certificate in it',
  'key-mismatch': 'the private key does not belong to the certificate',
  'name-mismatch': 'the certificate is for a different web address than the one it claims',
  'bad-issuer': 'the certificate was not signed by the issuer it ships with',
  expired: 'the certificate has expired',
  'not-yet-valid': "the certificate is not valid yet (is this PC's clock right?)",
  revoked: 'the certificate has been revoked by its issuer',
};

function defaultCacheDir() {
  if (process.platform === 'win32') {
    const base = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    return path.join(base, 'AJsMotionControls', 'cert');
  }
  return path.join(os.homedir(), '.cache', 'ajs-motion-controls', 'cert');
}

// ---------------------------------------------------------------- HTTP

function httpGet(url, opts = {}) {
  const { timeout = 15_000, maxRedirects = 5, maxBytes = 64 * 1024 * 1024 } = opts;
  return new Promise((resolve, reject) => {
    let u;
    try {
      u = new URL(url);
    } catch {
      return reject(new Error(`not a web address: ${url}`));
    }
    const mod = u.protocol === 'https:' ? https : u.protocol === 'http:' ? http : null;
    if (!mod) return reject(new Error(`unsupported address: ${url}`));
    const req = mod.get(u, { timeout }, (res) => {
      const code = res.statusCode;
      if (code >= 300 && code < 400 && res.headers.location) {
        res.resume();
        if (maxRedirects <= 0) return reject(new Error(`too many redirects from ${u.host}`));
        const next = new URL(res.headers.location, u).href;
        return resolve(httpGet(next, { ...opts, maxRedirects: maxRedirects - 1 }));
      }
      if (code !== 200) {
        res.resume();
        return reject(new Error(`${u.host} answered HTTP ${code}`));
      }
      const chunks = [];
      let size = 0;
      res.on('data', (c) => {
        size += c.length;
        if (size > maxBytes) req.destroy(new Error(`answer from ${u.host} is too large`));
        else chunks.push(c);
      });
      res.on('end', () => resolve(Buffer.concat(chunks)));
      res.on('error', reject);
    });
    req.on('timeout', () => req.destroy(new Error(`timed out talking to ${u.host}`)));
    req.on('error', reject);
  });
}

// ---------------------------------------------------------------- DER

function readTlv(buf, pos) {
  if (pos + 2 > buf.length) throw new Error('DER truncated');
  const tag = buf[pos];
  let len = buf[pos + 1];
  let p = pos + 2;
  if (len & 0x80) {
    const n = len & 0x7f;
    if (n === 0 || n > 4 || p + n > buf.length) throw new Error('DER bad length');
    len = 0;
    for (let i = 0; i < n; i++) len = len * 256 + buf[p++];
  }
  const end = p + len;
  if (end > buf.length) throw new Error('DER truncated');
  return { tag, start: pos, body: p, end };
}

function children(buf, tlv) {
  const out = [];
  for (let p = tlv.body; p < tlv.end; ) {
    const c = readTlv(buf, p);
    out.push(c);
    p = c.end;
  }
  return out;
}

function decodeOid(buf, tlv) {
  const bytes = buf.subarray(tlv.body, tlv.end);
  const parts = [Math.floor(bytes[0] / 40), bytes[0] % 40];
  let v = 0;
  for (let i = 1; i < bytes.length; i++) {
    v = v * 128 + (bytes[i] & 0x7f);
    if (!(bytes[i] & 0x80)) {
      parts.push(v);
      v = 0;
    }
  }
  return parts.join('.');
}

// Serials compare as uppercase hex without leading zeros, however each side wrote them.
function normSerial(hex) {
  return String(hex).replace(/^0+/, '').toUpperCase() || '0';
}

function parseTime(buf, tlv) {
  const s = buf.toString('ascii', tlv.body, tlv.end);
  const m = tlv.tag === 0x17 ? /^(\d\d)(\d\d)(\d\d)(\d\d)(\d\d)(\d\d)?Z$/.exec(s) : /^(\d{4})(\d\d)(\d\d)(\d\d)(\d\d)(\d\d)?Z$/.exec(s);
  if (!m) throw new Error('CRL has an unreadable date');
  let year = Number(m[1]);
  if (tlv.tag === 0x17) year += year >= 50 ? 1900 : 2000;
  return new Date(Date.UTC(year, Number(m[2]) - 1, Number(m[3]), Number(m[4]), Number(m[5]), Number(m[6] || 0)));
}

// First http(s) URI in the CRL Distribution Points extension (2.5.29.31) of a certificate.
function crlUrlFromCert(cert) {
  const der = cert.raw;
  const tbs = children(der, readTlv(der, 0))[0];
  const exts = children(der, tbs).find((c) => c.tag === 0xa3);
  if (!exts) return null;
  for (const ext of children(der, children(der, exts)[0])) {
    const parts = children(der, ext);
    if (decodeOid(der, parts[0]) !== '2.5.29.31') continue;
    const octets = parts[parts.length - 1];
    const urls = [];
    // URIs are [6] primitives (0x86) nested inside constructed wrappers.
    const walk = (buf, from, to) => {
      for (let p = from; p < to; ) {
        const t = readTlv(buf, p);
        if (t.tag === 0x86) urls.push(buf.toString('ascii', t.body, t.end));
        else if (t.tag & 0x20) walk(buf, t.body, t.end);
        p = t.end;
      }
    };
    walk(der, octets.body, octets.end);
    return urls.find((u) => /^https?:\/\//i.test(u)) || null;
  }
  return null;
}

const SIG_HASH = {
  '1.2.840.10045.4.3.2': 'sha256', // ecdsa-with-SHA256
  '1.2.840.10045.4.3.3': 'sha384',
  '1.2.840.10045.4.3.4': 'sha512',
  '1.2.840.113549.1.1.11': 'sha256', // sha256WithRSAEncryption
  '1.2.840.113549.1.1.12': 'sha384',
  '1.2.840.113549.1.1.13': 'sha512',
};

function parseCrl(input) {
  let der = Buffer.isBuffer(input) ? input : Buffer.from(input);
  const text = der.toString('latin1', 0, 64);
  if (text.includes('-----BEGIN')) {
    const m = /-----BEGIN X509 CRL-----([\s\S]*?)-----END X509 CRL-----/.exec(der.toString('latin1'));
    if (!m) throw new Error('CRL PEM is damaged');
    der = Buffer.from(m[1].replace(/\s+/g, ''), 'base64');
  }
  const top = children(der, readTlv(der, 0));
  if (top.length !== 3) throw new Error('CRL has the wrong shape');
  const [tbsTlv, algTlv, sigTlv] = top;
  const sigOid = decodeOid(der, children(der, algTlv)[0]);
  const signature = der.subarray(sigTlv.body + 1, sigTlv.end); // skip the unused-bits byte

  const f = children(der, tbsTlv);
  let i = f[0].tag === 0x02 ? 1 : 0; // optional version
  i += 2; // signature algorithm, issuer
  const thisUpdate = parseTime(der, f[i++]);
  let nextUpdate = null;
  if (f[i] && (f[i].tag === 0x17 || f[i].tag === 0x18)) nextUpdate = parseTime(der, f[i++]);
  const revoked = new Set();
  if (f[i] && f[i].tag === 0x30) {
    for (const entry of children(der, f[i])) {
      const serial = children(der, entry)[0];
      revoked.add(normSerial(der.subarray(serial.body, serial.end).toString('hex')));
    }
  }
  return { tbs: der.subarray(tbsTlv.start, tbsTlv.end), sigOid, signature, thisUpdate, nextUpdate, revoked };
}

function verifyCrl(crl, issuer) {
  const hash = SIG_HASH[crl.sigOid];
  if (!hash) throw new Error(`CRL uses a signature type we do not support (${crl.sigOid})`);
  return crypto.verify(hash, crl.tbs, issuer.publicKey, crl.signature);
}

// ---------------------------------------------------------------- inspection

function splitPem(pem) {
  return pem.match(/-----BEGIN CERTIFICATE-----[\s\S]*?-----END CERTIFICATE-----/g) || [];
}

function inspectBundle(bundle, { now = new Date() } = {}) {
  const bad = (reason, extra = {}) => ({ ok: false, reason, ...extra });
  let obj = bundle;
  try {
    if (Buffer.isBuffer(obj)) obj = obj.toString('utf8');
    if (typeof obj === 'string') obj = JSON.parse(obj);
  } catch {
    return bad('malformed');
  }
  if (!obj || typeof obj.name !== 'string' || typeof obj.chain !== 'string' || typeof obj.key !== 'string' || !obj.name) {
    return bad('malformed');
  }
  const pems = splitPem(obj.chain);
  let certs;
  let privateKey;
  try {
    certs = pems.map((p) => new crypto.X509Certificate(p));
    privateKey = crypto.createPrivateKey(obj.key);
  } catch {
    return bad('malformed');
  }
  if (certs.length === 0) return bad('malformed');
  if (certs.length < 2) return bad('bare-leaf');
  const [leaf, issuer] = certs;
  const info = {
    name: obj.name,
    serial: normSerial(leaf.serialNumber),
    notAfter: new Date(leaf.validTo),
    leaf,
    issuer,
    daysLeft: Math.floor((new Date(leaf.validTo) - now) / DAY_MS),
  };
  if (!leaf.checkPrivateKey(privateKey)) return bad('key-mismatch', info);
  const names = (leaf.subjectAltName || '').split(',').map((s) => s.trim());
  if (!names.includes(`DNS:${obj.name}`)) return bad('name-mismatch', info);
  let signedByIssuer = false;
  try {
    signedByIssuer = leaf.checkIssued(issuer) && leaf.verify(issuer.publicKey);
  } catch {
    signedByIssuer = false;
  }
  if (!signedByIssuer) return bad('bad-issuer', info);
  if (now > new Date(leaf.validTo)) return bad('expired', info);
  if (now < new Date(leaf.validFrom)) return bad('not-yet-valid', info);
  return { ok: true, reason: 'ok', ...info };
}

// ---------------------------------------------------------------- revocation

// Only ever 'good' when we read a CRL, proved the issuer signed it, and the serial is not in
// it. Anything we could not establish is 'unknown', never 'good'.
async function checkRevocation(leaf, issuer, { fetch = httpGet, now = new Date() } = {}) {
  const unknown = (detail) => ({ status: 'unknown', detail });
  let url;
  try {
    url = crlUrlFromCert(leaf);
  } catch (err) {
    return unknown(`could not read the revocation list address (${err.message})`);
  }
  if (!url) return unknown('the certificate names no revocation list');
  let crl;
  try {
    crl = parseCrl(await fetch(url));
  } catch (err) {
    return unknown(`could not get the revocation list from ${url} (${err.message})`);
  }
  let signed = false;
  try {
    signed = verifyCrl(crl, issuer);
  } catch (err) {
    return unknown(err.message);
  }
  if (!signed) return unknown('the revocation list is not signed by the certificate issuer');
  if (crl.nextUpdate && now > crl.nextUpdate) return unknown('the revocation list is out of date');
  const crlInfo = { url, count: crl.revoked.size, thisUpdate: crl.thisUpdate, nextUpdate: crl.nextUpdate, signatureVerified: true };
  const serial = normSerial(leaf.serialNumber);
  if (crl.revoked.has(serial)) return { status: 'revoked', detail: 'the issuer lists this certificate as revoked', crlInfo };
  return { status: 'good', detail: `not on the issuer's revocation list (${crl.revoked.size} entries)`, crlInfo };
}

// ---------------------------------------------------------------- cache + ensure

function writeAtomic(file, data) {
  const tmp = `${file}.tmp-${process.pid}`;
  fs.writeFileSync(tmp, data, { mode: 0o600 });
  fs.renameSync(tmp, file);
}

function writeCache(cacheDir, text) {
  const obj = JSON.parse(text);
  fs.mkdirSync(cacheDir, { recursive: true });
  const paths = { certPath: path.join(cacheDir, 'chain.pem'), keyPath: path.join(cacheDir, 'key.pem') };
  writeAtomic(paths.certPath, obj.chain);
  writeAtomic(paths.keyPath, obj.key);
  writeAtomic(path.join(cacheDir, 'pad-cert.json'), text);
  return paths;
}

function readCache(cacheDir, now) {
  let text;
  try {
    text = fs.readFileSync(path.join(cacheDir, 'pad-cert.json'), 'utf8');
  } catch {
    return null;
  }
  return { text, info: inspectBundle(text, { now }) };
}

async function defaultNudge(reason) {
  if (!NUDGE_REASONS.includes(reason)) return;
  await httpGet(`${RELEASE_BASE}nudge-${reason}`, { timeout: 8000 }).catch((err) => {
    // A 404 is expected until the publisher adds the file; it is a fire-and-forget ping.
    if (!/HTTP 404/.test(err.message)) throw err;
  });
}

async function sendNudge(nudge, reason, warnings) {
  try {
    await nudge(reason);
  } catch (err) {
    warnings.push(`Could not tell the certificate publisher about the problem (${err.message}).`);
  }
}

async function ensureCertificate(opts = {}) {
  const {
    cacheDir = defaultCacheDir(),
    url = BUNDLE_URL,
    now = new Date(),
    download = httpGet,
    nudge = defaultNudge,
    fetch = httpGet,
  } = opts;
  const warnings = [];
  const cache = readCache(cacheDir, now);
  const done = (source, info, status) => ({
    ok: true,
    source,
    name: info.name,
    notAfter: info.notAfter,
    daysLeft: info.daysLeft,
    status,
    warnings,
    ...writeCache(cacheDir, source === 'fresh' ? info.text : cache.text),
  });

  let problem = null;
  let offline = false;
  let raw = null;
  try {
    raw = await download(url);
  } catch (err) {
    offline = true;
    warnings.push(`Could not download the newest certificate (${err.message}).`);
  }

  if (raw) {
    const text = Buffer.isBuffer(raw) ? raw.toString('utf8') : String(raw);
    const info = inspectBundle(text, { now });
    if (info.ok) {
      const rev = await checkRevocation(info.leaf, info.issuer, { fetch, now });
      if (rev.status === 'revoked') problem = { reason: 'revoked', serial: info.serial };
      else {
        if (rev.status === 'unknown') warnings.push(`Could not confirm the certificate is still valid: ${rev.detail}.`);
        return done('fresh', { ...info, text }, rev.status);
      }
    } else {
      problem = { reason: info.reason, serial: info.serial };
    }
    warnings.push(`The newest published certificate cannot be used: ${REASON_TEXT[problem.reason] || problem.reason}.`);
    await sendNudge(nudge, problem.reason, warnings);
  }

  if (cache && cache.info.ok && !(problem && problem.reason === 'revoked' && cache.info.serial === problem.serial)) {
    warnings.push(
      offline
        ? 'Using the saved certificate; it cannot be checked for revocation without internet.'
        : 'Using the saved certificate instead; it was not checked for revocation.',
    );
    return done('cache', cache.info, 'unchecked');
  }

  let reason;
  if (problem) {
    reason = `The newest certificate cannot be used (${REASON_TEXT[problem.reason] || problem.reason}) and there is no good saved copy on this PC.`;
  } else if (cache) {
    reason = `Could not download a certificate, and the saved one cannot be used (${REASON_TEXT[cache.info.reason] || cache.info.reason}).`;
  } else {
    reason = 'Could not download a certificate and none is saved on this PC.';
  }
  return { ok: false, source: null, reason, warnings };
}

module.exports = {
  BUNDLE_URL,
  RELEASE_BASE,
  REASON_TEXT,
  checkRevocation,
  crlUrlFromCert,
  defaultCacheDir,
  ensureCertificate,
  httpGet,
  inspectBundle,
  parseCrl,
  verifyCrl,
};
