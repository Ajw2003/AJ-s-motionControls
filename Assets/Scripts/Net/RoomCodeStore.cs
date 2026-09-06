using System;
using System.IO;
using UnityEngine;

namespace Net
{
    /// <summary>
    /// Remembers the room code across launches, so a returning phone's stored/remembered join
    /// URL still points somewhere real after the host restarts, instead of landing on the join
    /// form because the room code changed underneath it.
    /// </summary>
    /// <remarks>
    /// Deliberately not PlayerPrefs, for the same reason <see cref="NetworkModeStore"/> isn't:
    /// on Windows that writes into the registry, which is invisible and isn't cleared by
    /// deleting the game folder. A plain one-line file beside the executable follows the same
    /// convention already used for <c>network-mode.txt</c>.
    ///
    /// This only seeds <c>preferredCode</c> on <see cref="RelayClient"/>'s first CREATE_ROOM —
    /// the relay (<c>handleCreateRoom</c>) still decides whether the code is actually free and
    /// may hand back a different one, which <see cref="RelayClient"/> then saves here via
    /// <see cref="Save"/>. So this file records "the code we'll ask for next time", not a
    /// guarantee.
    /// </remarks>
    public static class RoomCodeStore
    {
        private const string FileName = "room-code.txt";

        /// <summary>Empty on first run or any read failure — RelayClient then omits preferredCode.</summary>
        public static string Load()
        {
            try
            {
                var path = ResolvePath();
                return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoomCodeStore] Could not read {FileName}: {e.Message}");
                return string.Empty;
            }
        }

        public static void Save(string roomCode)
        {
            try
            {
                File.WriteAllText(ResolvePath(), roomCode ?? string.Empty);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoomCodeStore] Could not write {FileName}: {e.Message}");
            }
        }

        /// <summary>Same resolution as NetworkModeStore.ResolvePath / MatchSettingsStore.ResolvePath.</summary>
        private static string ResolvePath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));
    }
}
