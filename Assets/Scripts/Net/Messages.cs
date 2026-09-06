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

    /// <summary>
    /// How far the phones on the hotspot have got through joining — see relay/funnel.js and
    /// docs/plans/join-failure-reduction.md. Sent unprompted whenever the numbers change, and
    /// once on ROOM_CREATED so a host that just restarted is not blind until they next do.
    /// </summary>
    /// <remarks>
    /// A count of devices, never a roster of people: the relay can only key this by IP, so
    /// two phones behind one address count once and a phone that changes address counts
    /// twice. Fine for deciding what the TV says; never use it as a player count — that is
    /// TurnManager.Players, which is authoritative.
    ///
    /// The counts are cumulative, so <c>associated &gt;= pageOpen &gt;= playing</c> always
    /// holds and the interesting numbers are the differences between them.
    /// </remarks>
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

        /// <summary>
        /// The phone's rotation relative to where it was when the player tapped to aim.
        /// </summary>
        /// <remarks>
        /// Four floats rather than three angles, and that is the point. The phone used to
        /// decompose its attitude into aim / attack / roll and Unity re-composed it with
        /// two hand-found sign flips; a quaternion has no decomposition to get wrong and
        /// no degenerate pose. Identity means "however the player was holding it at the
        /// tap", drawn as a flat stone facing away from the camera.
        ///
        /// Still in the phone's own right-handed frame here. Game.PhoneAttitude.ToUnity is
        /// the only thing permitted to convert it — see that file.
        /// </remarks>
        public float qw = 1f;
        public float qx, qy, qz;

        /// <summary>
        /// How fast the phone is spinning about its own screen normal right now, deg/s,
        /// signed, as a decaying peak envelope — see decaySpinEnvelope in the controller.
        /// Cosmetic like the rest of this class.
        /// </summary>
        public float spinRate;

        /// <summary>
        /// How fast the phone is being swung right now, raw m/s — the same quantity
        /// ThrowMessage.swingSpeed carries, just sampled mid-swing instead of at release.
        /// See ThrowController.windup.
        /// </summary>
        /// <remarks>
        /// Not normalised. An earlier version sent a 0..1 `power` here, derived the same
        /// way ThrowMessage.power was — but that meant two lossy conversions of the same
        /// swing had to agree, on both the phone and in Unity, or the arc and the stone
        /// would disagree by construction. Sending the raw measurement once and converting
        /// it once, in Unity (SkipConstants.SpeedScale), removes the second chance to drift.
        ///
        /// Only ever an estimate, because the swing has not finished yet. AimPreview
        /// floors it at a nominal so a player holding still to aim still sees an arc.
        /// </remarks>
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

    /// <summary>
    /// A phone choosing the colour it is drawn in.
    /// </summary>
    /// <remarks>
    /// Unlike PickRockMessage this DOES carry its payload, because the TV cannot know what
    /// a player wants: a rock pick means "confirm the highlight the TV is already showing",
    /// but a colour comes off a picker the phone owns and the TV never sees.
    ///
    /// Sent as "#rrggbb" rather than three floats. That is what &lt;input type="color"&gt;
    /// produces natively and what CSS consumes on the way back, so the phone never converts
    /// and there is no float formatting to get wrong across a culture. Parsed with
    /// ColorUtility.TryParseHtmlString; an unparseable string is dropped, exactly as
    /// PickRockMessage's unknown rock id is.
    ///
    /// A request, not an instruction — Managers.PlayerColours decides what the player
    /// actually gets, and the settled colour comes back in the next SPECTATE.
    /// </remarks>
    [Serializable]
    public class PickColourMessage
    {
        public string playerId;
        public string colour;
    }

    /// <summary>
    /// A phone announcing (or changing) the display name for its seat. See
    /// docs/plans/seamless-rejoin.md Workstream C: playerId is now the device's stable
    /// identity, not the typed name, so the name has to travel as its own message rather
    /// than being read out of playerId — Managers.PlayerNames is the one place it lands.
    /// </summary>
    /// <remarks>
    /// Sent every time this device's controller reaches JOINED, first join or rejoin
    /// alike, and again whenever the player edits their name mid-match — a rename, not a
    /// new player, since playerId does not change. relayToHost stamps playerId
    /// server-side exactly as it does for PickColourMessage, so a phone cannot claim to
    /// be renaming someone else.
    /// </remarks>
    [Serializable]
    public class SetNameMessage
    {
        public string playerId;
        public string name;
    }

    /// <summary>
    /// A phone asking the match to move on — start from the lobby, resume from a re-pick,
    /// or begin the next match from the results screen.
    /// </summary>
    /// <remarks>
    /// Carries no target phase on purpose, exactly like PickRockMessage carries no rock id:
    /// the TV owns the phase, so this only means "whatever the button on my screen said".
    /// TurnManager runs the same switch for this as it does for the host's Space key, so the
    /// keyboard and the phones cannot advance the match to different places.
    ///
    /// Any connected phone may send it. A couch game has no host player, and adding one
    /// would need a rule for what happens when that particular phone drops.
    /// </remarks>
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

        /// <summary>
        /// Broadcast match state to every phone. Hand-written because the score map
        /// is a dictionary, which JsonUtility cannot serialise.
        /// </summary>
        /// <remarks>
        /// Sent on state changes only — a player joining or leaving, a turn starting, a
        /// throw completing, the match ending. Never on a timer. This used to also go out
        /// four times a second for the whole time a rock was in the air, to drive a live
        /// readout on phones nobody watches during a couch game; that was the single most
        /// expensive thing on the wire, and it is gone.
        /// </remarks>
        /// <param name="phase">LOBBY, PLAYING or RESULTS. Without it an idle phone cannot
        /// tell "waiting to start" from "final scores" and shows the wrong caption in two
        /// of the three phases.</param>
        /// <param name="lastPlayer">Who threw the <paramref name="lastSkips"/> result. Named
        /// separately from activePlayer on purpose: the turn has already advanced by the time
        /// this goes out, so without it a phone would credit the throw to the wrong player.</param>
        /// <param name="playerRocks">Every connected player's rock, so an idle phone in the
        /// lobby can show what everyone has claimed — not just <paramref name="rockName"/>,
        /// which only ever names the active player's stone. Null is fine; the "rocks" object
        /// is simply empty, which is what a build without RockSelection running degrades to.</param>
        /// <param name="playerColours">Every player's settled colour, as "#rrggbb", so a phone
        /// can paint its own accent to match its dot on the TV and tell the other players
        /// apart in the scores. Null is fine; the "colours" object is simply empty, which is
        /// what a build without PlayerColours running degrades to — the same tolerance
        /// <paramref name="playerRocks"/> has.</param>
        /// <param name="palette">The quick-pick swatches the phone's colour picker offers.
        /// Sent rather than duplicated in the controller so the palette is authored in one
        /// place — the PlayerColourPalette asset — and a phone needs no rebuild when it
        /// changes. Null leaves the picker on its custom-colour input alone.</param>
        /// <param name="choosing">True for a mid-match re-pick pause. Cosmetic only, and the
        /// one thing the phone knows about MatchPhase.Choosing: the wire phase stays "LOBBY"
        /// (see TurnManager.BroadcastSpectate) because a choose screen behaves identically
        /// either way, but the advance button on it must not offer to "start" a match that
        /// is already half played.</param>
        /// <param name="playerNames">Every connected player's display name, id → name — see
        /// docs/plans/seamless-rejoin.md Workstream C. Deliberately a parallel field rather
        /// than a substitution into <paramref name="activePlayer"/>/<paramref name="lastPlayer"/>
        /// or the <paramref name="scores"/> keys: those stay opaque device ids on the wire so
        /// app.js's identity comparisons (`=== myPlayerId`) keep working, and so two players
        /// who happen to type the same display name never collide as one dictionary key. Null
        /// is fine; the "names" object is simply empty, the same tolerance
        /// <paramref name="playerRocks"/> and <paramref name="playerColours"/> have.</param>
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
