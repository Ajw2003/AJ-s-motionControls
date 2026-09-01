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
        /// How fast the wrist was turning about the phone's screen normal, deg/s, signed.
        /// Turns the stone on screen, widens every forgiveness band (see
        /// SkipConstants.SpinToleranceScale), and — the real work — holds the stone's
        /// attack angle through each impact. Decays every bounce by
        /// SkipConstants.SpinRetention, so stability always runs out eventually.
        /// </summary>
        public float Spin;

        /// <summary>
        /// Accumulated attitude drift, degrees. A real stone skips because spin holds its
        /// face steady through the slap; without spin it tumbles, the attack angle
        /// randomises and the run dies in a bounce or two. This is that: every bounce adds
        /// drift in proportion to how UNstabilised the stone is, and the total is charged
        /// against the pitch and roll errors, so quality decays over a run at a rate spin
        /// controls.
        ///
        /// It is also what stops attitude being frozen at launch. Before this existed,
        /// quality was computed once from the release pose and never changed, so nothing
        /// about a run ever degraded.
        /// </summary>
        public float WanderDeg;

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
    ///
    /// A bounce is ONE ENERGY BUDGET: it takes a loss, then splits what survives between
    /// forward and upward motion. It never synthesises lift out of horizontal speed. That
    /// distinction is load-bearing — the earlier version did synthesise it, and stones
    /// gained energy every bounce and skipped for minutes on end. Because the loss is
    /// floored at SkipConstants.MinLoss and the split preserves magnitude exactly,
    /// outgoing speed is always strictly less than incoming speed, no matter what any
    /// tuning field is set to. Keep it that way: any future change here should be checked
    /// against that one invariant first. See docs/plans/Plan-SkipEnergyInvariant.md.
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

        // The falloff and hard-limit thresholds that used to live here as private consts are
        // now fields on SkipConstants (RollFalloffDeg, MaxRollDeg, PitchSteepFalloffDeg,
        // PitchShallowFalloffDeg, YawFalloffDeg, MaxIncidenceDeg). They decide what counts as
        // a flat-out failed throw, which is a feel question that has to be answerable without
        // a recompile. OptimalAttackDeg stays a const below: it is a fact about skipping, not
        // a preference.

        /// <summary>
        /// How much harder a bad bounce is than a clean one. Multiplies the energy loss at
        /// quality 0; quality 1 pays the authored EnergyLoss unmodified.
        /// </summary>
        private const float WorstQualityLossScale = 2.5f;

        /// <summary>Energy-loss scaling at glancing incidence and at the steepest survivable
        /// incidence. A steeper arrival slaps harder and costs more.</summary>
        private const float ShallowIncidenceLossScale = 0.8f;
        private const float SteepIncidenceLossScale = 1.6f;

        /// <summary>Hard ceiling on the share of surviving energy that can go upward. Well
        /// below 1 — a bounce that sent everything vertical would stop dead in the air.</summary>
        private const float MaxLiftShare = 0.5f;

        /// <summary>How sharply bank steers the stone, radians of heading change per unit
        /// sin(roll). A rotation of the horizontal velocity, never an addition to it — see
        /// the energy remarks in Step.</summary>
        private const float RollSteerRad = 0.05f;

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
                // every forgiveness band (see EffectiveBand) and, above all, stabilises the
                // stone against tumbling — see SkipState.WanderDeg.
                Spin = spinRate * Mathf.Clamp(tuning.SpinMultiplier, 0.8f, 1.2f),
                WanderDeg = 0f,
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
            if (speed < MinSkipSpeed || incidenceDeg > constants.MaxIncidenceDeg
                || Mathf.Abs(next.RollDeg) + next.WanderDeg > constants.MaxRollDeg)
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
            //
            // WanderDeg is charged against pitch and roll here: it is the tumble a stone
            // picks up when spin is not holding it steady, so a poorly spun run degrades
            // bounce by bounce instead of repeating one frozen launch pose forever.
            var pitchError = next.FaceAngleDeg - OptimalAttackDeg;
            var pitchBand = EffectiveBand(constants.ForgivenessPitchDeg, next.Spin, constants.SpinToleranceScale);
            var pitchFalloff = pitchError >= 0f ? constants.PitchSteepFalloffDeg : constants.PitchShallowFalloffDeg;
            var pitchQuality = BandQuality(Mathf.Abs(pitchError) + next.WanderDeg, pitchBand, pitchFalloff);

            var rollBand = EffectiveBand(constants.ForgivenessRollDeg, next.Spin, constants.SpinToleranceScale);
            var rollQuality = BandQuality(Mathf.Abs(next.RollDeg) + next.WanderDeg, rollBand, constants.RollFalloffDeg);

            // Sideslip: how far the velocity has drifted from where the stone was aimed.
            // Zero at launch by construction (CreateLaunch builds velocity straight off
            // heading); a banked run curves and this is what starts costing it.
            var yawErrorDeg = horizontalSpeed > 0.0001f
                ? Vector3.Angle(horizontal, next.HeadingDir)
                : 0f;
            var yawBand = EffectiveBand(constants.ForgivenessYawDeg, next.Spin, constants.SpinToleranceScale);
            var yawQuality = BandQuality(yawErrorDeg, yawBand, constants.YawFalloffDeg);

            var quality = pitchQuality * rollQuality * yawQuality;

            // ── ONE ENERGY BUDGET ────────────────────────────────────────────────────────
            // Take the loss first, then split what SURVIVES between forward and upward.
            // Lift is a share of the post-loss budget, never a quantity synthesised from
            // horizontal speed — that was the bug this model replaces, where a stone
            // arriving with vy ~ 0 left with vy = 0.09 * vh and runs never ended.
            //
            // The invariant, and it is structural rather than tuned: lossFraction is
            // floored at MinLoss > 0, and the split preserves magnitude exactly
            // (vy^2 + vh^2 = speedAfter^2), so outgoing speed is ALWAYS strictly less than
            // incoming speed — whatever quality, roll, or the rock's multipliers do.
            var incidenceFactor = Mathf.Clamp01(incidenceDeg / constants.MaxIncidenceDeg);
            var lossShape = Mathf.Lerp(1f, WorstQualityLossScale, 1f - quality)
                            * Mathf.Lerp(ShallowIncidenceLossScale, SteepIncidenceLossScale, incidenceFactor)
                            * Mathf.Clamp(tuning.HorizontalMultiplier, 0.95f, 1.05f);
            var lossFraction = Mathf.Clamp(constants.EnergyLoss * lossShape, constants.MinLoss, 0.95f);

            var speedAfter = speed * Mathf.Sqrt(1f - lossFraction);

            // Roll scales the lift down (a stone on its edge planes less) via the same
            // rotated face normal the mesh is drawn with — see SkipState.FaceNormal.
            var rollRad = next.RollDeg * Mathf.Deg2Rad;
            var liftShare = Mathf.Clamp(
                constants.LiftShare * quality * Mathf.Cos(rollRad) * Mathf.Clamp(tuning.VerticalMultiplier, 0.7f, 1.3f),
                0f, MaxLiftShare);

            var rise = speedAfter * Mathf.Sqrt(liftShare);
            if (rise < MinExitRiseSpeed)
            {
                next.Sunk = true;
                next.Velocity = Vector3.zero;
                result.Sank = true;
                result.State = next;
                return result;
            }

            // Bank steers by ROTATING the surviving horizontal velocity, not by adding a
            // lateral kick to it. Same visible curve, but a rotation cannot change the
            // vector's magnitude, so it cannot smuggle energy in the way the old additive
            // kick did.
            var exitHorizontalSpeed = speedAfter * Mathf.Sqrt(1f - liftShare);
            var heading = horizontalSpeed > 0.0001f ? horizontal / horizontalSpeed : next.HeadingDir;
            var steered = Quaternion.AngleAxis(
                Mathf.Sin(rollRad) * RollSteerRad * Mathf.Rad2Deg, Vector3.up) * heading;

            next.Velocity = steered * exitHorizontalSpeed + Vector3.up * rise;

            // Spin holds the stone steady; what spin does not cover accumulates as tumble,
            // and spin itself bleeds off — so stability always runs out and the run ends.
            var stability = Mathf.Clamp01(Mathf.Abs(next.Spin) / Mathf.Max(constants.SpinForStability, 0.0001f));
            next.WanderDeg += (1f - stability) * constants.WanderPerBounce;
            next.Spin *= constants.SpinRetention;

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
