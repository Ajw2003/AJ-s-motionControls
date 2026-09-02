namespace EventTypes.MatchEvents
{
    /// <summary>One player's position in the standings. Highest score first.</summary>
    /// <remarks>
    /// Lived in MatchEndedEvent.cs until that event was deleted for having no subscribers
    /// — ResultsScreen reads standings straight off TurnManager instead, so the snapshot
    /// the event carried was never read by anything.
    /// </remarks>
    public readonly struct PlayerStanding
    {
        public readonly string PlayerId;
        public readonly int Score;

        public PlayerStanding(string playerId, int score)
        {
            PlayerId = playerId;
            Score = score;
        }
    }
}
