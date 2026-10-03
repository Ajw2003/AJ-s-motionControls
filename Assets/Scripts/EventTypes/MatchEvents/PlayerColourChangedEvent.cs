namespace EventTypes.MatchEvents
{
    using EventSystems;
    using UnityEngine;

    // Published whenever a player's colour changes: the default assigned as they join, or a pick arriving from th... doc-ref 648c docs/4-systems/EventSystem.md
    public class PlayerColourChangedEvent : IEvent
    {
        public string PlayerId { get; }
        public Color Colour { get; }

        public PlayerColourChangedEvent(string playerId, Color colour)
        {
            PlayerId = playerId;
            Colour = colour;
        }
    }
}
