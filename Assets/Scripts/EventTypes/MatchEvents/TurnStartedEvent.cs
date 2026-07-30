namespace EventTypes.MatchEvents
{
    using EventSystems;

    /// <summary>Published when control passes to a player for their throw.</summary>
    public class TurnStartedEvent : IEvent
    {
        public string PlayerId { get; }
        public int RoundIndex { get; }

        public TurnStartedEvent(string playerId, int roundIndex)
        {
            PlayerId = playerId;
            RoundIndex = roundIndex;
        }
    }
}
