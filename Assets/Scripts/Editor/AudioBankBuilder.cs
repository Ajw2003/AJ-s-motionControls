using System.Collections.Generic;
using System.IO;
using System.Linq;
using Audio;
using EventTypes.AudioEvents;
using UnityEditor;
using UnityEngine;

namespace EditorTools
{
    // Points AudioBank at clips in Assets/Audio by filename prefix; unrecognised prefixes are reported,
    // and Rebuild keeps the existing tuning. doc-ref ac1f docs/4-systems/Audio.md
    public static class AudioBankBuilder
    {
        private const string ClipFolder = "Assets/Audio";
        private const string BankPath = "Assets/Resources/AudioBank.asset";

        // Per-prefix defaults: Skip's pitch ladder is capped at fourteen steps because semitones
        // compound, and world-less sounds are non-spatial. doc-ref 81e4 docs/4-systems/Audio.md
        private readonly struct Preset
        {
            public readonly string Prefix;
            public readonly SoundId Id;
            public readonly float Volume;
            public readonly float Semitones;
            public readonly int Cap;
            public readonly float Jitter;
            public readonly bool Spatial;
            public readonly AudioChannel Channel;

            public Preset(string prefix, SoundId id, float volume, float semitones = 0f,
                int cap = 14, float jitter = 0.03f, bool spatial = true,
                AudioChannel channel = AudioChannel.Sfx)
            {
                Prefix = prefix;
                Id = id;
                Volume = volume;
                Semitones = semitones;
                Cap = cap;
                Jitter = jitter;
                Spatial = spatial;
                Channel = channel;
            }
        }

        private static readonly Preset[] Presets =
        {
            new("skip",            SoundId.Skip,          0.75f, semitones: 1.0f, cap: 14, jitter: 0.04f),
            new("skip_milestone",  SoundId.SkipMilestone, 0.60f, semitones: 0.5f, cap: 8,  jitter: 0.02f),
            new("sink",            SoundId.Sink,          0.85f, jitter: 0.05f),
            new("throw_failed",    SoundId.ThrowFailed,   0.80f, jitter: 0.02f),
            new("throw_release",   SoundId.ThrowRelease,  0.55f, jitter: 0.06f),

            // Follows the skip ladder so the ear hears one climbing sound, not two.
            new("score_tick",      SoundId.ScoreTick,     0.35f, semitones: 1.0f, cap: 14, jitter: 0.03f),

            new("throw_scored",    SoundId.ThrowScored,   0.80f, jitter: 0f, spatial: false),
            new("turn_start",      SoundId.TurnStart,     0.55f, jitter: 0f, spatial: false),
            new("match_start",     SoundId.MatchStart,    0.70f, jitter: 0f, spatial: false),
            new("match_win",       SoundId.MatchWin,      0.90f, jitter: 0f, spatial: false),
            new("match_tie",       SoundId.MatchTie,      0.75f, jitter: 0f, spatial: false),
            new("player_joined",   SoundId.PlayerJoined,  0.50f, jitter: 0f, spatial: false, channel: AudioChannel.Ui),
            new("ui_confirm",      SoundId.UiConfirm,     0.45f, jitter: 0f, spatial: false, channel: AudioChannel.Ui),
        };

        [MenuItem("RockSkipping/Rebuild Audio Bank")]
        public static void Rebuild() => Build(resetTuning: false);

        [MenuItem("RockSkipping/Rebuild Audio Bank (reset tuning)")]
        public static void RebuildAndReset() => Build(resetTuning: true);

        private static void Build(bool resetTuning)
        {
            if (!Directory.Exists(ClipFolder))
            {
                Debug.LogError($"[AudioBankBuilder] No {ClipFolder} folder. Run " +
                               "`node tools/audio/generate-sfx.js` from the repo root first.");
                return;
            }

            var bank = LoadOrCreateBank();
            var existing = (bank.Entries ?? new AudioBank.Entry[0])
                .Where(e => e != null)
                .ToDictionary(e => e.Id, e => e);

            var clips = AssetDatabase
                .FindAssets("t:AudioClip", new[] { ClipFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<AudioClip>)
                .Where(c => c != null)
                .OrderBy(c => c.name, System.StringComparer.Ordinal)
                .ToList();

            var entries = new List<AudioBank.Entry>();
            var claimed = new HashSet<string>();

            foreach (var preset in Presets)
            {
                // Longest prefix wins, so skip_milestone_01 is never claimed by "skip".
                var variants = clips
                    .Where(c => Matches(c.name, preset.Prefix))
                    .ToArray();

                foreach (var clip in variants) claimed.Add(clip.name);

                var entry = existing.TryGetValue(preset.Id, out var kept) && !resetTuning
                    ? kept
                    : Defaults(preset);

                entry.Variants = variants;
                entries.Add(entry);

                if (variants.Length == 0)
                {
                    Debug.LogWarning($"[AudioBankBuilder] No clips matched \"{preset.Prefix}_*\" " +
                                     $"— {preset.Id} will fall back to its synthesised placeholder.");
                }
            }

            var orphans = clips.Select(c => c.name).Where(n => !claimed.Contains(n)).ToArray();
            if (orphans.Length > 0)
            {
                Debug.LogWarning($"[AudioBankBuilder] {orphans.Length} clip(s) matched no known " +
                                 $"prefix and will never play: {string.Join(", ", orphans)}");
            }

            bank.Entries = entries.ToArray();

            EditorUtility.SetDirty(bank);
            AssetDatabase.SaveAssets();

            Debug.Log($"[AudioBankBuilder] {BankPath}: {entries.Count} entries, " +
                      $"{claimed.Count} clips wired{(resetTuning ? ", tuning reset to defaults" : "")}.");
        }

        /// <summary>
        /// True when a clip belongs to a prefix, and to no longer one.
        /// </summary>
        /// <remarks>
        /// The longest-match rule is load-bearing rather than defensive: "skip" is a prefix
        /// of "skip_milestone", so a naive StartsWith would put every milestone accent into
        /// the Skip entry as well, and a milestone would then fire as an ordinary skip about
        /// a third of the time.
        /// </remarks>
        private static bool Matches(string clipName, string prefix)
        {
            if (!clipName.StartsWith(prefix + "_", System.StringComparison.Ordinal)) return false;

            return !Presets.Any(other =>
                other.Prefix.Length > prefix.Length &&
                clipName.StartsWith(other.Prefix + "_", System.StringComparison.Ordinal));
        }

        private static AudioBank.Entry Defaults(Preset preset) => new()
        {
            Id = preset.Id,
            Volume = preset.Volume,
            BasePitch = 1f,
            PitchJitter = preset.Jitter,
            SemitonesPerStep = preset.Semitones,
            StepCap = preset.Cap,
            Channel = preset.Channel,
            Spatial = preset.Spatial,
            MinDistance = 5f,
            MaxDistance = 150f,
        };

        private static AudioBank LoadOrCreateBank()
        {
            var bank = AssetDatabase.LoadAssetAtPath<AudioBank>(BankPath);
            if (bank != null) return bank;

            // Resources, specifically. AudioManager loads the bank by name at runtime, and
            // only a Resources folder ships assets that nothing references directly.
            Directory.CreateDirectory("Assets/Resources");

            bank = ScriptableObject.CreateInstance<AudioBank>();
            AssetDatabase.CreateAsset(bank, BankPath);

            Debug.Log($"[AudioBankBuilder] Created {BankPath}");
            return bank;
        }
    }
}
