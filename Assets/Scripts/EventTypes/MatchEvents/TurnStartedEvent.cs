namespace EventTypes.MatchEvents
{
    using EventSystems;

    // Published when the turn passes to a player. doc-ref 16dd docs/4-systems/EventSystem.md
    public class TurnStartedEvent : IEvent
    {
        /// <summary>Whose turn it now is.</summary>
        public string PlayerId { get; }

        /// <summary>
        /// Zero-based index of the round in progress, matching TurnManager.CurrentRound.
        /// Add one before showing it to anybody.
        /// </summary>
        public int Round { get; }

        /// <summary>Rounds in the whole match — a count, so it is already one-based.</summary>
        public int TotalRounds { get; }

        public TurnStartedEvent(string playerId, int round, int totalRounds)
        {
            PlayerId = playerId;
            Round = round;
            TotalRounds = totalRounds;
        }
    }
}
