// Visual check for issue #2, no phone needed: starts the PC server, opens the controller
// page as an emulated iPhone 13 and Pixel 7, feeds each synthetic devicemotion at 60 Hz
// (a phone tilted back and forth), and screenshots both phones and the PC monitor.
//
// Run from the repo root (needs Playwright with Chromium installed):
//   node tools/verify-pad-stream.js docs/media/verify
// Prints each phone's readout, the /api/pads snapshot and the server's [pad] log lines.
const { chromium, devices } = require("playwright");
const { spawn } = require("child_process");
const path = require("path");
const repo = path.resolve(__dirname, "..");
const outDir = path.resolve(process.argv[2] || "docs/media/verify");
const PORT = 3996;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Runs inside the page: a phone being tilted back and forth, rotating about x then z.
const fakeMotion = () => {
  window.__startFakeMotion = (phase) => {
    let t = 0;
    setInterval(() => {
      t += 1 / 60;
      const tilt = Math.sin(2 * Math.PI * 0.5 * t + phase);
      window.dispatchEvent(
        new DeviceMotionEvent("devicemotion", {
          accelerationIncludingGravity: {
            x: 9.80665 * 0.3 * tilt,
            y: 9.80665 * Math.sin(0.6 * tilt),
            z: 9.80665 * Math.cos(0.6 * tilt),
          },
          rotationRate: {
            alpha: 40 * Math.cos(2 * Math.PI * 0.25 * t),
            beta: 180 * Math.cos(2 * Math.PI * 0.5 * t + phase),
            gamma: 15,
          },
          interval: 16.67,
        }),
      );
    }, 1000 / 60);
  };
};

(async () => {
  const server = spawn(process.execPath, [path.join(repo, "relay/server.js")], {
    env: { ...process.env, PORT: String(PORT) },
    stdio: ["ignore", "pipe", "inherit"],
  });
  let serverLog = "";
  server.stdout.on("data", (d) => (serverLog += d));
  await sleep(700);
  const browser = await chromium.launch();
  try {
    const phones = [];
    for (const [i, device] of [
      [0, devices["iPhone 13"]],
      [1, devices["Pixel 7"]],
    ]) {
      const ctx = await browser.newContext({ ...device });
      await ctx.addInitScript(fakeMotion);
      const page = await ctx.newPage();
      page.on("pageerror", (e) =>
        console.log(`phone ${i} page error: ${e.message}`),
      );
      await page.goto(`http://localhost:${PORT}/pad.html`);
      await page.screenshot({
        path: path.join(outDir, `pad-${i}-before-start.png`),
      });
      await page.click("#start");
      await page.evaluate((p) => window.__startFakeMotion(p), i * 1.5);
      phones.push(page);
    }
    await sleep(3000);
    for (const [i, page] of phones.entries()) {
      await page.screenshot({
        path: path.join(outDir, `pad-${i}-streaming.png`),
      });
      console.log(
        `phone ${i}:`,
        await page.evaluate(() => ({
          status: document.getElementById("status").textContent,
          slot: document.getElementById("slot").textContent,
          rate: document.getElementById("rate").textContent,
          sent: document.getElementById("sent").textContent,
        })),
      );
    }
    const monitor = await (
      await browser.newContext({ viewport: { width: 1100, height: 700 } })
    ).newPage();
    await monitor.goto(`http://127.0.0.1:${PORT}/monitor.html`);
    await sleep(800);
    await monitor.screenshot({
      path: path.join(outDir, "monitor-two-phones.png"),
    });
    const api = await (await fetch(`http://127.0.0.1:${PORT}/api/pads`)).json();
    console.log(
      "api:",
      JSON.stringify(
        api.pads.map(
          (p) =>
            p && {
              slot: p.slot,
              rateHz: p.rateHz,
              received: p.received,
              dropped: p.dropped,
              rejected: p.rejected,
              latest: p.latest,
            },
        ),
      ),
    );
  } finally {
    await browser.close();
    server.kill();
    console.log(
      "server log:\n" +
        serverLog
          .split("\n")
          .filter((l) => l.includes("[pad]"))
          .join("\n"),
    );
  }
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
