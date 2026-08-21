using System;
using System.IO;
using UnityEngine;

namespace Net
{
    /// <summary>How this host reaches phones: an existing network, or one this PC creates.</summary>
    public enum NetworkMode
    {
        Lan,
        Hotspot,
    }

    /// <summary>
    /// Remembers the host's mode choice across launches, so a returning player on the same
    /// machine is not re-asked every session — but stays easily changeable, since it's read
    /// fresh every time StartScreen shows.
    /// </summary>
    /// <remarks>
    /// Deliberately not PlayerPrefs: on Windows that writes into the registry, which is
    /// invisible, isn't cleared by deleting the game folder, and isn't something a player can
    /// inspect or reset by hand. A plain one-line file beside the executable follows the same
    /// convention already used for relay-url.txt and relay-env.txt.
    /// </remarks>
    public static class NetworkModeStore
    {
        private const string FileName = "network-mode.txt";

        /// <summary>Falls back to Lan — the safe default — on first run or any read failure.</summary>
        public static NetworkMode Load(NetworkMode fallback = NetworkMode.Lan)
        {
            try
            {
                var path = ResolvePath();
                if (!File.Exists(path)) return fallback;

                var text = File.ReadAllText(path).Trim();
                return Enum.TryParse<NetworkMode>(text, ignoreCase: true, out var mode) ? mode : fallback;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkModeStore] Could not read {FileName}: {e.Message}");
                return fallback;
            }
        }

        public static void Save(NetworkMode mode)
        {
            try
            {
                File.WriteAllText(ResolvePath(), mode.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkModeStore] Could not write {FileName}: {e.Message}");
            }
        }

        /// <summary>
        /// Application.dataPath resolves to Assets/ in the editor and &lt;exe&gt;_Data in a
        /// build, so its parent is the right place to look both times — same resolution
        /// RelayClient.ReadOverride and LocalServices.ReadRelayEnvironment already use.
        /// </summary>
        private static string ResolvePath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));
    }
}
