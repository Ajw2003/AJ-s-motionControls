using UnityEngine;

namespace Game
{
    /// <summary>
    /// A disc's mass properties and the full Euler rigid-body integration of its attitude
    /// and angular velocity under an applied torque.
    /// </summary>
    /// <remarks>
    /// PURE. No Unity scene access, no events, no side effects — see the assembly-level
    /// remarks in <see cref="DiscContact"/>; this file lives alongside it for the same
    /// reason (EditMode-testable, referenced by nothing outside System.Collections.Generic
    /// / UnityEngine).
    ///
    /// docs/plans/real-skip-physics.md is explicit that this must be the FULL Euler
    /// equations, not the fast-spin gyroscopic shortcut (`ṅ̂ = (τ × n̂)/(I_a·Ω)`) that the
    /// literature usually reaches for: that approximation divides by the spin rate and
    /// diverges as `Ω → 0`, and an unspun throw tumbling on its own — with no invented
    /// `WanderDeg` fudge to make it happen — is exactly the behaviour this rewrite exists
    /// to get for free. [B] Eq. (A1) (Findings §2) is the standard Euler set this
    /// implements.
    /// </remarks>
    public static class RigidDisc
    {
        /// <summary>
        /// A disc's two distinct principal moments of inertia. Bocquet's axis convention
        /// (Findings §2 / [B] Appendix A): axis 2 is along the disc's own normal `n̂` (the
        /// spin axis) with `I₂ ≡ J₀`; axes 1 and 3 lie in the disc's plane (one of them is
        /// `t̂`) and share `I₁ = I₃ ≡ J₁`. In this file's local-frame convention (matching
        /// <see cref="PhoneAttitude.FaceNormal"/>, which is `attitude * Vector3.up`) that
        /// maps onto Unity's local axes as: local Y = `n̂` (axial, `J₀`), local X and local Z
        /// = the two transverse axes (`J₁`).
        /// </summary>
        public struct Inertia
        {
            /// <summary>`J₀ = ½MR²` — moment about the disc's own normal (spin axis).</summary>
            public float Axial;

            /// <summary>`J₁ = ¼MR² + ¹⁄₁₂Mh²` — moment about either in-plane axis.</summary>
            public float Transverse;
        }

        /// <summary>
        /// A uniform disc's inertia tensor from its mass properties. Standard closed-form
        /// solid-disc formulas, cited in docs/plans/real-skip-physics.md's model section.
        /// </summary>
        public static Inertia ComputeInertia(float mass, float radius, float thickness)
        {
            var radiusSq = radius * radius;
            return new Inertia
            {
                Axial = 0.5f * mass * radiusSq,
                Transverse = 0.25f * mass * radiusSq + (1f / 12f) * mass * thickness * thickness,
            };
        }

        /// <summary>
        /// The added moment of inertia of a disc rotating about a diameter in water:
        /// `I_added = (16/45)·ρ_w·R⁵`, the classical ideal-flow result for a circular plate.
        /// </summary>
        /// <remarks>
        /// **This is not a small correction.** For the reference stone — 5 cm radius, 100 g —
        /// it comes to 1.11e-4 kg·m² against the stone's own transverse moment of 6.3e-5.
        /// The water is nearly twice the stone: tumbling it means tumbling the water it
        /// drags, and leaving that out lets the disc spin up roughly three times too easily.
        /// Measured angular accelerations of ~1e6 rad/s² through contact
        /// (docs/Findings-RealSkipPhysics.md §8.1) are the symptom.
        ///
        /// **Only the transverse axes get it.** An ideal thin disc spinning about its own
        /// normal sweeps no fluid out of the way, so <see cref="Inertia.Axial"/> is
        /// untouched: the stone's spin is not slowed by this, only its tumble is. That
        /// asymmetry is the whole reason spin stabilises a skipping stone, and here it comes
        /// out of the geometry rather than being asserted.
        ///
        /// Scale by the wetted fraction — a stone in mid-flight drags no water, and one
        /// half in the water drags about half as much.
        /// </remarks>
        /// <param name="waterDensity">kg/m³.</param>
        /// <param name="radius">Disc radius, m.</param>
        /// <param name="wettedFraction">Wetted area over the disc's full area, 0 to 1.</param>
        public static float AddedTransverseInertia(float waterDensity, float radius, float wettedFraction)
        {
            if (radius <= 0f || wettedFraction <= 0f) return 0f;
            var r5 = radius * radius * radius * radius * radius;
            return (16f / 45f) * waterDensity * r5 * Mathf.Clamp01(wettedFraction);
        }

        /// <summary>
        /// This disc's inertia with the water it drags folded in, for use through contact.
        /// See <see cref="AddedTransverseInertia"/>.
        /// </summary>
        public static Inertia WithAddedMass(Inertia dry, float waterDensity, float radius, float wettedFraction)
        {
            dry.Transverse += AddedTransverseInertia(waterDensity, radius, wettedFraction);
            return dry;
        }

        /// <summary>
        /// Advance attitude and angular velocity by one substep under an applied
        /// world-space torque, integrating the full Euler rigid-body equations (not the
        /// fast-spin approximation — see the class remarks).
        /// </summary>
        /// <param name="attitude">World-space orientation. Updated in place.</param>
        /// <param name="angularVelocityWorld">World-space angular velocity, rad/s. Updated
        /// in place.</param>
        /// <param name="torqueWorld">Applied torque about the disc centre, world space,
        /// N·m (from <see cref="DiscContact.Evaluate"/> during contact; zero in free
        /// flight — gravity acts at the centre of mass and exerts no torque).</param>
        /// <param name="inertia">This disc's principal moments.</param>
        /// <param name="dt">Substep, seconds.</param>
        public static void Integrate(
            ref Quaternion attitude, ref Vector3 angularVelocityWorld,
            Vector3 torqueWorld, Inertia inertia, float dt)
        {
            var inverseAttitude = Quaternion.Inverse(attitude);

            // Move into the disc's own (principal-axis) frame — Euler's equations are only
            // simple in the frame the inertia tensor is diagonal in.
            var omegaBody = inverseAttitude * angularVelocityWorld;
            var torqueBody = inverseAttitude * torqueWorld;

            // Local Y is the spin axis (I₂ = Axial); local X and Z are the two transverse
            // axes (I₁ = I₃ = Transverse) — see the Inertia struct remarks.
            var i1 = inertia.Transverse;
            var i2 = Mathf.Max(inertia.Axial, 0.000001f);
            var i3 = inertia.Transverse;

            // [B] Eq. (A1) / Findings §2 — Euler's equations, full form, with the
            // gyroscopic coupling terms kept (that coupling is exactly what the fast-spin
            // approximation throws away, and exactly what stays correct as Ω → 0).
            //
            // Split into the torque part, which is integrated, and the free-precession
            // part, which is SOLVED. The split matters: for a disc `i1 = i3` exactly (the
            // Inertia struct cannot express anything else), so the free equations reduce to
            //
            //     ω̇_x = +λ·ω_z,   ω̇_z = −λ·ω_x,   λ = (i2 − i1)·ω_y / i1
            //
            // which is a pure rotation of (ω_x, ω_z) at rate λ — an OSCILLATOR. Explicit
            // Euler is unconditionally unstable on an oscillator: its amplitude grows by
            // ~(1 + (λ·dt)²/2) every step no matter how small the step is. At a hard 200
            // rev/s the precession rate is ~2500 rad/s, which grew the amplitude ~17% per
            // cycle and tore the attitude apart — so a fast-spun stone was LESS stable than
            // a slow one, exactly backwards, and no amount of spin could hold a bounce.
            //
            // Rotating (ω_x, ω_z) by λ·dt instead is exact for the free motion, costs one
            // sine and one cosine, and makes spin stabilising the way it is in the water.
            var angularAcceleration = new Vector3(
                torqueBody.x / Mathf.Max(i1, 0.000001f),
                torqueBody.y / i2,
                torqueBody.z / Mathf.Max(i3, 0.000001f));

            omegaBody += angularAcceleration * dt;

            var lambda = (i2 - i1) * omegaBody.y / Mathf.Max(i1, 0.000001f);
            var precession = lambda * dt;
            var cosPrecession = Mathf.Cos(precession);
            var sinPrecession = Mathf.Sin(precession);
            omegaBody = new Vector3(
                omegaBody.x * cosPrecession + omegaBody.z * sinPrecession,
                omegaBody.y,
                -omegaBody.x * sinPrecession + omegaBody.z * cosPrecession);

            // Advance the attitude by the exact rotation ω_body·dt represents, rather than
            // by `q + ½·q·ω·dt` and a renormalise. Same reason: the first-order form grows
            // the rotation it is meant to preserve, and normalising hides the error in the
            // norm while leaving it in the angle.
            var omegaMagnitude = omegaBody.magnitude;
            if (omegaMagnitude > 0.000001f)
            {
                var angle = omegaMagnitude * dt;
                var axis = omegaBody / omegaMagnitude;
                var half = 0.5f * angle;
                var sinHalf = Mathf.Sin(half);
                var step = new Quaternion(
                    axis.x * sinHalf, axis.y * sinHalf, axis.z * sinHalf, Mathf.Cos(half));

                attitude = Normalize(Multiply(attitude, step));
            }

            angularVelocityWorld = attitude * omegaBody;
        }

        /// <summary>Quaternion Hamilton product, written out explicitly rather than via
        /// Unity's `operator *`. <c>b</c> above is a PURE quaternion (w = 0, from an
        /// angular velocity, never unit length) — writing the product out directly keeps
        /// this independent of anything Unity's operator assumes about its operands being
        /// rotations.</summary>
        private static Quaternion Multiply(Quaternion a, Quaternion b) => new(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        private static Quaternion Normalize(Quaternion q)
        {
            var magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (magnitude < 0.000001f) return Quaternion.identity;
            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }
    }
}
