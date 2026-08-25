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
        /// Throw strength, 0..1 — the same number the phone sent.
        /// </summary>
        /// <remarks>
        /// Recovered from the launch speed rather than threaded down from the network
        /// message, and it is exact rather than approximate. CreateLaunch builds the
        /// velocity as heading * (speed * cos(pitch)) + up * (speed * sin(pitch)) with a
        /// unit horizontal heading, so its magnitude is the speed it started from with no
        /// error, and that speed is Lerp(MinSpeed, MaxSpeed, power). Inverting the lerp
        /// returns the original power.
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

        public RockLaunchedEvent(Vector3 origin, float power, int predictedSkips)
        {
            Origin = origin;
            Power = power;
            PredictedSkips = predictedSkips;
        }
    }
}
