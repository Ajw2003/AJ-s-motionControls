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
        /// The stone's orientation, straight from the phone. Applied to the mesh verbatim
        /// — this is what makes the stone match the phone.
        /// </summary>
        public Quaternion Attitude;

        /// <summary>
        /// Face angle in degrees above horizontal. The variable that decides a bounce,
        /// derived from <see cref="Attitude"/> at launch so the thing the player sees and
        /// the thing the water reacts to cannot drift apart.
        /// </summary>
        public float FaceAngleDeg;

        /// <summary>
        /// How fast the wrist was turning about the phone's screen normal at release,
        /// deg/s, signed. Visual only — it turns the stone on screen and has no effect
        /// whatsoever on where it goes.
        /// </summary>
        public float Spin;

        /// <summary>
        /// How hard this was thrown, 0..1. Set at launch and never changed — it is a
        /// property of the throw, not of the stone's current state.
        /// </summary>
        /// <remarks>
        /// The single driver of distance. It sets launch speed, how much speed survives
        /// each bounce, and how high the hops are, so a hard throw goes further, skips
        /// more times, and skips higher — all from one number the player controls with
        /// one thing they can feel.
        /// </remarks>
        public float Power;

        public int SkipCount;
        public bool Sunk;
    }

    /// <summary>What one integration step did.</summary>
    public struct SkipStep
    {
        public SkipState State;
        public bool Bounced;
        public bool Sank;
        public float ImpactSpeed;
    }

    /// <summary>
    /// Custom bounce solver. Deliberately not Unity rigidbodies: skipping needs exact
    /// per-bounce control over attitude and energy loss, and the aim preview needs to
    /// run the same maths headlessly to draw its arc.
    /// </summary>
    /// <remarks>
    /// PURE. Step takes state in and returns state out; it mutates nothing, touches no
    /// scene, and publishes no events.
    ///
    /// That last part is load-bearing. The aim preview runs Step hundreds of times per
    /// frame for a throw that has not happened. If Step published SkipOccurredEvent,
    /// the preview would score imaginary throws and the scoreboard would climb while a
    /// player merely aimed. RockController inspects the returned SkipStep and publishes
    /// — and only when driving a real throw. Audio, VFX and camera shake follow the
    /// same rule: observable effects belong to the caller, never in here.
    /// </remarks>
    public static class SkipPhysics
    {
        /// <summary>
        /// Steeper than this and the stone plows in instead of skipping. A constant
        /// rather than a tuning field: it is a statement about what skipping *is*, and
        /// nobody ever needed to dial it.
        /// </summary>
        private const float MaxIncidenceDeg = 45f;

        /// <summary>
        /// Leave the water slower than this, vertically, and the stone did not skip — it
        /// went in.
        /// </summary>
        /// <remarks>
        /// This is a guard against grinding, not the thing that ends a run. A badly angled
        /// throw gets no lift, but Restitution still mirrors a fraction of its descent, so
        /// without this it grinds along the waterline in ever-smaller hops and racks up a
        /// dozen of them before MinSkipSpeed catches it — which at ten points a skip made a
        /// bad throw score like a good one.
        ///
        /// Deliberately far below what a healthy bounce produces. It was 0.6, sized against
        /// a 33 degree exit; at the shallow angles a stone actually skips at, a 0.6 m/s rise
        /// needs nearly 7 m/s of horizontal, so it silently became the real skip limit and
        /// cut every run short well before MinSkipSpeed. Ending a run is MinSkipSpeed's job.
        /// </remarks>
        private const float MinExitRiseSpeed = 0.15f;

        /// <summary>Build the initial state from a throw descriptor.</summary>
        /// <param name="attitude">The phone's rotation, already in Unity space — see PhoneAttitude.</param>
        public static SkipState CreateLaunch(
            Vector3 origin, float power, Quaternion attitude, float spinRate, SkipTuning tuning)
        {
            power = Mathf.Clamp01(power);

            // Speed comes from power and nothing else. There used to be a timing-quality
            // multiplier here that could cut it to 40%, keyed off how fast the phone
            // stopped — which punished hard throws, because a hard swing takes longer to
            // stop. See SkipTuning's remarks.
            var speed = Mathf.Lerp(tuning.MinSpeed, tuning.MaxSpeed, power);

            // Where the stone is pointing IS where it goes. Both read off the same
            // rotation the mesh is drawn with, so the throw can never disagree with the
            // stone the player watched themselves aim.
            var heading = PhoneAttitude.HeadingDirection(attitude);

            var pitch = tuning.LaunchPitchDeg * Mathf.Deg2Rad;
            var velocity = heading * (speed * Mathf.Cos(pitch)) + Vector3.up * (speed * Mathf.Sin(pitch));

            return new SkipState
            {
                Position = origin,
                Velocity = velocity,
                Attitude = attitude,
                FaceAngleDeg = PhoneAttitude.FaceAngleDeg(attitude),
                // Signed. Which way the wrist turned is not something the bounce cares
                // about — spin has no effect on the path at all — but it is exactly what
                // the player watches, so throwing it away would make every stone turn the
                // same way regardless of what they did.
                Spin = spinRate,
                Power = power,
                SkipCount = 0,
                Sunk = false,
            };
        }

        /// <summary>Advance one fixed step. Pure — see the class remarks.</summary>
        public static SkipStep Step(in SkipState state, SkipTuning tuning, float dt)
        {
            var next = state;
            var result = new SkipStep();

            if (next.Sunk)
            {
                result.State = next;
                return result;
            }

            next.Velocity += Vector3.down * (tuning.Gravity * dt);
            next.Position += next.Velocity * dt;

            var crossedSurface = next.Position.y <= tuning.WaterY && next.Velocity.y < 0f;
            if (!crossedSurface)
            {
                result.State = next;
                return result;
            }

            next.Position.y = tuning.WaterY;

            var speed = next.Velocity.magnitude;
            result.ImpactSpeed = speed;

            // How steeply it is coming in, measured against the water plane.
            var horizontal = new Vector3(next.Velocity.x, 0f, next.Velocity.z);
            var incidenceDeg = horizontal.sqrMagnitude > 0.0001f
                ? Vector3.Angle(next.Velocity, horizontal)
                : 90f;

            if (speed < tuning.MinSkipSpeed || incidenceDeg > MaxIncidenceDeg)
            {
                next.Sunk = true;
                next.Velocity = Vector3.zero;
                result.Sank = true;
                result.State = next;
                return result;
            }

            // Angle is a gradient, not a cliff. A stone outside tolerance scores quality 0,
            // which costs it all its lift and leaves it on Restitution alone — it slaps
            // along and dies within a hop or two. There is no separate sink-on-bad-angle
            // branch, because that put a step in the middle of the skill curve: one degree
            // separated a nine-skip throw from zero, on a gesture that fires the instant
            // the wrist has pitched over.
            //
            // Measured against BestFaceAngleDeg, which defaults to 0 — a phone held as it
            // was at the tap is a good throw. This line used to compare against a separate
            // OptimalAngleDeg of 20 while FaceAngleDeg read 0 for a neutral phone, so
            // every throw anyone could actually make scored quality 0, lost all its lift,
            // and slapped along on Restitution alone. That was the "rocks barely skip" bug,
            // and it is why raising Restitution appeared to help: with lift gone it was the
            // only bounce term left, and it has no power in it to scale with.
            var angleError = Mathf.Abs(next.FaceAngleDeg - tuning.BestFaceAngleDeg);
            var quality = 1f - Mathf.Clamp01(angleError / Mathf.Max(tuning.ToleranceDeg, 0.0001f));

            var velocity = next.Velocity;

            // Power, not spin. This is the second half of "a hard throw goes further":
            // it starts faster, and it also bleeds speed more slowly, so the two compound
            // over a run. Spin used to sit here, which meant distance had two drivers and
            // the player could feel neither.
            var speedKept = Mathf.Lerp(tuning.SpeedKeptSoft, tuning.SpeedKeptHard, next.Power);

            // Speed loss first, so the lift below is computed against the horizontal the
            // stone actually leaves with. The other order made SkipExitAngleDeg a lie: the
            // true exit angle was atan(tan(x) / speedKept), and it moved whenever that did.
            velocity.x *= speedKept;
            velocity.z *= speedKept;

            // Water doesn't just mirror the stone — the face planes off the surface and
            // kicks it out steeper than it came in.
            //
            // The angle falls from the opening pop to the settled one over the first few
            // skips, which is what gives a throw a visible start and then a long shrinking
            // run. Nothing about the energy changes: the speed loss above already happened,
            // so this only redirects what is left.
            var settle = Mathf.Clamp01(next.SkipCount / Mathf.Max(tuning.OpeningHopSkips, 1f));
            var exitAngle = Mathf.Lerp(tuning.OpeningHopAngleDeg, tuning.SkipExitAngleDeg, settle);

            // Scaled by quality alone. There used to be a power term here as well, on the
            // theory that a hard throw should hop higher — but hop size already goes as
            // speed², so a hard throw's hops are enormously bigger without it. All it
            // actually did was give power a second, hidden route to the bounce, which is
            // what made the response to throw strength hard to read.
            var exitHorizontal = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            var liftY = exitHorizontal * Mathf.Tan(exitAngle * Mathf.Deg2Rad) * quality;

            var rise = Mathf.Max(-velocity.y * tuning.Restitution, liftY);
            if (rise < MinExitRiseSpeed)
            {
                next.Sunk = true;
                next.Velocity = Vector3.zero;
                result.Sank = true;
                result.State = next;
                return result;
            }

            velocity.y = rise;

            next.Velocity = velocity;
            next.SkipCount++;

            result.Bounced = true;
            result.State = next;
            return result;
        }

        /// <summary>
        /// Run the throw forward and collect its path. Used by the aim preview, and by
        /// any tooling that wants a trajectory without spawning a rock.
        /// </summary>
        /// <param name="path">Filled with world positions. Cleared first; reuse the list to avoid per-frame garbage.</param>
        /// <returns>The final state, so callers can read SkipCount and distance.</returns>
        public static SkipState Predict(SkipState start, SkipTuning tuning, List<Vector3> path)
        {
            path.Clear();
            path.Add(start.Position);

            var state = start;

            for (var i = 0; i < tuning.MaxPredictSteps; i++)
            {
                var step = Step(state, tuning, tuning.FixedStep);
                state = step.State;
                path.Add(state.Position);

                if (state.Sunk) break;
            }

            return state;
        }
    }
}
