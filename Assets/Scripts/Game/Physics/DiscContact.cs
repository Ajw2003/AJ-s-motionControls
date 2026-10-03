using UnityEngine;

namespace Game
{
    // Pure model whose formulas follow the Findings doc: lift uses the squared normal velocity, friction acts in the disc
    // plane, and no scene access is allowed. doc-ref 3781 docs/4-systems/SkipPhysics.md
    public static class DiscContact
    {
        // Wetted area is Bocquet's Eq. 9 rewritten in terms of d = R - s, so d = 0 is exactly half
        // wetted. doc-ref 32bd docs/4-systems/SkipPhysics.md
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

        // Centroid distance is (2/3)(R^2-d^2)^1.5 over the wetted area, never negative, and zero when
        // fully submerged. doc-ref a425 docs/4-systems/SkipPhysics.md
        public static float WettedCentroidDistance(float radius, float d)
        {
            var area = WettedArea(radius, d);
            if (area <= 0f) return 0f;

            var clamped = Mathf.Clamp(d, -radius, radius);
            var underRoot = Mathf.Max(0f, radius * radius - clamped * clamped);
            var moment = (2f / 3f) * Mathf.Pow(underRoot, 1.5f);
            return moment / area;
        }

        // The wetted axis is the in-plane direction into the water; a dead-flat disc falls back to
        // world forward projected onto it. doc-ref 6ab6 docs/4-systems/SkipPhysics.md
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

        // Evaluate is pure, and C_l and C_f are fields because Bocquet's constant-coefficient
        // assumption is the model's weakest. doc-ref df22 docs/4-systems/SkipPhysics.md
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

            // Lift applies only when the face moves into the water; squaring loses the sign, so without
            // this guard the water adds energy. doc-ref a036 docs/4-systems/SkipPhysics.md
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
