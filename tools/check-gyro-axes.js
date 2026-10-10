// Checks that a phone's rotation readings agree with its gravity readings (#30), using a DSU
// capture from tools/dsu-capture.js. Gravity is physical truth that needs no axis labels: for
// each move, the starting gravity is turned by the gyro readings and compared with the gravity
// measured afterwards. All 48 ways of ordering and signing the three gyro axes are scored; if
// the stream is right, "as sent" is the best fit.
//
// Record a capture while tilting the phone (lift each edge, put it back), then:
//   node tools/check-gyro-axes.js capture.jsonl
// Exits 1 if another mapping fits clearly better than what was sent.

const fs = require('fs');
const path = require('path');
const { phoneToDsuAxes } = require(path.join(__dirname, '..', 'relay', 'dsu.js'));

const DEG = Math.PI / 180;
const norm = (v) => {
  const n = Math.hypot(...v);
  return v.map((x) => x / n);
};
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const angleDeg = (a, b) => {
  const m = norm(b);
  return Math.acos(Math.max(-1, Math.min(1, a[0] * m[0] + a[1] * m[1] + a[2] * m[2]))) / DEG;
};

// Undo the relay's phone -> DSU mapping, so the check runs in the phone's own right-handed frame.
function toPhoneFrame(sample) {
  const basis = [0, 1, 2].map((k) => {
    const unit = [0, 0, 0];
    unit[k] = 1;
    return phoneToDsuAxes(unit, unit);
  });
  // phoneToDsuAxes is a signed permutation, so its inverse is its transpose.
  const back = (dsu, key) => [0, 1, 2].map((k) => basis[k][key].reduce((sum, v, j) => sum + v * dsu[j], 0));
  return { accel: back(sample.accel, 'accel'), gyro: back(sample.gyro, 'gyro') };
}

function main(file) {
  const raw = fs
    .readFileSync(file, 'utf8')
    .split('\n')
    .filter(Boolean)
    .map((line) => JSON.parse(line))
    .filter((s) => s.slot === 0);
  // Button repeats carry the last motion sample again; keep motion packets only.
  const samples = raw
    .filter((s, i) => i === 0 || s.timestampUs !== raw[i - 1].timestampUs)
    .map((s) => ({ ts: s.timestampUs, ...toPhoneFrame(s) }));

  const moves = [];
  let current = null;
  for (let i = 1; i < samples.length; i++) {
    if (Math.hypot(...samples[i].gyro) > 20) {
      if (!current || i - current[1] > 30) moves.push((current = [i, i]));
      current[1] = i;
    }
  }
  const windows = moves
    .map(([s, e]) => [Math.max(0, s - 15), Math.min(samples.length - 1, e + 15)])
    .filter(([a, b]) => angleDeg(norm(samples[a].accel), samples[b].accel) > 20);
  if (windows.length === 0) {
    console.log('No tilts found. Record a capture while lifting each edge of the phone and putting it back.');
    process.exit(1);
  }

  const score = (map) => {
    let total = 0;
    for (const [a, b] of windows) {
      let g = norm(samples[a].accel);
      for (let i = a + 1; i <= b; i++) {
        const dt = (samples[i].ts - samples[i - 1].ts) / 1e6;
        const w = map(samples[i].gyro).map((x) => x * DEG);
        const c = cross(w, g);
        g = norm(g.map((x, k) => x - c[k] * dt));
      }
      total += angleDeg(g, samples[b].accel);
    }
    return total / windows.length;
  };

  const perms = [
    [0, 1, 2],
    [0, 2, 1],
    [1, 0, 2],
    [1, 2, 0],
    [2, 0, 1],
    [2, 1, 0],
  ];
  const results = [];
  for (const p of perms) {
    for (let m = 0; m < 8; m++) {
      const sign = [m & 1 ? -1 : 1, m & 2 ? -1 : 1, m & 4 ? -1 : 1];
      const label = p.map((k, i) => (sign[i] < 0 ? '-' : '+') + 'xyz'[k]).join(' ');
      results.push({ label, error: score((g) => [0, 1, 2].map((i) => g[p[i]] * sign[i])) });
    }
  }
  results.sort((a, b) => a.error - b.error);
  const asSent = results.find((r) => r.label === '+x +y +z');
  console.log(`${windows.length} tilts. Mean disagreement with gravity, degrees (phone frame):`);
  for (const r of results.slice(0, 3)) console.log(`  ${r.label}  ${r.error.toFixed(1)}`);
  console.log(`  as sent (+x +y +z): ${asSent.error.toFixed(1)}`);
  const ok = asSent.error <= results[0].error + 1;
  console.log(ok ? 'OK: the rotation axes agree with gravity.' : 'MISMATCH: another axis mapping fits gravity better.');
  process.exit(ok ? 0 : 1);
}

main(process.argv[2] || 'capture.jsonl');
