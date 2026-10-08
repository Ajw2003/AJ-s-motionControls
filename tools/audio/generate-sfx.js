#!/usr/bin/env node
/**
 * Generates every sound effect in the game as a 16-bit PCM .wav, straight into
 * Assets/Audio/. No dependencies — Node writes the RIFF header itself.
 *
 * Run it:
 *   node tools/audio/generate-sfx.js
 *
 * Re-run it any time. It overwrites what it made and nothing else, so tweaking a recipe
 * below and running again is the whole edit loop. Every file it writes is reported with
 * its length and peak level, so a silent or clipped sound is visible without opening Unity.
 *
 * WHY FILES AND NOT RUNTIME SYNTHESIS
 * Assets/Scripts/Audio/ProceduralClips.cs still synthesises a stand-in for any sound with
 * no clip, and stays as the safety net. But a committed .wav can be auditioned in the
 * project window, tweaked in an editor, handed to someone else, and swapped for licensed
 * audio one file at a time without touching code. Generated-at-runtime cannot do any of
 * that, which is the difference between a placeholder and an asset.
 *
 * These are synthesised, not recorded. They are meant to be good enough to ship a
 * prototype and to be replaced individually when real audio arrives — the AudioBank maps
 * a SoundId to whatever clips are in its Variants array, so replacing one is a drag and a
 * drop with no code change.
 */

const fs = require("fs");
const path = require("path");

const SAMPLE_RATE = 48000;

const OUT_DIR = process.argv.includes("--out")
  ? process.argv[process.argv.indexOf("--out") + 1]
  : path.resolve(__dirname, "..", "..", "Assets", "Audio");

// ---------------------------------------------------------------- primitives

/** Deterministic PRNG. Seeded per variant, so a re-run reproduces byte-identical files. */
function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const samples = (seconds) => Math.max(1, Math.round(seconds * SAMPLE_RATE));
const buffer = (seconds) => new Float32Array(samples(seconds));

/** Exponential decay, 1 -> 0. `rate` is how many e-folds across the whole length. */
const decay = (life, rate) => Math.exp(-life * rate);

/** Attack then decay, both in seconds. Rounded rather than clicky at the peak. */
function ad(t, attack, release) {
  if (t < attack) return Math.sin((t / attack) * Math.PI * 0.5);
  return Math.exp(-(t - attack) / Math.max(release, 1e-4));
}

/** Exponential glide between two frequencies — how pitch is actually heard. */
const glide = (from, to, k) =>
  from * Math.pow(to / from, Math.min(1, Math.max(0, k)));

/**
 * An oscillator whose frequency may change per sample.
 * Phase is integrated rather than computed from t*f, or a sweep would fold back on itself.
 */
function osc(seconds, freqAt, shape = Math.sin) {
  const out = buffer(seconds);
  let phase = 0;

  for (let i = 0; i < out.length; i++) {
    const t = i / SAMPLE_RATE;
    phase += (2 * Math.PI * freqAt(t, i / out.length)) / SAMPLE_RATE;
    out[i] = shape(phase);
  }

  return out;
}

function noise(seconds, seed) {
  const random = rng(seed);
  const out = buffer(seconds);
  for (let i = 0; i < out.length; i++) out[i] = random() * 2 - 1;
  return out;
}

// A soft struck-wood tone from a few low additive harmonics, replacing the harsh FM bell.
// doc-ref 68fb docs/4-systems/Audio.md
function mallet(seconds, freq, { release = 0.45, partials = null } = {}) {
  // Near-integer ratios, detuned slightly so the partials beat gently against each other
  // instead of fusing into one sterile tone. Deliberately not a marimba's true 1 : 3.9 :
  // 9.2 — those upper partials are what make a real marimba bright, and bright is the
  // thing being avoided.
  const voices = partials ?? [
    [1, 1],
    [2.01, 0.2],
    [3.02, 0.07],
    [4.03, 0.02],
  ];

  const out = buffer(seconds);

  for (const [ratio, gain] of voices) {
    const partial = osc(seconds, () => freq * ratio);
    const partialRelease = release / (1 + (ratio - 1) * 1.7);

    shape(partial, (t) => ad(t, 0.005, partialRelease));

    for (let i = 0; i < out.length; i++) out[i] += partial[i] * gain;
  }

  // A final low-pass with a ceiling, so even a high note cannot get spiky.
  return svf(out, () => Math.min(freq * 7, 4000), 0.7, "low");
}

/**
 * Chamberlin state-variable filter. One pass, cutoff and Q may move per sample.
 * `mode` picks which of the three simultaneous outputs to keep.
 */
function svf(input, cutoffAt, q = 1, mode = "low") {
  const out = new Float32Array(input.length);
  let low = 0;
  let band = 0;

  for (let i = 0; i < input.length; i++) {
    const life = i / input.length;
    // Clamped well under Nyquist: this filter goes unstable as f approaches SR/2.
    const cutoff = Math.min(cutoffAt(life), SAMPLE_RATE * 0.35);
    const f = 2 * Math.sin((Math.PI * cutoff) / SAMPLE_RATE);
    const damp = Math.min(1 / Math.max(q, 0.5), 1.2);

    const high = input[i] - low - damp * band;
    band += f * high;
    low += f * band;

    out[i] = mode === "low" ? low : mode === "band" ? band : high;
  }

  return out;
}

/** Mixes sources into a canvas, each with a gain and a start offset in seconds. */
function mix(seconds, parts) {
  const out = buffer(seconds);

  for (const { source, gain = 1, at = 0 } of parts) {
    const offset = Math.round(at * SAMPLE_RATE);
    const n = Math.min(source.length, out.length - offset);
    for (let i = 0; i < n; i++) out[i + offset] += source[i] * gain;
  }

  return out;
}

/** Applies an envelope function of (t, life) in place. */
function shape(buf, envelope) {
  for (let i = 0; i < buf.length; i++) {
    buf[i] *= envelope(i / SAMPLE_RATE, i / buf.length);
  }
  return buf;
}

/**
 * Schroeder reverb: four parallel combs into two series allpasses. Small and cheap, and
 * enough to put a sound in a space rather than against a wall.
 */
function reverb(input, { amount = 0.25, room = 0.82 } = {}) {
  if (amount <= 0) return input;

  const combDelays = [1557, 1617, 1491, 1422];
  const allpassDelays = [225, 556];

  const wet = new Float32Array(input.length);

  for (const d of combDelays) {
    const line = new Float32Array(d);
    let idx = 0;
    for (let i = 0; i < input.length; i++) {
      const delayed = line[idx];
      wet[i] += delayed * 0.25;
      line[idx] = input[i] + delayed * room;
      idx = (idx + 1) % d;
    }
  }

  for (const d of allpassDelays) {
    const line = new Float32Array(d);
    let idx = 0;
    for (let i = 0; i < wet.length; i++) {
      const delayed = line[idx];
      const out = delayed - wet[i] * 0.5;
      line[idx] = wet[i] + delayed * 0.5;
      wet[i] = out;
      idx = (idx + 1) % d;
    }
  }

  const out = new Float32Array(input.length);
  for (let i = 0; i < input.length; i++)
    out[i] = input[i] * (1 - amount) + wet[i] * amount;

  return out;
}

function normalize(buf, peak = 0.89) {
  let max = 0;
  for (let i = 0; i < buf.length; i++) max = Math.max(max, Math.abs(buf[i]));
  if (max < 1e-6) return buf;

  const gain = peak / max;
  for (let i = 0; i < buf.length; i++) buf[i] *= gain;
  return buf;
}

/**
 * Fades both ends. A waveform that starts or stops at a non-zero sample is a step, and a
 * step is a click — on the skip sound that click would be louder than the sound.
 */
function deClick(buf, ms = 2) {
  const n = Math.min(
    Math.round((ms / 1000) * SAMPLE_RATE),
    Math.floor(buf.length / 3),
  );
  for (let i = 0; i < n; i++) {
    const k = i / n;
    buf[i] *= k;
    buf[buf.length - 1 - i] *= k;
  }
  return buf;
}

function writeWav(file, buf) {
  const bytes = Buffer.alloc(44 + buf.length * 2);

  bytes.write("RIFF", 0);
  bytes.writeUInt32LE(36 + buf.length * 2, 4);
  bytes.write("WAVE", 8);
  bytes.write("fmt ", 12);
  bytes.writeUInt32LE(16, 16);
  bytes.writeUInt16LE(1, 20); // PCM
  bytes.writeUInt16LE(1, 22); // mono
  bytes.writeUInt32LE(SAMPLE_RATE, 24);
  bytes.writeUInt32LE(SAMPLE_RATE * 2, 28);
  bytes.writeUInt16LE(2, 32);
  bytes.writeUInt16LE(16, 34);
  bytes.write("data", 36);
  bytes.writeUInt32LE(buf.length * 2, 40);

  for (let i = 0; i < buf.length; i++) {
    const s = Math.max(-1, Math.min(1, buf[i]));
    bytes.writeInt16LE(Math.round(s * 32767), 44 + i * 2);
  }

  fs.writeFileSync(file, bytes);
  return bytes.length;
}

// ---------------------------------------------------------------- the sounds

// A soft rounded plop carried by a fast pitch fall, with no bright click.
// doc-ref 67c5 docs/4-systems/Audio.md
function skip(seed, tune = 1) {
  const plop = shape(
    osc(0.2, (t) => glide(820 * tune, 300 * tune, Math.min(1, t / 0.025))),
    (t) => ad(t, 0.003, 0.05),
  );

  const water = shape(
    svf(noise(0.18, seed), (life) => glide(820, 280, life), 1.0, "low"),
    (t) => ad(t, 0.004, 0.042),
  );

  const mixed = mix(0.24, [
    { source: plop, gain: 0.78 },
    { source: water, gain: 0.2 },
  ]);

  // No saturation anywhere in this one. Saturation generates harmonics, and generated
  // harmonics on a sound that repeats thirty times a run is the definition of fatiguing.
  // The final low-pass is a backstop: the pitch ladder resamples this clip upward by as
  // much as fourteen semitones, which drags its whole spectrum with it.
  return normalize(
    deClick(
      reverb(
        svf(mixed, () => 2800, 0.7, "low"),
        { amount: 0.16, room: 0.78 },
      ),
      3,
    ),
    0.72,
  );
}

/** The accent over skips 5, 10, 15 — a soft mallet note, not a shimmer. */
function skipMilestone(seed, tune = 1) {
  return normalize(
    deClick(
      reverb(
        mix(0.6, [
          { source: mallet(0.55, 1046 * tune, { release: 0.3 }), gain: 0.7 },
          { source: mallet(0.55, 1568 * tune, { release: 0.18 }), gain: 0.22 },
        ]),
        { amount: 0.34, room: 0.88 },
      ),
    ),
    0.62,
  );
}

/**
 * The run ending. Falling pitch is what the ear reads as something going under.
 *
 * Kept the descent, softened everything around it: the band-passed "gulp" became a
 * low-pass, which removes the resonant peak that was reading as a squelch, and the
 * saturation is gone.
 */
function sink(seed) {
  const body = shape(
    osc(0.65, (t) => glide(380, 70, Math.min(1, t / 0.32))),
    (t) => ad(t, 0.012, 0.18),
  );

  const gulp = shape(
    svf(noise(0.5, seed), (life) => glide(620, 150, life), 1.1, "low"),
    (t) => ad(t, 0.04, 0.14),
  );

  const wash = shape(
    svf(noise(0.7, seed + 3), () => 700, 0.7, "low"),
    (t) => decay(t / 0.7, 5) * 0.5,
  );

  const mixed = mix(0.78, [
    { source: body, gain: 0.8 },
    { source: gulp, gain: 0.4 },
    { source: wash, gain: 0.16 },
  ]);

  return normalize(
    deClick(
      reverb(
        svf(mixed, () => 2000, 0.7, "low"),
        { amount: 0.3, room: 0.88 },
      ),
      3,
    ),
    0.8,
  );
}

/**
 * A stone that never skipped. Still deliberately unrewarding — no pitch fall, no ring,
 * nothing that resolves — but now dull rather than sharp.
 *
 * The distinction matters: the old version got its disappointment from a hard slap, which
 * is an unpleasant sound. This gets it from being muffled and going nowhere, which is a
 * disappointing sound. Only the second one is repeatable without becoming annoying.
 */
function throwFailed(seed) {
  const thud = shape(
    osc(0.24, (t) => glide(210, 115, Math.min(1, t / 0.07))),
    (t) => ad(t, 0.006, 0.06),
  );

  const damp = shape(
    svf(noise(0.16, seed), () => 420, 0.9, "low"),
    (t) => decay(t / 0.16, 22),
  );

  const mixed = mix(0.28, [
    { source: thud, gain: 0.78 },
    { source: damp, gain: 0.3 },
  ]);

  return normalize(
    deClick(
      reverb(
        svf(mixed, () => 1100, 0.7, "low"),
        { amount: 0.12, room: 0.7 },
      ),
      3,
    ),
    0.66,
  );
}

/** The throw itself. Air, not pitch — bandpassed noise sweeping up then away. */
function throwRelease(seed) {
  const air = shape(
    svf(
      noise(0.34, seed),
      (life) => glide(320, 2600, Math.min(1, life * 1.6)),
      1.3,
      "band",
    ),
    (t) => ad(t, 0.09, 0.09),
  );

  return normalize(deClick(reverb(air, { amount: 0.12, room: 0.7 })), 0.7);
}

/**
 * Rides under the skip sound, so it must be tiny and have no tail to smear across a run.
 *
 * Dropped an octave and stripped of its 6 kHz noise click. At 1850 Hz plus a high-passed
 * transient this was the brightest thing in the game, and it fired on EVERY skip alongside
 * the skip sound itself — two bright sounds thirty times a run.
 */
function scoreTick(seed, tune = 1) {
  return normalize(
    deClick(
      svf(mallet(0.1, 920 * tune, { release: 0.05 }), () => 2200, 0.7, "low"),
      2,
    ),
    0.4,
  );
}

/**
 * A run of struck notes. Used for every reward and announcement sound.
 *
 * More reverb than the earlier version and a softer voice under it. Reverb is doing real
 * work here rather than decorating: it softens each note's onset by smearing it into the
 * tail of the one before, which is most of why a warm phrase sounds warm.
 */
function phrase(
  notes,
  { seconds, release = 0.4, amount = 0.36, room = 0.88 } = {},
) {
  return normalize(
    deClick(
      reverb(
        mix(
          seconds,
          notes.map(({ hz, at, gain = 1 }) => ({
            source: mallet(seconds - at, hz, { release }),
            gain,
            at,
          })),
        ),
        { amount, room },
      ),
      3,
    ),
    0.78,
  );
}

// ---------------------------------------------------------------- catalogue

const CATALOGUE = [
  // Four variants so a thirty-skip run never repeats the same sample back to back. The
  // tuning offsets are small on purpose: they must read as the same stone, not four stones.
  ["skip_01", () => skip(101, 1.0)],
  ["skip_02", () => skip(202, 1.06)],
  ["skip_03", () => skip(303, 0.94)],
  ["skip_04", () => skip(404, 1.02)],

  ["skip_milestone_01", () => skipMilestone(501, 1.0)],
  ["skip_milestone_02", () => skipMilestone(502, 1.05)],

  ["sink_01", () => sink(601)],
  ["sink_02", () => sink(602)],

  ["throw_failed_01", () => throwFailed(701)],

  ["throw_release_01", () => throwRelease(801)],
  ["throw_release_02", () => throwRelease(802)],
  ["throw_release_03", () => throwRelease(803)],

  ["score_tick_01", () => scoreTick(901, 1.0)],
  ["score_tick_02", () => scoreTick(902, 1.04)],
  ["score_tick_03", () => scoreTick(903, 0.97)],

  // Rising and landing on the octave — the ka-ching shape. Dropped a fourth from the
  // first pass: the shape is what reads as a reward, and it reads just as clearly an
  // octave down without any note poking out of the mix.
  [
    "throw_scored_01",
    () =>
      phrase(
        [
          { hz: 523, at: 0 },
          { hz: 659, at: 0.085 },
          { hz: 988, at: 0.17, gain: 0.9 },
        ],
        { seconds: 0.8, release: 0.34 },
      ),
  ],

  // Two notes, short. This fires on every handover, so it cannot outstay its welcome.
  [
    "turn_start_01",
    () =>
      phrase(
        [
          { hz: 523, at: 0 },
          { hz: 784, at: 0.075, gain: 0.85 },
        ],
        { seconds: 0.42, release: 0.18, amount: 0.28 },
      ),
  ],

  [
    "match_start_01",
    () =>
      phrase(
        [
          { hz: 392, at: 0 },
          { hz: 523, at: 0.1 },
          { hz: 659, at: 0.2 },
        ],
        { seconds: 0.75, release: 0.36 },
      ),
  ],

  // The only sound in the set that resolves: four notes up to the octave, with a low
  // sustained note under it so it lands rather than merely stopping.
  [
    "match_win_01",
    () =>
      normalize(
        mix(1.7, [
          {
            source: phrase(
              [
                { hz: 392, at: 0 },
                { hz: 523, at: 0.13 },
                { hz: 659, at: 0.26 },
                { hz: 784, at: 0.39 },
              ],
              { seconds: 1.7, release: 0.6, amount: 0.42 },
            ),
            gain: 0.9,
          },
          {
            // Low-passed rather than a bare sine, so the pad sits under the melody as warmth
            // instead of as a separate audible tone.
            source: svf(
              shape(
                osc(1.7, () => 131),
                (t) => ad(t, 0.08, 0.75) * 0.5,
              ),
              () => 500,
              0.7,
              "low",
            ),
            gain: 0.4,
            at: 0.34,
          },
        ]),
        0.82,
      ),
  ],

  // Two notes that do NOT resolve — the second falls. A tie is not a win and the sound
  // must not claim otherwise. See MatchEndedEvent.
  [
    "match_tie_01",
    () =>
      phrase(
        [
          { hz: 466, at: 0 },
          { hz: 440, at: 0.16, gain: 0.8 },
        ],
        { seconds: 0.75, release: 0.34, amount: 0.3 },
      ),
  ],

  [
    "player_joined_01",
    () =>
      phrase(
        [
          { hz: 659, at: 0 },
          { hz: 880, at: 0.065, gain: 0.8 },
        ],
        { seconds: 0.34, release: 0.15, amount: 0.24 },
      ),
  ],

  [
    "ui_confirm_01",
    () =>
      phrase([{ hz: 784, at: 0 }], {
        seconds: 0.2,
        release: 0.08,
        amount: 0.16,
      }),
  ],
];

// ---------------------------------------------------------------- run

function main() {
  console.log("Rock Skipping — SFX generator");
  console.log(`  sample rate : ${SAMPLE_RATE} Hz, 16-bit mono`);
  console.log(`  output      : ${OUT_DIR}`);
  console.log("");

  fs.mkdirSync(OUT_DIR, { recursive: true });

  let failures = 0;

  CATALOGUE.forEach(([name, build], i) => {
    const step = String(i + 1).padStart(2, " ");

    let buf;
    try {
      buf = build();
    } catch (error) {
      console.log(`  ${step}. FAIL  ${name} — ${error.message}`);
      failures++;
      return;
    }

    let peak = 0;
    for (let s = 0; s < buf.length; s++)
      peak = Math.max(peak, Math.abs(buf[s]));

    const file = path.join(OUT_DIR, `${name}.wav`);
    const bytes = writeWav(file, buf);

    // A sound that came out silent or clipped is a bug in its recipe, and it is invisible
    // until someone plays the game. Checking here means the generator says so itself.
    const seconds = buf.length / SAMPLE_RATE;
    const ok = peak > 0.05 && peak <= 1.0;
    if (!ok) failures++;

    console.log(
      `  ${step}. ${ok ? "ok  " : "FAIL"}  ${name.padEnd(20)} ` +
        `${seconds.toFixed(2)}s  peak ${peak.toFixed(2)}  ${(bytes / 1024).toFixed(1)} KB`,
    );
  });

  console.log("");
  if (failures > 0) {
    console.log(
      `FAILED — ${failures} sound(s) came out silent, clipped, or threw.`,
    );
    process.exit(1);
  }

  console.log(`PASS — ${CATALOGUE.length} sounds written to ${OUT_DIR}`);
  console.log("");
  console.log("Next: in Unity, let the files import, then run");
  console.log("  RockSkipping > Rebuild Audio Bank");
  console.log("to point Resources/AudioBank.asset at them.");
}

main();
