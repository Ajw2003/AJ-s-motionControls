// Visual check for issues #3 and #5, no phone or emulator needed: starts the PC server, streams
// synthetic motion from an emulated phone, runs tools/dsu-client.js against the DSU port
// the way an emulator would, holds A and the left stick on the on-screen gamepad partway
// through, and screenshots the phone and the PC monitor while the client is reading.
//
// Run from the repo root (needs Playwright with Chromium installed):
//   node tools/verify-dsu.js docs/media/verify
// Prints the DSU client's output and the monitor's DSU status line.

const { chromium, devices } = require('playwright');
const { spawn } = require('child_process');
const path = require('path');

const repo = path.resolve(__dirname, '..');
const outDir = path.resolve(process.argv[2] || 'docs/media/verify');
const PORT = 3994;
const DSU_PORT = 26760;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// Runs inside the page: the phone is pitched forward and back once every two seconds.
const fakeMotion = () => {
  window.__startFakeMotion = () => {
    let t = 0;
    setInterval(() => {
      t += 1 / 60;
      const pitch = 0.6 * Math.sin(Math.PI * t);
      window.dispatchEvent(
        new DeviceMotionEvent('devicemotion', {
          accelerationIncludingGravity: {
            x: 0,
            y: 9.80665 * Math.sin(pitch),
            z: 9.80665 * Math.cos(pitch),
          },
          rotationRate: {
            alpha: 0,
            beta: (0.6 * Math.PI * Math.cos(Math.PI * t) * 180) / Math.PI,
            gamma: 0,
          },
          interval: 16.67,
        }),
      );
    }, 1000 / 60);
  };
};

(async () => {
  const server = spawn(process.execPath, [path.join(repo, 'relay/server.js')], {
    env: { ...process.env, PORT: String(PORT), DSU_PORT: String(DSU_PORT) },
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  let serverLog = '';
  server.stdout.on('data', (d) => (serverLog += d));
  await sleep(700);
  const browser = await chromium.launch();
  try {
    const phoneContext = await browser.newContext({ ...devices['Pixel 7 landscape'] });
    await phoneContext.addInitScript(fakeMotion);
    const phone = await phoneContext.newPage();
    phone.on('pageerror', (e) => console.log(`phone page error: ${e.message}`));
    await phone.goto(`http://localhost:${PORT}/pad.html`);
    await phone.click('#start');
    await phone.evaluate(() => window.__startFakeMotion());

    const client = spawn(
      process.execPath,
      [path.join(repo, 'tools/dsu-client.js'), String(DSU_PORT), '5'],
      {
        stdio: ['ignore', 'pipe', 'inherit'],
      },
    );
    let clientOut = '';
    client.stdout.on('data', (d) => (clientOut += d));
    const clientExit = new Promise((resolve) => client.on('exit', resolve));

    // Hold Cross (A, south) and push the left stick fully right, through Chrome's touch
    // emulation, while the DSU client is reading; let go after the screenshot.
    await sleep(1500);
    const box = async (selector) => {
      const b = await phone.locator(selector).boundingBox();
      return { x: b.x + b.width / 2, y: b.y + b.height / 2, w: b.width };
    };
    const a = await box('[data-button="south"]');
    const stick = await box('[data-stick="left"]');
    const touch = await phone.context().newCDPSession(phone);
    await touch.send('Input.dispatchTouchEvent', {
      type: 'touchStart',
      touchPoints: [
        { x: a.x, y: a.y, id: 1 },
        { x: stick.x + stick.w * 0.45, y: stick.y, id: 2 },
      ],
    });
    await sleep(1500);
    const monitor = await (
      await browser.newContext({ viewport: { width: 1100, height: 640 } })
    ).newPage();
    await monitor.goto(`http://127.0.0.1:${PORT}/monitor.html`);
    await sleep(800);
    await monitor.screenshot({ path: path.join(outDir, 'monitor-dsu-client.png') });
    console.log('monitor DSU line:', await monitor.textContent('#dsu'));
    await phone.screenshot({ path: path.join(outDir, 'dsu-buttons-phone.png') });
    await touch.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });

    const code = await clientExit;
    console.log(`--- dsu-client (exit ${code}) ---\n${clientOut}`);
  } finally {
    await browser.close();
    server.kill();
    console.log(
      'server log:\n' +
        serverLog
          .split('\n')
          .filter((l) => l.includes('[dsu]') || l.includes('[pad]'))
          .join('\n'),
    );
  }
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
