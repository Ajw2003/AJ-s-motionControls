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
        public const string Turn = "TURN";
        public const string Spectate = "SPECTATE";
        public const string Error = "ERROR";
        public const string RoomClosed = "ROOM_CLOSED";
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
        public float aimDeg;
        public float attackAngleDeg;
        public float rollDeg;
        public float windup;
    }

    /// <summary>The authoritative throw. Exactly one per gesture.</summary>
    [Serializable]
    public class ThrowMessage
    {
        public string playerId;
        public float power;
        public float timingQuality;
        public float aimDeg;
        public float attackAngleDeg;
        public float rollDeg;
        public float spinRate;

        // Diagnostics from the phone — useful on the debug HUD while tuning.
        public float peakAccel;
        public float releaseDelayMs;
        public int sampleCount;

        public override string ToString() =>
            $"{playerId}: pow {power:F2} tim {timingQuality:F2} aim {aimDeg:F0}° " +
            $"atk {attackAngleDeg:F0}° spin {spinRate:F0} (peak {peakAccel:F1} m/s², {sampleCount} samples)";
    }

    public static class OutboundMessages
    {
        /// <summary>Tell one phone whether it is their turn.</summary>
        public static string BuildTurn(string playerId, bool yourTurn) =>
            $"{{\"type\":\"{MessageType.Turn}\",\"targetPlayerId\":\"{Escape(playerId)}\"," +
            $"\"yourTurn\":{(yourTurn ? "true" : "false")}}}";

        /// <summary>
        /// Broadcast match state to every phone. Hand-written because the score map
        /// is a dictionary, which JsonUtility cannot serialise.
        /// </summary>
        public static string BuildSpectate(
            string activePlayer, string rockName, int liveSkips, float liveDistance,
            IReadOnlyDictionary<string, int> scores)
        {
            var sb = new StringBuilder(256);
            sb.Append('{')
              .Append($"\"type\":\"{MessageType.Spectate}\",")
              .Append($"\"activePlayer\":\"{Escape(activePlayer)}\",")
              .Append($"\"rockName\":\"{Escape(rockName)}\",")
              .Append($"\"liveSkips\":{liveSkips},")
              .Append($"\"liveDistance\":{liveDistance.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},")
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

            return sb.Append("}}").ToString();
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
