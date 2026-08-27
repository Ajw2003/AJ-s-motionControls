using System;
using System.IO;
using Game;
using UnityEngine;

namespace Managers
{
    /// <summary>
    /// Reads and writes the host's match settings as a plain JSON file beside the executable
    /// (repo root in the Editor, since <see cref="Application.dataPath"/> resolves to
    /// <c>Assets/</c> either way).
    /// </summary>
    /// <remarks>
    /// Deliberately not PlayerPrefs, for the same reason <see cref="Net.NetworkModeStore"/>
    /// isn't: on Windows PlayerPrefs writes into the registry, which is invisible, isn't
    /// cleared by deleting the game folder, and isn't something a host can inspect or edit by
    /// hand. This follows the identical file-beside-the-exe convention already used for
    /// <c>relay-url.txt</c>, <c>relay-env.txt</c> and <c>network-mode.txt</c> — JSON instead of
    /// a single line because there are three values, and <c>tuningSet</c> is stored as its enum
    /// name rather than <see cref="JsonUtility"/>'s default integer so the file reads sensibly
    /// if a host opens it.
    /// </remarks>
    public static class MatchSettingsStore
    {
        private const string FileName = "match-settings.json";

        [Serializable]
        private class SettingsData
        {
            public bool randomRocks;
            public string tuningSet = nameof(TuningSet.Authored);
            public int roundsPerMatch = 5;
        }

        public readonly struct Loaded
        {
            public readonly bool RandomRocks;
            public readonly TuningSet TuningSet;
            public readonly int RoundsPerMatch;

            public Loaded(bool randomRocks, TuningSet tuningSet, int roundsPerMatch)
            {
                RandomRocks = randomRocks;
                TuningSet = tuningSet;
                RoundsPerMatch = roundsPerMatch;
            }
        }

        /// <summary>Falls back to the shipped defaults on first run or any read/parse failure.</summary>
        public static Loaded Load()
        {
            try
            {
                var path = ResolvePath();
                if (!File.Exists(path)) return FromData(new SettingsData());

                var data = JsonUtility.FromJson<SettingsData>(File.ReadAllText(path));
                return FromData(data ?? new SettingsData());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MatchSettingsStore] Could not read {FileName}: {e.Message}");
                return FromData(new SettingsData());
            }
        }

        public static void Save(bool randomRocks, TuningSet tuningSet, int roundsPerMatch)
        {
            try
            {
                var data = new SettingsData
                {
                    randomRocks = randomRocks,
                    tuningSet = tuningSet.ToString(),
                    roundsPerMatch = roundsPerMatch,
                };
                File.WriteAllText(ResolvePath(), JsonUtility.ToJson(data, prettyPrint: true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MatchSettingsStore] Could not write {FileName}: {e.Message}");
            }
        }

        private static Loaded FromData(SettingsData data)
        {
            var tuningSet = Enum.TryParse<TuningSet>(data.tuningSet, ignoreCase: true, out var parsed)
                ? parsed
                : TuningSet.Authored;
            return new Loaded(data.randomRocks, tuningSet, Mathf.Clamp(data.roundsPerMatch, 1, 10));
        }

        /// <summary>Same resolution as NetworkModeStore.ResolvePath / RelayClient.ReadOverride.</summary>
        private static string ResolvePath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));
    }
}
