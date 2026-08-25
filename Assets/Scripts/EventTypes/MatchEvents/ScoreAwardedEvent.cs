namespace EventTypes.MatchEvents
{
    using EventSystems;

    /// <summary>Published when a completed throw's points are committed to a player.</summary>
    /// <remarks>
    /// From ScoreManager, which is documented as the only place that decides what anything
    /// is worth. Presentation reads the numbers off this rather than recomputing them, so a
    /// score popup and the scoreboard cannot disagree after a retune.
    ///
    /// Fires once per throw, not once per skip. ScoreManager banks points as skips arrive
    /// and commits the total on ThrowCompletedEvent — this is the commit. A per-skip tick
    /// should ride on SkipOccurredEvent instead.
    /// </remarks>
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
