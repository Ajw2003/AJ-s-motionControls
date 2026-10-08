using System;
using System.Collections.Generic;
using EventTypes.AudioEvents;
using UnityEngine;

namespace Audio
{
    // Synthesised stand-in clips, so every sound in the game is audible before any audio asset exists. doc-ref c731 docs/4-systems/Audio.md
    public static class ProceduralClips
    {
        private static readonly Dictionary<SoundId, AudioClip> Cache = new();

        /// <summary>The placeholder for a sound, built on first use and kept.</summary>
        public static AudioClip For(SoundId id)
        {
            if (Cache.TryGetValue(id, out var cached) && cached != null) return cached;

            var clip = Build(id);
            Cache[id] = clip;

            return clip;
        }

        /// <summary>
        /// Drops every cached clip. Needed because the cache is static and Unity statics
        /// survive a play session when Enter Play Mode has domain reload disabled — the
        /// same hazard SingletonBase resets _isQuitting for. Without this the second run
        /// holds AudioClips destroyed at the end of the first, which play as silence.
        /// </summary>
        public static void Clear() => Cache.Clear();

        private static AudioClip Build(SoundId id) => id switch
        {
            // A short bright plink with a noise transient on the front. The transient is
            // what makes it read as a CONTACT rather than a note — without it the pitch
            // ladder sounds like a xylophone rather than like stone hitting water.
            SoundId.Skip => Render("sfx_skip", 0.13f, (t, life) =>
                Tone(t, 760f) * Decay(life, 26f) * 0.55f +
                Noise(t) * Decay(life, 90f) * 0.35f),

            // Brighter and a fifth up, meant to be layered ON TOP of Skip rather than
            // replacing it, so a milestone reads as the same event with something added.
            SoundId.SkipMilestone => Render("sfx_skip_milestone", 0.30f, (t, life) =>
                (Tone(t, 1140f) * 0.5f + Tone(t, 1710f) * 0.25f) * Decay(life, 9f) * 0.5f),

            // A downward sweep into a low body. Falling pitch is what the ear reads as
            // something going under.
            SoundId.Sink => Render("sfx_sink", 0.45f, (t, life) =>
                Tone(t, Mathf.Lerp(320f, 90f, life)) * Decay(life, 5f) * 0.6f +
                Noise(t) * Decay(life, 12f) * 0.18f),

            // Deliberately unrewarding: one low thud, no sweep, no tail, nothing that
            // resolves. This is the sound of a stone that never skipped at all.
            SoundId.ThrowFailed => Render("sfx_throw_failed", 0.18f, (t, life) =>
                Tone(t, 150f) * Decay(life, 18f) * 0.6f +
                Noise(t) * Decay(life, 60f) * 0.15f),

            // Filtered noise swelling and cutting off — air, not pitch.
            SoundId.ThrowRelease => Render("sfx_throw_release", 0.22f, (t, life) =>
                Noise(t) * Mathf.Sin(life * Mathf.PI) * 0.35f),

            // Tiny and short. This rides underneath the skip sound, so anything with a
            // tail on it would smear across the run.
            SoundId.ScoreTick => Render("sfx_score_tick", 0.05f, (t, life) =>
                Tone(t, 1500f) * Decay(life, 60f) * 0.3f),

            // Three rising notes landing on a resolved fourth — the ka-ching shape.
            SoundId.ThrowScored => Render("sfx_throw_scored", 0.45f, (t, life) =>
                Arpeggio(t, life, 0.10f, 0.32f, 660f, 880f, 1320f) * 0.42f),

            SoundId.TurnStart => Render("sfx_turn_start", 0.28f, (t, life) =>
                Arpeggio(t, life, 0.09f, 0.5f, 620f, 930f) * 0.38f),

            SoundId.MatchStart => Render("sfx_match_start", 0.5f, (t, life) =>
                Arpeggio(t, life, 0.11f, 0.45f, 520f, 660f, 780f) * 0.42f),

            // Four notes, resolving upward. The only sound in the set that finishes.
            SoundId.MatchWin => Render("sfx_match_win", 1.1f, (t, life) =>
                Arpeggio(t, life, 0.16f, 0.30f, 520f, 660f, 780f, 1040f) * 0.45f),

            // Two notes that do NOT resolve — it stops on the second rather than landing.
            // A tie is not a win and must not sound like one.
            SoundId.MatchTie => Render("sfx_match_tie", 0.55f, (t, life) =>
                Arpeggio(t, life, 0.18f, 0.5f, 590f, 560f) * 0.38f),

            SoundId.PlayerJoined => Render("sfx_player_joined", 0.22f, (t, life) =>
                Arpeggio(t, life, 0.08f, 0.5f, 880f, 1170f) * 0.32f),

            _ => Render("sfx_ui_confirm", 0.12f, (t, life) =>
                Tone(t, 980f) * Decay(life, 30f) * 0.3f),
        };

        // ------------------------------------------------------------------ synthesis

        /// <summary>
        /// Builds a mono clip by sampling <paramref name="voice"/> across its length.
        /// </summary>
        /// <param name="voice">
        /// Given absolute time in seconds and normalised life 0..1, returns a sample in
        /// -1..1. Both are passed because pitch wants real time and envelopes want life.
        /// </param>
        private static AudioClip Render(string name, float seconds, Func<float, float, float> voice)
        {
            // The output rate rather than a hardcoded 44100. A clip created at a rate the
            // engine is not running at gets resampled, which shifts its pitch — and pitch is
            // the one thing these placeholders exist to let you tune.
            var rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            var count = Mathf.Max(1, Mathf.CeilToInt(rate * seconds));
            var data = new float[count];

            for (var i = 0; i < count; i++)
            {
                var t = i / (float)rate;
                var life = i / (float)count;

                data[i] = Mathf.Clamp(voice(t, life), -1f, 1f);
            }

            // A short fade on both ends. A waveform that starts or stops at a non-zero
            // sample is a step discontinuity, and a step is a click — which on the skip
            // sound would be louder than the sound itself.
            var edge = Mathf.Min(64, count / 4);
            for (var i = 0; i < edge; i++)
            {
                var k = i / (float)edge;
                data[i] *= k;
                data[count - 1 - i] *= k;
            }

            var clip = AudioClip.Create(name, count, 1, rate, false);
            clip.SetData(data, 0);

            return clip;
        }

        private static float Tone(float t, float hz) => Mathf.Sin(t * hz * 2f * Mathf.PI);

        private static float Decay(float life, float rate) => Mathf.Exp(-life * rate);

        /// <summary>Deterministic value noise. Random.value would differ per build.</summary>
        private static float Noise(float t)
        {
            var x = Mathf.Sin(t * 12543.7f) * 43758.5453f;
            return (x - Mathf.Floor(x)) * 2f - 1f;
        }

        /// <summary>
        /// A sequence of notes, each struck in turn and left to decay.
        /// </summary>
        /// <param name="step">Seconds between note onsets.</param>
        /// <param name="decay">Fraction of a step over which each note falls away.</param>
        private static float Arpeggio(float t, float life, float step, float decay, params float[] hz)
        {
            var index = Mathf.FloorToInt(t / step);
            if (index < 0 || index >= hz.Length) return 0f;

            // Each note's own age, so the envelope restarts on every onset rather than
            // following the clip's overall life.
            var within = (t - index * step) / step;

            // The whole run still fades out at the end, so a note struck near the tail does
            // not get cut off mid-ring by the edge fade.
            return Tone(t, hz[index]) * Mathf.Exp(-within / Mathf.Max(decay, 0.01f)) * (1f - life * 0.25f);
        }
    }
}
