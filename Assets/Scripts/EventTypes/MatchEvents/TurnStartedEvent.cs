namespace EventTypes.MatchEvents
{
    using EventSystems;

    /// <summary>Published when the turn passes to a player.</summary>
    /// <remarks>
    /// Fires on every turn handover, which with two to eight players is constantly — so
    /// anything hung off it has to stay short. Plan-SfxVfx.md section 6.9 puts the ceiling
    /// at about 1.2 seconds, past which the turn-change presentation becomes the dominant
    /// experience of the game.
    ///
    /// Also fires when the active player leaves mid-turn and the turn is handed to whoever
    /// takes their index, which is a genuine turn start and not a duplicate.
    /// </remarks>
    public class TurnStartedEvent : IEvent
    {
        /// <summary>Whose turn it now is.</summary>
        public string PlayerId { get; }

        /// <summary>
        /// Zero-based index of the round in progress, matching TurnManager.CurrentRound.
        /// Add one before showing it to anybody.
        /// </summary>
        public int Round { get; }

        /// <summary>Rounds in the whole match — a count, so it is already one-based.</summary>
        public int TotalRounds { get; }

        public TurnStartedEvent(string playerId, int round, int totalRounds)
        {
            PlayerId = playerId;
            Round = round;
            TotalRounds = totalRounds;
        }
    }
}
