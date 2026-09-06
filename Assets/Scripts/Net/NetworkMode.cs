using System;
using System.IO;
using UnityEngine;

namespace Net
{
    /// <summary>How this host reaches phones: an existing network, or one this PC creates.</summary>
    public enum NetworkMode
    {
        /// <summary>
        /// Deprecated, not deleted — see docs/plans/magic-domain-replacement.md Phase 3.
        /// LAN mode bought nothing Hotspot doesn't also get: it still required the phone on
        /// the host's own network, and its only reason to exist — reaching a certificate
        /// covering an arbitrary, varying LAN address — died with local-ip.sh, whose
        /// wildcard is what made an address-varying name certifiable at all. The replacement
        /// certificate (MagicDomain.HostName) is single-name for a fixed hostname, which LAN
        /// mode's varying address can never match, so HTTPS on this path no longer has a
        /// certificate that validates. StartScreen no longer offers this as a choice; it is
        /// still selectable by hand-editing network-mode.txt, kept reachable for a release in
        /// case Hotspot hits a machine it cannot work on.
        /// </summary>
        [Obsolete("LAN mode is deprecated — see docs/plans/magic-domain-replacement.md Phase 3. " +
                   "Still selectable via a hand-edited network-mode.txt, but StartScreen no " +
                   "longer offers it and its certificate no longer validates.")]
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

        /// <summary>
        /// Falls back to Hotspot on first run or any read failure. Used to default to Lan —
        /// flipped per docs/plans/magic-domain-replacement.md Phase 3, now that LAN mode is
        /// deprecated (its certificate no longer validates; see NetworkMode.Lan's remarks) and
        /// StartScreen no longer offers a picker at all. LAN stays reachable only by
        /// hand-editing network-mode.txt to the literal string "Lan".
        /// </summary>
        public static NetworkMode Load(NetworkMode fallback = NetworkMode.Hotspot)
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
