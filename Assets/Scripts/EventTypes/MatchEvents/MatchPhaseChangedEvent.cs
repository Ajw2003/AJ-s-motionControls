namespace EventTypes.MatchEvents
{
    using EventSystems;
    using Managers;

    // Published when the match moves between lobby, play and results. doc-ref 0e64 docs/4-systems/EventSystem.md
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
