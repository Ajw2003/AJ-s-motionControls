using UnityEngine;

namespace Game
{
    /// <summary>
    /// Every number behind the two things a player reads while aiming: what colour the arc
    /// is, and how the ghost stone answers a wind-up. One asset, edited in the inspector,
    /// no recompile.
    /// </summary>
    /// <remarks>
    /// A ScriptableObject at Resources/AimFeedbackTuning for exactly the reason TrailTuning
    /// is one. AimPreview is built by Bootstrap with AddComponent, so anything held as a
    /// [SerializeField] on it sits at its C# field initializer forever — AimPreview.Awake
    /// already carries the note about having been caught by that once, when "the whole game
    /// ran on C# field initializers and the numbers were only reachable by recompiling."
    /// These are look numbers, which is precisely the kind that get tuned twenty times in an
    /// evening, so they are the last ones that should need a compile.
    ///
    /// Nothing in here is read by the simulation. The arc's colour and the ghost's size are
    /// downstream of SkipPhysics.Predict and feed back into nothing — the same
    /// presentation-only guarantee RockTrail states, and it matters here for the same
    /// reason: the preview runs the solver every frame for a throw that never happened.
    ///
    /// Skip counts are stored as ABSOLUTE counts rather than fractions of anything, and that
    /// is not an oversight of the fractions rule TrailTuning documents. That rule exists
    /// because a threshold in metres-per-second silently stops meaning anything when
    /// SkipConstants.ReferenceSpeed is retuned. A skip count has no such scale behind it —
    /// six skips is six skips at any launch speed, and it is the number a player counts
    /// out loud.
    /// </remarks>
    [CreateAssetMenu(menuName = "RockSkipping/Aim Feedback Tuning", fileName = "AimFeedbackTuning")]
    public class AimFeedbackTuning : ScriptableObject
    {
        [Header("Arc colour")]
        [Tooltip("Tint the aim arc by how many skips the solver predicts from the current " +
                 "angle. Off leaves the arc at PoorColour and it reads as a plain guide.")]
        public bool ColourByPredictedSkips = true;

        [Tooltip("Predicted skips at or below which the arc is fully PoorColour.\n\n" +
                 "The bottom of the ramp, not the bottom of the game — a throw that sinks " +
                 "on contact still lands here, it just cannot go any redder.")]
        [Range(0, 20)] public int PoorSkips = 2;

        [Tooltip("Predicted skips at or above which the arc is fully GoodColour. Everything " +
                 "between this and PoorSkips is the gradient the player is actually reading.")]
        [Range(0, 40)] public int GoodSkips = 9;

        [Tooltip("A dud angle.")]
        public Color PoorColour = new(0.95f, 0.28f, 0.24f);

        [Tooltip("Halfway. The ramp passes through this on the way from poor to good, so it " +
                 "goes red to amber to green rather than red straight to green — a two-stop " +
                 "blend crosses a muddy brown at its midpoint, which reads as a third state " +
                 "rather than as the middle of one.")]
        public Color FairColour = new(0.98f, 0.72f, 0.20f);

        [Tooltip("The angle you want.")]
        public Color GoodColour = new(0.35f, 0.88f, 0.42f);

        [Tooltip("Alpha at the far end of the arc. The arc fades along its length so it " +
                 "reads as a prediction getting less certain, not as a drawn path.")]
        [Range(0f, 1f)] public float TailAlpha = 0.15f;

        [Tooltip("How fast the colour chases the predicted skip count, per second.\n\n" +
                 "Smoothed rather than applied raw because PredictedSkips is an INTEGER off " +
                 "a live phone. A hand holding near a threshold flips it between two values " +
                 "several times a second, and an un-smoothed tint strobes between two hues " +
                 "on a stone that is not moving. Slower than the attitude smoothing on " +
                 "purpose: the ghost should feel instant, the verdict should not twitch.")]
        [Range(0.5f, 40f)] public float ColourSmoothing = 7f;

        [Header("Ghost charge")]
        [Tooltip("Let the ghost stone swell and glow with how hard the phone is being swung. " +
                 "Off leaves it at its authored size and colour.")]
        public bool ChargeGlow = true;

        [Tooltip("Scale multiplier at rest, on top of the prefab's own scale.")]
        [Range(0.5f, 2f)] public float IdleScale = 1f;

        [Tooltip("Scale multiplier at a full-power wind-up. Kept close to Idle — the ghost " +
                 "still has to read as the size of the stone that is about to be thrown.")]
        [Range(0.5f, 2f)] public float ChargedScale = 1.16f;

        [Tooltip("Base colour at rest. Matches the live stone's grey so the ghost reads as " +
                 "the same object until it is charged.")]
        public Color IdleColour = new(0.28f, 0.27f, 0.27f);

        [Tooltip("Base colour at full charge.")]
        public Color ChargedColour = new(0.62f, 0.72f, 0.80f);

        [Tooltip("Emissive colour added at full charge, scaled by charge.\n\n" +
                 "Emission on top of base colour rather than instead of it, because base " +
                 "colour alone tops out at white and cannot get brighter than the lighting " +
                 "allows. Emission is also the half that would bloom if a Global Volume is " +
                 "ever added (Plan-SfxVfx.md section 6.11), and it looks correct without one.")]
        [ColorUsage(false, true)] public Color ChargedEmission = new(0.45f, 0.85f, 1f);

        [Tooltip("Wobbles per second in the charge pulse. Zero holds a steady glow.")]
        [Range(0f, 12f)] public float PulseHz = 3.2f;

        [Tooltip("How much of the scale the pulse swings, at full charge. Scaled by charge, " +
                 "so a stone at rest is perfectly still — a ghost that breathes while nobody " +
                 "is winding up reads as an idle animation rather than as feedback.")]
        [Range(0f, 0.5f)] public float PulseAmount = 0.045f;

        /// <summary>
        /// The 0..1 quality a predicted skip count sits at on the ramp between
        /// <see cref="PoorSkips"/> and <see cref="GoodSkips"/>.
        /// </summary>
        public float QualityOf(int predictedSkips)
        {
            // Ordered rather than trusted, because the two are independent inspector fields
            // and Good below Poor is a normal typo. InverseLerp answers a reversed range
            // with a flat 0, which pins the arc red forever and reads as the feature being
            // broken rather than as one number being the wrong way round.
            var poor = Mathf.Min(PoorSkips, GoodSkips);
            var good = Mathf.Max(PoorSkips, GoodSkips);

            if (good - poor <= 0) return 1f;
            return Mathf.InverseLerp(poor, good, predictedSkips);
        }

        /// <summary>Poor through fair to good, at a 0..1 quality.</summary>
        public Color ColourAt(float quality)
        {
            if (!ColourByPredictedSkips) return PoorColour;

            return quality < 0.5f
                ? Color.Lerp(PoorColour, FairColour, quality * 2f)
                : Color.Lerp(FairColour, GoodColour, (quality - 0.5f) * 2f);
        }

        /// <summary>
        /// Last-resort fallback so the preview still draws if the asset is missing. The
        /// authored asset at Resources/AimFeedbackTuning is what should normally load — see
        /// AimPreview.Awake. Reaching this means aim feedback is back to being a recompile,
        /// so it warns rather than silently living with it, the same way SkipTuning does.
        /// </summary>
        public static AimFeedbackTuning CreateDefault()
        {
            var tuning = CreateInstance<AimFeedbackTuning>();
            tuning.name = "AimFeedbackTuning (runtime default)";
            return tuning;
        }
    }
}
