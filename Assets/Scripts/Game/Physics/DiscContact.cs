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
        /// The area moments of the wetted segment about the disc centre, in the disc's own
        /// plane: `û` is the wetted axis (<see cref="WettedAxis"/>, pointing into the
        /// water) and `v̂ = n̂ × û` is the in-plane axis across it.
        /// </summary>
        /// <remarks>
        /// These exist because the pressure law is evaluated POINTWISE across the wetted
        /// area once rotation is included — see <see cref="Evaluate"/>. Integrating
        /// `(v_cm + ω × r)·n̂` squared over the segment needs the segment's second and third
        /// area moments, not just its area and centroid.
        ///
        /// All five are closed forms, derived from the same integral Bocquet's Eq. (9) is
        /// built from (Findings §2), over the region `{x ≥ d}` inside the disc:
        ///
        /// ```
        /// A     = ∫ dA      = R²·acos(d/R) − d·√(R²−d²)
        /// Mx    = ∫ x dA    = (2/3)·(R²−d²)^1.5
        /// Mxx   = ∫ x² dA   = (R⁴/4)·acos(d/R) − (d/4)·(2d²−R²)·√(R²−d²)
        /// Myy   = ∫ y² dA   = (R⁴/4)·acos(d/R) − (d/12)·(5R²−2d²)·√(R²−d²)
        /// Mxxx  = ∫ x³ dA   = (2/3)·R²·(R²−d²)^1.5 − (2/5)·(R²−d²)^2.5
        /// Mxyy  = ∫ x·y² dA = (2/15)·(R²−d²)^2.5
        /// ```
        ///
        /// `∫ y dA` and `∫ x·y dA` are both exactly zero — the segment is symmetric about
        /// the wetted axis — which is what collapses the pressure integral to the handful
        /// of terms <see cref="Evaluate"/> actually sums.
        ///
        /// Every one of these was checked against brute-force numerical integration before
        /// being written here (worst relative error 6e-5, at the grid's own resolution), and
        /// against the exact full-disc values at `d = −R`: `A = πR²`, `Mxx = Myy = πR⁴/4`,
        /// and `Mx = Mxxx = Mxyy = 0`. The EditMode tests assert the same.
        /// </remarks>
        public struct SegmentMoments
        {
            /// <summary>`A` — wetted area, m².</summary>
            public float Area;

            /// <summary>`Mx` — first moment along the wetted axis, m³.</summary>
            public float AlongFirst;

            /// <summary>`Mxx` — second moment along the wetted axis, m⁴.</summary>
            public float AlongSecond;

            /// <summary>`Myy` — second moment across the wetted axis, m⁴.</summary>
            public float AcrossSecond;

            /// <summary>`Mxxx` — third moment along the wetted axis, m⁵.</summary>
            public float AlongThird;

            /// <summary>`Mxyy` — mixed moment `∫x·y² dA`, m⁵.</summary>
            public float AlongAcrossSquared;
        }

        /// <summary>
        /// The wetted segment's area moments for a disc of radius <paramref name="radius"/>
        /// cut at signed distance <paramref name="d"/>. See <see cref="SegmentMoments"/>
        /// for the closed forms and how they were verified.
        /// </summary>
        public static SegmentMoments Moments(float radius, float d)
        {
            var moments = new SegmentMoments();
            if (radius <= 0f || d >= radius) return moments;

            var clamped = Mathf.Clamp(d, -radius, radius);
            var radiusSq = radius * radius;
            var radiusFourth = radiusSq * radiusSq;

            // `R² − d²` recurs at powers 0.5, 1.5 and 2.5; compute the root once.
            var underRoot = Mathf.Max(0f, radiusSq - clamped * clamped);
            var root = Mathf.Sqrt(underRoot);
            var root3 = underRoot * root;
            var root5 = underRoot * underRoot * root;

            var arc = Mathf.Acos(Mathf.Clamp(clamped / radius, -1f, 1f));

            moments.Area = radiusSq * arc - clamped * root;
            moments.AlongFirst = (2f / 3f) * root3;
            moments.AlongSecond = 0.25f * radiusFourth * arc
                                  - 0.25f * clamped * (2f * clamped * clamped - radiusSq) * root;
            moments.AcrossSecond = 0.25f * radiusFourth * arc
                                   - (1f / 12f) * clamped * (5f * radiusSq - 2f * clamped * clamped) * root;
            moments.AlongThird = (2f / 3f) * radiusSq * root3 - (2f / 5f) * root5;
            moments.AlongAcrossSquared = (2f / 15f) * root5;
            return moments;
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

        /// <summary>
        /// How many strips across the wetted segment <see cref="RotatingPressureIntegrals"/>
        /// uses. The integration is EXACT in `y`, so this only resolves the variation along
        /// the wetted axis, and 24 is already well past convergence — the EditMode tests
        /// check the ω = 0 result against the closed-form <see cref="Moments"/> and the
        /// harness's R9 re-runs whole throws at a finer count.
        /// </summary>
        private const int PressureStrips = 24;

        /// <summary>
        /// `∫u_n² dA`, `∫x·u_n² dA` and `∫y·u_n² dA` over the part of the wetted segment
        /// that is actually advancing into the water, where
        /// `u_n(x, y) = normalSpeed + a·y − b·x`.
        /// </summary>
        /// <remarks>
        /// **Why this is not a closed form.** The region is the disc, cut by the waterline
        /// (`x ≥ d`) and cut again by `u_n &lt; 0`. Two arbitrary chords across a circle make
        /// a circular polygon whose moments have no compact closed form, and the second cut
        /// cannot be dropped — see the remarks at the call site for what happens when it is.
        ///
        /// **But it is exact in `y`.** At fixed `x`, `u_n` is linear in `y`, so both the
        /// advancing interval and the integrals over it are closed forms:
        ///
        /// ```
        /// c = normalSpeed − b·x        u_n = c + a·y        y ∈ [−h, h],  h = √(R²−x²)
        /// u_n < 0  ⟺  y < −c/a (a > 0)  or  y > −c/a (a < 0)
        /// ∫ (c+a·y)²   dy = (c+a·y)³ / 3a
        /// ∫ y·(c+a·y)² dy = c²y²/2 + 2ac·y³/3 + a²y⁴/4
        /// ```
        ///
        /// So only the sweep along `x` is numerical — a midpoint rule over
        /// <see cref="PressureStrips"/> strips. That keeps the cost at a couple of dozen
        /// cheap evaluations per substep rather than a full 2-D quadrature, which matters
        /// because AimPreview runs the whole solver every frame.
        /// </remarks>
        public static void RotatingPressureIntegrals(
            float radius, float d, float normalSpeed, float a, float b,
            out float pressure, out float alongMoment, out float acrossMoment)
        {
            pressure = alongMoment = acrossMoment = 0f;
            if (radius <= 0f || d >= radius) return;

            var lower = Mathf.Max(d, -radius);
            if (lower >= radius) return;

            // Sweep in `θ` with `x = R·sinθ`, not in `x` directly. The strip height
            // `√(R²−x²)` has an infinite derivative at the rim, and a uniform rule in `x`
            // pays for it — at 24 strips the first moment came out 5% low against the
            // closed form. Under the substitution the height is exactly `R·cosθ` and the
            // integrand is smooth to the endpoint, which buys three orders of magnitude at
            // the same strip count for the cost of a sine and a cosine per strip.
            var thetaLow = Mathf.Asin(Mathf.Clamp(lower / radius, -1f, 1f));
            var thetaStep = (Mathf.PI * 0.5f - thetaLow) / PressureStrips;
            if (thetaStep <= 0f) return;

            for (var i = 0; i < PressureStrips; i++)
            {
                var theta = thetaLow + (i + 0.5f) * thetaStep;
                var x = radius * Mathf.Sin(theta);
                var halfChord = radius * Mathf.Cos(theta);
                if (halfChord <= 0f) continue;

                // dx = R·cosθ·dθ
                var weight = halfChord * thetaStep;

                AdvancingStrip(normalSpeed - b * x, a, halfChord,
                    out var squared, out var firstMoment);

                pressure += squared * weight;
                alongMoment += x * squared * weight;
                acrossMoment += firstMoment * weight;
            }
        }

        /// <summary>
        /// One strip's `∫u_n² dy` and `∫y·u_n² dy`, over the part of `y ∈ [−h, h]` where
        /// `u_n = c + a·y` is negative. See <see cref="RotatingPressureIntegrals"/>.
        /// </summary>
        private static void AdvancingStrip(float c, float a, float halfChord,
            out float squared, out float firstMoment)
        {
            squared = firstMoment = 0f;

            // Negligible roll rate: u_n is constant across the strip, so the strip is either
            // wholly advancing or wholly receding. Taking the general branch here would
            // divide by `a`.
            if (Mathf.Abs(a) < 0.0001f)
            {
                if (c < 0f) squared = c * c * 2f * halfChord;
                return;
            }

            // u_n < 0 on one side of y* = −c/a; which side depends on the sign of a.
            var crossing = -c / a;
            float low, high;
            if (a > 0f)
            {
                low = -halfChord;
                high = Mathf.Min(halfChord, crossing);
            }
            else
            {
                low = Mathf.Max(-halfChord, crossing);
                high = halfChord;
            }

            if (high <= low) return;

            squared = (Cube(c + a * high) - Cube(c + a * low)) / (3f * a);

            var cSq = c * c;
            var twoAc = 2f * a * c;
            var aSq = a * a;
            firstMoment = FirstMomentAntiderivative(high, cSq, twoAc, aSq)
                          - FirstMomentAntiderivative(low, cSq, twoAc, aSq);
        }

        private static float Cube(float v) => v * v * v;

        /// <summary>`∫ y·(c + a·y)² dy = c²y²/2 + 2ac·y³/3 + a²y⁴/4`.</summary>
        private static float FirstMomentAntiderivative(float y, float cSq, float twoAc, float aSq)
        {
            var y2 = y * y;
            return 0.5f * cSq * y2 + (twoAc / 3f) * y2 * y + 0.25f * aSq * y2 * y2;
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
            => Evaluate(heightAboveWater, normal, velocity, Vector3.zero,
                radius, waterDensity, liftCoefficient, dragCoefficient);

        /// <summary>
        /// Evaluate the fluid forces for one substep of contact, including the disc's own
        /// rotation. Pure — reads the disc's state, returns forces and torque, touches
        /// nothing else.
        /// </summary>
        /// <remarks>
        /// **Why rotation belongs in the pressure law.** [N] assumption (i) is that pressure
        /// goes as `(v·n)²` where `v` is "the speed of the body". For a rigid body the
        /// velocity of the surface at position `r` is `v_cm + ω × r`, not `v_cm` — using the
        /// centre-of-mass velocity uniformly across the wetted area is an approximation, and
        /// it is the one that was quietly costing this model its rotational damping.
        ///
        /// Applying the same law pointwise and integrating over the segment produces a
        /// hydrodynamic moment that opposes rotation, with **no new coefficient and no new
        /// force term** — it is the existing law, read correctly. A plate pitching down
        /// drives its leading half harder into the water and its trailing half less, and the
        /// pressure difference resists the pitch. That is the added-mass damping a real
        /// plate has and this model did not (Findings §8.4).
        ///
        /// **At `ω = 0` this reduces exactly to the previous model** — every rotational term
        /// carries a factor of `a` or `b` — so the magic angle (R1), which is measured at
        /// fixed attitude, is untouched by construction rather than by luck.
        ///
        /// Writing `û` for the wetted axis, `v̂ = n̂ × û`, and a point in the disc plane as
        /// `r = x·û + y·v̂`, the local normal velocity is
        ///
        /// ```
        /// u_n(r) = v_cm·n̂ + ω·(r × n̂) = v_n + a·y − b·x        a = ω·û,  b = ω·v̂
        /// ```
        ///
        /// so pure spin about `n̂` contributes nothing (it is perpendicular to both `û` and
        /// `v̂`) — correct, and a useful check that the frame is right. Integrating `u_n²`
        /// and `r × n̂·u_n²` over the segment, and dropping the terms that vanish by the
        /// segment's symmetry in `y`:
        ///
        /// ```
        /// ∫ u_n²   dA = v_n²·A  + a²·Myy  + b²·Mxx  − 2·v_n·b·Mx
        /// ∫ y·u_n² dA = 2·v_n·a·Myy − 2·a·b·Mxyy
        /// ∫ x·u_n² dA = v_n²·Mx + a²·Mxyy + b²·Mxxx − 2·v_n·b·Mxx
        /// ```
        ///
        /// with the moments from <see cref="Moments"/>. The damping sign falls out rather
        /// than being imposed: `v_n &lt; 0` while penetrating, so the `−2·v_n·b·Mxx` term
        /// enters the `v̂` moment with the sign that opposes `b`.
        /// </remarks>
        /// <param name="angularVelocity">Disc's angular velocity, world space, rad/s.</param>
        public static ContactForces Evaluate(
            float heightAboveWater, Vector3 normal, Vector3 velocity, Vector3 angularVelocity,
            float radius, float waterDensity, float liftCoefficient, float dragCoefficient)
        {
            var n = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            var d = SignedWaterDistance(heightAboveWater, n);

            var moments = Moments(radius, d);
            var forces = new ContactForces
            {
                WettedArea = moments.Area,
            };

            if (forces.WettedArea <= 0f) return forces;

            // ── Lift, along n̂, proportional to the SQUARED NORMAL velocity ──────────────
            // Findings §1.2 / [N]: "Hydrodynamic pressure p acting from water is
            // proportional to (v·n)²... For the part of the surface facing air, there is no
            // hydrodynamic force." This — not Bocquet's constant-coefficient V² form — is
            // what produces the magic angle (R1), so it is not optional.
            var normalSpeed = Vector3.Dot(velocity, n);

            // ── Only the face that is moving INTO the water feels any pressure ──────────
            // [N] assumption (ii): "For the part of the surface facing air, there is no
            // hydrodynamic force." Squaring the normal velocity loses its sign, so without
            // this restriction the lift is applied along +n̂ even where the face is
            // travelling AWAY from the water — and still water would be pushing the stone
            // along its own direction of travel, doing positive work.
            //
            // That is not a rounding error. Before it, a 12 m/s throw left its first
            // contact at 42.66 m/s: once the attitude tumbled far enough for n̂ to lean into
            // the velocity, every substep pumped in energy and the run became nonsense. It
            // is the same class of bug the old solver shipped with (see the "ONE ENERGY
            // BUDGET" remarks in SkipPhysics) — water can take energy from a stone and
            // redirect what is left, never add to it.
            //
            // This used to be a single test on the disc CENTRE's normal velocity. It is now
            // enforced pointwise inside RotatingPressureIntegrals, because with rotation the
            // centre no longer speaks for the whole face — see the remarks there.

            // The in-plane frame the segment moments are expressed in: `û` into the water,
            // `v̂` across it. (û, v̂, n̂) is right-handed, so `r × n̂ = y·û − x·v̂`.
            var wettedAxis = WettedAxis(n);
            var acrossAxis = Vector3.Cross(n, wettedAxis);

            // ω resolved onto the two in-plane axes. The component along n̂ is the stone's
            // spin and deliberately does not appear: it moves no part of the face into or
            // out of the water, so it changes no pressure.
            var a = Vector3.Dot(angularVelocity, wettedAxis);
            var b = Vector3.Dot(angularVelocity, acrossAxis);

            // ── The pressure integrals over the ADVANCING part of the wetted segment ────
            // Integrate only where `u_n < 0`. [N] assumption (ii) — "for the part of the
            // surface facing air, there is no hydrodynamic force" — is a statement about
            // each piece of the surface, not about the stone as a whole, and once the disc
            // is rotating the two stop being the same thing: `u_n = v_n + a·y − b·x` changes
            // sign across the face, so part of it is driving into the water while the rest
            // is lifting away from it.
            //
            // Integrating `u_n²` over the whole segment regardless — which is what a closed
            // form in the segment's area moments has to do — invents pressure on the half
            // that is receding. That is not a small correction: once `b·R` exceeds `|v_n|`,
            // which it does within a millisecond of a real contact, the fictitious half
            // dominates and every stone sinks.
            //
            // Cutting the region properly is what makes the energy invariant (R8)
            // STRUCTURAL rather than hoped-for. The rate of work of the pressure on the
            // surface it acts on is `½ρC_l·u_n²·(n̂·v_local) = ½ρC_l·u_n³`, and restricting
            // to `u_n < 0` makes every element of that integral negative. Water can take
            // energy from the stone and redirect what is left; it can never add to it. The
            // old scalar `isPenetrating` guard asserted the same thing for the centre alone.
            RotatingPressureIntegrals(radius, d, normalSpeed, a, b,
                out var pressureIntegral, out var alongMomentIntegral, out var acrossMomentIntegral);

            var pressureScale = 0.5f * waterDensity * liftCoefficient;
            forces.Lift = n * (pressureScale * pressureIntegral);

            // The hydrodynamic moment, integrated pointwise rather than applied at the
            // centroid — this is the term that carries the rotational damping.
            var liftTorque = pressureScale
                             * (wettedAxis * acrossMomentIntegral - acrossAxis * alongMomentIntegral);

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

            // ── Torque ──────────────────────────────────────────────────────────────────
            // The lift's share is `liftTorque` above: integrated pointwise over the wetted
            // area, so it carries the rotational damping. At ω = 0 it equals exactly what
            // the old centroid form gave — `Mx/A` is the centroid distance, and
            // `û × n̂ = −v̂` — which is why R1 is unaffected.
            //
            // Friction keeps the centroid treatment: "P is the centroid of the immersed
            // area" ([B] Appendix A / Findings §2). It is a single in-plane force with no
            // pointwise velocity dependence to integrate, and with `C_f` shipping at 0
            // (Findings §1.3) it contributes nothing at present in any case.
            var centroidOffset = wettedAxis * WettedCentroidDistance(radius, d);
            forces.Torque = liftTorque + Vector3.Cross(centroidOffset, forces.Friction);

            return forces;
        }
    }
}
