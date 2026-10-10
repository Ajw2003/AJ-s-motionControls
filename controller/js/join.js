// The join page (#14): two QR codes, then live slot status, all read from the loopback-only
// join listener. Only the PC sees this page, because it shows the Wi-Fi password.

/** WIFI: payload with the escaping from Rock Skipping's JoinScreen.cs. */
export function wifiPayload(ssid, passphrase) {
  const esc = (v) => v.replace(/([\\;,:"])/g, '\\$1');
  return `WIFI:T:WPA;S:${esc(ssid)};P:${esc(passphrase)};;`;
}

/** Draws text as a QR code on a canvas. /qr returns the module grid, not an image. */
async function drawQr(canvas, text) {
  const { size, rows } = await (await fetch(`/qr?data=${encodeURIComponent(text)}`)).json();
  const quiet = 4; // the blank border scanners need
  const scale = 8;
  canvas.width = canvas.height = (size + quiet * 2) * scale;
  const ctx = canvas.getContext('2d');
  ctx.fillStyle = '#fff';
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = '#000';
  rows.forEach((bits, y) => {
    for (let x = 0; x < size; x++) {
      if (bits[x] === '1') ctx.fillRect((x + quiet) * scale, (y + quiet) * scale, scale, scale);
    }
  });
}

async function start() {
  const details = await (await fetch('/api/join')).json();
  await drawQr(document.getElementById('wifi-qr'), wifiPayload(details.ssid, details.passphrase));
  await drawQr(document.getElementById('pad-qr'), details.padUrl);
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
