namespace EventTypes.SkipEvents
{
    using EventSystems;
    using UnityEngine;

    // Published the moment a rock stops skipping and goes under. doc-ref 0d39 docs/4-systems/EventSystem.md
    public class RockSunkEvent : IEvent
    {
        /// <summary>Where it went under, on the water plane.</summary>
        public Vector3 Position { get; }

        /// <summary>
        /// How fast it hit on the step it sank, in m/s.
        /// </summary>
        /// <remarks>
        /// Valid on a sinking step, which is worth stating because it looks like it might
        /// not be: SkipPhysics.Step assigns result.ImpactSpeed before either of the two
        /// branches that can set HasSunk, so both reach here with a real number rather than a
        /// default. Scale a splash off it.
        /// </remarks>
        public float ImpactSpeed { get; }

        /// <summary>
        /// Skips the run managed before this. Zero means it never skipped at all — the
        /// throw failed outright, which is a different sound and a different splash from a
        /// long run finally running out.
        /// </summary>
        public int SkipCount { get; }

        public RockSunkEvent(Vector3 position, float impactSpeed, int skipCount)
        {
            Position = position;
            ImpactSpeed = impactSpeed;
            SkipCount = skipCount;
        }
    }
}
