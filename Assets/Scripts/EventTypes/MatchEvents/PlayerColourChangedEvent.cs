namespace EventTypes.MatchEvents
{
    using EventSystems;
    using UnityEngine;

    /// <summary>
    /// Published whenever a player's colour changes: the default assigned as they join, or
    /// a pick arriving from their phone.
    /// </summary>
    /// <remarks>
    /// Exists so anything already drawing a player — the shore pointer today, a thrown
    /// stone or a scoreboard row later — can re-tint in place rather than polling
    /// <see cref="Managers.PlayerColours.For"/> every frame. Mirrors RockPickedEvent,
    /// which does the same job for a rock pick.
    ///
    /// <see cref="Colour"/> is the colour the host actually settled on, which is not
    /// necessarily the one the phone asked for — see PlayerColours.Pick, which nudges a
    /// pick that is unreadable against the lake or too close to another player's.
    /// </remarks>
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
