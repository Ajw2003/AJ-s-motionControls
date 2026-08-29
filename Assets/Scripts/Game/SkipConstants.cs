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
        [Tooltip("How lossy the water is, 0..1. The only authored knob behind how much " +
                 "speed a bounce keeps — the rest falls out of incidence and face-vs-velocity " +
                 "geometry inside SkipPhysics.Step.")]
        [Range(0f, 1f)] public float WaterLossCoefficient = 0.18f;

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
