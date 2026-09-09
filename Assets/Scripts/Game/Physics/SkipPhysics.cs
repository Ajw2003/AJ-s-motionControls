using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    /// <summary>Everything describing a rock mid-flight.</summary>
    public struct SkipState
    {
        public Vector3 Position;
        public Vector3 Velocity;

        /// <summary>
        /// The stone's orientation. Starts straight from the phone and is then integrated
        /// by the solver — the water tips it, and this is what it tips. Applied to the mesh
        /// verbatim, so the stone the player watches is the stone the physics is solving.
        /// </summary>
        public Quaternion Attitude;

        /// <summary>
        /// The stone's face normal in world space — <see cref="PhoneAttitude.FaceNormal"/>
        /// of <see cref="Attitude"/>, kept alongside it because the contact model reads it
        /// every substep. Derived, never authored: if it disagrees with Attitude, Attitude
        /// wins.
        /// </summary>
        public Vector3 FaceNormal;

        /// <summary>
        /// The stone's angular velocity, world space, rad/s. Both the spin the player put on
        /// it and whatever tumble the water has since added — they are the same vector and
        /// the solver does not distinguish them.
        /// </summary>
        /// <remarks>
        /// This is the field that makes spin do real work. It is what
        /// <see cref="RigidDisc.Integrate"/> advances and what
        /// <see cref="DiscContact.Evaluate"/> reads to damp rotation, so a well-spun stone
        /// holds its attack angle through a slap and a badly spun one is turned over by it —
        /// with nothing anywhere deciding that a run "should" now end.
        /// </remarks>
        public Vector3 AngularVelocity;

        /// <summary>
        /// Where the stone was aimed at launch, flattened to the water plane. The velocity
        /// starts aligned with this; over a run the contact can push the velocity away from
        /// it, which is the sideslip the yaw forgiveness band judges.
        /// </summary>
        public Vector3 HeadingDir;

        /// <summary>
        /// Face angle in degrees above horizontal, straight off the phone's tilt at launch
        /// — the toss angle AND the attack angle are the same number, on purpose. See
        /// PhoneAttitude.FaceAngleDeg and the plan's "no hidden neutral-pose offset" section.
        /// </summary>
        public float FaceAngleDeg;

        /// <summary>Bank at launch, degrees. See PhoneAttitude.RollDeg.</summary>
        public float RollDeg;

        /// <summary>
        /// How fast the wrist was turning about the phone's screen normal, deg/s, signed.
        /// Kept as the raw phone reading: it widens the forgiveness bands and drives the
        /// stone's drawn rotation. The PHYSICS uses <see cref="AngularVelocity"/>, which is
        /// this converted into the stone's own units by <see cref="SpinTransfer"/>.
        /// </summary>
        public float Spin;

        /// <summary>
        /// How far the face has been turned away from where it was launched, degrees.
        /// </summary>
        /// <remarks>
        /// This used to be an invented accumulator — every bounce added
        /// `WanderPerBounce · (1 − stability)` so that runs would decay. It is now a
        /// MEASUREMENT: the angle between the current face normal and the launch normal.
        /// Nothing adds to it and nothing tunes it; it is read out of the attitude the
        /// solver integrated. A stone that holds its face reports zero all run.
        /// </remarks>
        public float WanderDeg;

        /// <summary>
        /// Whether any part of the face was under water at the end of the last substep.
        /// </summary>
        /// <remarks>
        /// Carried in the state, not in a local, because a contact can straddle two Step
        /// calls: a bounce lasts a few milliseconds and the fixed step is twenty, so usually
        /// it does not — but "usually" is how a skip goes uncounted on the throw that
        /// happens to land on a step boundary.
        /// </remarks>
        public bool InContact;

        public int SkipCount;
        public bool HasSunk;
    }

    /// <summary>What one integration step did.</summary>
    public struct SkipStep
    {
        public SkipState State;
        public bool Bounced;
        public bool HasSunk;
        public float ImpactSpeed;
    }

    /// <summary>
    /// The skip solver: ballistic flight, and a sub-stepped fluid-dynamic water contact.
    /// </summary>
    /// <remarks>
    /// PURE. Step takes state in and returns state out; it mutates nothing, touches no
    /// scene, and publishes no events.
    ///
    /// That last part is load-bearing. The aim preview runs Step hundreds of times per
    /// frame for a throw that has not happened. If Step published SkipOccurredEvent, the
    /// preview would score imaginary throws and the scoreboard would climb while a player
    /// merely aimed. RockController inspects the returned SkipStep and publishes — and only
    /// when driving a real throw. Audio, VFX and camera shake follow the same rule:
    /// observable effects belong to the caller, never in here.
    ///
    /// <b>The model.</b> There is no bounce formula. The stone has a body — mass, radius,
    /// thickness, and the moments of inertia that follow (<see cref="SkipTuning"/>) — and
    /// while any part of its face is under water the solver integrates the real pressure
    /// force and its torque (<see cref="DiscContact"/>, <see cref="RigidDisc"/>) at a
    /// microsecond substep. Whether the stone skips or sinks is then an OUTCOME, not a
    /// branch: enough lift and it leaves, not enough and it goes under.
    ///
    /// Everything the old solver authored is now a consequence:
    ///
    /// - <b>Energy loss</b> is the work the pressure does against the stone. It cannot be
    ///   positive: pressure acts only where the face is advancing into the water, so its
    ///   work rate is `½ρC_l·u_n³` with `u_n &lt; 0` everywhere it acts. The old `MinLoss`
    ///   clamp that enforced this is gone because the structure now guarantees it.
    /// - <b>The forward/upward split</b> is the face normal's direction. Nothing shares out
    ///   an energy budget.
    /// - <b>Tumbling</b> is the contact torque against the stone's own inertia and spin.
    ///   There is no `WanderDeg` to invent and no stability threshold to type in.
    /// - <b>The 20° optimum</b> is not written down anywhere in this file. It emerges, and
    ///   the verification harness measures it rather than asserting it.
    ///
    /// See docs/plans/real-skip-physics.md and docs/Findings-RealSkipPhysics.md. The
    /// findings document, not this file, is the source of truth for the published numbers —
    /// including §9.9, which is candid that skip counts are still short of Bocquet's and
    /// that the shortfall currently lands on `SkipConstants.SpinScale`.
    /// </remarks>
    public static class SkipPhysics
    {
        /// <summary>
        /// How far the stone must fly between contacts, in radii, for the hop to count as a
        /// skip rather than surface chatter.
        /// </summary>
        /// <remarks>
        /// Without this the solver counted a stone vibrating on the surface as skipping —
        /// runs of "skips" two centimetres apart on a five-centimetre stone
        /// (docs/Findings-RealSkipPhysics.md §8.3). Real hops are metres. The gap between
        /// the two is enormous, so this threshold is not delicate; it is in radii so it
        /// scales with the stone instead of being a length typed in for one size of rock.
        /// </remarks>
        private const float MinimumHopRadii = 4f;

        /// <summary>
        /// Below this forward speed a run is over regardless. Not a skip/sink criterion —
        /// the physics decides that — just a floor that stops a stone that has effectively
        /// stopped from being simulated forever.
        /// </summary>
        private const float MinForwardSpeed = 0.05f;

        /// <summary>Ceiling on contact substeps inside a single Step, so a pathological
        /// state cannot hang the aim preview. A real contact is a few hundred.</summary>
        private const int MaxContactSubsteps = 4000;

        /// <summary>
        /// A forgiveness band widened by spin, deg per deg/s of measured wrist spin — see
        /// SkipConstants.SpinToleranceScale. A band authored at 0 stays off regardless of
        /// spin: spin buys tolerance, it does not turn tolerance on where none was authored.
        /// </summary>
        public static float EffectiveBand(float authoredBandDeg, float spinRate, float spinToleranceScale) =>
            authoredBandDeg <= 0f ? 0f : authoredBandDeg + Mathf.Abs(spinRate) * spinToleranceScale;

        /// <summary>Build the initial state from a throw descriptor.</summary>
        /// <param name="origin">Where the throw starts.</param>
        /// <param name="swingSpeed">Raw wrist speed off the phone, m/s — not normalised.</param>
        /// <param name="attitude">The phone's rotation, already in Unity space — see PhoneAttitude.</param>
        /// <param name="spinRate">Raw wrist spin off the phone, deg/s, signed.</param>
        public static SkipState CreateLaunch(
            Vector3 origin, float swingSpeed, Quaternion attitude, float spinRate,
            SkipConstants constants, SkipTuning tuning)
        {
            // Raw pass-through: the only conversion left is hand m/s to stone m/s. No
            // normalisation, no power curve — a harder swing is simply a faster stone.
            var speed = Mathf.Max(0f, swingSpeed) * constants.SpeedScale;

            // Where the stone is pointing IS where it goes. Both read off the same rotation
            // the mesh is drawn with, so the throw can never disagree with the stone the
            // player watched themselves aim.
            var heading = PhoneAttitude.HeadingDirection(attitude);

            // The phone's own tilt IS the toss angle — no separate LaunchPitchDeg, and no
            // hidden neutral-pose offset. A phone held flat throws flat.
            var pitchDeg = PhoneAttitude.FaceAngleDeg(attitude);
            var pitch = pitchDeg * Mathf.Deg2Rad;
            var velocity = heading * (speed * Mathf.Cos(pitch)) + Vector3.up * (speed * Mathf.Sin(pitch));

            var faceNormal = PhoneAttitude.FaceNormal(attitude);

            // The wrist's twist becomes the stone's spin about its own face normal. The
            // conversion is SpinTransfer's grip-radius model, then SpinScale — see the
            // tooltip there for why that factor is not 1.
            var stoneSpin = SpinTransfer.ToStoneSpinRadPerSec(spinRate, tuning.Radius)
                            * constants.SpinScale;

            return new SkipState
            {
                Position = origin,
                Velocity = velocity,
                Attitude = attitude,
                FaceNormal = faceNormal,
                AngularVelocity = faceNormal * stoneSpin,
                HeadingDir = heading,
                FaceAngleDeg = pitchDeg,
                RollDeg = PhoneAttitude.RollDeg(attitude),
                Spin = spinRate,
                WanderDeg = 0f,
                SkipCount = 0,
                HasSunk = false,
            };
        }

        /// <summary>Advance one fixed step. Pure — see the class remarks.</summary>
        public static SkipStep Step(in SkipState state, SkipConstants constants, SkipTuning tuning, float dt)
        {
            var next = state;
            var result = new SkipStep();

            if (next.HasSunk)
            {
                result.State = next;
                return result;
            }

            var radius = Mathf.Max(tuning.Radius, 0.001f);
            var mass = Mathf.Max(tuning.Mass, 0.001f);
            var dryInertia = tuning.Inertia;
            var contactStep = Mathf.Max(constants.ContactStep, 0.0000001f);
            var discArea = Mathf.PI * radius * radius;

            var remaining = dt;
            var substeps = 0;

            while (remaining > 0f && !next.HasSunk && substeps < MaxContactSubsteps)
            {
                substeps++;

                next.FaceNormal = (next.Attitude * Vector3.up).normalized;
                var heightAboveWater = next.Position.y - constants.WaterY;

                var forces = DiscContact.Evaluate(
                    heightAboveWater, next.FaceNormal, next.Velocity, next.AngularVelocity,
                    radius, constants.WaterDensity,
                    constants.LiftCoefficient, constants.DragCoefficient);

                var inContact = forces.WettedArea > 0f;

                // A skip completes on the WATER-TO-AIR transition, going up. Keying this off
                // "started rising this substep" instead does not work: lift turns the stone
                // upward well before it is clear of the water, so by the time the face is dry
                // it has been rising for hundreds of substeps and the moment is long past.
                if (next.InContact && !inContact && next.Velocity.y > 0f)
                {
                    // Count the hop by the flight it is ABOUT to make. The stone is ballistic
                    // the moment it is clear, so `2·v_y·v_h/g` is the hop exactly — no need to
                    // wait for it to land, and the splash is reported where it happened.
                    var horizontal = new Vector3(next.Velocity.x, 0f, next.Velocity.z).magnitude;
                    var hop = 2f * next.Velocity.y * horizontal / Mathf.Max(constants.Gravity, 0.0001f);

                    if (hop >= MinimumHopRadii * radius)
                    {
                        next.SkipCount++;
                        result.Bounced = true;
                    }
                }

                next.InContact = inContact;

                if (!inContact)
                {
                    // Airborne. Step straight to the water rather than creeping toward it:
                    // at a 20 ms fixed step a 12 m/s stone covers a quarter of a metre,
                    // which would sail clean over a contact zone about a centimetre deep.
                    var flightStep = Mathf.Min(remaining, TimeToContact(next, constants, radius, remaining));
                    if (flightStep <= 0f) flightStep = remaining;

                    Advance(ref next, Vector3.zero, mass, constants.Gravity, flightStep);

                    // No torque in free flight — gravity acts at the centre of mass — so the
                    // stone keeps whatever rotation it has.
                    IntegrateAttitude(ref next, Vector3.zero, dryInertia, flightStep);

                    remaining -= flightStep;
                    continue;
                }

                if (result.ImpactSpeed <= 0f) result.ImpactSpeed = next.Velocity.magnitude;

                // Sunk means the stone's CENTRE is a whole radius under, which is
                // tilt-independent and unambiguous.
                //
                // Not "fully wetted": those are different things and conflating them killed
                // the flattest throws outright. A level stone skimming with its centre a
                // millimetre under IS fully wetted — that is the whole planing regime, and
                // it is generating its maximum lift right then. Worse, DiscContact's
                // dead-flat branch reports its submersion as a large sentinel rather than a
                // real distance, so a wetted-depth test read a perfectly level stone as
                // infinitely deep and sank it on contact.
                if (heightAboveWater <= -radius)
                {
                    next.HasSunk = true;
                    break;
                }

                var step = Mathf.Min(contactStep, remaining);

                Advance(ref next, forces.Lift + forces.Friction, mass, constants.Gravity, step);

                // Tumbling the stone means tumbling the water it drags — see
                // RigidDisc.AddedTransverseInertia, which for a real stone is nearly twice
                // the stone's own transverse moment.
                var wetInertia = RigidDisc.WithAddedMass(
                    dryInertia, constants.WaterDensity, radius, forces.WettedArea / discArea);
                IntegrateAttitude(ref next, forces.Torque, wetInertia, step);

                remaining -= step;
            }

            // Drift is measured off the attitude the solver integrated, not accumulated.
            next.FaceNormal = (next.Attitude * Vector3.up).normalized;
            next.WanderDeg = Vector3.Angle(next.FaceNormal, PhoneAttitude.FaceNormal(state.Attitude));

            var forwardSpeed = new Vector3(next.Velocity.x, 0f, next.Velocity.z).magnitude;
            if (!next.HasSunk && forwardSpeed < MinForwardSpeed && next.Position.y - constants.WaterY <= radius)
            {
                next.HasSunk = true;
            }

            if (next.HasSunk)
            {
                next.Velocity = Vector3.zero;
                next.AngularVelocity = Vector3.zero;
                result.HasSunk = true;
            }

            result.State = next;
            return result;
        }

        /// <summary>
        /// Time until the disc's rim first reaches the water, under gravity alone, or
        /// <paramref name="horizon"/> if it will not within that window.
        /// </summary>
        /// <remarks>
        /// Contact begins when the centre is `R·sinθ` above the surface, not when it is AT
        /// the surface — a tilted disc touches with its rim first. Attitude is treated as
        /// fixed across the lookahead, which is safe because the result is only used to pick
        /// a step length: land slightly early and the next iteration simply finds no wetted
        /// area and looks again.
        /// </remarks>
        private static float TimeToContact(in SkipState state, SkipConstants constants, float radius, float horizon)
        {
            var n = state.FaceNormal;
            var sinTilt = Mathf.Sqrt(Mathf.Max(0f, 1f - n.y * n.y));
            var touchHeight = radius * sinTilt;

            // Height of the centre above the height at which the rim touches.
            var y0 = state.Position.y - constants.WaterY - touchHeight;
            if (y0 <= 0f) return 0f;

            var g = Mathf.Max(constants.Gravity, 0.0001f);
            var vy = state.Velocity.y;

            // y0 + vy·t − ½g·t² = 0
            var discriminant = vy * vy + 2f * g * y0;
            if (discriminant <= 0f) return horizon;

            var t = (vy + Mathf.Sqrt(discriminant)) / g;
            return t > 0f && t < horizon ? t : horizon;
        }

        /// <summary>Semi-implicit translation step: velocity first, then position.</summary>
        private static void Advance(ref SkipState state, Vector3 force, float mass, float gravity, float dt)
        {
            var acceleration = force / mass + Vector3.down * gravity;
            state.Velocity += acceleration * dt;
            state.Position += state.Velocity * dt;
        }

        private static void IntegrateAttitude(ref SkipState state, Vector3 torque, RigidDisc.Inertia inertia, float dt)
        {
            var attitude = state.Attitude;
            var angularVelocity = state.AngularVelocity;
            RigidDisc.Integrate(ref attitude, ref angularVelocity, torque, inertia, dt);
            state.Attitude = attitude;
            state.AngularVelocity = angularVelocity;
        }

        /// <summary>
        /// Run the throw forward and collect its path. Used by the aim preview, and by
        /// any tooling that wants a trajectory without spawning a rock.
        /// </summary>
        /// <param name="path">Filled with world positions. Cleared first; reuse the list to avoid per-frame garbage.</param>
        /// <returns>The final state, so callers can read SkipCount and distance.</returns>
        public static SkipState Predict(SkipState start, SkipConstants constants, SkipTuning tuning, List<Vector3> path)
        {
            path.Clear();
            path.Add(start.Position);

            var state = start;

            for (var i = 0; i < constants.MaxPredictSteps; i++)
            {
                var step = Step(state, constants, tuning, constants.FixedStep);
                state = step.State;
                path.Add(state.Position);

                if (state.HasSunk) break;
            }

            return state;
        }
    }
}
