namespace EventTypes.SkipEvents
{
    using EventSystems;

    /// <summary>Published once the rock has sunk or left play, ending a turn's throw.</summary>
    public class ThrowCompletedEvent : IEvent
    {
        public string PlayerId { get; }
        public int TotalSkips { get; }
        public float Distance { get; }

        public ThrowCompletedEvent(string playerId, int totalSkips, float distance)
        {
            PlayerId = playerId;
            TotalSkips = totalSkips;
            Distance = distance;
        }
    }
}
