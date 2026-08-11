using UnityEngine;

namespace Game
{
    /// <summary>
    /// Global feel constants for skipping. Everything tunable lives here so the game
    /// can be dialled in from the inspector during play without recompiling.
    /// </summary>
    /// <remarks>
    /// Expect tuning to take longer than implementation. These starting values are
    /// educated guesses, not measurements — the ~20 degree optimum is the one number
    /// grounded in real stone-skipping physics.
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

        [Tooltip("Bridges phone space and physics space. The phone reports pitch as a delta " +
                 "from wherever the player gripped, so neutral is 0 — but a real stone wants " +
                 "about 20 degrees. Without this offset a natural, untilted throw is always " +
                 "outside tolerance and always sinks. Set to the stone's optimal angle so " +
                 "'hold naturally' means 'good throw', and tilting is how you trade it away.")]
        public float AttackAngleOffsetDeg = 20f;
        [Tooltip("Speed multiplier at zero timing quality. 1 would make timing irrelevant.")]
        [Range(0.1f, 1f)] public float WorstTimingSpeedScale = 0.6f;

        [Header("Bounce")]
        [Tooltip("Steeper than this and the stone plows in instead of skipping.")]
        public float MaxIncidenceDeg = 45f;
        [Tooltip("Energy kept on a perfectly angled bounce.")]
        [Range(0f, 1f)] public float BestRestitution = 0.92f;
        [Tooltip("Energy kept on a bounce at the edge of tolerance.")]
        [Range(0f, 1f)] public float WorstRestitution = 0.3f;
        [Tooltip("Floor on how steeply a well-angled stone leaves the water. A mirror bounce " +
                 "alone loses height three times faster than it loses speed, so late skips " +
                 "flatten into a slide; this keeps every hop the same shape as it shrinks.")]
        [Range(0f, 40f)] public float SkipExitAngleDeg = 26f;

        [Header("Spin")]
        [Tooltip("Extra angle tolerance per unit of spin — gyroscopic stability.")]
        public float SpinToleranceScale = 0.02f;
        [Tooltip("Ceiling on that bonus, so a wild flick isn't an auto-win.")]
        public float MaxSpinToleranceBonus = 18f;

        [Tooltip("Yaw rate a stone in flight falls back to once its spin is spent. Never " +
                 "zero — a stone that has stopped turning reads as frozen rather than tired. " +
                 "The aim ghost deliberately does not use this floor: a phone held still " +
                 "must give a ghost held still, or the spin stops being feedback.")]
        public float MinVisualSpinDegPerSec = 45f;

        [Tooltip("Yaw rate at full spin. Kept modest on purpose: the stone is a flat plate, " +
                 "so a fast yaw aliases into a strobe rather than reading as speed.")]
        public float MaxVisualSpinDegPerSec = 900f;

        /// <summary>
        /// The spin at which the gyroscopic tolerance bonus saturates. Past it, more spin
        /// buys no more stability, so it must not look any faster either. Derived rather
        /// than hardcoded so retuning the two values above cannot drift the visual off it.
        /// </summary>
        public float SpinCap => SpinToleranceScale > 0.0001f
            ? MaxSpinToleranceBonus / SpinToleranceScale
            : 1f;

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
            Mathf.Lerp(atRest, MaxVisualSpinDegPerSec, Mathf.Clamp01(Mathf.Abs(spin) / SpinCap));

        [Header("Simulation")]
        public float FixedStep = 0.02f;
        [Tooltip("Safety cap on prediction length.")]
        public int MaxPredictSteps = 500;

        /// <summary>
        /// Runtime fallback so physics and the preview work before anyone authors an
        /// asset. Create a real SkipTuning asset once tuning starts in earnest.
        /// </summary>
        public static SkipTuning CreateDefault()
        {
            var tuning = CreateInstance<SkipTuning>();
            tuning.name = "SkipTuning (runtime default)";
            return tuning;
        }
    }
}
