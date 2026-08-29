using UnityEngine;

namespace Game
{
    /// <summary>
    /// One rock's character: exactly three bounded multipliers plus its display name.
    /// </summary>
    /// <remarks>
    /// Used to be 25 fields, most of them fighting each other, and the throw's own power
    /// barely moved the result next to a one-point tuning edit — see
    /// docs/plans/Plan-ThrowDrivenSkipModel.md for the numbers. The model now derives energy
    /// loss from bounce geometry (incidence, face-vs-velocity) rather than an authored
    /// retention constant, so there is nothing left here to author except how strongly THIS
    /// rock expresses that geometry: how much it spins, how high it hops, how far it carries.
    ///
    /// Everything else — gravity, water height, the forgiveness bands, the speed scale — is
    /// global, on <see cref="SkipConstants"/>, because it answers "how does this game feel"
    /// rather than "what is this rock," and a rock should not be able to quietly become
    /// easier instead of stronger.
    /// </remarks>
    [CreateAssetMenu(menuName = "RockSkipping/Skip Tuning", fileName = "SkipTuning")]
    public class SkipTuning : ScriptableObject
    {
        [Header("Character")]
        [Tooltip("How much of the phone's measured spin this rock keeps visually. Spin buys " +
                 "forgiveness (see SkipConstants.SpinToleranceScale) and nothing else.")]
        [Range(0.5f, 1.5f)] public float SpinMultiplier = 1f;

        [Tooltip("Hop height / lift. A flatter, thinner stone should sit higher.")]
        [Range(0.5f, 1.5f)] public float VerticalMultiplier = 1f;

        [Tooltip("Forward carry. A bigger, heavier stone should punch through further.")]
        [Range(0.5f, 1.5f)] public float HorizontalMultiplier = 1f;

        [Header("Presentation")]
        [Tooltip("Shown on the phones as the rock currently in play. A label, not a knob.")]
        public string DisplayName = "Pebble";

        /// <summary>
        /// Last-resort fallback so a rock still throws if its asset is missing. Reaching
        /// this means a rock's tuning is back to being a recompile, so it is worth noticing
        /// rather than silently living with.
        /// </summary>
        public static SkipTuning CreateDefault()
        {
            var tuning = CreateInstance<SkipTuning>();
            tuning.name = "SkipTuning (runtime default)";
            return tuning;
        }
    }
}
