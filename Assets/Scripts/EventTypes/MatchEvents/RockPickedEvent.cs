namespace EventTypes.MatchEvents
{
    using EventSystems;

    /// <summary>
    /// Published whenever a player's rock pick changes: a phone confirming a stone, a
    /// random assignment at match start, or the default seeded the moment they join.
    /// </summary>
    public class RockPickedEvent : IEvent
    {
        public string PlayerId { get; }
        public string RockId { get; }

        /// <summary>Catalog index of the pick — the same order the shore lays the stones out in.</summary>
        public int Index { get; }

        public RockPickedEvent(string playerId, string rockId, int index)
        {
            PlayerId = playerId;
            RockId = rockId;
            Index = index;
        }
    }
}
