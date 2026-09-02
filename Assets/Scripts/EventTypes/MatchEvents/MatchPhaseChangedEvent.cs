namespace EventTypes.MatchEvents
{
    using EventSystems;
    using Managers;

    /// <summary>Published when the match moves between lobby, play and results.</summary>
    /// <remarks>
    /// This is what music should key off, and not scene loads. Bootstrap's
    /// autoLoadFirstScene defaults to false — "jumping straight into an empty scene just
    /// looks like the game closed" — so the whole game currently runs inside the Bootstrap
    /// scene and the Lobby, Game and Results scenes exist as files that are never entered.
    /// A music director hung off SceneManager would therefore never hear anything, while
    /// the phase genuinely changes throughout a session.
    ///
    /// Only published on a real change. TurnManager routes every phase assignment through
    /// one setter that compares first, so a redundant assignment is silent rather than
    /// restarting a track that is already playing.
    /// </remarks>
    public class MatchPhaseChangedEvent : IEvent
    {
        public MatchPhase From { get; }
        public MatchPhase To { get; }

        public MatchPhaseChangedEvent(MatchPhase from, MatchPhase to)
        {
            From = from;
            To = to;
        }
    }
}
