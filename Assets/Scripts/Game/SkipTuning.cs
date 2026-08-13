using UnityEngine;

namespace Game
{
    /// <summary>
    /// Every number that shapes a throw. One asset, one place to edit, and no field
    /// whose value silently depends on another.
    /// </summary>
    /// <remarks>
    /// Expect tuning to take longer than implementation. These starting values are
    /// educated guesses, not measurements — the ~20 degree optimum is the one number
    /// grounded in real stone-skipping physics.
    ///
    /// The stone's own properties used to live in a separate RockStats struct that was
    /// hardcoded and unreachable from the inspector, so tuning half the model meant a
    /// recompile. They are folded in here. When per-rock variation lands, a RockDefinition
    /// should roll values that override these — not reintroduce a second editing surface.
    ///
    /// Nothing in here is derived from anything else in here. That rule is load-bearing:
    /// the previous version computed the spin normalizer from two gameplay constants, so
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
        [Tooltip("Speed at power = 0.")]
        public float MinSpeed = 2f;
        [Tooltip("Speed at power = 1.")]
        public float MaxSpeed = 26f;
        [Tooltip("Upward angle of the toss itself, separate from the stone's face angle.")]
        public float LaunchPitchDeg = 8f;
        [Tooltip("Speed multiplier at zero timing quality. 1 would make timing irrelevant.")]
        [Range(0.1f, 1f)] public float WorstTimingSpeedScale = 0.6f;

        [Header("Stone")]
        public string DisplayName = "Pebble";

        [Tooltip("Face angle, in degrees from horizontal, that skips best. ~20 in reality.\n\n" +
                 "Also bridges phone space and physics space: the phone reports pitch as a " +
                 "delta from wherever the player gripped, so neutral is 0, and this is what " +
                 "maps that onto the angle a real stone wants. There used to be a separate " +
                 "AttackAngleOffsetDeg for the second job, but only the difference between " +
                 "the two ever reached the maths, so one of them did nothing.")]
        public float OptimalAngleDeg = 20f;

        [Tooltip("How far from optimal the stone can be and still skip cleanly.")]
        public float BaseToleranceDeg = 15f;

        [Tooltip("How far past tolerance the stone will still skid before it sinks, as a " +
                 "multiple of tolerance.\n\n" +
                 "Bad angles used to sink the stone outright, which put a cliff in the middle " +
                 "of the skill curve: one degree separated a nine-skip throw from zero, and a " +
                 "throw fires on the phone stopping — which is exactly when the wrist has " +
                 "pitched over. Past tolerance the stone now keeps its worst-case bounce and " +
                 "no exit lift, so a mediocre release skids somewhere instead of vanishing, " +
                 "and the player can feel the gradient. 1 restores the old cliff.")]
        [Range(1f, 5f)] public float SinkAngleMultiplier = 2.5f;

        [Tooltip("Horizontal speed retained per bounce with no spin. The main control on total " +
                 "distance — there is no per-step air drag, so this is the only thing slowing " +
                 "the stone down between skips.")]
        public float Drag = 0.75f;

        [Tooltip("Horizontal speed retained per bounce at full spin. Above Drag, so a hard " +
                 "flick genuinely carries further.\n\n" +
                 "Spin used to buy angle tolerance and nothing else, which meant flicking " +
                 "harder could not add a single metre no matter how the rest was tuned. " +
                 "Drag is the right place for it: it is what actually ends a run, and a " +
                 "spinning stone really does hold its line better through a bounce.")]
        public float DragAtFullSpin = 0.86f;

        [Tooltip("Spin retained per bounce. Higher holds the stone stable for longer.")]
        public float SpinDecay = 0.75f;

        [Tooltip("Below this speed the stone stops skipping and sinks.")]
        public float MinSkipSpeed = 2.5f;

        [Header("Bounce")]
        [Tooltip("Steeper than this and the stone plows in instead of skipping.")]
        public float MaxIncidenceDeg = 45f;
        [Tooltip("Energy kept on a perfectly angled bounce.")]
        [Range(0f, 1f)] public float BestRestitution = 0.828f;
        [Tooltip("Energy kept on a bounce at the edge of tolerance.")]
        [Range(0f, 1f)] public float WorstRestitution = 0.27f;

        [Tooltip("Floor on how steeply a well-angled stone leaves the water. A mirror bounce " +
                 "alone loses height three times faster than it loses speed, so late skips " +
                 "flatten into a slide; this keeps every hop the same shape as it shrinks.\n\n" +
                 "This is the real exit angle. It used to be applied before drag, which made " +
                 "the true angle atan(tan(x) / Drag) — 33 degrees when the field said 26, and " +
                 "silently different again whenever Drag moved.")]
        [Range(0f, 60f)] public float SkipExitAngleDeg = 33.03f;

        [Header("Spin")]
        [Tooltip("The spin a solid throw produces, in deg/s. The single normalizer for spin: " +
                 "tolerance bonus, visual rate and drag bonus are all expressed as a fraction " +
                 "of it, so 'what counts as a lot of spin' is stated once and stated here.")]
        public float ReferenceSpinDegPerSec = 900f;

        [Tooltip("Extra angle tolerance at full spin — gyroscopic stability. Reached at " +
                 "ReferenceSpinDegPerSec; more spin than that buys no more stability.")]
        public float MaxSpinToleranceBonus = 18f;

        [Tooltip("Yaw rate a stone in flight falls back to once its spin is spent. Never " +
                 "zero — a stone that has stopped turning reads as frozen rather than tired. " +
                 "The aim ghost deliberately does not use this floor: a phone held still " +
                 "must give a ghost held still, or the spin stops being feedback.")]
        public float MinVisualSpinDegPerSec = 1f;

        [Tooltip("Yaw rate at full spin and full power.")]
        public float MaxVisualSpinDegPerSec = 1200f;

        [Tooltip("Fraction of the visual yaw rate a zero-power throw gets. The stone's " +
                 "apparent spin is scaled by how hard it was thrown, so a hard throw reads " +
                 "as hard rather than only as fast-moving. 1 disables the effect.\n\n" +
                 "Power comes from the phone's measured swing speed, so this is the peak " +
                 "force of the throw reaching the visuals.")]
        [Range(0f, 1f)] public float MinPowerSpinScale = 0.35f;

        /// <summary>
        /// Visual yaw rate, in deg/s, for a given spin and throw power.
        /// </summary>
        /// <remarks>
        /// Watch the ceiling here against the frame rate. The stone's silhouette repeats
        /// every full turn once it carries an asymmetric feature, so at 60fps anything
        /// past 10800 deg/s aliases into a backwards wagon-wheel; staying under about a
        /// quarter of that keeps it reading as rotation rather than judder. A previous
        /// version ran at 9000 against a two-fold symmetric silhouette, whose limit was
        /// half that again — which is what the strobing was.
        /// </remarks>
        public float VisualSpinDegPerSec(float spin, float atRest, float power) =>
            Mathf.Lerp(atRest, MaxVisualSpinDegPerSec, SpinFraction(spin))
            * Mathf.Lerp(MinPowerSpinScale, 1f, Mathf.Clamp01(power));

        [Header("Simulation")]
        public float FixedStep = 0.02f;

        [Tooltip("How far ahead the aim preview simulates. Was a raw step count, which meant " +
                 "the real horizon moved whenever FixedStep did — and at 500 steps it was 10s, " +
                 "shorter than a full-power throw, so the arc silently truncated on exactly " +
                 "the throws players try hardest for.")]
        public float PredictSeconds = 22f;

        public int MaxPredictSteps => Mathf.Max(1, Mathf.CeilToInt(PredictSeconds / Mathf.Max(FixedStep, 0.0001f)));

        /// <summary>Spin as a fraction of a solid throw's, clamped to 0..1.</summary>
        public float SpinFraction(float spin) =>
            Mathf.Clamp01(Mathf.Abs(spin) / Mathf.Max(ReferenceSpinDegPerSec, 0.0001f));

        /// <summary>
        /// Spin to the yaw rate that represents it, in deg/s.
        /// </summary>
        /// <remarks>
        /// Shared by the aim ghost and the live stone on purpose. If the two ever mapped
        /// spin to a visual rate differently, the same throw would turn at one speed in
        /// the preview and another in flight — the preview/live disagreement this codebase
        /// keeps closing everywhere else.
        ///
        /// <paramref name="atRest"/> is the one thing they are allowed to differ on, because
        /// they genuinely differ: see MinVisualSpinDegPerSec.
        /// </remarks>
        public float VisualSpinDegPerSec(float spin, float atRest) =>
            Mathf.Lerp(atRest, MaxVisualSpinDegPerSec, SpinFraction(spin));

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
