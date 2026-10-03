using UnityEngine;

namespace Game
{
    // Stone spin is phone rate times grip radius over stone radius; the remaining shortfall is the controller's
    // measurement, so do not inflate GripRadius. doc-ref 322d docs/4-systems/SkipPhysics.md
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
