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
            var angularAcceleration = new Vector3(
                (torqueBody.x + (i2 - i3) * omegaBody.y * omegaBody.z) / Mathf.Max(i1, 0.000001f),
                (torqueBody.y + (i3 - i1) * omegaBody.z * omegaBody.x) / i2,
                (torqueBody.z + (i1 - i2) * omegaBody.x * omegaBody.y) / Mathf.Max(i3, 0.000001f));

            omegaBody += angularAcceleration * dt;

            // Integrate attitude from the BODY-frame angular velocity: q̇ = ½·q·ω_body (pure
            // quaternion). Simple explicit Euler — R9 in the verification harness exists
            // specifically to check this is a small enough step that halving it barely
            // moves the answer, i.e. that results are physics and not integrator error.
            var omegaQuat = new Quaternion(omegaBody.x, omegaBody.y, omegaBody.z, 0f);
            var derivative = Multiply(attitude, omegaQuat);

            var updated = new Quaternion(
                attitude.x + 0.5f * dt * derivative.x,
                attitude.y + 0.5f * dt * derivative.y,
                attitude.z + 0.5f * dt * derivative.z,
                attitude.w + 0.5f * dt * derivative.w);

            attitude = Normalize(updated);
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
