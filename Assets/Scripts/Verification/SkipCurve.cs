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
    /// So this drives <see cref="SkipPhysics"/> directly against the authored assets. There
    /// is nothing to keep in sync, and no way for the measured curve to disagree with the
    /// game.
    ///
    /// Read the output against these, which is what the numbers were chosen to hit:
    /// distance, skips and hop height all rising with power; a neutral face angle (0)
    /// scoring full quality; steady-state hops low enough to read as skipping rather than
    /// lobbing; and a full-power throw staying on the water.
    ///
    /// <b>Six rocks, two sets.</b> Now that the rock catalog exists, one curve is no longer
    /// the whole story: every rock is swept against both its Authored and its Derived tuning,
    /// and each gets the same per-tuning verdict it would have got on its own — a badly
    /// authored rock fails loudly rather than averaging away. On top of that comes the check
    /// the two sets exist for: whether the rocks are actually <i>distinguishable</i>, run once
    /// per set so the report says outright which set separates better. See
    /// <see cref="TuningSet"/> and docs/plans/rock-choosing-system.md Part 6.
    ///
    /// With no catalog (or an empty one) this falls back to the original single-curve report
    /// against Resources/SkipTuning, which is what the game itself falls back to as well.
    /// </remarks>
    public static class SkipCurve
    {
        public static string ResultPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "skip-curve.txt"));

        private static readonly float[] Powers = { 0.1f, 0.2f, 0.35f, 0.5f, 0.65f, 0.8f, 1f };
        private static readonly float[] FaceAngles = { 0f, 5f, 10f, 15f, 20f, 30f, 45f, -15f, -30f };

        private static readonly TuningSet[] Sets = { TuningSet.Authored, TuningSet.Derived };

        /// <summary>The power the separation check is read at — a strong, ordinary throw.</summary>
        private const float SeparationPower = 0.8f;

        /// <summary>Across the rocks, skips at <see cref="SeparationPower"/> must span at least this.</summary>
        private const int MinSkipSpread = 4;

        /// <summary>Two rocks within this many skips AND <see cref="TooCloseMetres"/> are one rock.</summary>
        private const int TooCloseSkips = 1;

        private const float TooCloseMetres = 5f;

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

        /// <summary>One rock's reading at <see cref="SeparationPower"/>, for the separation tables.</summary>
        private struct Reading
        {
            public string Name;
            public bool Measured;
            public int Skips;
            public float Distance;
        }

        public static string Build()
        {
            var catalog = Resources.Load<RockCatalog>("RockCatalog");

            if (catalog == null || catalog.IsEmpty)
            {
                var single = Resources.Load<SkipTuning>("SkipTuning");
                if (single == null)
                {
                    return "FAIL  no RockCatalog at Resources/RockCatalog and no SkipTuning at " +
                           "Resources/SkipTuning — nothing to measure.\n";
                }

                var lone = new StringBuilder();
                lone.AppendLine("No usable catalog at Resources/RockCatalog — rock choosing is off, so");
                lone.AppendLine("this is the single-tuning report against Resources/SkipTuning. Run");
                lone.AppendLine("RockSkipping/Rocks/Build Catalog from FBX to measure the six rocks.");
                lone.AppendLine();
                lone.Append(ReportFor(single.name, single));
                return lone.ToString();
            }

            var report = new StringBuilder();
            report.AppendLine($"Skip curve — {catalog.Count} rocks x {Sets.Length} tuning sets, " +
                              "driven through the real solver.");
            report.AppendLine();

            // A catalog that breaks the shared-field rule makes every number below
            // incomparable: MaxSpeed differing by rock means power means a different thing per
            // rock. Say so at the top rather than letting the tables imply otherwise.
            var problems = catalog.Validate();
            if (problems.Count > 0)
            {
                report.AppendLine("CATALOG PROBLEMS — the readings below are not comparable until these are fixed:");
                foreach (var problem in problems) report.AppendLine($"  {problem}");
                report.AppendLine();
            }

            var readings = new Dictionary<TuningSet, Reading[]>();
            var perRockPassed = true;
            var anySetSeparates = false;
            var separationSummary = new StringBuilder();

            foreach (var set in Sets)
            {
                report.AppendLine(Rule());
                report.AppendLine(set.ToString().ToUpperInvariant());
                report.AppendLine(Rule());
                report.AppendLine();

                var setReadings = new Reading[catalog.Count];

                for (var i = 0; i < catalog.Count; i++)
                {
                    var rock = catalog.At(i);
                    var name = rock != null ? rock.DisplayName : $"slot {i}";
                    var tuning = rock != null ? rock.Tuning(set) : null;

                    setReadings[i] = new Reading { Name = name, Measured = false };

                    if (tuning == null)
                    {
                        report.AppendLine($"{name} — no {set} tuning, and no other set to fall back to.");
                        report.AppendLine();
                        perRockPassed = false;
                        continue;
                    }

                    var label = $"{name} ({rock.Id}) — {set}: {tuning.name}";
                    var body = ReportFor(label, tuning);
                    report.Append(body);
                    report.AppendLine();

                    if (body.Contains("FAIL")) perRockPassed = false;

                    var run = Measure(SeparationPower, tuning.BestFaceAngleDeg, tuning);
                    setReadings[i] = new Reading
                    {
                        Name = name,
                        Measured = true,
                        Skips = run.Skips,
                        Distance = run.Distance,
                    };
                }

                readings[set] = setReadings;

                var separated = Separation(set, setReadings, report);
                anySetSeparates |= separated;
                separationSummary.AppendLine(
                    $"  {(separated ? "PASS" : "FAIL")}  {set} separates the rocks — " +
                    $"{DescribeSpread(setReadings)}");
                report.AppendLine();
            }

            report.AppendLine(Rule());
            report.AppendLine("AUTHORED vs DERIVED");
            report.AppendLine(Rule());
            report.AppendLine();
            report.Append(Comparison(readings));
            report.AppendLine();

            report.AppendLine("Separation, per set:");
            report.Append(separationSummary);
            report.AppendLine($"  {(perRockPassed ? "PASS" : "FAIL")}  every rock passes its own " +
                              "verdict on both sets");
            report.AppendLine();
            report.AppendLine(perRockPassed && anySetSeparates ? "PASS" : "FAIL");
            report.AppendLine(BetterSet(readings));

            return report.ToString();
        }

        private static string Rule() => new('=', 70);

        /// <summary>The original single-tuning report: both sweeps and the acceptance checks.</summary>
        private static string ReportFor(string label, SkipTuning tuning)
        {
            var report = new StringBuilder();
            report.AppendLine(label);
            report.AppendLine($"  speed {tuning.MinSpeed}..{tuning.MaxSpeed} m/s   " +
                              $"kept {tuning.MinRetention}..{tuning.MaxRetention}/bounce   " +
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
        /// Whether the rocks in one set read as different stones.
        /// </summary>
        /// <remarks>
        /// This is the check the two sets exist to be judged by. Authored is expected to pass
        /// — separating them is what a person sat down and did. Derived failing is a result,
        /// not a blocker: it is the answer to "are mesh bounds enough", and it is only
        /// discoverable by measuring.
        ///
        /// Each rock is read at its own BestFaceAngleDeg rather than at one shared angle, so
        /// this compares stones each thrown well, which is the comparison a player makes.
        /// </remarks>
        private static bool Separation(TuningSet set, Reading[] readings, StringBuilder report)
        {
            report.AppendLine($"Separation, {set} — every rock at power {SeparationPower:F2}, " +
                              "thrown at its own best angle:");
            report.AppendLine();
            report.AppendLine("  rock                skips   distance");

            foreach (var reading in readings)
            {
                report.AppendLine(reading.Measured
                    ? $"  {reading.Name,-18}  {reading.Skips,5}   {reading.Distance,7:F1} m"
                    : $"  {reading.Name,-18}      -         -");
            }

            report.AppendLine();

            var measured = new List<Reading>();
            foreach (var reading in readings)
            {
                if (reading.Measured) measured.Add(reading);
            }

            if (measured.Count < 2)
            {
                report.AppendLine($"  FAIL  fewer than two {set} tunings could be measured.");
                return false;
            }

            var minSkips = int.MaxValue;
            var maxSkips = int.MinValue;
            foreach (var reading in measured)
            {
                minSkips = Mathf.Min(minSkips, reading.Skips);
                maxSkips = Mathf.Max(maxSkips, reading.Skips);
            }

            var spread = maxSkips - minSkips;
            var spreadOk = spread >= MinSkipSpread;
            report.AppendLine($"  {(spreadOk ? "PASS" : "FAIL")}  skip spread {spread} " +
                              $"({minSkips}..{maxSkips}), needs {MinSkipSpread} or more");

            var pairsOk = true;
            for (var a = 0; a < measured.Count; a++)
            {
                for (var b = a + 1; b < measured.Count; b++)
                {
                    var skipGap = Mathf.Abs(measured[a].Skips - measured[b].Skips);
                    var metreGap = Mathf.Abs(measured[a].Distance - measured[b].Distance);
                    if (skipGap > TooCloseSkips || metreGap > TooCloseMetres) continue;

                    pairsOk = false;
                    report.AppendLine($"  FAIL  {measured[a].Name} and {measured[b].Name} are the " +
                                      $"same rock — {skipGap} skip{(skipGap == 1 ? "" : "s")} and " +
                                      $"{metreGap:F1} m apart");
                }
            }

            if (pairsOk)
            {
                report.AppendLine($"  PASS  no two rocks within {TooCloseSkips} skip and " +
                                  $"{TooCloseMetres:F0} m of each other");
            }

            return spreadOk && pairsOk;
        }

        /// <summary>The two sets side by side, which is the table the comparison is read off.</summary>
        private static string Comparison(Dictionary<TuningSet, Reading[]> readings)
        {
            var table = new StringBuilder();
            table.AppendLine($"Every rock at power {SeparationPower:F2}, each thrown at its own best angle.");
            table.AppendLine("delta is derived minus authored, in skips.");
            table.AppendLine();
            table.AppendLine("                       authored               derived");
            table.AppendLine("  rock                skips     dist        skips     dist     delta");

            if (!readings.TryGetValue(TuningSet.Authored, out var authored) ||
                !readings.TryGetValue(TuningSet.Derived, out var derived))
            {
                table.AppendLine("  (a set is missing — nothing to compare)");
                return table.ToString();
            }

            var count = Mathf.Min(authored.Length, derived.Length);
            for (var i = 0; i < count; i++)
            {
                var a = authored[i];
                var d = derived[i];

                var aCells = a.Measured ? $"{a.Skips,5}   {a.Distance,7:F1}" : $"{"-",5}   {"-",7}";
                var dCells = d.Measured ? $"{d.Skips,5}   {d.Distance,7:F1}" : $"{"-",5}   {"-",7}";
                var delta = a.Measured && d.Measured ? $"{d.Skips - a.Skips,5}" : $"{"-",5}";

                table.AppendLine($"  {a.Name,-18}  {aCells}      {dCells}     {delta}");
            }

            return table.ToString();
        }

        /// <summary>Which set spreads the rocks further apart — the sentence the report exists for.</summary>
        private static string BetterSet(Dictionary<TuningSet, Reading[]> readings)
        {
            if (!readings.TryGetValue(TuningSet.Authored, out var authored) ||
                !readings.TryGetValue(TuningSet.Derived, out var derived))
            {
                return string.Empty;
            }

            var a = SkipSpread(authored);
            var d = SkipSpread(derived);

            if (a == d)
            {
                return $"  Authored and Derived spread the rocks equally ({a} skips). Neither is " +
                       "the obvious keeper on the offline sweep — decide it on real throws " +
                       "(rock-throws.csv).";
            }

            return a > d
                ? $"  Authored separates better ({a} skips of spread against Derived's {d})."
                : $"  Derived separates better ({d} skips of spread against Authored's {a}) — " +
                  "mesh bounds alone are enough.";
        }

        private static int SkipSpread(Reading[] readings)
        {
            var min = int.MaxValue;
            var max = int.MinValue;

            foreach (var reading in readings)
            {
                if (!reading.Measured) continue;
                min = Mathf.Min(min, reading.Skips);
                max = Mathf.Max(max, reading.Skips);
            }

            return max == int.MinValue ? 0 : max - min;
        }

        private static string DescribeSpread(Reading[] readings)
        {
            var spread = SkipSpread(readings);
            return $"{spread} skip{(spread == 1 ? "" : "s")} between the weakest and strongest rock";
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
