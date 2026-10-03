using UnityEngine;

namespace Game
{
    // SkipTuning is three bounded multipliers; everything global lives on SkipConstants, and all rocks
    // ship at 1.0 on purpose. doc-ref 36fa docs/4-systems/SkipPhysics.md
    [CreateAssetMenu(menuName = "RockSkipping/Skip Tuning", fileName = "SkipTuning")]
    public class SkipTuning : ScriptableObject
    {
        [Header("Character")]
        [Tooltip("How well this rock turns the player's wrist spin into gyroscopic stability.\n\n" +
                 "Stability is what holds the stone's attack angle through each impact. What it " +
                 "fails to hold accumulates as tumble (SkipConstants.WanderPerBounce), which is " +
                 "charged against the pitch and roll errors — so a spinny rock keeps its bounce " +
                 "quality, and its skips, further into a run. Spin also widens every forgiveness " +
                 "band slightly (SkipConstants.SpinToleranceScale).\n\n" +
                 "Above 1 = holds a spin better than average. 1.0 is neutral.")]
        [Range(0.8f, 1.2f)] public float SpinMultiplier = 1f;

        [Tooltip("This rock's share of the energy surviving a bounce that gets sent UPWARD.\n\n" +
                 "Forward and upward are a split of one fixed budget (see SkipConstants.LiftShare), " +
                 "so this trades carry for arc rather than adding anything: higher hops, fewer and " +
                 "longer skips, same total energy. A flatter, thinner stone should sit higher.\n\n" +
                 "Above 1 = hops higher. 1.0 is neutral.")]
        [Range(0.7f, 1.3f)] public float VerticalMultiplier = 1f;

        [Tooltip("Scales how much energy THIS rock loses per bounce (SkipConstants.EnergyLoss).\n\n" +
                 "Below 1 = slipperier, keeps more speed, skips further. Above 1 = draggier.\n\n" +
                 "Deliberately the narrowest range of the three: skip count goes as " +
                 "1 / ln(speed kept), which is hyperbolic, so a wide range here would let the " +
                 "rock outweigh how hard the player actually threw — the exact failure the " +
                 "throw-driven model was written to remove. 1.0 is neutral.")]
        [Range(0.95f, 1.05f)] public float HorizontalMultiplier = 1f;

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
