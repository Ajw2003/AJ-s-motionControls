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
    /// Rewritten for the throw-driven skip model
    /// (docs/plans/Plan-ThrowDrivenSkipModel.md) — there is one tuning per rock now, not
    /// two sets to compare, so this sweeps raw swing speed and all three phone axes
    /// (pitch, roll, yaw via roll-induced sideslip) through <see cref="SkipPhysics"/>
    /// directly against the catalog's own assets. There is nothing to keep in sync, and
    /// no way for the measured curve to disagree with the game.
    ///
    /// <b>The acceptance criterion for this whole change:</b> throw speed must move skip
    /// count more than any per-rock multiplier does. Concretely, doubling swing speed
    /// should beat driving a multiplier to its bound — see the "speed vs multiplier"
    /// check in <see cref="Verdict"/>, which is the number this report exists to answer.
    ///
    /// Bounces are counted from <see cref="SkipPhysics.Step"/>'s own <c>Bounced</c> flag
    /// rather than reconstructed by comparing consecutive path heights against WaterY.
    /// The old position-comparison approach had a contact-detector off-by-one
    /// (docs/Reference-SkipTuning.md) that a face angle of exactly 0 could trigger — and
    /// a face angle of 0 is now the ordinary case, since pitch comes straight off the
    /// phone's tilt rather than being biased toward an authored optimum. Reading the
    /// solver's own signal removes the epsilon comparison entirely.
    ///
    /// With no catalog (or an empty one) this falls back to a single-rock report against
    /// Resources/SkipTuning, which is what the game itself falls back to as well.
    /// </remarks>
    public static class SkipCurve
    {
        /// <summary>
        /// How far the drawn water runs ahead of the throw origin, in metres — read from
        /// <see cref="AimPreview"/>, which is the thing that builds the plane, so this cannot
        /// disagree with what is on screen.
        /// </summary>
        private static float DrawnWaterLength => AimPreview.WaterReachFromOrigin;

        public static string ResultPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "skip-curve.txt"));

        /// <summary>Raw swing speeds swept, m/s — a gentle push through a hard flick.</summary>
        private static readonly float[] SwingSpeeds = { 1.5f, 2.5f, 4f, 6f, 8f, 10f, 14f };

        /// <summary>
        /// Face angles swept, degrees above horizontal — straight off the phone, no
        /// authored offset. 0 is a phone held exactly as it was at the tap.
        /// </summary>
        private static readonly float[] PitchAngles = { -10f, 0f, 10f, 20f, 30f, 45f, 60f };

        /// <summary>Bank angles swept, degrees.</summary>
        private static readonly float[] RollAngles = { -70f, -40f, -20f, 0f, 20f, 40f, 70f };

        /// <summary>The physically optimal attack angle — see SkipPhysics.OptimalAttackDeg.</summary>
        private const float OptimalAttackDeg = 20f;

        /// <summary>A firm, ordinary throw — used wherever the sweep needs to hold speed fixed.</summary>
        private const float ReferenceSwingSpeed = 6f;

        public static string Build()
        {
            var constants = SkipConstants.LoadOrWarn();
            var catalog = Resources.Load<RockCatalog>("RockCatalog");

            var report = new StringBuilder();
            report.AppendLine("Skip curve — driven through the real solver.");
            report.AppendLine(
                $"Constants: SpeedScale {constants.SpeedScale}   WaterLoss {constants.WaterLossCoefficient}   " +
                $"forgiveness pitch/roll/yaw {constants.ForgivenessPitchDeg}/{constants.ForgivenessRollDeg}/{constants.ForgivenessYawDeg} deg   " +
                $"spin tolerance {constants.SpinToleranceScale}   reference speed {constants.ReferenceSpeed} m/s");
            report.AppendLine();

            if (catalog == null || catalog.IsEmpty)
            {
                var single = Resources.Load<SkipTuning>("SkipTuning");
                if (single == null)
                {
                    report.AppendLine("FAIL  no RockCatalog at Resources/RockCatalog and no SkipTuning at " +
                                      "Resources/SkipTuning — nothing to measure.");
                    return report.ToString();
                }

                report.AppendLine("No usable catalog at Resources/RockCatalog — rock choosing is off, so");
                report.AppendLine("this is the single-tuning report against Resources/SkipTuning. Run");
                report.AppendLine("RockSkipping/Rocks/Build Catalog from FBX to measure the six rocks.");
                report.AppendLine();
                report.Append(ReportFor(single.name, constants, single));
                report.AppendLine();
                report.Append(SpeedVsMultiplierCheck(constants, single));
                return report.ToString();
            }

            var problems = catalog.Validate();
            if (problems.Count > 0)
            {
                report.AppendLine("CATALOG PROBLEMS — the readings below are not comparable until these are fixed:");
                foreach (var problem in problems) report.AppendLine($"  {problem}");
                report.AppendLine();
            }

            var allPassed = true;
            var referenceRow = new StringBuilder();
            referenceRow.AppendLine($"Every rock at {ReferenceSwingSpeed:F1} m/s, pitch {OptimalAttackDeg:F0} deg, roll 0:");
            referenceRow.AppendLine();
            referenceRow.AppendLine("  rock                spin   vert   horiz   skips   distance");

            for (var i = 0; i < catalog.Count; i++)
            {
                var rock = catalog.At(i);
                var name = rock != null ? rock.DisplayName : $"slot {i}";
                var tuning = rock != null ? rock.Tuning : null;

                report.AppendLine(Rule());
                report.AppendLine($"{name} ({rock?.Id ?? "?"})");
                report.AppendLine(Rule());
                report.AppendLine();

                if (tuning == null)
                {
                    report.AppendLine($"{name} — no tuning.");
                    report.AppendLine();
                    allPassed = false;
                    referenceRow.AppendLine($"  {name,-18}      -       -       -       -         -");
                    continue;
                }

                var body = ReportFor(name, constants, tuning);
                report.Append(body);
                report.AppendLine();
                if (body.Contains("FAIL")) allPassed = false;

                var reference = Measure(ReferenceSwingSpeed, OptimalAttackDeg, 0f, constants, tuning);
                referenceRow.AppendLine(
                    $"  {name,-18}  {tuning.SpinMultiplier,5:F2}  {tuning.VerticalMultiplier,5:F2}   " +
                    $"{tuning.HorizontalMultiplier,5:F2}   {reference.Skips,5}   {reference.Distance,7:F1} m");
            }

            report.AppendLine(Rule());
            report.AppendLine("REFERENCE");
            report.AppendLine(Rule());
            report.AppendLine();
            report.Append(referenceRow);
            report.AppendLine();

            // The acceptance criterion, run once against the catalog's first usable rock
            // — every rock shares the same global constants, so one is representative.
            var sample = catalog.At(0)?.Tuning;
            if (sample != null)
            {
                report.Append(SpeedVsMultiplierCheck(constants, sample));
                if (report.ToString().Contains("FAIL  speed vs multiplier")) allPassed = false;
            }

            report.AppendLine();
            report.AppendLine(allPassed ? "PASS" : "FAIL");

            return report.ToString();
        }

        private static string Rule() => new('=', 70);

        /// <summary>One rock's full sweep: speed, pitch, roll, then the acceptance checks.</summary>
        private static string ReportFor(string label, SkipConstants constants, SkipTuning tuning)
        {
            var report = new StringBuilder();
            report.AppendLine($"{label}  —  spin {tuning.SpinMultiplier:F2}  vertical {tuning.VerticalMultiplier:F2}  " +
                              $"horizontal {tuning.HorizontalMultiplier:F2}");
            report.AppendLine();

            report.AppendLine($"Speed sweep, at pitch {OptimalAttackDeg:F0} deg (the physical optimum), roll 0:");
            report.AppendLine();
            report.AppendLine("  speed    skips   distance   launch arc   1st height   settled height");

            foreach (var speed in SwingSpeeds)
            {
                var run = Measure(speed, OptimalAttackDeg, 0f, constants, tuning);
                report.AppendLine(
                    $"  {speed,5:F1}   {run.Skips,5}   {run.Distance,7:F1} m   " +
                    $"{run.LaunchArcLength,8:F1} m   {run.FirstHopHeight,8:F2} m   {run.SettledHopHeight,12:F2} m");
            }

            report.AppendLine();
            report.AppendLine($"Pitch sweep, at {ReferenceSwingSpeed:F1} m/s, roll 0:");
            report.AppendLine();
            report.AppendLine("   pitch   skips   distance");

            foreach (var pitch in PitchAngles)
            {
                var run = Measure(ReferenceSwingSpeed, pitch, 0f, constants, tuning);
                report.AppendLine($"  {pitch,5:F0}   {run.Skips,5}   {run.Distance,7:F1} m");
            }

            report.AppendLine();
            report.AppendLine($"Roll sweep, at {ReferenceSwingSpeed:F1} m/s, pitch {OptimalAttackDeg:F0} deg:");
            report.AppendLine();
            report.AppendLine("    roll   skips   distance");

            foreach (var roll in RollAngles)
            {
                var run = Measure(ReferenceSwingSpeed, OptimalAttackDeg, roll, constants, tuning);
                report.AppendLine($"  {roll,5:F0}   {run.Skips,5}   {run.Distance,7:F1} m");
            }

            report.AppendLine();
            report.Append(Verdict(constants, tuning));
            return report.ToString();
        }

        /// <summary>
        /// The number the whole model change exists to produce: throw speed must move
        /// skip count more than any per-rock multiplier can. Doubling swing speed is
        /// compared against driving VerticalMultiplier and HorizontalMultiplier both to
        /// their upper bound (1.5) at the base speed — the largest change a rock alone
        /// can make.
        /// </summary>
        private static string SpeedVsMultiplierCheck(SkipConstants constants, SkipTuning baseline)
        {
            var lines = new StringBuilder();

            var maxed = SkipTuning.CreateDefault();
            maxed.SpinMultiplier = 1.5f;
            maxed.VerticalMultiplier = 1.5f;
            maxed.HorizontalMultiplier = 1.5f;

            var normalSpeed = Measure(ReferenceSwingSpeed, OptimalAttackDeg, 0f, constants, baseline);
            var doubledSpeed = Measure(ReferenceSwingSpeed * 2f, OptimalAttackDeg, 0f, constants, baseline);
            var maxedMultipliers = Measure(ReferenceSwingSpeed, OptimalAttackDeg, 0f, constants, maxed);

            var ok = doubledSpeed.Skips > maxedMultipliers.Skips && doubledSpeed.Distance > maxedMultipliers.Distance;

            lines.AppendLine("Speed vs multiplier — the acceptance criterion:");
            lines.AppendLine();
            lines.AppendLine($"  base       ({ReferenceSwingSpeed:F1} m/s, multipliers 1.00) — " +
                             $"{normalSpeed.Skips} skips, {normalSpeed.Distance:F1} m");
            lines.AppendLine($"  2x speed   ({ReferenceSwingSpeed * 2f:F1} m/s, multipliers 1.00) — " +
                             $"{doubledSpeed.Skips} skips, {doubledSpeed.Distance:F1} m");
            lines.AppendLine($"  maxed rock ({ReferenceSwingSpeed:F1} m/s, multipliers 1.50) — " +
                             $"{maxedMultipliers.Skips} skips, {maxedMultipliers.Distance:F1} m");
            lines.AppendLine();
            lines.AppendLine($"  {(ok ? "PASS" : "FAIL")}  speed vs multiplier — doubling swing speed beats " +
                             "maxing every multiplier at once");
            lines.AppendLine();

            return lines.ToString();
        }

        /// <summary>One throw's shape, measured rather than derived.</summary>
        private struct Shape
        {
            public int Skips;
            public float Distance;
            public float LaunchArcLength;
            public float FirstHopHeight;
            public float SettledHopHeight;
        }

        /// <summary>
        /// Run one throw through the real solver and measure the shape of the result.
        /// </summary>
        /// <remarks>
        /// Builds the attitude directly in Unity space — SkipPhysics.CreateLaunch takes
        /// attitude already converted, and PhoneAttitude.ToUnity's device-frame round
        /// trip has nothing to add here. Pitch comes from the forward vector's own
        /// elevation; roll is then applied as a rotation about that same forward axis, so
        /// it changes bank without touching pitch — the same "one rotated vector" property
        /// SkipPhysics.Step relies on.
        /// </remarks>
        private static Shape Measure(float swingSpeed, float pitchDeg, float rollDeg, SkipConstants constants, SkipTuning tuning)
        {
            var pitchRad = pitchDeg * Mathf.Deg2Rad;
            var forward = new Vector3(0f, Mathf.Sin(pitchRad), Mathf.Cos(pitchRad));
            var level = Quaternion.LookRotation(forward, Vector3.up);
            var attitude = Quaternion.AngleAxis(rollDeg, forward) * level;

            var state = SkipPhysics.CreateLaunch(Vector3.zero, swingSpeed, attitude, 0f, constants, tuning);

            var contacts = new List<float>();
            var apexes = new List<float>();
            var apex = state.Position.y;

            for (var i = 0; i < constants.MaxPredictSteps; i++)
            {
                var step = SkipPhysics.Step(state, constants, tuning, constants.FixedStep);
                state = step.State;
                apex = Mathf.Max(apex, state.Position.y);

                // Bounces are read off the solver's own Bounced flag, not reconstructed by
                // comparing consecutive heights against WaterY — see the class remarks.
                if (step.Bounced)
                {
                    contacts.Add(Vector3.Distance(Vector3.zero, state.Position));
                    apexes.Add(apex);
                    apex = state.Position.y;
                }

                if (state.Sunk) break;
            }

            var run = new Shape
            {
                Skips = state.SkipCount,
                Distance = Vector3.Distance(Vector3.zero, state.Position),
            };

            if (contacts.Count > 0)
            {
                run.LaunchArcLength = contacts[0];
                run.FirstHopHeight = apexes[0];
            }

            // "Settled" means a mid-run skip once the run has more than a couple of
            // bounces behind it — the height a typical skip in the run actually has.
            var settledIndex = Mathf.Min(3, apexes.Count - 1);
            if (settledIndex >= 0) run.SettledHopHeight = apexes[settledIndex];

            return run;
        }

        /// <summary>The acceptance checks, so a bad retune fails loudly rather than quietly.</summary>
        private static string Verdict(SkipConstants constants, SkipTuning tuning)
        {
            var soft = Measure(SwingSpeeds[1], OptimalAttackDeg, 0f, constants, tuning);
            var mid = Measure(SwingSpeeds[3], OptimalAttackDeg, 0f, constants, tuning);
            var hard = Measure(SwingSpeeds[^1], OptimalAttackDeg, 0f, constants, tuning);
            var offPitch = Measure(ReferenceSwingSpeed, OptimalAttackDeg + 60f, 0f, constants, tuning);
            var offRoll = Measure(ReferenceSwingSpeed, OptimalAttackDeg, 90f, constants, tuning);
            var strong = Measure(ReferenceSwingSpeed, OptimalAttackDeg, 0f, constants, tuning);

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
                soft.Skips <= mid.Skips && mid.Skips <= hard.Skips && soft.Skips < hard.Skips,
                $"{soft.Skips} / {mid.Skips} / {hard.Skips}");

            // The regression this whole pass exists to prevent coming back. A neutral
            // pitch is no longer a good throw by construction — there is no offset — so
            // this checks the physically optimal angle instead, which every rock should
            // still handle well at an ordinary swing speed.
            Check("a good throw at the optimal angle skips well",
                strong.Skips >= 6,
                $"{strong.Skips} skips at {ReferenceSwingSpeed:F1} m/s, pitch {OptimalAttackDeg:F0} deg");

            // Well past the pitch forgiveness band and its falloff — the stone should
            // plow in rather than run on.
            Check("a badly pitched throw is punished",
                offPitch.Skips <= 3,
                $"{offPitch.Skips} skips at pitch {OptimalAttackDeg + 60f:F0} deg " +
                $"(good throw gets {strong.Skips})");

            // Past MaxRollDeg (SkipPhysics) — the leading edge should catch and the stone
            // should sink almost immediately rather than skipping normally.
            Check("a badly rolled throw catches and sinks",
                offRoll.Skips <= 2,
                $"{offRoll.Skips} skips at 90 deg of bank (good throw gets {strong.Skips})");

            Check("a settled hop reads as skipping, not lobbing",
                hard.SettledHopHeight >= 0f && hard.SettledHopHeight < 1.5f,
                $"{hard.SettledHopHeight:F2} m");

            Check("full power stays on the drawn water",
                hard.Distance < DrawnWaterLength,
                $"{hard.Distance:F1} m against {DrawnWaterLength:F0} m of drawn water");

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
