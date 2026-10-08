using Game;
using NUnit.Framework;
using UnityEngine;

namespace RockSkipping.Physics.Tests
{
    // Closed-form geometry tests; the centroid derivation is recorded in the docs.
    // doc-ref e6d5 docs/4-systems/Verification.md
    public class DiscContactTests
    {
        private const float Radius = 0.05f; // ReferenceStone.Radius — arbitrary here, any R>0 works
        private const float Tolerance = 0.0001f;

        [Test]
        public void WettedArea_AtZeroImmersion_IsZero()
        {
            // d = +R: the waterline sits exactly at the far rim — the disc is bone dry.
            Assert.That(DiscContact.WettedArea(Radius, Radius), Is.EqualTo(0f).Within(Tolerance));

            // Anything past the far rim is equally dry — the clamp, not the closed form,
            // has to hold here (the closed form is only valid for |d| < R).
            Assert.That(DiscContact.WettedArea(Radius, Radius * 2f), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void WettedArea_AtHalfImmersion_IsHalfTheDiscArea()
        {
            // d = 0: the waterline passes exactly through the disc centre.
            var fullArea = Mathf.PI * Radius * Radius;
            var wetted = DiscContact.WettedArea(Radius, 0f);

            Assert.That(wetted, Is.EqualTo(fullArea * 0.5f).Within(Tolerance));
        }

        [Test]
        public void WettedArea_AtFullImmersion_IsTheWholeDisc()
        {
            // d = -R: the waterline sits at the far (dry-side) rim's mirror — the whole
            // disc is submerged.
            var fullArea = Mathf.PI * Radius * Radius;

            Assert.That(DiscContact.WettedArea(Radius, -Radius), Is.EqualTo(fullArea).Within(Tolerance));
            Assert.That(DiscContact.WettedArea(Radius, -Radius * 2f), Is.EqualTo(fullArea).Within(Tolerance));
        }

        [Test]
        public void WettedArea_IsSymmetric_ComplementaryDepthsSumToTheFullDisc()
        {
            // The wetted cap at +d and the wetted cap at -d are complementary halves of the
            // same disc — together they must reconstruct the whole circle, for any depth,
            // not just the three landmark cases above.
            var fullArea = Mathf.PI * Radius * Radius;

            foreach (var d in new[] { -0.04f, -0.02f, -0.01f, 0.01f, 0.02f, 0.04f })
            {
                var wetAtD = DiscContact.WettedArea(Radius, d);
                var wetAtMinusD = DiscContact.WettedArea(Radius, -d);

                Assert.That(wetAtD + wetAtMinusD, Is.EqualTo(fullArea).Within(Tolerance),
                    $"WettedArea(R, {d}) + WettedArea(R, {-d}) should reconstruct the full disc.");
            }
        }

        [Test]
        public void WettedCentroidDistance_AtFullImmersion_IsTheDiscCentre()
        {
            // A fully wetted disc's centroid IS the disc's own centre — zero offset.
            Assert.That(DiscContact.WettedCentroidDistance(Radius, -Radius), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void WettedCentroidDistance_AtHalfImmersion_MatchesTheSemicircleCentroid()
        {
            // Textbook result: a semicircle's centroid sits 4R/(3*pi) from its flat edge.
            var expected = 4f * Radius / (3f * Mathf.PI);
            var measured = DiscContact.WettedCentroidDistance(Radius, 0f);

            Assert.That(measured, Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void WettedCentroidDistance_AtZeroImmersion_IsZero()
        {
            // No wetted area at all — nothing to take a centroid of.
            Assert.That(DiscContact.WettedCentroidDistance(Radius, Radius), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void WettedCentroidDistance_MovesTowardTheFarRimAsImmersionShrinks()
        {
            // As the wetted cap shrinks toward the far rim, its own centroid must get
            // closer to that rim (and further from the disc centre) — monotonic, not just
            // correct at the three landmark points.
            var atHalf = DiscContact.WettedCentroidDistance(Radius, 0f);
            var atThreeQuarters = DiscContact.WettedCentroidDistance(Radius, Radius * 0.5f);

            Assert.That(atThreeQuarters, Is.GreaterThan(atHalf),
                "A smaller wetted cap's centroid should sit further from the disc centre " +
                "(closer to the far rim) than a half-wetted disc's.");
        }

        [Test]
        public void WettedArea_IsMonotonic_AsWaterlineSweepsFromDryToFullyWet()
        {
            // d sweeps from +R (dry) to -R (fully wet) — wetted area must never decrease
            // along that sweep. A regression here (e.g. a sign flip in the clamp) would
            // otherwise slip past the three landmark-value tests above.
            var previous = -1f;
            for (var d = Radius; d >= -Radius; d -= Radius / 20f)
            {
                var area = DiscContact.WettedArea(Radius, d);
                Assert.That(area, Is.GreaterThanOrEqualTo(previous - Tolerance),
                    $"Wetted area decreased while sweeping d from dry to wet at d={d}.");
                previous = area;
            }
        }
    }
}
