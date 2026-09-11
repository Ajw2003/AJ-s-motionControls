using Game;
using NUnit.Framework;
using UnityEngine;

namespace RockSkipping.Physics.Tests
{
    /// <summary>
    /// EditMode coverage for the pure skip solver (Assets/Scripts/Game/Physics/SkipPhysics.cs).
    /// These exist so the invariants documented in that file's class remarks — purity and
    /// the one-energy-budget guarantee — are checked by CI rather than only by eyeballing
    /// skip-curve.txt.
    /// </summary>
    public class SkipPhysicsTests
    {
        private static SkipConstants DefaultConstants() => SkipConstants.CreateDefault();
        private static SkipTuning DefaultTuning() => SkipTuning.CreateDefault();

        private static SkipState MakeLaunchState(SkipConstants constants, SkipTuning tuning,
            float swingSpeed = 30f, float pitchDeg = 20f, float rollDeg = 0f, float spinRate = 400f)
        {
            var pitchRad = pitchDeg * Mathf.Deg2Rad;
            var forward = new Vector3(0f, Mathf.Sin(pitchRad), Mathf.Cos(pitchRad));
            var level = Quaternion.LookRotation(forward, Vector3.up);
            var attitude = Quaternion.AngleAxis(rollDeg, forward) * level;

            return SkipPhysics.CreateLaunch(Vector3.zero, swingSpeed, attitude, spinRate, constants, tuning);
        }

        [Test]
        public void Step_CalledTwiceWithIdenticalInput_ProducesIdenticalOutput()
        {
            var constants = DefaultConstants();
            var tuning = DefaultTuning();
            var state = MakeLaunchState(constants, tuning);

            var resultA = SkipPhysics.Step(in state, constants, tuning, constants.FixedStep);
            var resultB = SkipPhysics.Step(in state, constants, tuning, constants.FixedStep);

            Assert.That(resultB.State.Position, Is.EqualTo(resultA.State.Position),
                "Step must be pure: identical input must produce identical output position.");
            Assert.That(resultB.State.Velocity, Is.EqualTo(resultA.State.Velocity),
                "Step must be pure: identical input must produce identical output velocity.");
            Assert.That(resultB.Bounced, Is.EqualTo(resultA.Bounced),
                "Step must be pure: identical input must produce identical Bounced flag.");
            Assert.That(resultB.HasSunk, Is.EqualTo(resultA.HasSunk),
                "Step must be pure: identical input must produce identical HasSunk flag.");
            Assert.That(resultB.ImpactSpeed, Is.EqualTo(resultA.ImpactSpeed),
                "Step must be pure: identical input must produce identical ImpactSpeed.");
        }

        [Test]
        public void Step_DoesNotMutateTheInputState()
        {
            var constants = DefaultConstants();
            var tuning = DefaultTuning();
            var state = MakeLaunchState(constants, tuning);

            var before = state;

            SkipPhysics.Step(in state, constants, tuning, constants.FixedStep);

            Assert.That(state.Position, Is.EqualTo(before.Position),
                "Step must not mutate the caller's SkipState — the aim preview runs Step " +
                "speculatively and must not disturb the state it was given.");
            Assert.That(state.Velocity, Is.EqualTo(before.Velocity),
                "Step must not mutate the caller's SkipState (Velocity).");
            Assert.That(state.SkipCount, Is.EqualTo(before.SkipCount),
                "Step must not mutate the caller's SkipState (SkipCount).");
            Assert.That(state.HasSunk, Is.EqualTo(before.HasSunk),
                "Step must not mutate the caller's SkipState (HasSunk).");
        }

        [Test]
        public void Run_AlwaysTerminates_AndEveryBounceLosesSpeed()
        {
            var constants = DefaultConstants();
            var tuning = DefaultTuning();
            var state = MakeLaunchState(constants, tuning, swingSpeed: 30f, pitchDeg: 20f, rollDeg: 0f);

            // Generous time budget — far beyond SkipConstants.PredictSeconds — so a
            // solver that fails to terminate is caught here rather than only in
            // skip-curve.txt's "every throw sinks — no runaway" check.
            const float terminationBudgetSeconds = 120f;
            var maxSteps = Mathf.CeilToInt(terminationBudgetSeconds / constants.FixedStep);

            var terminated = false;
            for (var i = 0; i < maxSteps; i++)
            {
                var incomingSpeed = state.Velocity.magnitude;
                var step = SkipPhysics.Step(in state, constants, tuning, constants.FixedStep);
                state = step.State;

                if (step.Bounced)
                {
                    // ONE ENERGY BUDGET: a bounce must never return more energy than it
                    // received. ImpactSpeed is the incoming speed at the moment of
                    // contact; the outgoing speed is the post-bounce velocity magnitude.
                    Assert.That(step.State.Velocity.magnitude, Is.LessThan(step.ImpactSpeed),
                        $"Bounce at step {i} returned more (or equal) speed than it received: " +
                        $"impact {step.ImpactSpeed}, outgoing {step.State.Velocity.magnitude}.");
                }

                if (state.HasSunk)
                {
                    terminated = true;
                    break;
                }
            }

            Assert.That(terminated, Is.True,
                $"A throw must sink within {terminationBudgetSeconds} s — a run that never " +
                "terminates means a bounce is returning more energy than it received.");
        }

        [Test]
        public void CreateLaunch_ScalesSwingSpeedBySpeedScale()
        {
            // D1: 9c3f201 dropped `* constants.SpeedScale` from CreateLaunch, so every
            // rock-throws.csv row since then under-reported swing speed. Guard it directly:
            // launch velocity magnitude must scale with SpeedScale, not just pass swingSpeed
            // straight through.
            var constants = DefaultConstants();
            constants.SpeedScale = 2f;
            var tuning = DefaultTuning();
            const float swingSpeed = 15f;

            var state = MakeLaunchState(constants, tuning, swingSpeed: swingSpeed, pitchDeg: 0f, rollDeg: 0f);

            Assert.That(state.Velocity.magnitude, Is.EqualTo(swingSpeed * constants.SpeedScale).Within(0.01f),
                $"CreateLaunch must scale swingSpeed by SpeedScale; expected " +
                $"{swingSpeed * constants.SpeedScale}, got {state.Velocity.magnitude}.");
        }

        [Test]
        public void CreateLaunch_ClampsSpinMultiplier()
        {
            // D1: 9c3f201 also dropped the [0.8, 1.2] clamp on SpinMultiplier from launch
            // spin. A rock authored (or mis-authored) outside that range must still only
            // ever scale spin within the clamp.
            var constants = DefaultConstants();
            var tuning = DefaultTuning();
            tuning.SpinMultiplier = 5f;
            const float spinRate = 300f;

            var state = MakeLaunchState(constants, tuning, spinRate: spinRate);

            Assert.That(state.Spin, Is.EqualTo(spinRate * 1.2f).Within(0.01f),
                $"CreateLaunch must clamp SpinMultiplier to [0.8, 1.2]; expected " +
                $"{spinRate * 1.2f}, got {state.Spin}.");
        }

        [Test]
        public void ReferenceThrow_ProducesAtLeastOneSkip()
        {
            var constants = DefaultConstants();
            var tuning = DefaultTuning();

            // A sane, firm reference throw at the physically optimal attack angle
            // (see SkipPhysics.OptimalAttackDeg / docs remarks) — this should clearly skip.
            var state = MakeLaunchState(constants, tuning, swingSpeed: 30f, pitchDeg: 20f, rollDeg: 0f, spinRate: 400f);

            for (var i = 0; i < 5000 && !state.HasSunk; i++)
            {
                var step = SkipPhysics.Step(in state, constants, tuning, constants.FixedStep);
                state = step.State;
            }

            Assert.That(state.SkipCount, Is.GreaterThanOrEqualTo(1),
                $"A sane throw (30 m/s swing, {20f} deg pitch, 0 roll, 400 deg/s spin) should " +
                $"produce at least one skip; got {state.SkipCount}.");
        }
    }
}
