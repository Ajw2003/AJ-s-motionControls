using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Net
{
    /// <summary>
    /// Reads and writes the host's Wi-Fi hotspot credentials as a plain JSON file in this
    /// Windows user's app-data folder, so a returning phone can remember this network instead of
    /// being handed a fresh SSID and passphrase — and therefore a fresh QR to scan — every launch.
    /// </summary>
    /// <remarks>
    /// The file lives in <c>Application.persistentDataPath</c>
    /// (<c>%USERPROFILE%\AppData\LocalLow\&lt;company&gt;\&lt;product&gt;</c>), not beside the
    /// executable like <see cref="Managers.MatchSettingsStore"/> (#81). The installer puts the
    /// game in Program Files, which the game cannot write to, so a save beside the exe failed and
    /// every launch minted a new network that no phone had saved. A new build folder did the same.
    /// App data survives both, and the Editor and every build on one PC share it. A plain file,
    /// not PlayerPrefs, so a host can still open it by hand to read the passphrase off a stuck TV.
    ///
    /// A file left beside the executable by an older version is copied over once, never moved,
    /// so phones that saved that network keep it and the tools that read it there still work.
    ///
    /// Security note: a guest who once played keeps access to this hotspot forever, since the
    /// credentials no longer change. With Share internet on (the default since #72) a
    /// returning guest gets this PC's internet and LAN, which the host accepted by leaving it
    /// on; with it off, forwarding stays disabled and they reach only the game's relay and
    /// the other phones on it.
    /// </remarks>
    public static class HotspotCredentialsStore
    {
        private const string FileName = "hotspot-credentials.json";

        // Excludes visually ambiguous characters (0/O, 1/l/I) — the passphrase may need to be
        // typed by hand if a QR scan fails. Kept identical to HotspotService's own alphabet so
        // a first-run value generated here looks the same as one generated there.
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

        [Serializable]
        private class CredentialsData
        {
            public string ssid;
            public string passphrase;
        }

        public readonly struct Loaded
        {
            public readonly string Ssid;
            public readonly string Passphrase;

            public Loaded(string ssid, string passphrase)
            {
                Ssid = ssid;
                Passphrase = passphrase;
            }
        }

        /// <summary>
        /// Returns the stored SSID/passphrase, or mints and saves a fresh pair on first run or
        /// any read/parse failure — the same first-run path HotspotService used to run every
        /// launch.
        /// </summary>
        public static Loaded LoadOrCreate()
        {
            if (TryRead(ResolvePath(), out var stored)) return stored;

            if (TryRead(LegacyPath(), out var legacy))
            {
                Debug.Log($"[HotspotCredentialsStore] Copying the saved network '{legacy.Ssid}' from beside the game to {ResolvePath()}.");
                Save(legacy);
                return legacy;
            }

            var fresh = new Loaded(GenerateSsid(), GeneratePassphrase());
            Debug.Log($"[HotspotCredentialsStore] No saved network; created '{fresh.Ssid}' at {ResolvePath()}.");
            Save(fresh);
            return fresh;
        }

        private static bool TryRead(string path, out Loaded loaded)
        {
            loaded = default;
            try
            {
                if (!File.Exists(path)) return false;

                var data = JsonUtility.FromJson<CredentialsData>(File.ReadAllText(path));
                if (data == null || string.IsNullOrEmpty(data.ssid) || string.IsNullOrEmpty(data.passphrase))
                {
                    Debug.LogWarning($"[HotspotCredentialsStore] {path} has no SSID or passphrase; ignoring it.");
                    return false;
                }

                loaded = new Loaded(data.ssid, data.passphrase);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HotspotCredentialsStore] Could not read {path}: {e.Message}");
                return false;
            }
        }

        private static void Save(Loaded loaded)
        {
            try
            {
                var data = new CredentialsData { ssid = loaded.Ssid, passphrase = loaded.Passphrase };
                var path = ResolvePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(data, prettyPrint: true));
            }
            catch (Exception e)
            {
                // An error, not a warning: an unsaved network is a different network next launch,
                // and every phone that saved this one will not rejoin it.
                Debug.LogError($"[HotspotCredentialsStore] Could not save {FileName}, so the network will change next launch: {e.Message}");
            }
        }

        private static string GenerateSsid() => $"Rock Skipping {RandomAlphanumeric(4)}";

        /// <summary>WPA2 allows 8-63 chars; 12 is comfortably strong and still typeable.</summary>
        private static string GeneratePassphrase() => RandomAlphanumeric(12);

        private static string RandomAlphanumeric(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);

            var chars = new char[length];
            for (var i = 0; i < length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return new string(chars);
        }

        private static string ResolvePath() =>
            Path.GetFullPath(Path.Combine(Application.persistentDataPath, FileName));

        /// <summary>Where versions before #81 kept it: beside the executable, like MatchSettingsStore.</summary>
        private static string LegacyPath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));
    }
}
