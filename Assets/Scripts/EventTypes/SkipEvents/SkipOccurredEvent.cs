namespace EventTypes.SkipEvents
{
    using EventSystems;
    using UnityEngine;

    /// <summary>Published each time the rock successfully bounces off the water.</summary>
    public class SkipOccurredEvent : IEvent
    {
        public int SkipIndex { get; }
        public float Speed { get; }

        /// <summary>Where it touched down, already clamped to the water plane.</summary>
        public Vector3 Position { get; }

        public SkipOccurredEvent(int skipIndex, float speed, Vector3 position)
        {
            SkipIndex = skipIndex;
            Speed = speed;
            Position = position;
        }
    }
}
