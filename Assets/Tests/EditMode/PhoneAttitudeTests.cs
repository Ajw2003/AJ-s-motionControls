using Game;
using NUnit.Framework;
using UnityEngine;

namespace RockSkipping.Physics.Tests
{
    /// <summary>
    /// EditMode coverage for PhoneAttitude (Assets/Scripts/Game/Physics/PhoneAttitude.cs) —
    /// the one place phone space becomes Unity space. CLAUDE.md calls this "one conversion,
    /// reused everywhere a heading is needed", so these tests pin down what it actually
    /// returns.
    /// </summary>
    public class PhoneAttitudeTests
    {
        private const float Tolerance = 0.001f;

        private static Quaternion YawAttitude(float yawDeg) => Quaternion.AngleAxis(yawDeg, Vector3.up);

        [Test]
        public void HeadingDeg_AndHeadingDirection_Agree_ForSeveralKnownYaws()
        {
            foreach (var yawDeg in new[] { 0f, 30f, 90f, -45f, 135f, 179f, -179f })
            {
                var attitude = YawAttitude(yawDeg);

                var headingDeg = PhoneAttitude.HeadingDeg(attitude);
                var headingDir = PhoneAttitude.HeadingDirection(attitude);

                // The direction's own angle, recovered the same way HeadingDeg computes it,
                // must equal what HeadingDeg reports — they are two views of the same
                // rotated forward vector and must never disagree.
                var directionAngleDeg = Mathf.Atan2(headingDir.x, headingDir.z) * Mathf.Rad2Deg;

                Assert.That(directionAngleDeg, Is.EqualTo(headingDeg).Within(Tolerance),
                    $"At yaw {yawDeg} deg, HeadingDirection's own angle ({directionAngleDeg}) " +
                    $"must equal HeadingDeg ({headingDeg}) — both derive from the same rotated " +
                    "forward vector.");
            }
        }

        [Test]
        public void IdentityAttitude_GivesForwardHeading()
        {
            var attitude = Quaternion.identity;

            var headingDeg = PhoneAttitude.HeadingDeg(attitude);
            var headingDir = PhoneAttitude.HeadingDirection(attitude);

            Assert.That(headingDeg, Is.EqualTo(0f).Within(Tolerance),
                "A phone held at the neutral pose (identity attitude) must read as heading 0 deg.");
            Assert.That(Vector3.Distance(headingDir, Vector3.forward), Is.EqualTo(0f).Within(Tolerance),
                "A phone held at the neutral pose must produce a heading direction of Vector3.forward.");
        }

        [Test]
        public void HeadingDirection_IsAlwaysFlatAndNormalized()
        {
            // Sweep pitch and roll too, not just yaw — HeadingDirection must flatten
            // whatever attitude it is given onto the water plane.
            foreach (var pitchDeg in new[] { -80f, -30f, 0f, 20f, 45f, 70f })
            foreach (var rollDeg in new[] { -60f, 0f, 60f })
            foreach (var yawDeg in new[] { 0f, 90f, 200f })
            {
                var attitude = Quaternion.Euler(pitchDeg, yawDeg, rollDeg);
                var dir = PhoneAttitude.HeadingDirection(attitude);

                Assert.That(dir.y, Is.EqualTo(0f).Within(Tolerance),
                    $"HeadingDirection must be flat on the water plane (y ~ 0) for pitch {pitchDeg}, " +
                    $"roll {rollDeg}, yaw {yawDeg}; got y = {dir.y}.");
                Assert.That(dir.magnitude, Is.EqualTo(1f).Within(Tolerance),
                    $"HeadingDirection must be normalized for pitch {pitchDeg}, roll {rollDeg}, " +
                    $"yaw {yawDeg}; got magnitude {dir.magnitude}.");
            }
        }
    }
}
