namespace EventTypes.SkipEvents
{
    using EventSystems;
    using UnityEngine;

    /// <summary>Published the moment a real rock leaves the thrower's hand.</summary>
    /// <remarks>
    /// From RockController.Launch, never from SkipPhysics.CreateLaunch. The aim preview
    /// calls CreateLaunch every frame to draw its arc, for a throw that has not happened
    /// and may never happen — an event published there would fire continuously while a
    /// player merely aims. RockController is documented as the only place skip events are
    /// published and this keeps to it.
    /// </remarks>
    public class RockLaunchedEvent : IEvent
    {
        /// <summary>Where it was released.</summary>
        public Vector3 Origin { get; }

        // Raw swing speed, m/s — recovered from the launch velocity's magnitude divided by SkipConstants.SpeedScale,... doc-ref ca68 docs/4-systems/EventSystem.md
        public float SwingSpeed { get; }

        /// <summary>
        /// Throw strength as a fraction of SkipConstants.ReferenceSpeed. Not clamped to
        /// 0..1 — a very hard swing can exceed the reference. Kept alongside SwingSpeed
        /// because presentation code (SfxDirector) wants a 0..1-ish intensity, not a raw
        /// speed with no natural scale.
        /// </summary>
        public float Power { get; }

        /// <summary>
        /// How many skips the solver expects this throw to get.
        /// </summary>
        /// <summary>Face angle at launch, degrees above horizontal — PhoneAttitude.FaceAngleDeg.</summary>
        public float FaceAngleDeg { get; }

        /// <summary>Bank at launch, degrees — PhoneAttitude.RollDeg.</summary>
        public float RollDeg { get; }

        /// <summary>
        /// Wrist spin at launch, deg/s, signed, after SkipTuning.SpinMultiplier — the same
        /// rate the stone is actually drawn turning at.
        /// </summary>
        public float SpinRate { get; }

        public RockLaunchedEvent(
            Vector3 origin, float swingSpeed, float power,
            float faceAngleDeg, float rollDeg, float spinRate)
        {
            Origin = origin;
            SwingSpeed = swingSpeed;
            Power = power;
            FaceAngleDeg = faceAngleDeg;
            RollDeg = rollDeg;
            SpinRate = spinRate;
        }
    }
}
