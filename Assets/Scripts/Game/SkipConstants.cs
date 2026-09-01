using UnityEngine;

namespace Game
{
    /// <summary>
    /// Every number that is NOT a property of one rock: the simulation itself, the one
    /// unit conversion the model cannot do without, and the three forgiveness bands that
    /// are the only softening left anywhere in the pipeline.
    /// </summary>
    /// <remarks>
    /// Split out of SkipTuning so a rock's file cannot grow global knobs back in — see
    /// docs/plans/Plan-ThrowDrivenSkipModel.md. Loaded once from Resources/SkipConstants,
    /// same pattern as SkipTuning and RockCatalog.
    /// </remarks>
    [CreateAssetMenu(menuName = "RockSkipping/Skip Constants", fileName = "SkipConstants")]
    public class SkipConstants : ScriptableObject
    {
        [Header("World")]
        public float Gravity = 9.81f;
        public float WaterY = 0f;

        [Header("Launch")]
        [Tooltip("Converts the phone's raw swing speed (m/s at the wrist) into a stone's " +
                 "launch speed. The one constant the model cannot remove — everything else " +
                 "is either raw phone signal or falls out of geometry.")]
        public float SpeedScale = 1f;

        [Header("Bounce")]
        [Tooltip("Fraction of kinetic energy the water takes on every bounce — THE one dial " +
                 "for how many skips a good throw gets.\n\n" +
                 "The bounce takes this loss first, then splits what survives between forward " +
                 "and upward motion, so outgoing speed is always less than incoming speed and " +
                 "a run can never run away.\n\n" +
                 "Approximate skips for a clean throw:\n" +
                 "    skips ~= ln(MinSkipSpeed / launchSpeed) / (0.5 * ln(1 - EnergyLoss))\n\n" +
                 "That is an upper bound — a wobbly (low-spin) throw ends sooner, because " +
                 "tumble ends a run before the energy budget does.\n\n" +
                 "Measured against a 30 m/s swing at the optimal angle: 0.17 ~= 29 skips, " +
                 "0.20 ~= 25 (the default), 0.23 ~= 21, 0.26 ~= 18. Raise it for shorter runs, " +
                 "lower it for longer.")]
        [Range(0.02f, 0.6f)] public float EnergyLoss = 0.20f;

        [Tooltip("Share of the energy surviving a bounce that gets redirected upward into the " +
                 "next hop, before quality/roll/rock scaling. Sets the shape of the arc, not " +
                 "the length of the run — it is a split of a fixed budget, so raising it buys " +
                 "higher hops at the cost of forward carry. ~0.03 is a 10-degree exit angle.")]
        [Range(0.005f, 0.2f)] public float LiftShare = 0.030f;

        [Tooltip("Floor under the per-bounce energy loss, whatever quality and the rock's " +
                 "multiplier work out to. This is the guarantee that every run ends: energy " +
                 "strictly falls every bounce, so the stone always reaches MinSkipSpeed " +
                 "eventually. Do not set to 0.")]
        [Range(0.01f, 0.3f)] public float MinLoss = 0.05f;

        [Header("Gyroscopic stability")]
        [Tooltip("Wrist spin, deg/s, at which the stone is fully stabilised. Spin holds a real " +
                 "stone's attack angle through each impact — below this the stone starts to " +
                 "tumble (see WanderPerBounce) and the run dies early. Measured phone throws " +
                 "sit around 200-450 deg/s.")]
        public float SpinForStability = 400f;

        [Tooltip("Degrees of attitude drift added per bounce when the stone has NO spin, scaled " +
                 "down to zero at SpinForStability. Drift is added to the pitch and roll errors, " +
                 "so quality decays over a run and an unspun stone tumbles out in a couple of " +
                 "bounces — which is exactly what an unspun stone does.")]
        public float WanderPerBounce = 3f;

        [Tooltip("Fraction of its spin the stone keeps per bounce. Below 1 so even a " +
                 "well-spun stone eventually loses stability and starts to tumble.\n\n" +
                 "Lower than ~0.98 and this, rather than EnergyLoss, becomes what ends every " +
                 "run: spin bleeds out, tumble caps the skip count, and throwing twice as hard " +
                 "stops buying more skips. Keep it high enough that the energy budget is the " +
                 "thing that runs out first, or EnergyLoss silently stops being the dial.")]
        [Range(0.5f, 1f)] public float SpinRetention = 0.99f;

        [Header("Forgiveness")]
        [Tooltip("Nose up/down (PhoneAttitude.FaceAngleDeg). Error inside this band counts " +
                 "as zero; only the excess outside it costs quality. 0 disables forgiveness " +
                 "on this axis entirely, and spin cannot turn it back on.")]
        public float ForgivenessPitchDeg = 12f;

        [Tooltip("Bank (PhoneAttitude.RollDeg). Same rule as pitch.")]
        public float ForgivenessRollDeg = 15f;

        [Tooltip("Aim left/right (PhoneAttitude.HeadingDeg, read as sideslip against the " +
                 "stone's own heading). Same rule as pitch.")]
        public float ForgivenessYawDeg = 10f;

        [Tooltip("How much wrist spin widens each forgiveness band, deg per deg/s. Spin buys " +
                 "tolerance and nothing else — it never adds distance on its own.")]
        public float SpinToleranceScale = 0.01f;

        [Header("Presentation")]
        [Tooltip("The speed several presentation systems (water ripples, SFX, the trail) " +
                 "normalise against, since none of them can read a per-rock MaxSpeed anymore " +
                 "— there isn't one. A representative hard-throw speed, not a hard limit.")]
        public float ReferenceSpeed = 30f;

        [Header("Simulation")]
        public float FixedStep = 0.02f;

        [Tooltip("How far ahead the aim preview simulates, in seconds.")]
        public float PredictSeconds = 22f;

        public int MaxPredictSteps => Mathf.Max(1, Mathf.CeilToInt(PredictSeconds / Mathf.Max(FixedStep, 0.0001f)));

        /// <summary>
        /// Last-resort fallback so physics and the preview still work if the asset is
        /// missing. Reaching this means tuning is back to being a recompile.
        /// </summary>
        public static SkipConstants CreateDefault()
        {
            var constants = CreateInstance<SkipConstants>();
            constants.name = "SkipConstants (runtime default)";
            return constants;
        }

        /// <summary>The authored asset, or a runtime default with a warning.</summary>
        public static SkipConstants LoadOrWarn()
        {
            var constants = Resources.Load<SkipConstants>("SkipConstants");
            if (constants != null) return constants;

            Debug.LogWarning("[SkipConstants] No Resources/SkipConstants asset — running on " +
                              "code defaults, which cannot be tuned without a recompile.");
            return CreateDefault();
        }
    }
}
