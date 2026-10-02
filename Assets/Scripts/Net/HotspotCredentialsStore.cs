using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Net
{
    /// <summary>
    /// Reads and writes the host's Wi-Fi hotspot credentials as a plain JSON file beside the
    /// executable, so a returning phone can remember this network instead of being handed a
    /// fresh SSID and passphrase — and therefore a fresh QR to scan — every single launch.
    /// </summary>
    /// <remarks>
    /// Same <c>JsonUtility</c> + file-beside-the-executable convention as
    /// <see cref="Managers.MatchSettingsStore"/> and <see cref="RoomCodeStore"/>: not
    /// PlayerPrefs, because on Windows that writes into the registry, which is invisible and
    /// isn't cleared by deleting the game folder, and a host can open this file by hand to read
    /// the passphrase off a stuck TV.
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
            try
            {
                var path = ResolvePath();
                if (File.Exists(path))
                {
                    var data = JsonUtility.FromJson<CredentialsData>(File.ReadAllText(path));
                    if (data != null && !string.IsNullOrEmpty(data.ssid) &&
                        !string.IsNullOrEmpty(data.passphrase))
                    {
                        return new Loaded(data.ssid, data.passphrase);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HotspotCredentialsStore] Could not read {FileName}: {e.Message}");
            }

            var fresh = new Loaded(GenerateSsid(), GeneratePassphrase());
            Save(fresh);
            return fresh;
        }

        private static void Save(Loaded loaded)
        {
            try
            {
                var data = new CredentialsData { ssid = loaded.Ssid, passphrase = loaded.Passphrase };
                File.WriteAllText(ResolvePath(), JsonUtility.ToJson(data, prettyPrint: true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HotspotCredentialsStore] Could not write {FileName}: {e.Message}");
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

        /// <summary>Same resolution as MatchSettingsStore.ResolvePath / HotspotCredentialsStore.ResolvePath.</summary>
        private static string ResolvePath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));
    }
}
