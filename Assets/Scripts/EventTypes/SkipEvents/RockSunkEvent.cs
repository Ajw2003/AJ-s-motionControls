namespace EventTypes.SkipEvents
{
    using EventSystems;
    using UnityEngine;

    /// <summary>Published the moment a rock stops skipping and goes under.</summary>
    /// <remarks>
    /// Distinct from ThrowCompletedEvent on purpose, and the difference is not cosmetic.
    /// ThrowCompletedEvent is about the TURN — who threw, how well, what the match should
    /// do next — and every subscriber to it changes match state. This one is about the
    /// STONE, at a position, at a speed, and exists so presentation can react to a splash
    /// without subscribing to something that also ends turns.
    ///
    /// They fire in the same frame, this one first: RockController publishes it on the
    /// sinking step, before calling Finish. So a splash is already underway by the time the
    /// score is banked and the turn rotates. Plan-SfxVfx.md section 7.1 has the pile-up
    /// this creates and argues the presentation layer should schedule rather than react.
    /// </remarks>
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
        /// branches that can set Sank, so both reach here with a real number rather than a
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
