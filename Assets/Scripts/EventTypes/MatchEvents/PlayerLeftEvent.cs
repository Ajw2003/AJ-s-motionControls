namespace EventTypes.MatchEvents
{
    using EventSystems;

    /// <summary>Published when a phone disconnects from the room.</summary>
    public class PlayerLeftEvent : IEvent
    {
        public string PlayerId { get; }

        public PlayerLeftEvent(string playerId)
        {
            PlayerId = playerId;
        }
    }
}
