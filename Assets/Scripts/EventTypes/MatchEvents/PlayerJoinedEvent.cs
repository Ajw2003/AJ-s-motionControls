namespace EventTypes.MatchEvents
{
    using EventSystems;

    /// <summary>Published when a phone joins the room, including on reconnect.</summary>
    public class PlayerJoinedEvent : IEvent
    {
        public string PlayerId { get; }
        public bool Reconnected { get; }

        public PlayerJoinedEvent(string playerId, bool reconnected)
        {
            PlayerId = playerId;
            Reconnected = reconnected;
        }
    }
}
