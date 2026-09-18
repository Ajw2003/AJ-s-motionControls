'use strict';

/**
 * Verifies the connectivity-probe answers and the :80 route fix, against a real spawned
 * relay. Run with: npm test
 *
 * This is the half a machine can prove: that the relay answers Android's and Apple's probes
 * with the "there is internet here" answer every time, whoever asks and whatever they have
 * or have not opened. It cannot prove that a phone's OS reacts by not flagging "No internet"
 * — that needs a real phone.
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

describe('Connectivity probes', () => {
  let server;

  before(async () => {
    server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
      env: {
        ...process.env,
        PORT: String(PORT),
        // Hotspot mode, as on the host. The relay also tries to bind :53 and :80 whenever DNS
        // is on; both degrade quietly if they cannot, and nothing in this block needs either —
        // every request below goes to the main port, which routes through the same
        // serveConnectivityProbe the :80 listener uses.
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

  test('Android gets a bare 204, even from a device that has not opened the controller', async () => {
    const res = await request(PORT, '/generate_204');
    assert.equal(res.status, 204, 'anything but a bare 204 makes Android show a login page');
    assert.equal(res.body, '');
  });

  test('Apple gets the exact literal "Success", even before the controller is opened', async () => {
    const res = await request(PORT, '/hotspot-detect.html');
    assert.equal(res.status, 200);
    assert.equal(
      res.body,
      'Success',
      'Apple needs this exact literal to stop flagging "No internet"',
    );
  });

  test('the answers do not change once the controller has been opened', async () => {
    const page = await request(PORT, '/');
    assert.equal(
      page.status,
      200,
      'the controller page itself must still be served on the main port',
    );

    const android = await request(PORT, '/generate_204');
    assert.equal(android.status, 204);
    assert.equal(android.body, '');

    const apple = await request(PORT, '/hotspot-detect.html');
    assert.equal(apple.body, 'Success');
  });

  test('there is no /portal page', async () => {
    const res = await request(PORT, '/portal');
    assert.equal(res.status, 404);
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
