namespace EventTypes.MatchEvents
{
    using EventSystems;

    /// <summary>
    /// Published whenever a player's display name changes: the fallback label assigned as
    /// they join, or a SET_NAME arriving from their phone (first join, a silent rejoin, or
    /// a rename mid-match).
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="PlayerColourChangedEvent"/>, which does the same job for colour —
    /// exists so anything already drawing a player's name (the roster line, the scoreboard,
    /// the active-player label) can re-render in place rather than polling
    /// <see cref="Managers.PlayerNames.DisplayNameFor"/> every frame.
    ///
    /// See docs/plans/seamless-rejoin.md Workstream C: <c>PlayerId</c> is the device's
    /// stable identity now, not the typed name, which is exactly what makes a rename safe —
    /// it changes what this event carries without changing who the player is to
    /// ScoreManager, TurnManager, or PlayerColours.
    /// </remarks>
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
