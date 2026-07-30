namespace EventTypes.SkipEvents
{
    using EventSystems;

    /// <summary>Published each time the rock successfully bounces off the water.</summary>
    public class SkipOccurredEvent : IEvent
    {
        public int SkipIndex { get; }
        public float Speed { get; }

        public SkipOccurredEvent(int skipIndex, float speed)
        {
            SkipIndex = skipIndex;
            Speed = speed;
        }
    }
}
