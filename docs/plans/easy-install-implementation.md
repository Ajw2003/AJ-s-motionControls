# One Installer, Two QR Codes: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Builders are paused (Ajw2003/AjsClaudeCodeTools#192 open), so this runs inline.

**Goal:** A friend downloads `AJ-Motion-Setup.exe`, installs it, clicks **AJ Motion**, and joins
a phone with two QR codes. Sudachi and Dolphin then see it with no hand edits to their settings.

**Architecture:**
- The relay gains a plain-HTTP listener bound to `127.0.0.1` only. It serves the join page and
  `/api/join`, which holds the Wi-Fi password, so phones never see either.
- A PowerShell launcher runs these in order: hotspot credentials, emulator setup, relay (as its
  own child process), hotspot, join page. When the relay exits, it stops the hotspot.
- Inno Setup packs a portable Node, the relay, the controller page and the launcher. A GitHub
  workflow builds it and attaches it to a Release.

**Tech stack:**
- Node 22 (relay, `node:test`), plain browser JS for the join page;
- Windows PowerShell 5.1 for the launcher and emulator setup;
- Inno Setup 6 and GitHub Actions on `windows-latest`.

**Spec:** [`easy-install.md`](easy-install.md). Issues: #23 (parent), #12, #14, #24, #25, #26.

## Global constraints

- Windows PowerShell **5.1** (`powershell.exe`), with `Set-StrictMode -Version Latest` and
  `$ErrorActionPreference = 'Stop'`. Call `npm.cmd`, never `npm`. Native tools' stderr must not
  become terminating errors (see `Invoke-Hotspot` in `tools/test-pad.ps1`).
- Hotspot address `192.168.137.1`. Controller URL `https://pad.skiprocking.ca:8443/pad.html`. DSU
  server `127.0.0.1:26760`.
- Join listener: `http://127.0.0.1:8780`, bound to loopback only.
- Pinned Node: **v22.19.0** (`node-v22.19.0-win-x64.zip`), checked against nodejs.org
  `SHASUMS256.txt`.
- Emulator edits:
  - keep the file's newline style and encoding (UTF-8 without BOM);
  - take a dated backup before the first change;
  - never touch existing player mappings;
  - skip an emulator that's running, and change nothing on a second run.
- Per-PC state lives in `%LOCALAPPDATA%\AJsMotionControls\` (the certificate cache is already
  there).
- No new npm dependencies.
- Commits end with `Committed by AJ's agent` and say `Refs #N`.

## Change from the spec (proposed 2026-10-10, waiting on aj)

**Emulator setup turns on DSU input only. It doesn't add input profiles.**

- **Sudachi:** auto-mapped everything from the **Input Device** dropdown on aj's PC, so a profile
  adds nothing.
- **Dolphin:** the button names on aj's PC can't be confirmed. Dolphin's source has
  `L1`/`R1`/`L2`, but `docs/4-systems/DsuServer.md` recorded `Shoulder R` and `Trigger L` on aj's
  PC. A profile with wrong names maps nothing. Dolphin's **Default** button already maps it
  correctly (DsuServer.md, 2026-10-09).
- **Later:** Dolphin profiles come with the upright layout (#28), once the axes for an upright
  hold are known.

The join page tells the user the one dropdown or button to use instead.

## Files

| File | Job | Task |
|---|---|---|
| `relay/server.js` | loopback join listener, `/api/join`, `/api/stop` | 1 |
| `relay/start-pad.js` | sets `JOIN_PORT=8780` by default | 1 |
| `relay/test-join.js` | tests for the above | 1 |
| `controller/join.html`, `controller/js/join.js` | the join page | 2 |
| `controller/test-join.js` | Wi-Fi QR payload and escaping tests | 2 |
| `tools/emulators.ps1` | Sudachi and Dolphin DSU setup | 3 |
| `tools/test-emulators.ps1` | tests against fixture copies | 3 |
| `tools/launch.ps1` | the launcher; replaces `tools/test-pad.ps1` | 4 |
| `installer/ajmotion.iss`, `installer/firewall.ps1`, `installer/stage.ps1` | the installer | 5 |
| `.github/workflows/release.yml` | build and Release | 6 |

---

### Task 1: Loopback join listener in the relay (#14)

**Files:**
- Modify: `relay/server.js`: `servePadSnapshot` at :641; routes in `handleRequest` near :620;
  listen near :868.
- Modify: `relay/start-pad.js`
- Create: `relay/test-join.js`
- Modify: `relay/package.json`: add `test-join.js` to `scripts.test`.

**Interfaces:**
- Produces: `GET /api/join` (loopback only). It returns
  `{ ssid, passphrase, padUrl, emulators }`, where:
  - `ssid` and `passphrase` come from env `JOIN_SSID` and `JOIN_PASS`;
  - `padUrl` is `https://${DNS_NAME}:${PORT}/pad.html`;
  - `emulators` is `JSON.parse(env.JOIN_EMULATORS || '[]')`.
- Produces: `POST /api/stop` (loopback only). It answers `204`, then exits the process with code
  0 after 100 ms.
- Produces: env `JOIN_PORT`. Above 0, it adds `http://127.0.0.1:JOIN_PORT` with the same routes,
  `join.html` included.

- [ ] **Step 1: Write the failing test** in `relay/test-join.js`:

```js
'use strict';
// The join page's data and stop endpoints, on the loopback-only listener (#14).
const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('child_process');
const http = require('http');
const path = require('path');

const PORT = 39181;
const JOIN_PORT = 39182;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function request(port, method, urlPath) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port, method, path: urlPath }, (res) => {
      let body = '';
      res.on('data', (c) => (body += c));
      res.on('end', () => resolve({ status: res.statusCode, body }));
    });
    req.on('error', reject);
    req.end();
  });
}

let server;
let exitCode = null;
before(async () => {
  server = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
    env: {
      ...process.env, PORT: String(PORT), JOIN_PORT: String(JOIN_PORT), DSU_PORT: '0',
      JOIN_SSID: 'AJ Motion 1A2B', JOIN_PASS: 'p;ss,word', DNS_NAME: 'pad.skiprocking.ca',
      JOIN_EMULATORS: JSON.stringify([{ emulator: 'Sudachi', status: 'changed', detail: 'x' }]),
    },
    stdio: ['ignore', 'ignore', 'inherit'],
  });
  server.on('exit', (code) => (exitCode = code));
  await sleep(600);
});
after(() => { if (exitCode === null) server.kill('SIGKILL'); });

test('the join listener serves /api/join with the hotspot details', async () => {
  const { status, body } = await request(JOIN_PORT, 'GET', '/api/join');
  assert.equal(status, 200);
  assert.deepEqual(JSON.parse(body), {
    ssid: 'AJ Motion 1A2B', passphrase: 'p;ss,word',
    padUrl: `https://pad.skiprocking.ca:${PORT}/pad.html`,
    emulators: [{ emulator: 'Sudachi', status: 'changed', detail: 'x' }],
  });
});

test('the join listener serves join.html', async () => {
  const { status, body } = await request(JOIN_PORT, 'GET', '/join.html');
  assert.equal(status, 200);
  assert.match(body, /<title>/);
});

test('POST /api/stop ends the relay with code 0', async () => {
  const { status } = await request(JOIN_PORT, 'POST', '/api/stop');
  assert.equal(status, 204);
  await sleep(400);
  assert.equal(exitCode, 0);
});
```

- [ ] **Step 2: Run it to see it fail.** `node --test relay/test-join.js`. Expect the first test
  to fail with a 404 or a connection refused on 39182.

- [ ] **Step 3: Implement.** In `relay/server.js`, pull the loopback check out of
  `servePadSnapshot` into a helper next to it, and use it there:

```js
/** True when the request came from this PC itself. */
function isLoopback(req) {
  const from = req.socket ? req.socket.remoteAddress : null;
  return from === '127.0.0.1' || from === '::1' || from === '::ffff:127.0.0.1';
}
```

`servePadSnapshot` becomes `if (!isLoopback(req)) { res.writeHead(403, …).end('Forbidden'); return; }`.
Add these routes in `handleRequest`, after `/api/pads`:

```js
    if (url.pathname === '/api/join') {
      serveJoinDetails(req, res);
      return;
    }

    if (url.pathname === '/api/stop' && req.method === 'POST') {
      stopFromJoinPage(req, res);
      return;
    }
```

Add the two handlers below `servePadSnapshot`:

```js
/**
 * What the join page shows: the hotspot name and password (for the Wi-Fi QR code), the
 * controller address, and what the launcher's emulator setup did. Loopback only, since it holds
 * the Wi-Fi password.
 */
function serveJoinDetails(req, res) {
  if (!isLoopback(req)) {
    res.writeHead(403, { 'Content-Type': 'text/plain' }).end('Forbidden');
    return;
  }
  let emulators = [];
  try {
    emulators = JSON.parse(process.env.JOIN_EMULATORS || '[]');
  } catch (err) {
    log('JOIN_EMULATORS is not valid JSON, showing none:', err.message);
  }
  res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }).end(
    JSON.stringify({
      ssid: process.env.JOIN_SSID || '',
      passphrase: process.env.JOIN_PASS || '',
      padUrl: `https://${DNS_NAME}:${PORT}/pad.html`,
      emulators,
    }),
  );
}

/** The join page's Stop button. The launcher sees the relay exit and turns the hotspot off. */
function stopFromJoinPage(req, res) {
  if (!isLoopback(req)) {
    res.writeHead(403, { 'Content-Type': 'text/plain' }).end('Forbidden');
    return;
  }
  log('Stop requested from the join page.');
  res.writeHead(204).end();
  setTimeout(() => process.exit(0), 100);
}
```

After `httpServer.listen(...)` (near :868), add the listener:

```js
// The join page shows the Wi-Fi password, so it gets its own plain-HTTP listener on loopback
// only (#14). Plain HTTP is fine here: nothing on it leaves this PC.
const JOIN_PORT = Number(process.env.JOIN_PORT || 0);
if (JOIN_PORT > 0) {
  http
    .createServer(handleRequest)
    .on('error', (err) => log(`Join page listener on 127.0.0.1:${JOIN_PORT} FAILED: ${err.code || err.message}`))
    .listen(JOIN_PORT, '127.0.0.1', () => log(`Join page on http://127.0.0.1:${JOIN_PORT}/join.html`));
}
```

Create a placeholder `controller/join.html` containing only
`<!doctype html><title>AJ Motion</title>` so the second test can pass; Task 2 replaces it. In
`relay/start-pad.js`, next to `env.PORT`, add:

```js
  env.JOIN_PORT = env.JOIN_PORT || '8780'; // the join page, loopback only
```

Add `test-join.js` to the end of `scripts.test` in `relay/package.json`.

- [ ] **Step 4: Run the tests.** `npm --prefix relay test`. Expect `# fail 0`, with 3 more passes
  than the 130 before.
- [ ] **Step 5: Commit.**

```bash
git add relay/server.js relay/start-pad.js relay/test-join.js relay/package.json controller/join.html
git commit -m "feat: loopback-only join listener with /api/join and /api/stop" -m "Refs #14" -m "Committed by AJ's agent"
```

---

### Task 2: The join page (#14)

**Files:**
- Replace: `controller/join.html`
- Create: `controller/js/join.js`, `controller/test-join.js`
- Modify: `controller/package.json`: append `&& node test-join.js` to `scripts.test`.

**Interfaces:**
- Consumes: `GET /api/join`, `GET /api/pads`, `POST /api/stop`, and `GET /qr?data=` (all from
  Task 1 or existing).
- Produces: `wifiPayload(ssid, passphrase)`, exported from `controller/js/join.js`. It returns
  `WIFI:T:WPA;S:<escaped>;P:<escaped>;;`, escaping `\ ; , : "` with a backslash, as in
  `Assets/Scripts/UI/JoinScreen.cs:138`.

- [ ] **Step 1: Write the failing test** in `controller/test-join.js`:

```js
// The Wi-Fi QR text both phone cameras read as "join this network".
import assert from 'node:assert/strict';
import { wifiPayload } from './js/join.js';

assert.equal(wifiPayload('AJ Motion 1A2B', 'abc123'), 'WIFI:T:WPA;S:AJ Motion 1A2B;P:abc123;;');
assert.equal(wifiPayload('a;b', 'c,d:e"f\\g'), 'WIFI:T:WPA;S:a\\;b;P:c\\,d\\:e\\"f\\\\g;;');
console.log('join: ok');
```

- [ ] **Step 2: Run it to see it fail.** `node controller/test-join.js`. Expect
  `ERR_MODULE_NOT_FOUND`. (`controller/package.json` must have `"type": "module"`; check that
  first, and if it doesn't, name the test `test-join.mjs` instead.)

- [ ] **Step 3: Implement** `controller/js/join.js`:

```js
// The join page (#14): two QR codes, then live slot status, all read from the loopback-only
// join listener. Only the PC sees this page, because it shows the Wi-Fi password.

/** WIFI: payload with the escaping from Rock Skipping's JoinScreen.cs. */
export function wifiPayload(ssid, passphrase) {
  const esc = (v) => v.replace(/([\\;,:"])/g, '\\$1');
  return `WIFI:T:WPA;S:${esc(ssid)};P:${esc(passphrase)};;`;
}

const qrSrc = (text) => `/qr?data=${encodeURIComponent(text)}`;

async function start() {
  const details = await (await fetch('/api/join')).json();
  document.getElementById('wifi-qr').src = qrSrc(wifiPayload(details.ssid, details.passphrase));
  document.getElementById('pad-qr').src = qrSrc(details.padUrl);
  document.getElementById('ssid').textContent = details.ssid;
  document.getElementById('pass').textContent = details.passphrase;
  document.getElementById('pad-url').textContent = details.padUrl;
  const list = document.getElementById('emulators');
  for (const e of details.emulators) {
    const li = document.createElement('li');
    li.textContent = `${e.emulator}: ${e.detail}`;
    list.append(li);
  }
  document.getElementById('stop').addEventListener('click', async () => {
    await fetch('/api/stop', { method: 'POST' });
    document.body.textContent = 'AJ Motion stopped. You can close this tab.';
  });
  setInterval(showPads, 1000);
}

async function showPads() {
  try {
    const { pads } = await (await fetch('/api/pads')).json();
    const joined = pads.map((p, i) => (p ? `Phone ${i + 1} connected` : null)).filter(Boolean);
    document.getElementById('pads').textContent = joined.length ? joined.join(' · ') : 'No phone yet';
  } catch {
    document.getElementById('pads').textContent = 'AJ Motion has stopped.';
  }
}

if (typeof document !== 'undefined') start();
```

Then `controller/join.html`. It follows `monitor.html`'s conventions: color tokens on `:root`, a
dark-mode override, and a layout that works at phone width.

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>AJ Motion</title>
  <style>
    :root { --bg: #f3f5f8; --panel: #fff; --ink: #17202e; --muted: #5b6676; --accent: #1f7fb8; }
    @media (prefers-color-scheme: dark) { :root { --bg: #0f1520; --panel: #172030; --ink: #e6ebf2; --muted: #97a3b4; --accent: #4cb3ec; } }
    body { margin: 0; background: var(--bg); color: var(--ink); font: 18px/1.5 system-ui, sans-serif; }
    main { max-width: 64rem; margin: 0 auto; padding: 24px 16px; display: grid; gap: 24px; }
    .steps { display: grid; grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr)); gap: 24px; }
    .step { background: var(--panel); border-radius: 16px; padding: 20px; display: grid; gap: 10px; justify-items: center; text-align: center; }
    .step img { width: min(100%, 22rem); aspect-ratio: 1; image-rendering: pixelated; background: #fff; padding: 12px; border-radius: 8px; }
    .muted { color: var(--muted); }
    #pads { font-weight: 700; color: var(--accent); }
    button { font: inherit; padding: 8px 16px; border-radius: 8px; }
  </style>
</head>
<body>
  <main>
    <h1>AJ Motion</h1>
    <div class="steps">
      <section class="step">
        <h2>1. Join the Wi-Fi</h2>
        <img id="wifi-qr" alt="QR code to join the Wi-Fi" />
        <p class="muted">Or join <b id="ssid"></b>, password <b id="pass"></b></p>
      </section>
      <section class="step">
        <h2>2. Open the controller</h2>
        <img id="pad-qr" alt="QR code to open the controller" />
        <p class="muted" id="pad-url"></p>
      </section>
    </div>
    <p id="pads">No phone yet</p>
    <section>
      <h2>Emulators</h2>
      <ul id="emulators"></ul>
      <p class="muted">Sudachi: Controls → Player 1 → Input Device → <b>UDP Controller 0</b>.
        Dolphin: Controllers → Configure → Device <b>DSUClient/…</b> → <b>Default</b>.</p>
    </section>
    <button id="stop">Stop AJ Motion</button>
  </main>
  <script type="module" src="js/join.js"></script>
</body>
</html>
```

- [ ] **Step 4: Run the tests.** `npm --prefix controller test`, which should print `join: ok`.
  Then `npm --prefix relay test` (Task 1's join.html test still passes).
- [ ] **Step 5: Check it headless.**
  1. Start `node relay/server.js` with `PORT=39181 JOIN_PORT=8780 DSU_PORT=0 JOIN_SSID="AJ Motion 1A2B" JOIN_PASS=abc123`.
  2. Open `http://127.0.0.1:8780/join.html` in the built-in browser and take a screenshot.
  3. Confirm both QR codes render.
  4. Decode the Wi-Fi one by fetching `/qr?data=` directly. It must equal the `wifiPayload`
     output.
  5. Stop the server.
- [ ] **Step 6: Commit.**

```bash
git add controller/join.html controller/js/join.js controller/test-join.js controller/package.json
git commit -m "feat: join page with Wi-Fi and controller QR codes" -m "Refs #14" -m "Committed by AJ's agent"
```

---

### Task 3: Emulator DSU setup (#24)

**Files:**
- Create: `tools/emulators.ps1`, `tools/test-emulators.ps1`

**Interfaces:**
- Produces: `Invoke-EmulatorSetup [-SudachiConfig <path>] [-DolphinConfigDir <path>] [-IsRunning <scriptblock>]`.
  - It returns an array of `[pscustomobject]@{ emulator; status; detail }`. `status` is one of
    `changed`, `already`, `running`, `missing` or `unknown`.
  - The defaults are the real paths and `{ param($name) [bool](Get-Process -Name $name -ErrorAction SilentlyContinue) }`.
  - Dot-sourcing the script defines the functions. Running it prints the results as JSON.

Rules, as implemented below:

| Emulator | File | Change |
|---|---|---|
| Sudachi | `qt-config.ini` | `enable_udp_controller\default=false`, `enable_udp_controller=true`. If `udp_input_servers` doesn't contain `127.0.0.1:26760`, append `,127.0.0.1:26760` (or set it when empty) and set `udp_input_servers\default=false` |
| Dolphin | `DSUClient.ini` | If missing, create `[Server]`, `Enabled = True`, `Entries = AJ Motion:127.0.0.1:26760;`. If any entry already ends `:127.0.0.1:26760`, keep the entries (aj's is `ajsMotion:127.0.0.1:26760;`). Otherwise append `AJ Motion:127.0.0.1:26760;`. Make sure `Enabled = True` |

`unknown` means an expected key wasn't found. That file is left exactly as it was.

- [ ] **Step 1: Write the failing test** in `tools/test-emulators.ps1`:

```powershell
#requires -Version 5.1
# Runs tools/emulators.ps1 against throwaway copies; exits 1 on the first failure.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'emulators.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ("emu-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path (Join-Path $root 'dolphin') | Out-Null
$failures = 0
function Check([string] $name, [bool] $ok) { if ($ok) { Write-Host "ok   $name" } else { Write-Host "FAIL $name" -ForegroundColor Red; $script:failures++ } }
function Write-Crlf([string] $path, [string[]] $lines) { [IO.File]::WriteAllText($path, ($lines -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding $false)) }
$notRunning = { param($n) $false }

$sudachi = Join-Path $root 'qt-config.ini'
Write-Crlf $sudachi @('[Controls]', 'udp_input_servers\default=true', 'udp_input_servers=127.0.0.1:26760',
  'enable_udp_controller\default=true', 'enable_udp_controller=false',
  'player_0_button_a="engine:sdl,port:0,guid:abc,button:1"')
$before = [IO.File]::ReadAllText($sudachi)

$r = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'dolphin') -IsRunning $notRunning
$after = [IO.File]::ReadAllText($sudachi)
Check 'sudachi changed' (($r | Where-Object emulator -eq 'Sudachi').status -eq 'changed')
Check 'sudachi udp on' ($after -match "(?m)^enable_udp_controller=true`r$")
Check 'sudachi crlf kept' (-not ($after -match "[^`r]`n"))
Check 'sudachi mapping untouched' ($after -match [regex]::Escape('player_0_button_a="engine:sdl,port:0,guid:abc,button:1"'))
Check 'sudachi only two lines differ' ((Compare-Object ($before -split "`r`n") ($after -split "`r`n")).Count -eq 4)
Check 'sudachi backup made' ((Get-ChildItem $root -Filter 'qt-config.ini.ajmotion-*.bak').Count -eq 1)
Check 'dolphin created' ((Get-Content (Join-Path $root 'dolphin\DSUClient.ini') -Raw) -eq "[Server]`r`nEnabled = True`r`nEntries = AJ Motion:127.0.0.1:26760;`r`n")

$r2 = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'dolphin') -IsRunning $notRunning
Check 'second run is a no-op' (@($r2 | Where-Object status -ne 'already').Count -eq 0)

Write-Crlf (Join-Path $root 'dolphin\DSUClient.ini') @('[Server]', 'Enabled = False', 'Entries = ajsMotion:127.0.0.1:26760;')
$r3 = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'dolphin') -IsRunning $notRunning
$d = Get-Content (Join-Path $root 'dolphin\DSUClient.ini') -Raw
Check 'dolphin keeps existing entry' ($d -match 'Entries = ajsMotion:127\.0\.0\.1:26760;\r\n' -and $d -notmatch 'AJ Motion')
Check 'dolphin enabled' ($d -match 'Enabled = True')

$running = { param($n) $n -eq 'sudachi' }
Write-Crlf $sudachi @('[Controls]', 'enable_udp_controller=false')
$r4 = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'dolphin') -IsRunning $running
Check 'running sudachi skipped' (($r4 | Where-Object emulator -eq 'Sudachi').status -eq 'running')
Check 'running sudachi untouched' ([IO.File]::ReadAllText($sudachi) -match 'enable_udp_controller=false')

Write-Crlf $sudachi @('[UI]', 'theme=dark')
$r5 = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'nothing') -IsRunning $notRunning
Check 'unknown format left alone' ((($r5 | Where-Object emulator -eq 'Sudachi').status -eq 'unknown') -and ([IO.File]::ReadAllText($sudachi) -eq "[UI]`r`ntheme=dark`r`n"))
Check 'dolphin missing reported' (($r5 | Where-Object emulator -eq 'Dolphin').status -eq 'missing')

Remove-Item -Recurse -Force $root
if ($failures) { Write-Host "$failures failed" -ForegroundColor Red; exit 1 }
Write-Host 'all passed' -ForegroundColor Green
```

- [ ] **Step 2: Run it to see it fail.**
  `powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-emulators.ps1`. Expect it to
  fail because `emulators.ps1` doesn't exist yet.

- [ ] **Step 3: Implement** `tools/emulators.ps1`:

```powershell
#requires -Version 5.1
<#
.SYNOPSIS
    Turns on DSU input in Sudachi and Dolphin so they see the phone (#24). Changes only the
    keys below: existing player mappings are never touched. A dated backup is taken first,
    newline style is kept, an emulator that's running is skipped (it would overwrite the
    edit on exit), and a second run changes nothing.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest

$DsuServer = '127.0.0.1:26760'

function Read-Config([string] $Path) {
    $text = [IO.File]::ReadAllText($Path)
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    return @{ Text = $text; Newline = $newline }
}

function Save-Config([string] $Path, [string] $Text) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item -LiteralPath $Path -Destination "$Path.ajmotion-$stamp.bak"
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $false))
}

# Replaces the value of one `key=value` line; returns $null when the key isn't there.
function Set-IniValue([string] $Text, [string] $Key, [string] $Value) {
    $pattern = '(?m)^(' + [regex]::Escape($Key) + ')=[^\r\n]*'
    if ($Text -notmatch $pattern) { return $null }
    return [regex]::Replace($Text, $pattern, { param($m) "$($m.Groups[1].Value)=$Value" }, 1)
}

function Set-SudachiDsu([string] $Path) {
    $config = Read-Config $Path
    $text = $config.Text
    foreach ($pair in @(@('enable_udp_controller\default', 'false'), @('enable_udp_controller', 'true'))) {
        $text = Set-IniValue $text $pair[0] $pair[1]
        if ($null -eq $text) { return 'unknown', "no $($pair[0]) line in $Path; left as it was" }
    }
    $servers = [regex]::Match($text, '(?m)^udp_input_servers=([^\r\n]*)')
    if (-not $servers.Success) { return 'unknown', "no udp_input_servers line in $Path; left as it was" }
    if ($servers.Groups[1].Value -notmatch [regex]::Escape($DsuServer)) {
        $list = if ($servers.Groups[1].Value) { "$($servers.Groups[1].Value),$DsuServer" } else { $DsuServer }
        $text = Set-IniValue $text 'udp_input_servers' $list
        $withDefault = Set-IniValue $text 'udp_input_servers\default' 'false'
        if ($null -ne $withDefault) { $text = $withDefault }
    }
    if ($text -ceq $config.Text) { return 'already', 'UDP controllers already on' }
    Save-Config $Path $text
    return 'changed', 'turned on UDP controllers'
}

function Set-DolphinDsu([string] $ConfigDir) {
    $path = Join-Path $ConfigDir 'DSUClient.ini'
    if (-not (Test-Path -LiteralPath $path)) {
        [IO.File]::WriteAllText($path, "[Server]`r`nEnabled = True`r`nEntries = AJ Motion:${DsuServer};`r`n", (New-Object Text.UTF8Encoding $false))
        return 'changed', 'added the AJ Motion DSU server'
    }
    $config = Read-Config $path
    $text = $config.Text
    $entries = [regex]::Match($text, '(?m)^Entries = ([^\r\n]*)')
    $enabled = [regex]::Match($text, '(?m)^Enabled = [^\r\n]*')
    if (-not $entries.Success -or -not $enabled.Success) { return 'unknown', "unexpected layout in $path; left as it was" }
    if ($entries.Groups[1].Value -notmatch (':' + [regex]::Escape($DsuServer) + ';')) {
        $text = $text.Remove($entries.Index, $entries.Length).Insert($entries.Index, "Entries = $($entries.Groups[1].Value)AJ Motion:${DsuServer};")
    }
    $text = [regex]::Replace($text, '(?m)^Enabled = [^\r\n]*', 'Enabled = True', 1)
    if ($text -ceq $config.Text) { return 'already', 'DSU server already set up' }
    Save-Config $path $text
    return 'changed', 'turned on the DSU server'
}

function Invoke-EmulatorSetup {
    param(
        [string] $SudachiConfig = (Join-Path $env:APPDATA 'sudachi\config\qt-config.ini'),
        [string] $DolphinConfigDir = (Join-Path $env:APPDATA 'Dolphin Emulator\Config'),
        [scriptblock] $IsRunning = { param($name) [bool](Get-Process -Name $name -ErrorAction SilentlyContinue) }
    )
    $jobs = @(
        @{ Name = 'Sudachi'; Process = 'sudachi'; Target = $SudachiConfig; Exists = { Test-Path -LiteralPath $SudachiConfig }; Run = { Set-SudachiDsu $SudachiConfig } },
        @{ Name = 'Dolphin'; Process = 'Dolphin'; Target = $DolphinConfigDir; Exists = { Test-Path -LiteralPath $DolphinConfigDir }; Run = { Set-DolphinDsu $DolphinConfigDir } }
    )
    foreach ($job in $jobs) {
        $status, $detail = if (-not (& $job.Exists)) { 'missing', 'not installed' }
            elseif (& $IsRunning $job.Process) { 'running', "close $($job.Name), then start AJ Motion again" }
            else { & $job.Run }
        [pscustomobject]@{ emulator = $job.Name; status = $status; detail = $detail }
    }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-EmulatorSetup | ConvertTo-Json -Compress }
```

- [ ] **Step 4: Run the tests.** The same command must print `all passed`.
- [ ] **Step 5: Run it against copies of aj's real files.**
  1. Copy `%APPDATA%\sudachi\config\qt-config.ini` and the Dolphin `Config` folder to the
     scratchpad.
  2. Run `Invoke-EmulatorSetup` on the copies.
  3. Expect `already` for both: Sudachi already has UDP on, and Dolphin already has
     `ajsMotion`.
  4. Byte-compare each copy with its original, with `fc.exe /b`, and expect no differences.
- [ ] **Step 6: Commit.**

```bash
git add tools/emulators.ps1 tools/test-emulators.ps1
git commit -m "feat: turn on DSU input in Sudachi and Dolphin without touching mappings" -m "Refs #24" -m "Committed by AJ's agent"
```

---

### Task 4: The launcher (#12)

**Files:**
- Create: `tools/launch.ps1`
- Delete: `tools/test-pad.ps1`; its job moves here. Update the references in
  `docs/Handoff-2026-10-10-PhoneTest.md` and `docs/3-state/ProjectState.md`.

**Interfaces:**
- Consumes: `Invoke-EmulatorSetup` (Task 3), env `JOIN_PORT`, `JOIN_SSID`, `JOIN_PASS` and
  `JOIN_EMULATORS` (Task 1), `/health` returning `dns.bound`, and `hotspot/hotspot-control.ps1`
  with `-Action Start|Stop -Ssid -Passphrase`.
- Produces: `launch.ps1 [-Stop]`. The installer's shortcut runs it with no arguments.

Behaviour, in order:

1. **Credentials.** Read `%LOCALAPPDATA%\AJsMotionControls\hotspot.json` as `{ ssid, passphrase }`.
   If it's missing, make the name `AJ Motion ` plus 4 random characters from
   `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`, and a 12-character password from
   `abcdefghjkmnpqrstuvwxyz23456789`, using `[Security.Cryptography.RandomNumberGenerator]`.
   Save it.
2. **Emulators.** Run `Invoke-EmulatorSetup`. Print each line, and pass the results as JSON in
   `JOIN_EMULATORS`.
3. **Clear old runs.** Stop the hotspot, ignoring a failure, and stop any `node` listening on
   8443. A non-node owner of 8443 stops the launch with a plain message, as in
   `test-pad.ps1:Stop-Relay`.
4. **Node.** Use `..\node\node.exe` when it exists (the installed layout). Otherwise use `node`
   from PATH, and if that's missing, stop with "Install Node 20+".
5. **Packages.** When run from a checkout (no `relay\node_modules`), run
   `npm.cmd --prefix relay ci --omit=dev`.
6. **Relay.** Start it with `Start-Process -FilePath $node -ArgumentList start-pad.js -WorkingDirectory relay -NoNewWindow -PassThru`.
   Its log goes to this console, so the user can see it.
7. **Wait** up to 90 s for `http://127.0.0.1:8780/health` to report `dns.bound` as true. If the
   relay process exits first, stop and say the relay's own last lines explain why. The most
   common cause is no certificate yet; the fix is to connect to the internet once.
8. **Hotspot.** Start it with the saved credentials. If that fails, stop the relay and print the
   hotspot's error.
9. **Join page.** `Start-Process 'http://127.0.0.1:8780/join.html'`.
10. **Wait** for the relay to exit (the join page's Stop button, or Ctrl+C). In `finally`, stop
    the hotspot and the relay.

`-Stop` runs only the stop half. Ceiling: closing the console window ends the relay but may
leave the hotspot on. The next launch's step 3 clears it, and the join page's Stop button is the
clean way out.

- [ ] **Step 1:** Write `tools/launch.ps1` to the steps above. Reuse `Invoke-Hotspot`,
  `Get-RelayProcessId` and `Stop-Relay` from `tools/test-pad.ps1` verbatim, since both of the
  5.1 fixes are already in them.
- [ ] **Step 2: Run it for real on aj's PC.**
  1. `powershell -ExecutionPolicy Bypass -File tools\launch.ps1`.
  2. Expect the emulator lines, `Relay listening`, `Hotspot on.`, and the join page opening.
  3. `hotspot.json` exists with an `AJ Motion XXXX` name.
  4. The hotspot actually runs under that name. Check `hotspot-control.ps1`'s JSON output, and
     look for its address with `Get-NetIPAddress` (`192.168.137.1`).
- [ ] **Step 3: Check Stop.** Click **Stop AJ Motion** on the join page. Expect `Hotspot off.`,
  and port 8443 no longer listening (`Get-NetTCPConnection -LocalPort 8443`).
- [ ] **Step 4: Check a second run** reuses the same name and password.
- [ ] **Step 5: Commit**, including the doc reference updates.

```bash
git add tools/launch.ps1 tools/test-pad.ps1 docs/Handoff-2026-10-10-PhoneTest.md docs/3-state/ProjectState.md
git commit -m "feat: one-command launcher with remembered hotspot credentials and join page" -m "Refs #12" -m "Committed by AJ's agent"
```

- [ ] **Step 6: aj's check.** A phone that has forgotten the hotspot joins with the two QR codes
  only, and Sudachi reads it. Record the result in the handoff.

---

### Task 5: The installer (#25)

**Files:**
- Create: `installer/stage.ps1`, which builds `installer/stage/` (gitignored):
  `node/`, `relay/` (with `node_modules`), `controller/`, `hotspot/`, `tools/launch.ps1`,
  `tools/emulators.ps1`, `installer/firewall.ps1`.
- Create: `installer/firewall.ps1 [-Remove]`. It adds rules named `AJ Motion TCP 8443`,
  `AJ Motion TCP 80` and `AJ Motion UDP 53` (inbound, Allow, Profile Any). It's idempotent, and
  `-Remove` deletes rules whose display name starts with `AJ Motion `.
- Create: `installer/ajmotion.iss`.
- Modify: `.gitignore` to add `installer/stage/` and `installer/Output/`.

**Interfaces:**
- Consumes: `tools/launch.ps1`, which looks for `..\node\node.exe` relative to its own folder.
  The stage puts `node\` at the install root, next to `tools\`.
- Produces: `installer/Output/AJ-Motion-Setup.exe`.

- [ ] **Step 1:** Write `installer/stage.ps1`. Its parameters are `-NodeVersion 22.19.0` and
  `-OutDir installer\stage`. It:
  1. downloads `https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-win-x64.zip` and
     `SHASUMS256.txt`, and stops unless the zip's SHA-256 matches its line;
  2. expands `node.exe` into `stage\node\`;
  3. copies `relay\` without `node_modules`, `fixtures`, `test-*.js` and `*.txt`, plus
     `controller\`, `hotspot\` and the three scripts;
  4. runs `npm.cmd --prefix stage\relay ci --omit=dev`;
  5. prints the stage's size.
- [ ] **Step 2:** Write `installer/firewall.ps1` as described. Run it elevated on aj's PC only in
  Task 7. Here, check it parses:
  `powershell -NoProfile -Command "[ScriptBlock]::Create((Get-Content -Raw installer\firewall.ps1)) | Out-Null"`.
- [ ] **Step 3:** Write `installer/ajmotion.iss`:

```ini
; AJ Motion installer (#25). Built by .github/workflows/release.yml from installer\stage\,
; which installer\stage.ps1 fills. Unsigned for now (#27).
#define MyAppName "AJ Motion"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{8AD57C62-5266-4FC9-AFDD-F221A7EA55FD}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\AJ Motion
DefaultGroupName=AJ Motion
OutputBaseFilename=AJ-Motion-Setup
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}

[Files]
Source: "stage\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\AJ Motion"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\launch.ps1"""; WorkingDir: "{app}"
Name: "{autodesktop}\AJ Motion"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\launch.ps1"""; WorkingDir: "{app}"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\firewall.ps1"""; Flags: runhidden waituntilterminated; StatusMsg: "Allowing phones through Windows Firewall..."

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\firewall.ps1"" -Remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewall"
```

The `AppId` stays fixed forever, so upgrades replace the old install instead of sitting beside
it. `runhidden` is acceptable here: the firewall step is short, and its result is checked in
Task 7.

- [ ] **Step 4:** Run `installer/stage.ps1` locally. Expect the checksum to match and the stage
  to be built. Compiling happens in Task 6, because Inno Setup isn't installed on aj's PC, and
  installing it is not in scope.
- [ ] **Step 5: Commit.**

```bash
git add installer/stage.ps1 installer/firewall.ps1 installer/ajmotion.iss .gitignore
git commit -m "feat: Inno Setup installer with portable Node and firewall rules" -m "Refs #25" -m "Committed by AJ's agent"
```

---

### Task 6: Build on GitHub and attach to a Release (#26)

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] **Step 1:** Write the workflow:

```yaml
name: Release installer
on:
  push:
    tags: ['v*']
  workflow_dispatch:
permissions:
  contents: write
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v5
      - uses: actions/setup-node@v5
        with:
          node-version: 22.19.0
      - name: Stage files
        shell: powershell
        run: .\installer\stage.ps1 -NodeVersion 22.19.0 -OutDir installer\stage
      - name: Install Inno Setup
        run: choco install innosetup --no-progress -y
      - name: Compile installer
        shell: powershell
        run: |
          $version = if ($env:GITHUB_REF_TYPE -eq 'tag') { $env:GITHUB_REF_NAME.TrimStart('v') } else { '0.0.0-dev' }
          & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" "/DMyAppVersion=$version" installer\ajmotion.iss
          if ($LASTEXITCODE -ne 0) { throw "ISCC failed with $LASTEXITCODE" }
      - uses: actions/upload-artifact@v4
        with:
          name: AJ-Motion-Setup
          path: installer/Output/AJ-Motion-Setup.exe
      - name: Attach to Release
        if: github.ref_type == 'tag'
        env:
          GH_TOKEN: ${{ github.token }}
        run: gh release create "${{ github.ref_name }}" installer/Output/AJ-Motion-Setup.exe --title "AJ Motion ${{ github.ref_name }}" --notes "Download AJ-Motion-Setup.exe and run it. Windows may say it protected your PC — click **More info**, then **Run anyway** (the installer isn't signed yet)."
```

- [ ] **Step 2:** Push the branch and run the workflow manually: `gh workflow run release.yml --ref AjsAgent/pad-cert-fixtures`.
  Watch it with `gh run watch`, and read the log for the ISCC line `Successful compile`.
- [ ] **Step 3:** Download the artifact with
  `gh run download <id> -n AJ-Motion-Setup -D <scratchpad>`. Its size should be roughly 30–40 MB.
- [ ] **Step 4: Commit** the workflow, plus any fixes the run needed.

```bash
git add .github/workflows/release.yml
git commit -m "ci: build the installer and attach it to tagged Releases" -m "Refs #26" -m "Committed by AJ's agent"
```

---

### Task 7: Install for real, then document (#23)

- [ ] **Step 1:** Ask aj before installing, because it adds firewall rules and Program Files
  entries. Then run the downloaded `AJ-Motion-Setup.exe` on aj's PC.
  1. Check `Get-NetFirewallRule -DisplayName 'AJ Motion *'`: three rules.
  2. Check the desktop shortcut exists.
  3. Check `C:\Program Files\AJ Motion\node\node.exe --version` prints `v22.19.0`.
- [ ] **Step 2:** Start AJ Motion from the desktop shortcut, then aj joins a phone with the two QR
  codes and Sudachi reads it. Record the result.
- [ ] **Step 3:** Uninstall from Settings → Apps. Expect no `AJ Motion *` firewall rules, no
  install folder, and no shortcuts. Check each one.
- [ ] **Step 4:** Tag `v0.1.0`, but only once aj says the build is good. Check the Release has
  the exe and the SmartScreen note.
- [ ] **Step 5: Docs.**
  - `docs/4-systems/`: a short `Launcher.md` covering the start order, the join listener and the
    emulator setup.
  - `docs/3-state/ProjectState.md`: what's built and checked.
  - `docs/1-landing/README.md`: "Download the installer" first.
  - `docs/5-today/Today.md`: this session.
  - Mark the spec's Dolphin-profile line as moved to #28.
- [ ] **Step 6: Commit** the docs, then open a pull request from `AjsAgent/pad-cert-fixtures`
  to `main` with `Refs #23` (never "Closes"). aj closes the issues after testing.
