using System.Collections.Generic;
using System.IO;
using System.Text;
using Game;
using UnityEngine;

namespace Verification
{
    /// <summary>
    /// Prints what the current tuning actually does, by running the real solver.
    /// </summary>
    /// <remarks>
    /// This exists because the tuning has now been recomputed by hand twice and both
    /// times against a throwaway port of SkipPhysics rather than SkipPhysics itself. The
    /// second time, the port was fed a face angle no player can produce, which is exactly
    /// how a bug that zeroed the bounce quality on every real throw survived a sweep that
    /// looked healthy.
    ///
    /// So this drives <see cref="SkipPhysics"/> directly against the authored
    /// Resources/SkipTuning asset. There is nothing to keep in sync, and no way for the
    /// measured curve to disagree with the game.
    ///
    /// Read the output against these, which is what the numbers were chosen to hit:
    /// distance, skips and hop height all rising with power; a neutral face angle (0)
    /// scoring full quality; steady-state hops low enough to read as skipping rather than
    /// lobbing; and a full-power throw staying on the water.
    /// </remarks>
    public static class SkipCurve
    {
        public static string ResultPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "skip-curve.txt"));

        private static readonly float[] Powers = { 0.1f, 0.2f, 0.35f, 0.5f, 0.65f, 0.8f, 1f };
        private static readonly float[] FaceAngles = { 0f, 5f, 10f, 15f, 20f, 30f, 45f, -15f, -30f };

        /// <summary>One throw's shape, measured rather than derived.</summary>
        private struct Shape
        {
            public int Skips;
            public float Distance;
            public float FirstHopLength;
            public float FirstHopHeight;
            public float LaunchArcLength;
            public float SettledHopHeight;
        }

        public static string Build()
        {
            var tuning = Resources.Load<SkipTuning>("SkipTuning");
            if (tuning == null)
            {
                return "FAIL  no SkipTuning at Resources/SkipTuning — nothing to measure.\n";
            }

            var report = new StringBuilder();
            report.AppendLine($"Skip curve for {tuning.name}");
            report.AppendLine($"  speed {tuning.MinSpeed}..{tuning.MaxSpeed} m/s   " +
                              $"kept {tuning.SpeedKeptSoft}..{tuning.SpeedKeptHard}/bounce   " +
                              $"exit {tuning.OpeningHopAngleDeg}deg -> {tuning.SkipExitAngleDeg}deg " +
                              $"over {tuning.OpeningHopSkips} skips");
            report.AppendLine($"  best face {tuning.BestFaceAngleDeg}deg +/- {tuning.ToleranceDeg}deg   " +
                              $"min skip speed {tuning.MinSkipSpeed} m/s");
            report.AppendLine();

            // Power sweep, held at the face angle a player gets for free.
            report.AppendLine($"Power sweep, at the best face angle ({tuning.BestFaceAngleDeg} deg):");
            report.AppendLine();
            report.AppendLine("  power   skips   distance   launch arc   1st hop   1st height   settled height");

            foreach (var power in Powers)
            {
                var run = Measure(power, tuning.BestFaceAngleDeg, tuning);
                report.AppendLine(
                    $"  {power,5:F2}   {run.Skips,5}   {run.Distance,7:F1} m   " +
                    $"{run.LaunchArcLength,8:F1} m   {run.FirstHopLength,6:F1} m   " +
                    $"{run.FirstHopHeight,8:F2} m   {run.SettledHopHeight,12:F2} m");
            }

            // Face-angle sweep, so the tolerance window can be read off directly rather
            // than inferred from ToleranceDeg — the two came apart once already.
            report.AppendLine();
            report.AppendLine("Face angle sweep, at a strong throw (power 0.8):");
            report.AppendLine();
            report.AppendLine("   face   skips   distance");

            foreach (var face in FaceAngles)
            {
                var run = Measure(0.8f, face, tuning);
                report.AppendLine($"  {face,5:F0}   {run.Skips,5}   {run.Distance,7:F1} m");
            }

            report.AppendLine();
            report.Append(Verdict(tuning));
            return report.ToString();
        }

        /// <summary>
        /// Run one throw through the real solver and measure the shape of the result.
        /// </summary>
        /// <remarks>
        /// The face angle is applied by building the attitude the phone would have sent
        /// for it, so this goes through PhoneAttitude exactly as a real throw does. Asking
        /// for a face angle and then setting SkipState.FaceAngleDeg directly would skip
        /// the conversion, which is the half of the path most likely to be wrong.
        /// </remarks>
        private static Shape Measure(float power, float faceAngleDeg, SkipTuning tuning)
        {
            // Pitching the phone's top edge up by N degrees is a rotation of N about the
            // device's own x axis. See PhoneAttitude for the frame.
            var half = faceAngleDeg * Mathf.Deg2Rad * 0.5f;
            var attitude = PhoneAttitude.ToUnity(Mathf.Cos(half), Mathf.Sin(half), 0f, 0f);

            var launch = SkipPhysics.CreateLaunch(Vector3.zero, power, attitude, 0f, tuning);

            var path = new List<Vector3>(tuning.MaxPredictSteps + 1);
            var final = SkipPhysics.Predict(launch, tuning, path);

            // Where the stone touched down, and how high it got between touches. Read off
            // the path rather than recomputed, so this measures what was drawn.
            var contacts = new List<float>();   // distance along the path at each contact
            var apexes = new List<float>();
            var apex = 0f;

            for (var i = 1; i < path.Count; i++)
            {
                apex = Mathf.Max(apex, path[i].y);

                var touched = path[i].y <= tuning.WaterY + 0.0001f &&
                              path[i - 1].y > tuning.WaterY + 0.0001f;

                if (!touched) continue;

                contacts.Add(Vector3.Distance(Vector3.zero, path[i]));
                apexes.Add(apex);
                apex = 0f;
            }

            var run = new Shape
            {
                Skips = final.SkipCount,
                Distance = Vector3.Distance(Vector3.zero, final.Position),
            };

            if (contacts.Count > 0)
            {
                run.LaunchArcLength = contacts[0];
                run.FirstHopHeight = apexes[0];
            }

            if (contacts.Count > 1)
            {
                run.FirstHopLength = contacts[1] - contacts[0];
                run.FirstHopHeight = apexes.Count > 1 ? apexes[1] : run.FirstHopHeight;
            }

            // "Settled" means past the opening pop, where the exit angle has reached its
            // steady value — the height a mid-run skip actually has.
            var settledIndex = Mathf.CeilToInt(tuning.OpeningHopSkips) + 1;
            if (apexes.Count > settledIndex) run.SettledHopHeight = apexes[settledIndex];

            return run;
        }

        /// <summary>The acceptance checks, so a bad retune fails loudly rather than quietly.</summary>
        private static string Verdict(SkipTuning tuning)
        {
            var soft = Measure(0.2f, tuning.BestFaceAngleDeg, tuning);
            var mid = Measure(0.5f, tuning.BestFaceAngleDeg, tuning);
            var hard = Measure(1f, tuning.BestFaceAngleDeg, tuning);
            var offAngle = Measure(0.8f, tuning.BestFaceAngleDeg + 35f, tuning);
            var strong = Measure(0.8f, tuning.BestFaceAngleDeg, tuning);

            var lines = new StringBuilder();
            var passed = true;

            void Check(string label, bool ok, string detail)
            {
                if (!ok) passed = false;
                lines.AppendLine($"  {(ok ? "PASS" : "FAIL")}  {label} — {detail}");
            }

            Check("harder goes further",
                soft.Distance < mid.Distance && mid.Distance < hard.Distance,
                $"{soft.Distance:F1} / {mid.Distance:F1} / {hard.Distance:F1} m");

            Check("harder skips more times",
                soft.Skips < mid.Skips && mid.Skips < hard.Skips,
                $"{soft.Skips} / {mid.Skips} / {hard.Skips}");

            Check("harder hops higher",
                soft.FirstHopHeight < mid.FirstHopHeight && mid.FirstHopHeight < hard.FirstHopHeight,
                $"{soft.FirstHopHeight:F2} / {mid.FirstHopHeight:F2} / {hard.FirstHopHeight:F2} m");

            // The regression this whole pass exists to prevent coming back. A player who
            // holds the phone as they gripped it and throws must get a good throw.
            Check("a neutral throw is a good throw", hard.Skips >= 10,
                $"{hard.Skips} skips at the best face angle and full power");

            // Measured against its own launch arc, not against a good throw's distance. A
            // hard throw at a hopeless angle still flies a long way before it ever touches
            // the water, and that flight is not a fault — it is just a throw. The failure
            // to check for is the stone carrying on afterwards, so what this asserts is
            // that it dies roughly where it lands rather than running on.
            //
            // The skip count is the real punishment and the real score: at ten points a
            // skip against one a metre, a 3-skip slap is worth a fraction of a clean run
            // even when the two travelled comparable distances.
            Check("a badly angled throw is punished",
                offAngle.Skips <= 4 && offAngle.Distance < offAngle.LaunchArcLength * 1.6f,
                $"{offAngle.Skips} skips, dead at {offAngle.Distance:F1} m off a " +
                $"{offAngle.LaunchArcLength:F1} m toss — a neutral throw goes {strong.Distance:F1} m " +
                $"in {strong.Skips} skips");

            Check("the opening hop does not out-throw the launch arc",
                hard.FirstHopLength <= hard.LaunchArcLength,
                $"{hard.FirstHopLength:F1} m hop vs {hard.LaunchArcLength:F1} m arc");

            Check("a settled hop reads as skipping, not lobbing",
                hard.SettledHopHeight > 0f && hard.SettledHopHeight < 0.6f,
                $"{hard.SettledHopHeight:F2} m");

            Check("full power stays on the water", hard.Distance < 250f,
                $"{hard.Distance:F1} m against roughly 260 m of lake");

            return $"{(passed ? "PASS" : "FAIL")}\n{lines}";
        }

        /// <summary>Menu entry point, and the batchmode one via ProjectSetup.</summary>
        public static void Run()
        {
            var report = Build();
            Debug.Log($"[SkipCurve]\n{report}");

            try { File.WriteAllText(ResultPath, report); }
            catch (IOException e) { Debug.LogWarning($"Could not write the skip curve: {e.Message}"); }
        }
    }
}
