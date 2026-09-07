'use strict';

/**
 * Verifies the captive-portal nudge (P2 in docs/plans/join-failure-reduction.md) and the
 * :80 route fix, against a real spawned relay. Run with: npm test
 *
 * This is the half of P2 a machine can prove: that the relay's ANSWERS change with the
 * requesting device's funnel stage. It cannot prove the half that matters on party night —
 * that a phone's OS reacts to those answers by opening its portal sheet, and stops flagging
 * "No internet" once the device is playing. That needs a real phone; the procedure is in the
 * plan's Verification section.
 *
 * Both halves are asserted in one direction and then the other, deliberately. Getting the
 * nudge right and the suppression wrong trades a join failure for a mid-match disconnect,
 * which is strictly worse than doing nothing — so "it goes back to 204 afterwards" is as
 * load-bearing a test as "it redirects beforehand".
 *
 * The funnel is keyed by IP and confined to DNS_ALLOW, so the spawned relay is given
 * 127.0.0.0/8 — that makes this test's own loopback requests count as a device, which is
 * exactly the mechanism a phone on 192.168.137.0/24 goes through.
 */

const { test, describe, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('child_process');
const http = require('http');
const path = require('path');

const PORT = 3987;
const HOST_NAME = 'play.skiprocking.ca';

/** Does not follow redirects — the redirect itself is what is under test. */
function request(port, requestPath) {
  return new Promise((resolve) => {
    const req = http.get({ host: '127.0.0.1', port, path: requestPath }, (res) => {
      let body = '';
      res.on('data', (chunk) => {
        body += chunk;
      });
      res.on('end', () =>
        resolve({ status: res.statusCode, location: res.headers.location, body }),
      );
    });
    // Resolves rather than rejects so a listener that never bound (:80 taken by IIS, Docker
    // Desktop, or simply not permitted) reads as a skippable condition instead of a failure.
    req.on('error', (err) => resolve({ error: err.code }));
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

describe('Captive-portal nudge', () => {
  let server;

  before(async () => {
    server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
      env: {
        ...process.env,
        PORT: String(PORT),
        // Enables the funnel. The relay also tries to bind :53 and :80 whenever DNS is on;
        // both degrade quietly if they cannot, and nothing in this block needs either —
        // every request below goes to the main port, which routes through the same
        // serveCaptiveProbe the :80 listener uses.
        DNS_RESPOND_IP: '127.0.0.1',
        DNS_ALLOW: '127.0.0.0/8',
        DNS_NAME: HOST_NAME,
      },
      stdio: ['ignore', 'ignore', 'inherit'],
    });
    await sleep(700);
  });

  after(() => {
    server.kill('SIGKILL');
  });

  test('before the controller is opened, Android is told there is a portal', async () => {
    const res = await request(PORT, '/generate_204');
    assert.equal(res.status, 302, 'a bare 204 here would suppress the sheet we want opened');
    // The IP literal, not HOST_NAME, is the fix for Defect 3: browsers upgrade http:// URLs
    // for registrable names to HTTPS and land on :443 where nothing listens. This value is
    // the DNS_RESPOND_IP the harness starts the server with.
    assert.equal(res.location, 'http://127.0.0.1/portal');
  });

  test('before the controller is opened, Apple gets the portal page, not "Success"', async () => {
    const res = await request(PORT, '/hotspot-detect.html');
    assert.equal(res.status, 200);
    assert.notEqual(res.body, 'Success');
    assert.match(res.body, /<!doctype html>/i);
  });

  test('the portal page hands off to the game rather than embedding it', async () => {
    const res = await request(PORT, '/portal');
    assert.equal(res.status, 200);
    // The link out is the entire point: the CNA sheet cannot be trusted with
    // DeviceMotion.requestPermission(), so the controller must never be what loads here.
    assert.match(res.body, new RegExp(`https://${HOST_NAME}:${PORT}/`));
    assert.doesNotMatch(res.body, /controller\.js/);
  });

  test('opening the controller advances the device past the nudge', async () => {
    const page = await request(PORT, '/');
    assert.equal(
      page.status,
      200,
      'the controller page itself must still be served on the main port',
    );

    // Same requester, so the funnel now has this IP at PAGE_OPEN.
    const android = await request(PORT, '/generate_204');
    assert.equal(android.status, 204, 'a device that is in the game must not keep being nudged');
    assert.equal(android.body, '');

    const apple = await request(PORT, '/hotspot-detect.html');
    assert.equal(
      apple.body,
      'Success',
      'Apple needs this exact literal to stop flagging "No internet"',
    );
  });
});

describe('port 80 serves no controller', () => {
  const ownPort = PORT + 1;
  const expectedLocation = `https://${HOST_NAME}:${ownPort}/`;
  let server;
  let isOurListener = false;

  before(async () => {
    server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
      env: {
        ...process.env,
        PORT: String(ownPort),
        DNS_RESPOND_IP: '127.0.0.1',
        DNS_ALLOW: '127.0.0.0/8',
        DNS_NAME: HOST_NAME,
      },
      stdio: ['ignore', 'ignore', 'inherit'],
    });
    await sleep(700);

    // :80 is a single machine-wide port, so it may already belong to something else — most
    // often the game's own relay, running because someone pressed Play in Unity. Identify
    // ours by the port it redirects to rather than by "did anything answer", or this test
    // silently grades a listener it did not start.
    const probe = await request(80, '/');
    isOurListener = probe.location === expectedLocation;
  });

  after(() => {
    server.kill('SIGKILL');
  });

  test('the controller is redirected to HTTPS, never served in the clear', async (t) => {
    if (!isOurListener) {
      t.skip(
        ':80 is held by another process (a running relay, IIS, Docker Desktop) or unavailable',
      );
      return;
    }

    // The regression this guards: :80 used to reuse handleRequest, whose last branch is
    // serveStatic, so it served the whole controller over plain HTTP. It loaded, looked
    // correct, and could read no sensors, because DeviceMotion needs a secure context.
    for (const requestPath of ['/', '/controller.js', '/index.html']) {
      const res = await request(80, requestPath);
      assert.equal(res.status, 302, `${requestPath} must redirect, not be served`);
      assert.equal(res.location, expectedLocation);
      assert.equal(res.body, '', `${requestPath} must not leak any file content`);
    }
  });
});
