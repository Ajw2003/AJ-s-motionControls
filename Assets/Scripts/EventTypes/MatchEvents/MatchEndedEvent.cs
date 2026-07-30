namespace EventTypes.MatchEvents
{
    using System.Collections.Generic;
    using EventSystems;

    /// <summary>One player's final position. Part of the MatchEndedEvent contract.</summary>
    public readonly struct PlayerStanding
    {
        public readonly string PlayerId;
        public readonly int Score;

        public PlayerStanding(string playerId, int score)
        {
            PlayerId = playerId;
            Score = score;
        }
    }

    /// <summary>
    /// Published once when the final round's last turn completes. The match is over and
    /// no further throws are accepted until it is restarted.
    /// </summary>
    /// <remarks>
    /// Carries the standings as a snapshot rather than pointing at ScoreManager, because
    /// starting the next match clears those scores — anything reacting to this later (a
    /// results jingle, a high-score table) would otherwise read zeroes.
    /// </remarks>
    public class MatchEndedEvent : IEvent
    {
        /// <summary>Highest score first. Empty only if the match ended with no players.</summary>
        public IReadOnlyList<PlayerStanding> Standings { get; }

        /// <summary>Null when there were no players, or when the top score is tied.</summary>
        public string WinnerId { get; }

        public int RoundsPlayed { get; }

        public MatchEndedEvent(IReadOnlyList<PlayerStanding> standings, string winnerId, int roundsPlayed)
        {
            Standings = standings;
            WinnerId = winnerId;
            RoundsPlayed = roundsPlayed;
        }
    }
}
