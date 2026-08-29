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

        /// <summary>
        /// Throw strength, as a fraction of SkipConstants.ReferenceSpeed. Not clamped to
        /// 0..1 — a very hard swing can exceed the reference.
        /// </summary>
        /// <remarks>
        /// Recovered from the launch speed rather than threaded down from the network
        /// message, and it is exact rather than approximate. CreateLaunch builds the
        /// velocity as heading * (speed * cos(pitch)) + up * (speed * sin(pitch)) with a
        /// unit horizontal heading, so its magnitude is the speed it started from with no
        /// error.
        ///
        /// Worth preferring over an extra parameter because power would otherwise have to
        /// be carried through Launch purely to be re-published, and a second copy of a
        /// value is a second thing that can disagree.
        /// </remarks>
        public float Power { get; }

        /// <summary>
        /// How many skips the solver expects this throw to get.
        /// </summary>
        /// <remarks>
        /// A prediction, not a result — the run really can end early, and anything keying
        /// off this must treat it as a hint. Free to carry: Launch already runs Predict to
        /// derive the per-bounce spin decay, so this reads a number that was computed
        /// anyway rather than simulating the throw a second time.
        /// </remarks>
        public int PredictedSkips { get; }

        /// <summary>
        /// The face angle the bounce actually cared about, in degrees.
        /// </summary>
        /// <remarks>
        /// Carried because the offline sweep in Verification/SkipCurve reads every rock at an
        /// ideal face angle and players do not — the gap between the two is the whole reason
        /// the Authored and Derived tuning sets need real throws to choose between them, and
        /// Verification/RockTelemetry cannot record that gap without this number.
        ///
        /// Read off SkipState rather than recomputed from the attitude here: SkipPhysics
        /// already ran the attitude through PhoneAttitude.FaceAngleDeg to build the launch, and
        /// a second conversion at this call site would be a second thing that can disagree —
        /// exactly what PhoneAttitude exists to prevent.
        /// </remarks>
        public float FaceAngleDeg { get; }

        public RockLaunchedEvent(Vector3 origin, float power, int predictedSkips, float faceAngleDeg)
        {
            Origin = origin;
            Power = power;
            PredictedSkips = predictedSkips;
            FaceAngleDeg = faceAngleDeg;
        }
    }
}
