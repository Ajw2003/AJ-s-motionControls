namespace Game
{
    /// <summary>
    /// Bocquet's standard stone — the exact dimensions and coefficients [B]'s closed-form
    /// results (Findings §3) were derived for. Everything R1-R7 in the verification harness
    /// asserts is a published number for THIS stone, so measuring it here is reproducing
    /// the papers' own apparatus, not something merely analogous to it.
    /// </summary>
    /// <remarks>
    /// Source: L. Bocquet, "The physics of stone skipping", Am. J. Phys. 71(2), 150-155
    /// (2003), arXiv:physics/0210015 — docs/Findings-RealSkipPhysics.md §3, first row:
    /// "M = 0.1 kg, a = 0.1 m diameter (R = 0.05 m), C_l ≈ C_f ≈ 1, ρ_w = 1000 kg/m³,
    /// θ ≈ 10°."
    ///
    /// <b>Not a SkipTuning.</b> docs/plans/real-skip-physics.md's Phase 1 table describes
    /// this as "a code-built SkipTuning", but that plan's Phase 1 also lists rewriting
    /// SkipTuning.cs itself (three multipliers → Mass/Radius/Thickness), and this pass was
    /// scoped NOT to touch SkipTuning.cs, SkipPhysics.cs, SkipConstants.cs or any other
    /// gameplay file — Phase 1 here is additive only, so the existing game keeps compiling
    /// and playing unchanged while these new pieces are built and measured standalone. This
    /// class is therefore a plain, asset-free, dependency-free data holder instead — it is
    /// still exactly what the harness needs (SkipReality drives DiscContact/RigidDisc
    /// directly, never through SkipTuning), and it means adopting the real dimensions as an
    /// actual SkipTuning conversion is a clean, isolated Phase 2 decision rather than one
    /// smuggled in here.
    ///
    /// <b>Thickness is NOT a published number.</b> Neither [B] nor [C] state a thickness —
    /// both treat the stone as an idealised thin disc. `Thickness` below is an engineering
    /// assumption (a plausible thin flat stone, R/10), not a citation, and exists only so
    /// <see cref="RigidDisc.ComputeInertia"/> has a transverse-inertia term to compute; it
    /// is deliberately small enough that the transverse moment is dominated by the ¼MR²
    /// term the papers DO ground, exactly as the thin-disc idealisation intends.
    /// </remarks>
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

        /// <summary>
        /// `C_f` — Bocquet's tangential friction coefficient. **Zero, deliberately, and this
        /// is NOT the value [B] quotes.**
        /// </summary>
        /// <remarks>
        /// [B] quotes `C_f ≈ 1`, but that number belongs to [B]'s own self-consistent model
        /// where lift AND friction both scale with the full `V²`. We use [N]'s `(v·n̂)²` lift
        /// instead, because [B]'s `V²` form has no interior optimum in tilt and therefore
        /// cannot produce the magic angle at all (verified — see below).
        ///
        /// Mixing the two is not a small error. At the shallow angles skipping happens at,
        /// `(v·n̂)² ≪ V²`, so a `V²` friction term dwarfs the lift and the stone ploughs in.
        /// Measured with `tools/physics/verify-disc-contact.py`, sweeping tilt 5–60° at 15°
        /// incidence:
        ///
        ///     C_f = 0.00  ->  optimum 30 deg, v_min  4.44 m/s
        ///     C_f = 0.10  ->  optimum 35 deg, v_min  6.52 m/s
        ///     C_f = 0.25  ->  optimum 40 deg, v_min 16.22 m/s
        ///     C_f = 0.50  ->  NEVER BOUNCES at any tilt or any speed
        ///     C_f = 1.00  ->  NEVER BOUNCES at any tilt or any speed
        ///
        /// So `C_f = 1` — the value the first draft of the findings doc specified — sinks
        /// every throw ever made. The Birkhoff/[N] model is pressure-only by construction:
        /// the stone still decelerates, because a tilted disc's normal force already has a
        /// backward component. Friction does not need to be added on top, and cannot be.
        ///
        /// **Raising this above ~0.1 will break the model, not tune it.** See
        /// `docs/Findings-RealSkipPhysics.md` §1.3.
        /// </remarks>
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
