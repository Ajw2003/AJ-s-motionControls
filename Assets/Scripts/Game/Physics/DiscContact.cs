using UnityEngine;

namespace Game
{
    /// <summary>
    /// The published planing-disc contact model: wetted area, its centroid, and the two
    /// forces that act on it. Pure, closed-form geometry plus one force evaluation — no
    /// integration lives here, that is <see cref="RigidDisc"/>'s job.
    /// </summary>
    /// <remarks>
    /// Every formula here is cited in docs/Findings-RealSkipPhysics.md and must not drift
    /// from it — that document, not this file's comments, is the source of truth for the
    /// numbers. In particular:
    ///
    /// - Wetted area is Bocquet [B] Eq. (9) (Findings §2), algebraically identical to the
    ///   form quoted in docs/plans/real-skip-physics.md.
    /// - The force law is the corrected one from Findings §1, NOT the plan's first draft:
    ///   lift goes as the SQUARED NORMAL velocity `(v·n̂)²` (the Nagahiro/Birkhoff form,
    ///   Findings §1.2 — Bocquet's constant-coefficient `V²` form cannot produce a magic
    ///   angle), and friction acts along `t̂`, which lies IN the disc's own plane, not
    ///   against the flight path (Findings §1.1). There is no force at all on the dry face.
    ///
    /// PURE. No Unity scene access, no events, no side effects, deterministic — this file
    /// lives in the RockSkipping.Physics assembly (`"references": []`) alongside
    /// SkipPhysics/PhoneAttitude/SkipConstants/SkipTuning specifically so it can be used
    /// standalone by EditMode tests and by the verification harness. Do not add a
    /// reference to anything outside System.Collections.Generic/UnityEngine.
    /// </remarks>
    public static class DiscContact
    {
        /// <summary>
        /// The wetted area of a circular disc of radius <paramref name="radius"/>, cut by a
        /// plane whose intersection with the disc's own plane sits a signed distance
        /// <paramref name="d"/> from the disc centre, measured along the axis that points
        /// INTO the water (see <see cref="WettedAxis"/>).
        /// </summary>
        /// <remarks>
        /// Bocquet [B] Eq. (9) / Findings §2, `S_im(s) = R²·[acos(1−s/R) − (1−s/R)·√(1−(1−s/R)²)]`
        /// with `s = |z|/sinθ`. Substituting `d = R − s` gives exactly the form used here,
        /// `R²·acos(d/R) − d·√(R²−d²)`, which the Findings doc confirms is algebraically
        /// identical.
        ///
        /// `d` is the distance from the disc centre to the waterline, along the wetted
        /// axis: `d = −R` means the whole disc is submerged (the far side, `d = +R`, means
        /// bone dry). At `d = 0` the waterline passes exactly through the centre and the
        /// disc is exactly half wetted — this is what the EditMode geometry tests check.
        /// </remarks>
        public static float WettedArea(float radius, float d)
        {
            if (radius <= 0f) return 0f;

            // Fully dry / fully wet clamp — the closed form is only valid for |d| < R;
            // outside that band the physical answer is exactly 0 or the full disc.
            if (d >= radius) return 0f;
            if (d <= -radius) return Mathf.PI * radius * radius;

            var ratio = d / radius;
            return radius * radius * Mathf.Acos(ratio) - d * Mathf.Sqrt(radius * radius - d * d);
        }

        /// <summary>
        /// Distance from the disc centre to the wetted segment's centroid, along the same
        /// wetted axis <paramref name="d"/> is measured on. Always ≥ 0: the centroid of the
        /// wetted cap sits between the waterline and the far (wettest) rim, never behind
        /// the disc centre.
        /// </summary>
        /// <remarks>
        /// Closed form for a circular segment's centroid, derived directly from
        /// `WettedArea`'s own integral (see the derivation in the class remarks of the
        /// EditMode test file — it is not separately published in [B], but it follows from
        /// the same integral [B] Eq. (9) is built from):
        ///
        /// ```
        /// x̄ = (2/3)·(R² − d²)^1.5 / A_wet
        /// ```
        ///
        /// At `d = −R` (fully submerged) this correctly gives 0 — the centroid of a whole
        /// circle is its own centre. At `d = 0` (half wetted) it gives the textbook
        /// semicircle centroid, `4R/(3π) ≈ 0.424·R`, measured from the flat edge.
        /// </remarks>
        public static float WettedCentroidDistance(float radius, float d)
        {
            var area = WettedArea(radius, d);
            if (area <= 0f) return 0f;

            var clamped = Mathf.Clamp(d, -radius, radius);
            var underRoot = Mathf.Max(0f, radius * radius - clamped * clamped);
            var moment = (2f / 3f) * Mathf.Pow(underRoot, 1.5f);
            return moment / area;
        }

        /// <summary>
        /// The in-plane direction that points INTO the water, given the disc's outward
        /// face normal <paramref name="normal"/>. This is the axis <see cref="WettedArea"/>
        /// and <see cref="WettedCentroidDistance"/>'s `d` is measured along, and the
        /// direction the wetted centroid is offset from the disc centre in world space.
        /// </summary>
        /// <remarks>
        /// Moving a distance `x` along this axis lowers height by `x·sinθ`, where `θ` is
        /// the disc's tilt off horizontal (`sinθ = |horizontal component of normal|`) — so
        /// `d = z / sinθ` (see <see cref="SignedWaterDistance"/>) is exactly the disc-plane
        /// distance to the waterline that trigonometry predicts.
        ///
        /// Degenerate when the disc is dead flat (`normal` parallel to world up): there is
        /// no well-defined "downhill" direction on a level disc, so this falls back to
        /// world forward projected onto the disc's plane — an arbitrary but stable choice
        /// (a flat disc's contact is radially symmetric, so which in-plane axis is picked
        /// does not matter physically).
        /// </remarks>
        public static Vector3 WettedAxis(Vector3 normal)
        {
            var n = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;

            // Project world-up onto the disc's own plane, then flip: the result points
            // "downhill" (into the water) rather than "uphill".
            var uphill = Vector3.up - n * Vector3.Dot(n, Vector3.up);
            if (uphill.sqrMagnitude > 0.0001f) return -uphill.normalized;

            // Disc is flat (normal parallel to up) — pick a stable arbitrary in-plane axis.
            var fallback = Vector3.forward - n * Vector3.Dot(n, Vector3.forward);
            if (fallback.sqrMagnitude > 0.0001f) return -fallback.normalized;
            return -Vector3.Cross(n, Vector3.right).normalized;
        }

        /// <summary>
        /// The signed disc-plane distance from the disc centre to the waterline (the `d`
        /// every method above takes), given the centre's height above the water and the
        /// disc's tilt.
        /// </summary>
        /// <param name="heightAboveWater">Disc centre's height minus the water height —
        /// positive means the centre sits above the water.</param>
        /// <param name="normal">The disc's outward face normal.</param>
        public static float SignedWaterDistance(float heightAboveWater, Vector3 normal)
        {
            var n = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            var sinTheta = Mathf.Sqrt(Mathf.Max(0f, 1f - n.y * n.y));

            // Dead flat: there is no tilt to project the height through. A flat disc
            // touches everywhere at once — treat as fully wet the instant the centre is at
            // or below the water, fully dry otherwise. Callers should rarely land exactly
            // here (a perfectly flat contact is a measure-zero case) but must not divide by
            // zero when they do.
            if (sinTheta < 0.001f) return heightAboveWater <= 0f ? -1000f : 1000f;

            return heightAboveWater / sinTheta;
        }

        /// <summary>One substep's fluid forces and the torque they exert about the disc centre.</summary>
        public struct ContactForces
        {
            public float WettedArea;
            public Vector3 Lift;
            public Vector3 Friction;
            public Vector3 Torque;
        }

        /// <summary>
        /// Evaluate the fluid forces for one substep of contact. Pure — reads the disc's
        /// state, returns forces and torque, touches nothing else.
        /// </summary>
        /// <param name="heightAboveWater">Disc centre's height minus the water height.</param>
        /// <param name="normal">Disc's outward face normal, world space, unit length.</param>
        /// <param name="velocity">Disc centre's velocity, world space, m/s.</param>
        /// <param name="radius">Disc radius, m.</param>
        /// <param name="waterDensity">kg/m³ — Bocquet [B] uses 1000 (fresh water).</param>
        /// <param name="liftCoefficient">`C_l` — Findings §3, "≈ 1", constant (Bocquet's
        /// weakest assumption, see Findings §5 item 1 — exposed as a field, not baked in,
        /// for exactly that reason).</param>
        /// <param name="dragCoefficient">`C_f` — same source, "≈ 1".</param>
        public static ContactForces Evaluate(
            float heightAboveWater, Vector3 normal, Vector3 velocity,
            float radius, float waterDensity, float liftCoefficient, float dragCoefficient)
        {
            var n = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            var d = SignedWaterDistance(heightAboveWater, n);

            var forces = new ContactForces
            {
                WettedArea = WettedArea(radius, d),
            };

            if (forces.WettedArea <= 0f) return forces;

            // ── Lift, along n̂, proportional to the SQUARED NORMAL velocity ──────────────
            // Findings §1.2 / [N]: "Hydrodynamic pressure p acting from water is
            // proportional to (v·n)²... For the part of the surface facing air, there is no
            // hydrodynamic force." This — not Bocquet's constant-coefficient V² form — is
            // what produces the magic angle (R1), so it is not optional.
            var normalSpeed = Vector3.Dot(velocity, n);

            // ── The face must be moving INTO the water to feel any pressure ─────────────
            // [N] assumption (ii): "For the part of the surface facing air, there is no
            // hydrodynamic force." Squaring normalSpeed loses its sign, so without this
            // guard the lift is applied along +n̂ even when the face is travelling AWAY
            // from the water — and still water would be pushing the stone along its own
            // direction of travel, doing positive work.
            //
            // That is not a rounding error. Before this guard, a 12 m/s throw left its
            // first contact at 42.66 m/s: once the attitude tumbled far enough for n̂ to
            // lean into the velocity, every substep pumped in energy and the run became
            // nonsense. It is the same class of bug the old solver shipped with (see the
            // "ONE ENERGY BUDGET" remarks in SkipPhysics) — water can take energy from a
            // stone and redirect what is left, never add to it.
            //
            // The guard is what makes that structural rather than hoped-for: with
            // normalSpeed < 0 required, the lift always opposes penetration, so its work
            // is always negative. R8 in SkipReality asserts the consequence.
            var isPenetrating = normalSpeed < 0f;

            var liftMagnitude = isPenetrating
                ? 0.5f * waterDensity * liftCoefficient * normalSpeed * normalSpeed * forces.WettedArea
                : 0f;
            forces.Lift = n * liftMagnitude;

            // ── Friction, along t̂, IN the disc's own plane ──────────────────────────────
            // Findings §1.1 / [B] Eq. (1): the force decomposes along n̂ and along t̂, where
            // t̂ lies in the stone's own plane — NOT along −v̂ (that was the plan's original,
            // wrong, draft). t̂ here is the in-plane component of velocity, reversed, so
            // friction opposes the disc's slide across the water. Magnitude uses the FULL
            // speed squared, per [B] Eq. (1) (`V²`), not just the in-plane component.
            var inPlaneVelocity = velocity - n * normalSpeed;
            if (inPlaneVelocity.sqrMagnitude > 0.0001f)
            {
                var tangent = -inPlaneVelocity.normalized;
                var frictionMagnitude = 0.5f * waterDensity * dragCoefficient * velocity.sqrMagnitude * forces.WettedArea;
                forces.Friction = tangent * frictionMagnitude;
            }

            // ── Torque, about the wetted segment's centroid, not the disc centre ────────
            // "P is the centroid of the immersed area" ([B] Appendix A / Findings §2) — the
            // force effectively acts there, offset forward of the disc centre along the
            // wetted axis, so it exerts a torque that tries to flip the stone.
            var centroidDistance = WettedCentroidDistance(radius, d);
            var wettedAxis = WettedAxis(n);
            var centroidOffset = wettedAxis * centroidDistance;
            forces.Torque = Vector3.Cross(centroidOffset, forces.Lift + forces.Friction);

            return forces;
        }
    }
}
