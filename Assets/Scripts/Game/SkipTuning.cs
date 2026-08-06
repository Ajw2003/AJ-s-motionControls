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
        public float MinSpeed = 6f;
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
