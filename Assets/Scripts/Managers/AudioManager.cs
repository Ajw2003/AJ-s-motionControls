using System.Collections.Generic;
using Audio;
using EventSystems;
using EventTypes.AudioEvents;
using Singleton;
using UnityEngine;
using UnityEngine.Audio;

namespace Managers
{
    // Owns every sound in the game. doc-ref ed06 docs/4-systems/Audio.md
    public class AudioManager : SingletonBase<AudioManager>
    {
        [Header("Pool")]
        [Tooltip("Concurrent sounds. A long run lands skips 150-400 ms apart and the " +
                 "previous one is still ringing, so this is how many can overlap before " +
                 "the oldest is recycled.")]
        [SerializeField] private int voiceCount = 12;

        [Header("Mixer")]
        [Tooltip("Optional. Loaded from Resources/GameMixer if present. Without it every " +
                 "sound still plays, just with no group routing and no volume sliders.")]
        [SerializeField] private string mixerResourcePath = "GameMixer";

        private AudioBank _bank;
        private AudioMixer _mixer;

        private readonly Dictionary<AudioChannel, AudioMixerGroup> _groups = new();

        // Round-robin cursor per sound, so a repeated sound never picks the same variant
        // twice running. Keyed by SoundId rather than held on the bank entry because the
        // bank is a shared asset — writing playback state into it would persist to disk in
        // the editor, which is how an asset quietly ends up with a runtime value in it.
        private readonly Dictionary<SoundId, int> _variantCursor = new();

        private AudioSource[] _voices;
        private int _nextVoice;

        protected override void Awake()
        {
            // Before base.Awake, not after. SingletonBase.Awake reads this field to decide
            // whether to call DontDestroyOnLoad, so setting it in Start — as the ported
            // version did — is a no-op that kills the manager on the first scene load.
            persistBetweenScenes = true;
            base.Awake();

            // A second instance destroys itself in base.Awake and must not then build a
            // pool or subscribe. Instance is the survivor.
            if (Instance != this) return;

            // Static caches outlive a play session when Enter Play Mode has domain reload
            // disabled, and the clips they hold do not — the second run would find a cache
            // full of destroyed AudioClips and play silence.
            ProceduralClips.Clear();

            LoadBank();
            LoadMixer();
            BuildVoicePool();

            EventManager.Instance?.Subscribe<PlaySoundEvent>(this, OnPlaySound);
        }

        protected override void OnDestroy()
        {
            // Mandatory, not tidiness — the house rule every manager here follows.
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }

        // ------------------------------------------------------------------ setup

        private void LoadBank()
        {
            _bank = Resources.Load<AudioBank>("AudioBank");

            if (_bank == null)
            {
                // Not fatal, and worth saying out loud rather than running silently: every
                // sound falls back to its synthesised placeholder, at default volume, with
                // no pitch ladder. The game is audible but nothing is tuned.
                Debug.LogWarning("[AudioManager] No Resources/AudioBank asset — every sound " +
                                 "will use its synthesised placeholder with default settings. " +
                                 "Create one via Assets > Create > RockSkipping > Audio Bank.");
            }
        }

        private void LoadMixer()
        {
            _mixer = Resources.Load<AudioMixer>(mixerResourcePath);
            if (_mixer == null) return;

            // Matched by group name, so adding a channel is an edit to the mixer asset and
            // this enum rather than a code change here.
            foreach (AudioChannel channel in System.Enum.GetValues(typeof(AudioChannel)))
            {
                var found = _mixer.FindMatchingGroups(channel.ToString());
                if (found is { Length: > 0 }) _groups[channel] = found[0];
            }
        }

        // Builds the pool of 3D sources every sound plays through. doc-ref 5c1f docs/4-systems/Audio.md
        private void BuildVoicePool()
        {
            _voices = new AudioSource[Mathf.Max(1, voiceCount)];

            for (var i = 0; i < _voices.Length; i++)
            {
                var go = new GameObject($"Voice {i}");
                go.transform.SetParent(transform, false);

                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;

                // Logarithmic rather than Unity's default linear rolloff: linear is silent
                // at MaxDistance and half volume at the midpoint, which puts most of the
                // audible range in the first few metres. Logarithmic keeps a stone at 100 m
                // quiet but present, which is what a lake sounds like.
                source.rolloffMode = AudioRolloffMode.Logarithmic;

                _voices[i] = source;
            }
        }

        // ------------------------------------------------------------------ playback

        private void OnPlaySound(PlaySoundEvent e)
        {
            var entry = _bank?.Find(e.Id);
            var clip = ResolveClip(e.Id, entry);
            if (clip == null) return;

            var source = AcquireVoice();
            if (source == null) return;

            source.clip = clip;

            // PlayOneShot is deliberately not used. It cannot be pitched or positioned per
            // shot and cannot be stopped individually, which is the whole reason for the
            // pool — see BuildVoicePool.
            source.pitch = ResolvePitch(entry, e.Step);

            // Set on the source rather than baked into the clip, and safe to do so only
            // because this source is dedicated to this shot for its duration. On a shared
            // source this is the defect that makes one quiet sound duck every later one.
            source.volume = (entry?.Volume ?? 1f) * e.Intensity;

            var spatial = entry?.Spatial ?? false;
            if (spatial && e.Position.HasValue)
            {
                source.spatialBlend = 1f;
                source.transform.position = e.Position.Value;
                source.minDistance = entry.MinDistance;
                source.maxDistance = entry.MaxDistance;
            }
            else
            {
                source.spatialBlend = 0f;
                source.transform.localPosition = Vector3.zero;
            }

            source.outputAudioMixerGroup =
                _groups.TryGetValue(entry?.Channel ?? AudioChannel.Sfx, out var group) ? group : null;

            if (e.Delay > 0f) source.PlayDelayed(e.Delay);
            else source.Play();
        }

        /// <summary>
        /// The next variant for a sound, or its synthesised placeholder if the bank has
        /// none.
        /// </summary>
        private AudioClip ResolveClip(SoundId id, AudioBank.Entry entry)
        {
            var variants = entry?.Variants;
            if (variants == null || variants.Length == 0) return ProceduralClips.For(id);

            _variantCursor.TryGetValue(id, out var cursor);

            // Walk forward from the cursor rather than indexing it directly, so a bank
            // entry with empty slots in its array cannot hand back a null clip.
            for (var i = 0; i < variants.Length; i++)
            {
                var index = (cursor + i) % variants.Length;
                if (variants[index] == null) continue;

                _variantCursor[id] = index + 1;
                return variants[index];
            }

            return ProceduralClips.For(id);
        }

        /// <summary>
        /// Pitch for one shot: the ladder for its position in a run, plus per-shot jitter.
        /// </summary>
        /// <remarks>
        /// The cap is the load-bearing part. Semitones compound — an uncapped ladder at one
        /// semitone a step puts skip 30 more than two octaves above skip 0, which is a dog
        /// whistle rather than a climax. Past the cap the note holds and volume, variant and
        /// the milestone layer carry the rest of the run.
        /// </remarks>
        private static float ResolvePitch(AudioBank.Entry entry, int step)
        {
            if (entry == null) return 1f;

            var capped = Mathf.Min(step, entry.StepCap);
            var semitones = capped * entry.SemitonesPerStep;

            var jitter = entry.PitchJitter > 0f
                ? Random.Range(-entry.PitchJitter, entry.PitchJitter)
                : 0f;

            // Pitch is a frequency ratio, so semitones are an exponent rather than an
            // addition: twelve of them double it.
            return Mathf.Max(0.01f, entry.BasePitch * Mathf.Pow(2f, semitones / 12f) + jitter);
        }

        /// <summary>
        /// A free source, or the oldest busy one if every source is in use.
        /// </summary>
        /// <remarks>
        /// Steals rather than dropping, matching how WaterRipples recycles its rings
        /// oldest-first when a long run exceeds MaxRipples. Dropping the new sound would
        /// silence exactly the skips at the end of a long run, which are the ones the ladder
        /// has been climbing toward.
        /// </remarks>
        private AudioSource AcquireVoice()
        {
            if (_voices == null || _voices.Length == 0) return null;

            for (var i = 0; i < _voices.Length; i++)
            {
                var index = (_nextVoice + i) % _voices.Length;
                if (_voices[index] != null && !_voices[index].isPlaying)
                {
                    _nextVoice = index + 1;
                    return _voices[index];
                }
            }

            // All busy. _nextVoice has advanced past every source that was taken, so the one
            // it points at is the least recently handed out.
            var stolen = _voices[_nextVoice % _voices.Length];
            _nextVoice++;

            return stolen;
        }
    }
}
