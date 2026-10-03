namespace EventTypes.MatchEvents
{
    using EventSystems;

    // Published once, when the last round is done and the match is over. doc-ref 978c docs/4-systems/EventSystem.md
    public class MatchEndedEvent : IEvent
    {
        /// <summary>
        /// The winner, or null on a tie at the top.
        /// </summary>
        /// <remarks>
        /// Null is a real, expected value here and must be handled rather than defended
        /// against. TurnManager refuses to name a winner when the top two scores are level,
        /// and says why: "claiming one would be a lie the results screen then has to
        /// render." A fanfare that crowns somebody on a tie is that same lie in audio.
        /// </remarks>
        public string WinnerId { get; }

        /// <summary>True when the match ended level at the top and nobody won.</summary>
        public bool IsTie => string.IsNullOrEmpty(WinnerId);

        public MatchEndedEvent(string winnerId)
        {
            WinnerId = winnerId;
        }
    }
}
