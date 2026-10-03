namespace EventTypes.MatchEvents
{
    using EventSystems;

    // Published whenever a player's display name changes: the fallback label assigned as they join, or a SET_NAME... doc-ref 9825 docs/4-systems/EventSystem.md
    public class PlayerNameChangedEvent : IEvent
    {
        public string PlayerId { get; }
        public string Name { get; }

        public PlayerNameChangedEvent(string playerId, string name)
        {
            PlayerId = playerId;
            Name = name;
        }
    }
}
