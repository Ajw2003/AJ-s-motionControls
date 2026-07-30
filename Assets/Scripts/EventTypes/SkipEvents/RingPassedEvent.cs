namespace EventTypes.SkipEvents
{
    using EventSystems;

    /// <summary>Published when the rock passes through one of the three ring gates.</summary>
    public class RingPassedEvent : IEvent
    {
        public int RingIndex { get; }

        public RingPassedEvent(int ringIndex)
        {
            RingIndex = ringIndex;
        }
    }
}
