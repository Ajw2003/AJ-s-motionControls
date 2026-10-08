using System;
using System.IO;
using UnityEngine;

namespace Net
{
    // Remembers the room code across launches, so a returning phone's stored/remembered join URL still points som... doc-ref 588e docs/4-systems/HostingAndCertificates.md
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

        /// <summary>Same resolution as MatchSettingsStore.ResolvePath / RoomCodeStore.ResolvePath.</summary>
        private static string ResolvePath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));
    }
}
