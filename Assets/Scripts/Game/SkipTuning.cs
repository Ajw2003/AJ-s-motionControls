using UnityEngine;

namespace Game
{
    /// <summary>
    /// Every number that shapes a throw. One asset, one place to edit, and no field
    /// whose value silently depends on another.
    /// </summary>
    /// <remarks>
    /// This was 25 fields. Most of them existed to compensate for each other, and
    /// several actively fought the thing the game is supposed to reward: a hard throw
    /// going further. The rule now is that <b>power is the only driver of distance</b> —
    /// it sets the launch speed, how much speed survives each bounce, and how high the
    /// hops are. Nothing else scales any of those.
    ///
    /// Two things that used to live here are deliberately gone:
    ///
    /// <b>Timing.</b> Launch speed was multiplied by up to 0.4 depending on how quickly
    /// the phone stopped after its acceleration peak. But a hard swing takes longer to
    /// stop than a gentle one, so the penalty landed hardest on exactly the throws it
    /// should have rewarded. The gesture already fires on the stop; a second hidden
    /// multiplier on top of it only made effort and result disagree.
    ///
    /// <b>Spin.</b> Wrist spin used to buy angle tolerance and per-bounce speed
    /// retention, which meant distance had two drivers and neither was legible. Spin is
    /// now purely visual: the stone turns at the rate the phone measured, and that is
    /// all it does.
    ///
    /// Nothing in here is derived from anything else in here. That rule is load-bearing:
    /// an earlier version computed the spin normalizer from two gameplay constants, so
    /// retuning how forgiving spin was silently rescaled how fast every stone appeared to
    /// turn. If a value needs to be expressed in terms of another, prefer stating both
    /// and documenting the relationship over deriving one and hiding it.
    /// </remarks>
    [CreateAssetMenu(menuName = "RockSkipping/Skip Tuning", fileName = "SkipTuning")]
    public class SkipTuning : ScriptableObject
    {
        [Header("World")]
        public float Gravity = 9.81f;
        public float WaterY = 0f;

        [Header("Launch")]
        [Tooltip("Speed at power = 0 — the softest throw that still counts as one.")]
        public float MinSpeed = 5f;
        [Tooltip("Speed at power = 1 — a full-effort throw.")]
        public float MaxSpeed = 40f;
        [Tooltip("Upward angle of the toss itself, separate from the stone's face angle.")]
        public float LaunchPitchDeg = 11f;

        [Header("Angle")]
        [Tooltip("The face angle the PHONE reports that skips best, in degrees above " +
                 "horizontal. 0 means a phone held exactly as it was at the tap is already a " +
                 "perfect throw.\n\n" +
                 "This is the forgiveness offset and it is deliberately invisible — the stone " +
                 "is always drawn at the angle the phone is really held at. Tune it negative " +
                 "if the wrist consistently pitches over before the stop sample is taken; the " +
                 "debug HUD prints the face angle of every throw so that bias can be read off " +
                 "rather than guessed.")]
        public float BestFaceAngleDeg = 0f;

        [Tooltip("How far from BestFaceAngleDeg the face can be before the bounce is " +
                 "worthless. At the edge the stone keeps only Restitution and gets no lift, so " +
                 "it skids and dies over the next hop or two rather than vanishing on one " +
                 "degree of error.\n\n" +
                 "Wide on purpose. The throw fires the instant the phone stops, which is " +
                 "exactly when the wrist has pitched over, so the release angle is " +
                 "systematically off-neutral and this window has to absorb that.")]
        public float ToleranceDeg = 30f;

        [Header("Run length")]
        [Tooltip("Minimum fraction of horizontal speed kept per bounce.")]
        [Range(0f, 1f)] public float MinRetention = 0.75f;

        [Tooltip("Maximum fraction of horizontal speed kept per bounce.")]
        [Range(0f, 1f)] public float MaxRetention = 0.99f;

        [Tooltip("Optimal angle for skipping.")]
        public float OptimalAngle = 15f;

        [Tooltip("Below this speed the stone stops skipping and sinks.")]
        public float MinSkipSpeed = 4f;

        [Header("Bounce shape")]
        [Tooltip("How steeply the stone leaves the water once the run has settled.\n\n" +
                 "Shallow, and that is not a stylistic choice — hop LENGTH is " +
                 "(2 tan(angle) / g) * speed², so the angle sets the whole shape of a run. " +
                 "A real skipping stone exits at 2-6 degrees. At 33 degrees a 26 m/s stone's " +
                 "first hop was 59 m long and 8 m high, which reads as mortar fire rather " +
                 "than skipping, and it was the other half of 'rocks barely skip'.\n\n" +
                 "Applied after the speed loss, so this is the real exit angle. Applying it " +
                 "before made the true angle atan(tan(x) / speedKept), and it moved silently " +
                 "whenever speedKept did.")]
        [Range(0f, 60f)] public float SkipExitAngleDeg = 5f;

        [Tooltip("Exit angle of the very first bounce — the pop at the start of a throw.\n\n" +
                 "Above SkipExitAngleDeg so a throw opens with a hop you can see from the " +
                 "couch and then settles into a long shrinking run. Purely presentational: " +
                 "it costs nothing, because the speed loss per bounce is unchanged.")]
        [Range(0f, 60f)] public float OpeningHopAngleDeg = 11f;

        [Tooltip("How many skips the opening pop fades away over.")]
        [Range(1f, 10f)] public float OpeningHopSkips = 3f;

        [Tooltip("Energy kept by a bounce with no lift — a badly angled stone slapping off the " +
                 "surface. Low, but not zero: at zero the stone grinds along the waterline and " +
                 "racks up dozens of meaningless skips before it slows down.")]
        [Range(0f, 1f)] public float Restitution = 0.4f;

        [Header("Simulation")]
        public float FixedStep = 0.02f;

        [Tooltip("How far ahead the aim preview simulates. Was a raw step count, which meant " +
                 "the real horizon moved whenever FixedStep did — and at 500 steps it was 10s, " +
                 "shorter than a full-power throw, so the arc silently truncated on exactly " +
                 "the throws players try hardest for.")]
        public float PredictSeconds = 22f;

        public int MaxPredictSteps => Mathf.Max(1, Mathf.CeilToInt(PredictSeconds / Mathf.Max(FixedStep, 0.0001f)));

        [Header("Presentation")]
        [Tooltip("Shown on the phones as the rock currently in play. A label, not a knob.")]
        public string DisplayName = "Pebble";

        /// <summary>
        /// Last-resort fallback so physics and the preview still work if the asset is
        /// missing. The authored asset at Resources/SkipTuning is what should normally
        /// load — see AimPreview.Awake. Reaching this means tuning is back to being a
        /// recompile, so it is worth noticing rather than silently living with.
        /// </summary>
        public static SkipTuning CreateDefault()
        {
            var tuning = CreateInstance<SkipTuning>();
            tuning.name = "SkipTuning (runtime default)";
            return tuning;
        }
    }
}
