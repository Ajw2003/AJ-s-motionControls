using UnityEngine;

namespace Game
{
    /// <summary>
    /// One rock's physical body: mass, radius and thickness, in SI units, plus its display
    /// name. Nothing here is a multiplier and nothing here is unitless.
    /// </summary>
    /// <remarks>
    /// This used to be three bounded multipliers — `SpinMultiplier`, `VerticalMultiplier`,
    /// `HorizontalMultiplier` — and before that 25 fields that fought each other. Both
    /// versions had the same problem: they scaled the OUTPUT of a bounce, so there was no
    /// external fact they could be checked against and no way for a rock to be wrong, only
    /// for it to feel wrong.
    ///
    /// The solver now integrates real fluid forces over the stone's own geometry (see
    /// <see cref="DiscContact"/>), so what it needs from a rock is what the rock physically
    /// IS. A heavier stone needs more lift to bounce; a wider one has more area to get it
    /// with and a longer moment arm to be tipped by; a thicker one resists tumbling slightly
    /// more. All three of those are consequences now, not authored behaviours.
    ///
    /// **These are real quantities with right answers.** A skipping stone is roughly 5–8 cm
    /// across and 50–250 g, and a rock authored outside that is not "tuned aggressively", it
    /// is wrong — the catalog validator should say so. The reference values here are
    /// Bocquet's standard stone ([B] §III, see <see cref="ReferenceStone"/>), which is the
    /// one stone in the project with a published right answer to be checked against.
    /// </remarks>
    [CreateAssetMenu(menuName = "RockSkipping/Skip Tuning", fileName = "SkipTuning")]
    public class SkipTuning : ScriptableObject
    {
        [Header("Body")]

        [Tooltip("Stone mass, kilograms.\n\n" +
                 "A real skipping stone is 50-250 g. Mass sets how much lift is needed to " +
                 "bounce at all: the Lift Condition is a contest between the pressure the " +
                 "face can generate and this. Heavier sinks sooner, and there is no " +
                 "compensating benefit hidden anywhere - it is not a difficulty dial.")]
        [Range(0.02f, 0.5f)] public float Mass = ReferenceStone.Mass;

        [Tooltip("Stone radius, metres.\n\n" +
                 "A real skipping stone is 2.5-4 cm in radius (5-8 cm across). This is the " +
                 "most consequential number on the rock: wetted area goes as radius squared, " +
                 "so lift does too, but so does the moment arm that tips the stone over, and " +
                 "the spin needed to stay stable falls with it.")]
        [Range(0.015f, 0.08f)] public float Radius = ReferenceStone.Radius;

        [Tooltip("Stone thickness, metres.\n\n" +
                 "Barely matters to the bounce and is not a feel dial. It enters only the " +
                 "transverse moment of inertia (one twelfth of mass times thickness " +
                 "squared), where the radius term dominates it. Present so the inertia is " +
                 "the real one rather than an infinitely thin disc's.")]
        [Range(0.002f, 0.03f)] public float Thickness = ReferenceStone.Thickness;

        [Header("Presentation")]
        [Tooltip("Shown on the phones as the rock currently in play. A label, not a knob.")]
        public string DisplayName = "Pebble";

        /// <summary>
        /// This stone's principal moments of inertia, from its own body. Axial is what the
        /// player's spin acts on; transverse is what the water tips.
        /// </summary>
        public RigidDisc.Inertia Inertia => RigidDisc.ComputeInertia(Mass, Radius, Thickness);

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
