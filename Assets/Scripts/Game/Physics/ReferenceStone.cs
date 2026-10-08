namespace Game
{
    // Bocquet's standard stone; thickness is an engineering assumption (R/10), not a published number,
    // and this is not a SkipTuning. doc-ref b5a9 docs/4-systems/SkipPhysics.md
    public static class ReferenceStone
    {
        /// <summary>kg — [B], Findings §3.</summary>
        public const float Mass = 0.1f;

        /// <summary>m — [B]'s "a = 0.1 m diameter", Findings §3.</summary>
        public const float Radius = 0.05f;

        /// <summary>
        /// m — NOT published (see class remarks). R/10, a plausible thin flat stone.
        /// </summary>
        public const float Thickness = Radius / 10f;

        /// <summary>`C_l` — [B], Findings §3: "C_l ≈ C_f ≈ 1".</summary>
        public const float LiftCoefficient = 1f;

        // C_f is zero on purpose: with squared-normal lift, friction above about 0.1 sinks every throw,
        // so do not raise it. doc-ref d69f docs/4-systems/SkipPhysics.md
        public const float DragCoefficient = 0f;

        /// <summary>kg/m³ — [B] Eq. (1) / Findings §1.1: fresh water.</summary>
        public const float WaterDensity = 1000f;

        /// <summary>m/s² — standard gravity; [B] uses this implicitly throughout.</summary>
        public const float Gravity = 9.81f;

        /// <summary>
        /// deg — [B]'s own worked tilt for the standard stone, Findings §3 ("θ ≈ 10°").
        /// NOT the magic angle (R1's ≈20° is a RESULT the harness measures, never an
        /// input) — this is the incidental tilt Bocquet's own closed-form examples use.
        /// </summary>
        public const float WorkedTiltDeg = 10f;

        public static RigidDisc.Inertia Inertia => RigidDisc.ComputeInertia(Mass, Radius, Thickness);
    }
}
