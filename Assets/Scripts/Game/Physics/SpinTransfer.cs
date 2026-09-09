using UnityEngine;

namespace Game
{
    /// <summary>
    /// Converts the phone's measured rotation rate into the stone's spin rate.
    /// </summary>
    /// <remarks>
    /// <b>The problem this solves.</b> The phone reports `spinRate` in deg/s about its own
    /// screen normal — how fast the wrist twisted. The solver needs the stone's spin about
    /// its face normal. The old code treated those as the SAME NUMBER, and that identity is
    /// an invented assumption, not physics: a phone and a pebble are different sizes, so
    /// equal angular rates are not equal throws.
    ///
    /// What is actually conserved at release is the TANGENTIAL SPEED AT THE GRIP POINT. A
    /// real skipper holds the stone with the index finger curled around its edge; the finger
    /// pulls, the edge moves at the finger's speed, and the stone's spin follows from its own
    /// radius:
    ///
    ///     v_tangential = ω_phone · r_grip          (what the hand actually did)
    ///     Ω_stone      = v_tangential / R_stone    (what the stone does with it)
    ///
    /// so
    ///
    ///     Ω_stone = ω_phone · (r_grip / R_stone)
    ///
    /// A big object turning slowly has the same rim speed as a small object turning fast,
    /// which is why the ratio is above 1 for any stone smaller than a phone is long. This is
    /// the whole model: one ratio of two measurable lengths, no free parameter.
    ///
    /// <b>What this does NOT fix, and nothing on this side can.</b> Measured against 124 real
    /// throws (`docs/Findings-RealSkipPhysics.md` §6.2), the phone's best recorded twist is
    /// 3.83 rev/s. Bocquet's bounce ceiling `N_c = R·Ω²/g` wants ~14 rev/s for a world-record
    /// run. This conversion lifts the best throw from about 3 bounces to about 7 — a real
    /// improvement, and still far short.
    ///
    /// The residual is not a tuning shortfall, it is a measurement one: a wrist twist is not
    /// a finger flick, and the phone is not being asked to perform one. A real flick moves the
    /// fingertip at roughly 4.4 m/s (= 88 rad/s × 0.05 m); the phone gesture manages roughly
    /// 1.7 m/s of rim speed. Closing the rest means changing what the CONTROLLER captures, not
    /// what this file computes. Do not "fix" it by inflating <see cref="GripRadius"/> past a
    /// real phone's half-length — that would turn a measured constant back into a feel knob,
    /// which is the failure mode this whole rewrite exists to end.
    /// </remarks>
    public static class SpinTransfer
    {
        /// <summary>
        /// Effective radius from the phone's rotation axis to the grip point, in metres —
        /// about half the long edge of an ordinary phone (~15 cm).
        /// </summary>
        /// <remarks>
        /// A measured length, not a tuning value. If it ever needs to change it is because
        /// phones changed size, and the honest fix is to measure one. See the class remarks
        /// on why inflating it to buy skips is the wrong move.
        /// </remarks>
        public const float GripRadius = 0.075f;

        /// <summary>
        /// The stone's spin in rad/s, from the phone's measured rotation in deg/s.
        /// </summary>
        /// <param name="phoneRateDegPerSec">
        /// Signed, straight off the wire (<c>ThrowMessage.spinRate</c>). Sign is preserved:
        /// which way the wrist turned decides which way the stone turns on screen.
        /// </param>
        /// <param name="stoneRadius">
        /// The stone's radius in metres. Smaller stones spin FASTER for the same hand motion,
        /// which is the physically correct direction and falls out rather than being authored.
        /// </param>
        public static float ToStoneSpinRadPerSec(float phoneRateDegPerSec, float stoneRadius)
        {
            // A zero or negative radius has no rim to impart spin to, and would divide by
            // zero. Return the honest answer rather than an infinity that poisons the solver.
            if (stoneRadius <= 0f) return 0f;

            var phoneRateRadPerSec = phoneRateDegPerSec * Mathf.Deg2Rad;
            var tangentialSpeed = phoneRateRadPerSec * GripRadius;

            return tangentialSpeed / stoneRadius;
        }

        /// <summary>
        /// Tangential speed at the grip point, m/s — the quantity that is actually conserved.
        /// </summary>
        /// <remarks>
        /// Exposed because it is the number to compare against a real finger flick (~4.4 m/s)
        /// when judging whether the CONTROLLER is capturing enough of the gesture. The solver
        /// itself never needs it; <see cref="ToStoneSpinRadPerSec"/> folds it in.
        /// </remarks>
        public static float GripTangentialSpeed(float phoneRateDegPerSec) =>
            phoneRateDegPerSec * Mathf.Deg2Rad * GripRadius;
    }
}
