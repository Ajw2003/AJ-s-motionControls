using System;
using System.IO;
using UnityEngine;

namespace Managers
{
    /// <summary>
    /// Reads and writes the host's match settings as a plain JSON file beside the executable
    /// (repo root in the Editor, since <see cref="Application.dataPath"/> resolves to
    /// <c>Assets/</c> either way).
    /// </summary>
    /// <remarks>
    /// Deliberately not PlayerPrefs: on Windows PlayerPrefs writes into the registry, which is
    /// invisible, isn't cleared by deleting the game folder, and isn't something a host can
    /// inspect or edit by hand. This follows the identical file-beside-the-exe convention
    /// already used for <c>relay-url.txt</c> and <c>relay-env.txt</c>.
    ///
    /// <c>tuningSet</c> used to live in this file — the Authored/Derived comparison it picked
    /// between is gone (docs/plans/Plan-ThrowDrivenSkipModel.md). SettingsData no longer
    /// declares that field, and JsonUtility silently ignores unknown JSON keys on parse, so an
    /// existing <c>match-settings.json</c> with a stale <c>tuningSet</c> entry loads fine
    /// rather than erroring — the key is just never read again, and the next Save drops it.
    /// </remarks>
    public static class MatchSettingsStore
    {
        private const string FileName = "match-settings.json";

        [Serializable]
        private class SettingsData
        {
            public bool randomRocks;
            public int roundsPerMatch = 5;
            // Initializer matters: JsonUtility keeps it for files written before this field existed.
            public bool shareInternet = true;
        }

        public readonly struct Loaded
        {
            public readonly bool RandomRocks;
            public readonly int RoundsPerMatch;
            public readonly bool ShareInternet;

            public Loaded(bool randomRocks, int roundsPerMatch, bool shareInternet)
            {
                RandomRocks = randomRocks;
                RoundsPerMatch = roundsPerMatch;
                ShareInternet = shareInternet;
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

        public static void Save(bool randomRocks, int roundsPerMatch, bool shareInternet)
        {
            try
            {
                var data = new SettingsData
                {
                    randomRocks = randomRocks,
                    roundsPerMatch = roundsPerMatch,
                    shareInternet = shareInternet,
                };
                File.WriteAllText(ResolvePath(), JsonUtility.ToJson(data, prettyPrint: true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MatchSettingsStore] Could not write {FileName}: {e.Message}");
            }
        }

        private static Loaded FromData(SettingsData data) =>
            new(data.randomRocks, Mathf.Clamp(data.roundsPerMatch, 1, 10), data.shareInternet);

        /// <summary>Same resolution as RelayClient.ReadOverride / RoomCodeStore.ResolvePath.</summary>
        private static string ResolvePath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));
    }
}
