using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    /// <summary>Everything describing a rock mid-flight.</summary>
    public struct SkipState
    {
        public Vector3 Position;
        public Vector3 Velocity;

        /// <summary>Face angle in degrees from horizontal. The variable that decides a bounce.</summary>
        public float AttitudeDeg;

        /// <summary>Gyroscopic stability. Widens the angle tolerance while it lasts.</summary>
        public float Spin;

        /// <summary>
        /// How hard this was thrown, 0..1. Set at launch and never changed — it is a
        /// property of the throw, not of the stone's current state.
        /// </summary>
        /// <remarks>
        /// Carried so the visuals can scale with effort: a stone flung hard should look
        /// like it, and spin rate alone does not convey that. Kept on the state rather
        /// than passed alongside it so the aim ghost and the live stone cannot disagree.
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
        /// <summary>Build the initial state from a throw descriptor.</summary>
        public static SkipState CreateLaunch(
            Vector3 origin, Vector3 forward, float power, float timingQuality,
            float aimDeg, float attackAngleDeg, float spinRate, SkipTuning tuning)
        {
            // Poor timing costs speed rather than blocking the throw — a mistimed
            // release should feel weak, not broken.
            var speed = Mathf.Lerp(tuning.MinSpeed, tuning.MaxSpeed, Mathf.Clamp01(power))
                      * Mathf.Lerp(tuning.WorstTimingSpeedScale, 1f, Mathf.Clamp01(timingQuality));

            var heading = Quaternion.AngleAxis(aimDeg, Vector3.up) * forward.normalized;
            var pitch = tuning.LaunchPitchDeg * Mathf.Deg2Rad;

            var velocity = heading * (speed * Mathf.Cos(pitch)) + Vector3.up * (speed * Mathf.Sin(pitch));

            return new SkipState
            {
                Position = origin,
                Velocity = velocity,

                // Phone space -> physics space. attackAngleDeg is a delta from the
                // player's grip, so it is 0 when they hold naturally; OptimalAngleDeg
                // maps that onto the angle a real stone wants. See SkipTuning for why
                // this is the same field the bounce measures error against.
                AttitudeDeg = attackAngleDeg + tuning.OptimalAngleDeg,
                Spin = Mathf.Abs(spinRate),
                Power = Mathf.Clamp01(power),
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

            // Spin holds the stone's attitude steady, so it forgives a worse angle.
            var tolerance = tuning.BaseToleranceDeg
                          + tuning.MaxSpinToleranceBonus * tuning.SpinFraction(next.Spin);

            var angleError = Mathf.Abs(next.AttitudeDeg - tuning.OptimalAngleDeg);

            var tooSlow = speed < tuning.MinSkipSpeed;
            var tooSteep = incidenceDeg > tuning.MaxIncidenceDeg;

            // Past tolerance is a bad bounce, not an instant sink — quality below falls to
            // zero there, so the stone skids on its worst restitution with no exit lift and
            // dies out over the next hop or two. Only a hopeless angle ends it outright.
            // See SkipTuning.SinkAngleMultiplier for why the cliff had to go.
            var hopeless = angleError > tolerance * tuning.SinkAngleMultiplier;

            if (tooSlow || tooSteep || hopeless)
            {
                next.Sunk = true;
                next.Velocity = Vector3.zero;
                result.Sank = true;
                result.State = next;
                return result;
            }

            // Closer to the stone's optimal angle keeps more energy.
            var quality = 1f - Mathf.Clamp01(angleError / Mathf.Max(tolerance, 0.0001f));
            var restitution = Mathf.Lerp(tuning.WorstRestitution, tuning.BestRestitution, quality);

            var velocity = next.Velocity;

            // Spin holds the stone's line through the bounce, so it keeps more of its
            // speed. This is the only route by which a harder flick reaches further —
            // spin is deliberately absent from the launch speed, which is set by power.
            var drag = Mathf.Lerp(tuning.Drag, tuning.DragAtFullSpin, tuning.SpinFraction(next.Spin));

            // Drag first, so the lift below is computed against the horizontal the stone
            // actually leaves with. The other order made SkipExitAngleDeg a lie: the true
            // exit angle was atan(tan(x) / Drag), and it moved whenever Drag did.
            velocity.x *= drag;
            velocity.z *= drag;

            // Water doesn't just mirror the stone — the face planes off the surface and
            // kicks it out steeper than it came in. Without a floor the vertical decays
            // three times faster than the horizontal (restitution vs Drag) and the skips
            // flatten into a slide. Scaled by quality so a badly angled stone still skids.
            var exitHorizontal = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            var liftY = exitHorizontal * Mathf.Tan(tuning.SkipExitAngleDeg * Mathf.Deg2Rad) * quality;
            velocity.y = Mathf.Max(-velocity.y * restitution, liftY);

            next.Velocity = velocity;
            next.Spin *= tuning.SpinDecay;
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
        public static SkipState Predict(
            SkipState start, SkipTuning tuning, List<Vector3> path, int maxSteps = 0)
        {
            path.Clear();
            path.Add(start.Position);

            var state = start;
            var steps = maxSteps > 0 ? maxSteps : tuning.MaxPredictSteps;

            for (var i = 0; i < steps; i++)
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
