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

const fs = require('fs');
const path = require('path');

const SAMPLE_RATE = 48000;

const OUT_DIR = process.argv.includes('--out')
  ? process.argv[process.argv.indexOf('--out') + 1]
  : path.resolve(__dirname, '..', '..', 'Assets', 'Audio');

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
const glide = (from, to, k) => from * Math.pow(to / from, Math.min(1, Math.max(0, k)));

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

/**
 * A struck-metal tone via 2-operator FM. The modulation index falls faster than the
 * amplitude, so the sound starts bright and clangy and settles into a pure note — which is
 * what makes it read as struck rather than as a synth pad.
 */
function bell(seconds, freq, { ratio = 2.76, index = 6, brightness = 14, release = 0.35 } = {}) {
  const out = buffer(seconds);
  let carrier = 0;
  let modulator = 0;

  for (let i = 0; i < out.length; i++) {
    const t = i / SAMPLE_RATE;
    const life = i / out.length;

    modulator += (2 * Math.PI * freq * ratio) / SAMPLE_RATE;
    const depth = index * decay(life, brightness);

    carrier += (2 * Math.PI * freq) / SAMPLE_RATE;
    out[i] = Math.sin(carrier + depth * Math.sin(modulator)) * Math.exp(-t / release);
  }

  return out;
}

/**
 * Chamberlin state-variable filter. One pass, cutoff and Q may move per sample.
 * `mode` picks which of the three simultaneous outputs to keep.
 */
function svf(input, cutoffAt, q = 1, mode = 'low') {
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

    out[i] = mode === 'low' ? low : mode === 'band' ? band : high;
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
  for (let i = 0; i < input.length; i++) out[i] = input[i] * (1 - amount) + wet[i] * amount;

  return out;
}

/** Gentle saturation. Rounds transient peaks instead of letting normalise crush everything. */
function saturate(buf, drive = 1.4) {
  for (let i = 0; i < buf.length; i++) buf[i] = Math.tanh(buf[i] * drive) / Math.tanh(drive);
  return buf;
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
  const n = Math.min(Math.round((ms / 1000) * SAMPLE_RATE), Math.floor(buf.length / 3));
  for (let i = 0; i < n; i++) {
    const k = i / n;
    buf[i] *= k;
    buf[buf.length - 1 - i] *= k;
  }
  return buf;
}

function writeWav(file, buf) {
  const bytes = Buffer.alloc(44 + buf.length * 2);

  bytes.write('RIFF', 0);
  bytes.writeUInt32LE(36 + buf.length * 2, 4);
  bytes.write('WAVE', 8);
  bytes.write('fmt ', 12);
  bytes.writeUInt32LE(16, 16);
  bytes.writeUInt16LE(1, 20); // PCM
  bytes.writeUInt16LE(1, 22); // mono
  bytes.writeUInt32LE(SAMPLE_RATE, 24);
  bytes.writeUInt32LE(SAMPLE_RATE * 2, 28);
  bytes.writeUInt16LE(2, 32);
  bytes.writeUInt16LE(16, 34);
  bytes.write('data', 36);
  bytes.writeUInt32LE(buf.length * 2, 40);

  for (let i = 0; i < buf.length; i++) {
    const s = Math.max(-1, Math.min(1, buf[i]));
    bytes.writeInt16LE(Math.round(s * 32767), 44 + i * 2);
  }

  fs.writeFileSync(file, bytes);
  return bytes.length;
}

// ---------------------------------------------------------------- the sounds

/**
 * A stone touching water and coming off it. Three layers, and all three are needed:
 * the click is the contact, the pitched blip is the stone's mass, the wet tail is the lake.
 * Drop the click and it becomes a musical note; drop the tail and it happens in a vacuum.
 */
function skip(seed, tune = 1) {
  const click = shape(
    svf(noise(0.05, seed), () => 3200, 0.8, 'high'),
    (t) => decay(t / 0.05, 26)
  );

  const body = shape(
    osc(0.16, (t) => glide(1500 * tune, 620 * tune, Math.min(1, t / 0.035))),
    (t) => Math.exp(-t / 0.035)
  );

  const water = shape(
    svf(noise(0.2, seed + 7), (life) => glide(1400, 380, life), 2.2, 'band'),
    (t) => ad(t, 0.006, 0.05)
  );

  return normalize(
    deClick(
      reverb(saturate(mix(0.22, [
        { source: click, gain: 0.5 },
        { source: body, gain: 0.62 },
        { source: water, gain: 0.5 },
      ]), 1.5), { amount: 0.12, room: 0.7 })
    ),
    0.85
  );
}

/** The accent layered over skip 5, 10, 15. Brighter, rings longer, no contact click. */
function skipMilestone(seed, tune = 1) {
  return normalize(
    deClick(
      reverb(mix(0.6, [
        { source: bell(0.55, 1180 * tune, { index: 5, brightness: 18, release: 0.14 }), gain: 0.6 },
        { source: bell(0.55, 1770 * tune, { index: 3, brightness: 22, release: 0.1 }), gain: 0.3 },
        {
          source: shape(svf(noise(0.12, seed), () => 5000, 1, 'high'), (t) => decay(t / 0.12, 20)),
          gain: 0.18,
        },
      ]), { amount: 0.3, room: 0.85 })
    ),
    0.8
  );
}

/** The run ending. Falling pitch is what the ear reads as something going under. */
function sink(seed) {
  const body = shape(
    osc(0.6, (t) => glide(430, 72, Math.min(1, t / 0.3))),
    (t) => ad(t, 0.008, 0.16)
  );

  const gulp = shape(
    svf(noise(0.5, seed), (life) => glide(900, 170, life), 3.2, 'band'),
    (t) => ad(t, 0.03, 0.13)
  );

  const wash = shape(
    svf(noise(0.7, seed + 3), () => 1100, 0.7, 'low'),
    (t) => decay(t / 0.7, 5) * 0.5
  );

  return normalize(
    deClick(
      reverb(saturate(mix(0.75, [
        { source: body, gain: 0.75 },
        { source: gulp, gain: 0.5 },
        { source: wash, gain: 0.22 },
      ]), 1.3), { amount: 0.26, room: 0.86 })
    ),
    0.88
  );
}

/**
 * A stone that never skipped. Deliberately unrewarding: one low slap, no pitch fall, no
 * ring, nothing that resolves. Everything that makes `sink` satisfying is removed on purpose.
 */
function throwFailed(seed) {
  const thud = shape(
    osc(0.22, (t) => glide(180, 120, Math.min(1, t / 0.08))),
    (t) => Math.exp(-t / 0.045)
  );

  const slap = shape(
    svf(noise(0.14, seed), () => 700, 1.1, 'low'),
    (t) => decay(t / 0.14, 30)
  );

  return normalize(
    deClick(saturate(mix(0.25, [
      { source: thud, gain: 0.8 },
      { source: slap, gain: 0.45 },
    ]), 1.6)),
    0.82
  );
}

/** The throw itself. Air, not pitch — bandpassed noise sweeping up then away. */
function throwRelease(seed) {
  const air = shape(
    svf(noise(0.34, seed), (life) => glide(320, 2600, Math.min(1, life * 1.6)), 1.3, 'band'),
    (t) => ad(t, 0.09, 0.09)
  );

  return normalize(deClick(reverb(air, { amount: 0.12, room: 0.7 })), 0.7);
}

/** Rides under the skip sound, so it must be tiny and have no tail to smear across a run. */
function scoreTick(seed, tune = 1) {
  return normalize(
    deClick(mix(0.07, [
      { source: shape(osc(0.06, () => 1850 * tune), (t) => Math.exp(-t / 0.011)), gain: 0.7 },
      {
        source: shape(svf(noise(0.02, seed), () => 6000, 1, 'high'), (t) => decay(t / 0.02, 30)),
        gain: 0.25,
      },
    ]), 1),
    0.6
  );
}

/** A run of struck notes. Used for every reward and announcement sound. */
function phrase(notes, { seconds, release = 0.3, amount = 0.3, room = 0.85, index = 5 } = {}) {
  return normalize(
    deClick(
      reverb(
        mix(seconds, notes.map(({ hz, at, gain = 1 }) => ({
          source: bell(seconds - at, hz, { index, brightness: 13, release }),
          gain,
          at,
        }))),
        { amount, room }
      )
    ),
    0.86
  );
}

// ---------------------------------------------------------------- catalogue

const CATALOGUE = [
  // Four variants so a thirty-skip run never repeats the same sample back to back. The
  // tuning offsets are small on purpose: they must read as the same stone, not four stones.
  ['skip_01', () => skip(101, 1.0)],
  ['skip_02', () => skip(202, 1.06)],
  ['skip_03', () => skip(303, 0.94)],
  ['skip_04', () => skip(404, 1.02)],

  ['skip_milestone_01', () => skipMilestone(501, 1.0)],
  ['skip_milestone_02', () => skipMilestone(502, 1.05)],

  ['sink_01', () => sink(601)],
  ['sink_02', () => sink(602)],

  ['throw_failed_01', () => throwFailed(701)],

  ['throw_release_01', () => throwRelease(801)],
  ['throw_release_02', () => throwRelease(802)],
  ['throw_release_03', () => throwRelease(803)],

  ['score_tick_01', () => scoreTick(901, 1.0)],
  ['score_tick_02', () => scoreTick(902, 1.04)],
  ['score_tick_03', () => scoreTick(903, 0.97)],

  // Rising and landing on the octave — the ka-ching shape.
  ['throw_scored_01', () => phrase(
    [{ hz: 659, at: 0 }, { hz: 880, at: 0.085 }, { hz: 1319, at: 0.17, gain: 0.9 }],
    { seconds: 0.75, release: 0.26 }
  )],

  // Two notes, short. This fires on every handover, so it cannot outstay its welcome.
  ['turn_start_01', () => phrase(
    [{ hz: 622, at: 0 }, { hz: 932, at: 0.075, gain: 0.85 }],
    { seconds: 0.38, release: 0.14, amount: 0.22 }
  )],

  ['match_start_01', () => phrase(
    [{ hz: 523, at: 0 }, { hz: 659, at: 0.1 }, { hz: 784, at: 0.2 }],
    { seconds: 0.7, release: 0.3 }
  )],

  // The only sound in the set that resolves: four notes up to the octave, with a low
  // sustained note under it so it lands rather than merely stopping.
  ['match_win_01', () => normalize(
    mix(1.6, [
      {
        source: phrase(
          [{ hz: 523, at: 0 }, { hz: 659, at: 0.13 }, { hz: 784, at: 0.26 }, { hz: 1047, at: 0.39 }],
          { seconds: 1.6, release: 0.5, amount: 0.4 }
        ),
        gain: 0.9,
      },
      {
        source: shape(osc(1.6, () => 131), (t) => ad(t, 0.05, 0.7) * 0.5),
        gain: 0.35,
        at: 0.36,
      },
    ]),
    0.9
  )],

  // Two notes that do NOT resolve — the second falls. A tie is not a win and the sound
  // must not claim otherwise. See MatchEndedEvent.
  ['match_tie_01', () => phrase(
    [{ hz: 587, at: 0 }, { hz: 554, at: 0.16, gain: 0.8 }],
    { seconds: 0.7, release: 0.28, amount: 0.24 }
  )],

  ['player_joined_01', () => phrase(
    [{ hz: 880, at: 0 }, { hz: 1175, at: 0.065, gain: 0.8 }],
    { seconds: 0.3, release: 0.11, amount: 0.18 }
  )],

  ['ui_confirm_01', () => phrase(
    [{ hz: 988, at: 0 }],
    { seconds: 0.16, release: 0.05, amount: 0.1 }
  )],
];

// ---------------------------------------------------------------- run

function main() {
  console.log('Rock Skipping — SFX generator');
  console.log(`  sample rate : ${SAMPLE_RATE} Hz, 16-bit mono`);
  console.log(`  output      : ${OUT_DIR}`);
  console.log('');

  fs.mkdirSync(OUT_DIR, { recursive: true });

  let failures = 0;

  CATALOGUE.forEach(([name, build], i) => {
    const step = String(i + 1).padStart(2, ' ');

    let buf;
    try {
      buf = build();
    } catch (error) {
      console.log(`  ${step}. FAIL  ${name} — ${error.message}`);
      failures++;
      return;
    }

    let peak = 0;
    for (let s = 0; s < buf.length; s++) peak = Math.max(peak, Math.abs(buf[s]));

    const file = path.join(OUT_DIR, `${name}.wav`);
    const bytes = writeWav(file, buf);

    // A sound that came out silent or clipped is a bug in its recipe, and it is invisible
    // until someone plays the game. Checking here means the generator says so itself.
    const seconds = buf.length / SAMPLE_RATE;
    const ok = peak > 0.05 && peak <= 1.0;
    if (!ok) failures++;

    console.log(
      `  ${step}. ${ok ? 'ok  ' : 'FAIL'}  ${name.padEnd(20)} ` +
      `${seconds.toFixed(2)}s  peak ${peak.toFixed(2)}  ${(bytes / 1024).toFixed(1)} KB`
    );
  });

  console.log('');
  if (failures > 0) {
    console.log(`FAILED — ${failures} sound(s) came out silent, clipped, or threw.`);
    process.exit(1);
  }

  console.log(`PASS — ${CATALOGUE.length} sounds written to ${OUT_DIR}`);
  console.log('');
  console.log('Next: in Unity, let the files import, then run');
  console.log('  RockSkipping > Rebuild Audio Bank');
  console.log('to point Resources/AudioBank.asset at them.');
}

main();
