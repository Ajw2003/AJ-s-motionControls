using UnityEngine;

namespace Game
{
    /// <summary>
    /// Every number that is NOT a property of one rock: the water, the simulation, the two
    /// unit conversions the model cannot do without, and the three forgiveness bands that
    /// are the only softening left anywhere in the pipeline.
    /// </summary>
    /// <remarks>
    /// Split out of SkipTuning so a rock's file cannot grow global knobs back in. Loaded once
    /// from Resources/SkipConstants, same pattern as SkipTuning and RockCatalog.
    ///
    /// **What changed, and why the old dials are gone.** `EnergyLoss`, `LiftShare`,
    /// `MinLoss`, `SpinRetention`, `WanderPerBounce` and `SpinForStability` all described a
    /// bounce that was scored and then scaled. The solver now integrates real fluid forces
    /// through the water contact (see <see cref="DiscContact"/>), so every one of those is a
    /// consequence rather than a setting:
    ///
    /// - energy loss **is** the work the pressure does against the stone, and is negative by
    ///   construction rather than by a `MinLoss` clamp;
    /// - the split between forward and upward **is** the face normal's direction;
    /// - tumbling **is** the contact torque against the stone's own inertia and spin, so
    ///   there is no `WanderDeg` to invent and no stability threshold to type in.
    ///
    /// What is left divides cleanly in two. The **physical** block has right answers that
    /// can be looked up and got wrong — fresh water is 1000 kg/m³, not a taste. The
    /// **calibration** block converts the phone's units into the stone's, and is the only
    /// place a number is chosen because of how the game plays.
    ///
    /// See docs/plans/real-skip-physics.md and docs/Findings-RealSkipPhysics.md; the
    /// findings document, not this file, is the source of truth for the published values.
    /// </remarks>
    [CreateAssetMenu(menuName = "RockSkipping/Skip Constants", fileName = "SkipConstants")]
    public class SkipConstants : ScriptableObject
    {
        [Header("World")]
        public float Gravity = 9.81f;
        public float WaterY = 0f;

        [Tooltip("Water density, kg/m^3. Fresh water is 1000. This is a physical fact, not " +
                 "a dial - changing it to tune the game changes what 'water' means.")]
        public float WaterDensity = ReferenceStone.WaterDensity;

        [Header("Hydrodynamics")]
        [Tooltip("Lift coefficient C_l in the pressure law p = 0.5 * rho * C_l * (v.n)^2.\n\n" +
                 "Bocquet quotes ~1 for a law in the FULL speed squared. This model uses the " +
                 "Nagahiro form in the NORMAL velocity squared, which is the only one that " +
                 "produces a magic angle (Findings 1.2/1.4), and at skipping angles " +
                 "(v.n)^2 is a small fraction of V^2 - so the equivalent value here is much " +
                 "larger than 1. Around 16 reproduces contact durations of a few " +
                 "milliseconds, which is the published order (R3).")]
        [Range(0.5f, 64f)] public float LiftCoefficient = 16f;

        [Tooltip("Friction coefficient C_f, along the face.\n\n" +
                 "SHIPS AT ZERO, and this is not neglect. Combining Bocquet's V^2 friction " +
                 "with the (v.n)^2 pressure law sinks the stone at every tilt and every " +
                 "speed - measured, Findings 1.3. Above about 0.1 this breaks the model " +
                 "rather than tuning it. The stone still decelerates without it: a tilted " +
                 "face's normal force already points partly backwards.")]
        [Range(0f, 0.1f)] public float DragCoefficient = ReferenceStone.DragCoefficient;

        [Header("Calibration - phone units to stone units")]
        [Tooltip("Converts the phone's raw swing speed (m/s at the wrist) into the stone's " +
                 "launch speed. A harder swing is simply a faster stone - no power curve.\n\n" +
                 "Well below 1, and that is measured rather than taste: the phone's median " +
                 "swing is 26 m/s at the wrist (Findings 6) while real stone skipping happens " +
                 "at 5-12 m/s. Passing the wrist speed straight through threw 30 m/s stones, " +
                 "and since contact torque grows with speed while spin does not, those were " +
                 "flipped over by the first bounce every time.")]
        public float SpeedScale = 0.3f;

        [Tooltip("Converts the phone's raw wrist spin (deg/s) into the stone's spin.\n\n" +
                 "Expect this to be the sensitive one. Spin is what holds the stone's " +
                 "attitude through a contact, and the model currently needs roughly 2.5x " +
                 "more of it than Bocquet's published stability floor to do so (Findings " +
                 "9.9). That factor lives HERE, in the open, rather than inside the solver. " +
                 "If the stability gap is closed later, this is the one number to revisit.")]
        public float SpinScale = 60f;

        [Header("Forgiveness - the only softening in the pipeline")]
        [Tooltip("Half-width of the pitch band, degrees. Inside it the throw is treated as " +
                 "physics-optimal; outside, only the excess reaches the solver. A band at 0 " +
                 "is off.")]
        public float ForgivenessPitchDeg = 12f;

        [Tooltip("Half-width of the roll band, degrees. A band at 0 is off.")]
        public float ForgivenessRollDeg = 15f;

        [Tooltip("Half-width of the yaw band, degrees. A band at 0 is off.")]
        public float ForgivenessYawDeg = 10f;

        [Tooltip("Extra forgiveness per deg/s of measured wrist spin. Spin buys tolerance " +
                 "and nothing else - it never adds distance on its own. A band authored at " +
                 "0 stays off no matter how fast the wrist turned.")]
        public float SpinToleranceScale = 0.01f;

        [Header("Presentation")]
        [Tooltip("The speed several presentation systems (water ripples, SFX, the trail) " +
                 "normalise against, since none of them can read a per-rock MaxSpeed anymore " +
                 "— there isn't one. A representative hard-throw speed, not a hard limit.")]
        public float ReferenceSpeed = 30f;

        [Header("Simulation")]
        public float FixedStep = 0.02f;

        [Tooltip("Substep used while any part of the face is in the water, seconds.\n\n" +
                 "A whole bounce lasts single-digit milliseconds, so the flight step is " +
                 "orders of magnitude too coarse to resolve one. 20 microseconds is well " +
                 "inside convergence - the outcome moves by under one percent across a 200x " +
                 "range of this value (Findings 8.1).")]
        public float ContactStep = 0.00002f;

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
