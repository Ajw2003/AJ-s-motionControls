// Visual check for issue #4, no phone needed: starts the PC server, opens the controller page
// as an emulated iPhone 13 and Pixel 7 in landscape, screenshots every layout, then holds three
// touches at once (a face button, the left stick, the D-pad) through Chrome's touch emulation
// and screenshots the phone and the PC monitor while they are held.
//
// Run from the repo root (needs Playwright with Chromium installed):
//   node tools/verify-gamepad.js docs/media/verify
// Prints what the PC received for the held touches.

const { chromium, devices } = require('playwright');
const { spawn } = require('child_process');
const path = require('path');

const repo = path.resolve(__dirname, '..');
const outDir = path.resolve(process.argv[2] || 'docs/media/verify');
const PORT = 3993;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function centreOf(page, selector) {
  const box = await page.locator(selector).boundingBox();
  if (!box) throw new Error(`no element for ${selector}`);
  return { x: box.x + box.width / 2, y: box.y + box.height / 2, w: box.width };
}

async function startWithLayout(page, layoutId) {
  if (await page.evaluate(() => document.body.classList.contains('playing'))) {
    await page.click('#chip'); // back to the picker
  }
  await page.click(`.layout-choice[data-layout="${layoutId}"]`);
  await page.click('#start');
  await sleep(300);
}

(async () => {
  const server = spawn(process.execPath, [path.join(repo, 'relay/server.js')], {
    env: { ...process.env, PORT: String(PORT), DSU_PORT: '0' },
    stdio: ['ignore', 'ignore', 'inherit'],
  });
  await sleep(700);
  const browser = await chromium.launch();
  try {
    for (const [name, device] of [
      ['iphone13', devices['iPhone 13 landscape']],
      ['pixel7', devices['Pixel 7 landscape']],
    ]) {
      const page = await (await browser.newContext({ ...device })).newPage();
      page.on('pageerror', (e) => console.log(`${name} page error: ${e.message}`));
      await page.goto(`http://localhost:${PORT}/pad.html`);
      await page.screenshot({ path: path.join(outDir, `gamepad-${name}-picker.png`) });
      // In landscape Start is below the fold, so the setup screen must let a thumb pan.
      // Headless Chromium's synthetic touch swipe scrolls nothing, not even a plain page, so
      // check the rule that decides it (every ancestor of Start allows panning) and scroll
      // with the wheel for the screenshot.
      const touchRule = await page.evaluate(() => {
        const blocked = [];
        for (let n = document.getElementById('start'); n; n = n.parentElement) {
          const ta = getComputedStyle(n).touchAction;
          if (ta === 'none' || ta === 'pinch-zoom')
            blocked.push(`${n.tagName.toLowerCase()}#${n.id}: ${ta}`);
        }
        return {
          blocked,
          padTouchAction: getComputedStyle(document.getElementById('pad')).touchAction,
        };
      });
      console.log(`${name} touch rules:`, JSON.stringify(touchRule));
      await page.mouse.wheel(0, 600);
      await sleep(300);
      await page.screenshot({ path: path.join(outDir, `gamepad-${name}-picker-scrolled.png`) });
      for (const layoutId of ['xbox', 'switch', 'gamecube', 'wii']) {
        await startWithLayout(page, layoutId);
        await page.screenshot({ path: path.join(outDir, `gamepad-${name}-${layoutId}.png`) });
      }
      if (name !== 'iphone13') {
        await page.close();
        continue;
      }

      // Three thumbs at once on the Xbox layout.
      await startWithLayout(page, 'xbox');
      const a = await centreOf(page, '[data-button="south"]');
      const stick = await centreOf(page, '[data-stick="left"]');
      const dpad = await centreOf(page, '.control.dpad');
      const cdp = await page.context().newCDPSession(page);
      const points = [
        { x: a.x, y: a.y, id: 1 },
        { x: stick.x + stick.w * 0.3, y: stick.y - stick.w * 0.3, id: 2 }, // up and right
        { x: dpad.x, y: dpad.y - dpad.w * 0.35, id: 3 }, // up
      ];
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: points });
      await sleep(400);
      await page.screenshot({ path: path.join(outDir, 'gamepad-three-touches-phone.png') });

      const monitor = await (
        await browser.newContext({ viewport: { width: 1100, height: 700 } })
      ).newPage();
      await monitor.goto(`http://127.0.0.1:${PORT}/monitor.html`);
      await sleep(800);
      await monitor.screenshot({ path: path.join(outDir, 'gamepad-three-touches-monitor.png') });
      const held = (await (await fetch(`http://127.0.0.1:${PORT}/api/pads`)).json()).pads[0]
        .gamepad;
      console.log('held, PC sees:', JSON.stringify(held));

      await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
      await sleep(400);
      const released = (await (await fetch(`http://127.0.0.1:${PORT}/api/pads`)).json()).pads[0]
        .gamepad;
      console.log('released, PC sees:', JSON.stringify(released));
      await page.close();
    }
  } finally {
    await browser.close();
    server.kill();
  }
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
