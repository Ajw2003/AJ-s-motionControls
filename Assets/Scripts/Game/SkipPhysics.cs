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
        /// The stone's face normal at launch, in world space — <see cref="PhoneAttitude.FaceNormal"/>
        /// of <see cref="Attitude"/>. Roll's two effects on a bounce (lift scaled by
        /// cos(roll), lift deflected sideways) both fall out of this one rotated vector, so
        /// Step reads it directly rather than decomposing and recomposing pitch/roll
        /// scalars — see the plan's remarks on the old three-angle bug.
        /// </summary>
        public Vector3 FaceNormal;

        /// <summary>
        /// Where the stone was aimed at launch, flattened to the water plane. The velocity
        /// starts aligned with this; over a run, roll's sideways lift can push the velocity
        /// away from it, which is the sideslip the yaw forgiveness band judges.
        /// </summary>
        public Vector3 HeadingDir;

        /// <summary>
        /// Face angle in degrees above horizontal, straight off the phone's tilt at launch
        /// — the toss angle AND the attack angle the bounce judges are the same number, on
        /// purpose. See PhoneAttitude.FaceAngleDeg and the plan's "no hidden neutral-pose
        /// offset" section.
        /// </summary>
        public float FaceAngleDeg;

        /// <summary>Bank at launch, degrees. See PhoneAttitude.RollDeg.</summary>
        public float RollDeg;

        /// <summary>
        /// How fast the wrist was turning about the phone's screen normal at release,
        /// deg/s, signed. Mostly visual — it turns the stone on screen — but it also widens
        /// every forgiveness band (see SkipConstants.SpinToleranceScale): spin buys
        /// tolerance and nothing else.
        /// </summary>
        public float Spin;

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
    ///
    /// The model: the throw drives, the rock modulates. Speed comes straight from the
    /// phone's swing (SkipConstants.SpeedScale converts hand m/s to stone m/s); energy
    /// loss per bounce falls out of how steeply the stone hits and how far its face is
    /// from level, not from an authored retention constant; and each rock only scales
    /// that geometry with three bounded multipliers. See
    /// docs/plans/Plan-ThrowDrivenSkipModel.md for the full reasoning.
    /// </remarks>
    public static class SkipPhysics
    {
        /// <summary>
        /// The empirically optimal attack angle for a skipping stone (Clanet, Hersen &amp;
        /// Bocquet, 2004). A statement about what skipping IS, not a per-rock or global
        /// knob — nobody ever needed to dial it, only how forgiving missing it is
        /// (SkipConstants.ForgivenessPitchDeg).
        /// </summary>
        private const float OptimalAttackDeg = 20f;

        /// <summary>How fast quality falls away on the steep side of the optimum — plowing in
        /// is the abrupt failure mode, so this is a tight window.</summary>
        private const float SteepFalloffDeg = 22f;

        /// <summary>How fast quality fades on the shallow side — a weak-lift skim is the
        /// gradual, survivable failure mode, so this is a wide window.</summary>
        private const float ShallowFalloffDeg = 55f;

        /// <summary>How fast quality fades outside the roll forgiveness band.</summary>
        private const float RollFalloffDeg = 35f;

        /// <summary>How fast quality fades outside the yaw (sideslip) forgiveness band.</summary>
        private const float YawFalloffDeg = 40f;

        /// <summary>Past this bank the leading edge digs in — the stone catches and sinks,
        /// exactly like a real over-rolled stone does.</summary>
        private const float MaxRollDeg = 80f;

        /// <summary>
        /// Steeper than this and the stone plows in instead of skipping. A constant
        /// rather than a tuning field: it is a statement about what skipping *is*, and
        /// nobody ever needed to dial it.
        /// </summary>
        private const float MaxIncidenceDeg = 45f;

        /// <summary>
        /// The real exit angle a healthy skipping stone leaves the water at — 2-6 degrees.
        /// Applied to the horizontal speed that survives the bounce, so it never has to be
        /// corrected for the loss the way an authored pre-loss angle would.
        /// </summary>
        private const float ExitAngleDeg = 5f;

        /// <summary>
        /// Energy a bounce keeps even with zero lift quality — the physical minimum for a
        /// stone slapping the surface rather than a free parameter. Not zero: at zero the
        /// stone grinds along the waterline in meaningless skips before the speed floor
        /// below catches it.
        /// </summary>
        private const float MinimalRestitution = 0.25f;

        /// <summary>Below this speed the stone stops skipping and sinks, regardless of angle.</summary>
        private const float MinSkipSpeed = 2.5f;

        /// <summary>
        /// Leave the water slower than this, vertically, and the stone did not skip — it
        /// went in. A guard against grinding, not the thing that ends a run — see MinSkipSpeed.
        /// </summary>
        private const float MinExitRiseSpeed = 0.3f;

        /// <summary>
        /// A forgiveness band widened by spin, deg per deg/s of measured wrist spin — see
        /// SkipConstants.SpinToleranceScale. A band authored at 0 stays off regardless of
        /// spin: spin buys tolerance, it does not turn tolerance on where none was authored.
        /// </summary>
        private static float EffectiveBand(float authoredBandDeg, float spinRate, float spinToleranceScale) =>
            authoredBandDeg <= 0f ? 0f : authoredBandDeg + Mathf.Abs(spinRate) * spinToleranceScale;

        /// <summary>0..1 quality from how far a measured value sits outside its forgiveness
        /// band, fading to 0 over falloffDeg once past it.</summary>
        private static float BandQuality(float errorDeg, float effectiveBandDeg, float falloffDeg)
        {
            var excess = Mathf.Max(0f, Mathf.Abs(errorDeg) - effectiveBandDeg);
            return Mathf.Clamp01(1f - excess / Mathf.Max(falloffDeg, 0.0001f));
        }

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

            // Where the stone is pointing IS where it goes. Both read off the same
            // rotation the mesh is drawn with, so the throw can never disagree with the
            // stone the player watched themselves aim.
            var heading = PhoneAttitude.HeadingDirection(attitude);

            // The phone's own tilt IS the toss angle — no separate LaunchPitchDeg, and no
            // hidden neutral-pose offset. A phone held flat throws flat.
            var pitchDeg = PhoneAttitude.FaceAngleDeg(attitude);
            var pitch = pitchDeg * Mathf.Deg2Rad;
            var velocity = heading * (speed * Mathf.Cos(pitch)) + Vector3.up * (speed * Mathf.Sin(pitch));

            return new SkipState
            {
                Position = origin,
                Velocity = velocity,
                Attitude = attitude,
                FaceNormal = PhoneAttitude.FaceNormal(attitude),
                HeadingDir = heading,
                FaceAngleDeg = pitchDeg,
                RollDeg = PhoneAttitude.RollDeg(attitude),
                // Signed. Which way the wrist turned is mostly visual, but it also widens
                // every forgiveness band — see EffectiveBand.
                Spin = spinRate * Mathf.Clamp(tuning.SpinMultiplier, 0.5f, 1.5f),
                SkipCount = 0,
                Sunk = false,
            };
        }

        /// <summary>Advance one fixed step. Pure — see the class remarks.</summary>
        public static SkipStep Step(in SkipState state, SkipConstants constants, SkipTuning tuning, float dt)
        {
            var next = state;
            var result = new SkipStep();

            if (next.Sunk)
            {
                result.State = next;
                return result;
            }

            next.Velocity += Vector3.down * (constants.Gravity * dt);
            next.Position += next.Velocity * dt;

            var crossedSurface = next.Position.y <= constants.WaterY && next.Velocity.y < 0f;
            if (!crossedSurface)
            {
                result.State = next;
                return result;
            }

            next.Position.y = constants.WaterY;

            var speed = next.Velocity.magnitude;
            result.ImpactSpeed = speed;

            // How steeply it is coming in, measured against the water plane.
            var horizontal = new Vector3(next.Velocity.x, 0f, next.Velocity.z);
            var horizontalSpeed = horizontal.magnitude;
            var incidenceDeg = horizontalSpeed > 0.0001f
                ? Vector3.Angle(next.Velocity, horizontal)
                : 90f;

            // Bank far enough and the leading edge digs in — the same normal that gives a
            // level stone its lift is what says an over-rolled one catches and sinks.
            if (speed < MinSkipSpeed || incidenceDeg > MaxIncidenceDeg || Mathf.Abs(next.RollDeg) > MaxRollDeg)
            {
                next.Sunk = true;
                next.Velocity = Vector3.zero;
                result.Sank = true;
                result.State = next;
                return result;
            }

            // Three axes, three bands, three qualities, multiplied together. Each is a
            // gradient, not a cliff — see BandQuality — and past the pitch band the falloff
            // is asymmetric: too shallow fades gradually (a survivable skim), too steep
            // collapses fast (an abrupt plow-in). That asymmetry is the whole reason pitch
            // has two falloff constants instead of one.
            var pitchError = next.FaceAngleDeg - OptimalAttackDeg;
            var pitchBand = EffectiveBand(constants.ForgivenessPitchDeg, next.Spin, constants.SpinToleranceScale);
            var pitchFalloff = pitchError >= 0f ? SteepFalloffDeg : ShallowFalloffDeg;
            var pitchQuality = BandQuality(pitchError, pitchBand, pitchFalloff);

            var rollBand = EffectiveBand(constants.ForgivenessRollDeg, next.Spin, constants.SpinToleranceScale);
            var rollQuality = BandQuality(next.RollDeg, rollBand, RollFalloffDeg);

            // Sideslip: how far the velocity has drifted from where the stone was aimed.
            // Zero at launch by construction (CreateLaunch builds velocity straight off
            // heading); a banked run curves and this is what starts costing it.
            var yawErrorDeg = horizontalSpeed > 0.0001f
                ? Vector3.Angle(horizontal, next.HeadingDir)
                : 0f;
            var yawBand = EffectiveBand(constants.ForgivenessYawDeg, next.Spin, constants.SpinToleranceScale);
            var yawQuality = BandQuality(yawErrorDeg, yawBand, YawFalloffDeg);

            var quality = pitchQuality * rollQuality * yawQuality;

            // Energy loss falls out of geometry: how steeply it hit, and how far off the
            // face was from a good bounce. No authored retention constant — the rock only
            // scales the result via HorizontalMultiplier.
            var incidenceFactor = Mathf.Clamp01(incidenceDeg / MaxIncidenceDeg);
            var lossFraction = constants.WaterLossCoefficient * Mathf.Lerp(incidenceFactor, 1f, 1f - quality);
            var speedKept = Mathf.Clamp01(1f - lossFraction) * Mathf.Clamp(tuning.HorizontalMultiplier, 0.5f, 1.5f);

            var velocity = next.Velocity;
            velocity.x *= speedKept;
            velocity.z *= speedKept;

            // Roll's two effects, both from the one rotated face normal: lift falls off
            // with bank (cos), and what remains is deflected sideways (sin), so a banked
            // throw curves. Not decomposed pitch/roll scalars recomposed later — see the
            // FaceNormal remarks on SkipState.
            var rollRad = next.RollDeg * Mathf.Deg2Rad;
            var rollLiftScale = Mathf.Cos(rollRad);

            var exitHorizontal = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            var liftY = exitHorizontal * Mathf.Tan(ExitAngleDeg * Mathf.Deg2Rad) * quality * rollLiftScale
                        * Mathf.Clamp(tuning.VerticalMultiplier, 0.5f, 1.5f);

            var rise = Mathf.Max(-velocity.y * MinimalRestitution, liftY);
            if (rise < MinExitRiseSpeed)
            {
                next.Sunk = true;
                next.Velocity = Vector3.zero;
                result.Sank = true;
                result.State = next;
                return result;
            }

            // The lateral deflection: what lift there is gets pushed sideways by sin(roll),
            // banking the whole reaction force the way a real edge-tilted face does.
            var lateralDir = Vector3.Cross(Vector3.up, next.HeadingDir).normalized;
            var lateralKick = liftY * Mathf.Sin(rollRad);
            velocity += lateralDir * lateralKick;

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

                if (state.Sunk) break;
            }

            return state;
        }
    }
}
