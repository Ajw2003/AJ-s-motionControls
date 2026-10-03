using System;
using System.Collections.Generic;
using System.Text;

namespace Net
{
    /// <summary>
    /// Wire format shared with the relay and the controller web app.
    /// </summary>
    /// <remarks>
    /// Every inbound message is deliberately flat so Unity's built-in JsonUtility
    /// can parse it — no Newtonsoft dependency. JsonUtility needs public fields
    /// rather than properties, hence the field style throughout.
    ///
    /// Outbound SPECTATE carries a per-player score map, which JsonUtility cannot
    /// serialise, so that one is written by hand in BuildSpectateJson.
    /// </remarks>
    public static class MessageType
    {
        public const string CreateRoom = "CREATE_ROOM";
        public const string RoomCreated = "ROOM_CREATED";
        public const string PlayerJoined = "PLAYER_JOINED";
        public const string PlayerLeft = "PLAYER_LEFT";
        public const string Aim = "AIM";
        public const string Throw = "THROW";
        public const string PickRock = "PICK_ROCK";
        public const string PickColour = "PICK_COLOUR";
        public const string SetName = "SET_NAME";
        public const string Advance = "ADVANCE";
        public const string Turn = "TURN";
        public const string Spectate = "SPECTATE";
        public const string DeviceFunnel = "DEVICE_FUNNEL";
        public const string Error = "ERROR";
    }

    /// <summary>Peeked first to decide which concrete type to parse into.</summary>
    [Serializable]
    public class MessageEnvelope
    {
        public string type;
    }

    [Serializable]
    public class RoomCreatedMessage
    {
        public string roomCode;

        /// <summary>True when the relay still held the room and its players across the drop.</summary>
        public bool resumed;

        /// <summary>Who was still in the room on resume. Empty on a fresh room.</summary>
        public string[] players;
    }

    [Serializable]
    public class PlayerJoinedMessage
    {
        public string playerId;
        public bool reconnected;
    }

    [Serializable]
    public class PlayerLeftMessage
    {
        public string playerId;
    }

    // How far the phones on the hotspot have got through joining — see relay/funnel.js and docs/plans/join-failur... doc-ref 6112 docs/4-systems/Networking.md
    [Serializable]
    public class DeviceFunnelMessage
    {
        /// <summary>Devices on the hotspot Wi-Fi at all.</summary>
        public int associated;

        /// <summary>Of those, how many have loaded the controller page.</summary>
        public int pageOpen;

        /// <summary>Of those, how many have actually joined a room.</summary>
        public int playing;
    }

    [Serializable]
    public class ErrorMessage
    {
        public string code;
        public string message;
    }

    /// <summary>
    /// Cosmetic aim frame, ~20Hz. Never authoritative — dropping these may only
    /// degrade the preview, never change a throw's outcome.
    /// </summary>
    [Serializable]
    public class AimMessage
    {
        public string playerId;
        public int seq;

        // The phone's rotation relative to where it was when the player tapped to aim. doc-ref 504c docs/4-systems/Networking.md
        public float qw = 1f;
        public float qx, qy, qz;

        /// <summary>
        /// How fast the phone is spinning about its own screen normal right now, deg/s,
        /// signed, as a decaying peak envelope — see decaySpinEnvelope in the controller.
        /// Cosmetic like the rest of this class.
        /// </summary>
        public float spinRate;

        // How fast the phone is being swung right now, raw m/s — the same quantity ThrowMessage.swingSpeed carries, j... doc-ref ccd6 docs/4-systems/Networking.md
        public float swingSpeed;
    }


    /// <summary>
    /// A phone confirming its highlighted stone. Carries no rock id on purpose — the TV
    /// owns the shore layout and therefore the highlight; the tap only means "confirm
    /// whatever that is". Dropped outside the Lobby phase, mirroring ThrowMessage's guard.
    /// </summary>
    [Serializable]
    public class PickRockMessage
    {
        public string playerId;
    }

    // A phone choosing the colour it is drawn in. doc-ref 5aea docs/4-systems/Networking.md
    [Serializable]
    public class PickColourMessage
    {
        public string playerId;
        public string colour;
    }

    // A phone announcing (or changing) the display name for its seat. doc-ref 1757 docs/4-systems/Networking.md
    [Serializable]
    public class SetNameMessage
    {
        public string playerId;
        public string name;
    }

    // A phone asking the match to move on — start from the lobby, resume from a re-pick, or begin the next match... doc-ref 99ee docs/4-systems/Networking.md
    [Serializable]
    public class AdvanceMessage
    {
        public string playerId;
    }

    /// <summary>The authoritative throw. Exactly one per gesture.</summary>
    [Serializable]
    public class ThrowMessage
    {
        public string playerId;

        /// <summary>
        /// Raw wrist speed at release, m/s. Not normalised — the only conversion left is
        /// SkipConstants.SpeedScale, inside SkipPhysics.CreateLaunch. The only thing that
        /// decides how far the stone goes, how many times it skips and how high it hops.
        /// </summary>
        public float swingSpeed;

        /// <summary>Attitude at release, in the phone's frame. See AimMessage.qw.</summary>
        public float qw = 1f;
        public float qx, qy, qz;

        /// <summary>Wrist spin at release, deg/s. Visual only.</summary>
        public float spinRate;

        public int sampleCount;

        public override string ToString() =>
            $"{playerId}: speed {swingSpeed:F2} m/s spin {spinRate:F0} ({sampleCount} samples)";
    }

    public static class OutboundMessages
    {
        /// <summary>Tell one phone whether it is their turn.</summary>
        public static string BuildTurn(string playerId, bool yourTurn) =>
            $"{{\"type\":\"{MessageType.Turn}\",\"targetPlayerId\":\"{Escape(playerId)}\"," +
            $"\"yourTurn\":{(yourTurn ? "true" : "false")}}}";

        // Broadcast match state to every phone. doc-ref 7c11 docs/4-systems/Networking.md
        public static string BuildSpectate(
            string activePlayer, string rockName,
            string lastPlayer, int lastSkips, float lastDistance,
            IReadOnlyDictionary<string, int> scores,
            string phase = "PLAYING", int round = 1, int totalRounds = 1,
            IEnumerable<(string PlayerId, string RockId, string RockName)> playerRocks = null,
            bool choosing = false,
            IEnumerable<KeyValuePair<string, UnityEngine.Color>> playerColours = null,
            IEnumerable<UnityEngine.Color> palette = null,
            IEnumerable<KeyValuePair<string, string>> playerNames = null)
        {
            var sb = new StringBuilder(288);
            sb.Append('{')
              .Append($"\"type\":\"{MessageType.Spectate}\",")
              .Append($"\"activePlayer\":\"{Escape(activePlayer)}\",")
              .Append($"\"rockName\":\"{Escape(rockName)}\",")
              .Append($"\"phase\":\"{Escape(phase)}\",")
              .Append($"\"round\":{round},")
              .Append($"\"totalRounds\":{totalRounds},")
              .Append($"\"choosing\":{(choosing ? "true" : "false")},")
              .Append($"\"lastPlayer\":\"{Escape(lastPlayer)}\",")
              .Append($"\"lastSkips\":{lastSkips},")
              .Append($"\"lastDistance\":{lastDistance.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},")
              .Append("\"scores\":{");

            if (scores != null)
            {
                var first = true;
                foreach (var pair in scores)
                {
                    if (!first) sb.Append(',');
                    sb.Append($"\"{Escape(pair.Key)}\":{pair.Value}");
                    first = false;
                }
            }

            sb.Append("},\"names\":{");

            if (playerNames != null)
            {
                var first = true;
                foreach (var pair in playerNames)
                {
                    if (!first) sb.Append(',');
                    sb.Append($"\"{Escape(pair.Key)}\":\"{Escape(pair.Value)}\"");
                    first = false;
                }
            }

            sb.Append("},\"rocks\":{");

            if (playerRocks != null)
            {
                var first = true;
                foreach (var (playerId, rockId, playerRockName) in playerRocks)
                {
                    if (!first) sb.Append(',');
                    sb.Append($"\"{Escape(playerId)}\":{{\"rockId\":\"{Escape(rockId)}\"," +
                              $"\"rockName\":\"{Escape(playerRockName)}\"}}");
                    first = false;
                }
            }

            sb.Append("},\"colours\":{");

            if (playerColours != null)
            {
                var first = true;
                foreach (var pair in playerColours)
                {
                    if (!first) sb.Append(',');
                    sb.Append($"\"{Escape(pair.Key)}\":\"#{UnityEngine.ColorUtility.ToHtmlStringRGB(pair.Value)}\"");
                    first = false;
                }
            }

            sb.Append("},\"palette\":[");

            if (palette != null)
            {
                var first = true;
                foreach (var colour in palette)
                {
                    if (!first) sb.Append(',');
                    sb.Append($"\"#{UnityEngine.ColorUtility.ToHtmlStringRGB(colour)}\"");
                    first = false;
                }
            }

            return sb.Append("]}").ToString();
        }

        /// <summary>Minimal JSON string escaping — player names are user-supplied.</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                        .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
