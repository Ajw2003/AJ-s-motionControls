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
    /// machine is not re-asked every session.
    /// </summary>
    public static class NetworkModeStore
    {
        private const string Key = "RockSkipping.NetworkMode";

        public static bool HasSavedMode => PlayerPrefs.HasKey(Key);

        /// <summary>
        /// Stored as the enum's underlying int — fine for two values, but reordering or
        /// inserting members later would silently reinterpret every existing save.
        /// </summary>
        public static NetworkMode Load(NetworkMode fallback = NetworkMode.Lan) =>
            HasSavedMode ? (NetworkMode)PlayerPrefs.GetInt(Key) : fallback;

        public static void Save(NetworkMode mode)
        {
            PlayerPrefs.SetInt(Key, (int)mode);

            // Explicit flush: a crash between choosing and the next launch must not lose the
            // choice and re-prompt, which is the one thing this store exists to avoid.
            PlayerPrefs.Save();
        }
    }
}
