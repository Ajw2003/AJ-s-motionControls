namespace EventTypes.MatchEvents
{
    using EventSystems;

    // Published when a completed throw's points are committed to a player. doc-ref 26a4 docs/4-systems/EventSystem.md
    public class ScoreAwardedEvent : IEvent
    {
        public string PlayerId { get; }

        /// <summary>Points from this throw alone — skips plus distance.</summary>
        public int Points { get; }

        /// <summary>The player's running total, this throw already included.</summary>
        public int NewTotal { get; }

        public ScoreAwardedEvent(string playerId, int points, int newTotal)
        {
            PlayerId = playerId;
            Points = points;
            NewTotal = newTotal;
        }
    }
}
