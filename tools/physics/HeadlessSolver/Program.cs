using System;
using System.Globalization;
using UnityEngine;

namespace HeadlessSolver
{
    /// <summary>
    /// Drives the project's real <see cref="Game.DiscContact"/> / <see cref="Game.RigidDisc"/>
    /// outside Unity, so the skip model can be calibrated in seconds instead of one Editor
    /// menu click at a time.
    /// </summary>
    /// <remarks>
    /// The run loop here is the one that becomes SkipPhysics.Step in phase 2 — flight under
    /// gravity at a coarse step, contact sub-stepped finely, attitude integrated throughout.
    /// Calibrating it here and then porting the settled numbers is the whole point: the
    /// alternative is guessing in the Editor.
    /// </remarks>
    internal static class Program
    {
        // Measured envelope from 124 real throws — docs/Findings-RealSkipPhysics.md §6.
        private const float MeasuredMinSwing = 1.83f;
        private const float MeasuredMedianSwing = 26.09f;
        private const float MeasuredP95Swing = 52.97f;
        private const float MeasuredMaxSwing = 74.81f;

        private const float MeasuredMinSpinDeg = 100.6f;
        private const float MeasuredMedianSpinDeg = 522.5f;
        private const float MeasuredP90SpinDeg = 1000.2f;
        private const float MeasuredMaxSpinDeg = 1380.0f;

        /// <summary>Bocquet's optimum, and what a decent thrower actually achieves.</summary>
        private const float LaunchTiltDeg = 20f;

        /// <summary>The record Bocquet cites (J. Coleman-McGhee), and the stated target.</summary>
        private const int WorldRecordSkips = 38;

        private struct Setup
        {
            public float Mass;
            public float Radius;
            public float Thickness;
            public float SpeedScale;
            public float WaterDensity;
            public float LiftCoefficient;
            public float DragCoefficient;
            public float Gravity;
            public float FlightStep;
            public float ContactStep;
        }

        private struct RunResult
        {
            public int Skips;
            public float Distance;
            public bool Sank;
            public float FinalSpeed;
            public float FirstGapMetres;
            public float LastGapMetres;
            public string EndReason;
        }

        /// <summary>Per-bounce detail, printed by trace mode. Diagnosing beats sweeping.</summary>
        private static bool _trace;

        /// <summary>Diagnostic: zero the contact torque, i.e. pretend spin holds the stone
        /// perfectly. Isolates whether rotational instability is what caps skip count.</summary>
        private static bool _noTorque;

        private static Setup Baseline() => new Setup
        {
            Mass = Game.ReferenceStone.Mass,
            Radius = Game.ReferenceStone.Radius,
            Thickness = Game.ReferenceStone.Thickness,
            SpeedScale = 1f,
            WaterDensity = Game.ReferenceStone.WaterDensity,
            LiftCoefficient = Game.ReferenceStone.LiftCoefficient,
            DragCoefficient = Game.ReferenceStone.DragCoefficient,
            Gravity = Game.ReferenceStone.Gravity,
            FlightStep = 0.002f,
            ContactStep = 0.00002f,
        };

        /// <summary>
        /// Simulate one whole throw: launch, then flight/contact until it sinks or the
        /// budget runs out. Returns what a player would actually see.
        /// </summary>
        private static RunResult Simulate(Setup s, float swingSpeed, float phoneSpinDeg, float tiltDeg) =>
            SimulateDirect(s, swingSpeed * s.SpeedScale,
                Game.SpinTransfer.ToStoneSpinRadPerSec(phoneSpinDeg, s.Radius), tiltDeg);

        /// <summary>
        /// The same run, driven by SI quantities directly, so the model can be checked
        /// against Bocquet's own worked examples without going through phone units.
        /// </summary>
        private static RunResult SimulateDirect(Setup s, float releaseSpeed, float spinRad, float tiltDeg)
        {
            var tilt = tiltDeg * Mathf.Deg2Rad;

            // Face normal tilted back against the direction of travel by the attack angle.
            // FromToRotation rather than Euler: no sign convention to get wrong.
            var normal = new Vector3(0f, Mathf.Cos(tilt), -Mathf.Sin(tilt)).normalized;
            var attitude = Quaternion.FromToRotation(Vector3.up, normal);

            // Bocquet throws flat and lets gravity supply the incidence angle; the stone is
            // released just above the surface travelling horizontally.
            var position = new Vector3(0f, 0.30f, 0f);
            var velocity = new Vector3(0f, 0f, releaseSpeed);

            var angularVelocity = normal * spinRad;

            var inertia = Game.RigidDisc.ComputeInertia(s.Mass, s.Radius, s.Thickness);

            var result = new RunResult();
            var wasInContact = false;
            var lastContactZ = 0f;
            var firstGap = -1f;
            var lastGap = -1f;

            var elapsed = 0f;
            var contactStart = 0f;
            const float budgetSeconds = 60f;

            while (elapsed < budgetSeconds)
            {
                var n = (attitude * Vector3.up).normalized;
                var height = position.y;
                var forces = Game.DiscContact.Evaluate(
                    height, n, velocity, s.Radius, s.WaterDensity,
                    s.LiftCoefficient, s.DragCoefficient);

                var inContact = forces.WettedArea > 0f;
                var dt = inContact ? s.ContactStep : s.FlightStep;

                // Fully submerged with no lift left to give — that is a sink, not a skip.
                var submergedDepth = Game.DiscContact.SignedWaterDistance(height, n);
                if (submergedDepth <= -s.Radius)
                {
                    result.Sank = true;
                    result.EndReason = "fully submerged";
                    break;
                }

                var acceleration = (forces.Lift + forces.Friction) / s.Mass
                                   + Vector3.down * s.Gravity;
                velocity += acceleration * dt;
                position += velocity * dt;
                Game.RigidDisc.Integrate(ref attitude, ref angularVelocity,
                    inContact && !_noTorque ? forces.Torque : Vector3.zero, inertia, dt);

                // A skip is completed when the stone LEAVES the water going up.
                if (wasInContact && !inContact && velocity.y > 0f)
                {
                    result.Skips++;
                    var gap = position.z - lastContactZ;
                    if (firstGap < 0f) firstGap = gap;
                    lastGap = gap;
                    lastContactZ = position.z;

                    if (_trace)
                    {
                        // Tilt of the face away from level: this is the tumble that ends runs.
                        var tiltNow = Mathf.Acos(Mathf.Clamp(n.y, -1f, 1f)) * Mathf.Rad2Deg;
                        var spinNow = Vector3.Dot(angularVelocity, n) / (2f * Mathf.PI);
                        Console.WriteLine($"   {result.Skips,4}{contactStart - elapsed + elapsed,0:F0}"
                                          + $"{gap,9:F2}{velocity.magnitude,10:F2}"
                                          + $"{(elapsed - contactStart) * 1000f,11:F2}"
                                          + $"{tiltNow,10:F1}{spinNow,10:F2}");
                    }
                }
                else if (!wasInContact && inContact)
                {
                    lastContactZ = position.z;
                    contactStart = elapsed;
                }

                wasInContact = inContact;
                elapsed += dt;

                // Below the surface and going nowhere: it has stopped skipping.
                if (position.y < -s.Radius * 2f)
                {
                    result.Sank = true;
                    result.EndReason = "sank below the surface";
                    break;
                }
                if (velocity.z <= 0.01f)
                {
                    result.Sank = true;
                    result.EndReason = "forward motion stopped";
                    break;
                }
            }

            result.EndReason ??= "ran out of time budget";

            result.Distance = position.z;
            result.FinalSpeed = velocity.magnitude;
            result.FirstGapMetres = Mathf.Max(0f, firstGap);
            result.LastGapMetres = Mathf.Max(0f, lastGap);
            return result;
        }

        private static void PrintEnvelope(Setup s)
        {
            Console.WriteLine($"  stone: R={s.Radius * 100f:F2} cm  M={s.Mass * 1000f:F1} g  "
                              + $"h={s.Thickness * 1000f:F1} mm   SpeedScale={s.SpeedScale:F3}");
            Console.WriteLine($"  {"input",-22}{"release m/s",13}{"stone rev/s",13}{"skips",8}{"dist m",10}");
            Console.WriteLine("  " + new string('-', 66));

            var cases = new (string Label, float Swing, float Spin)[]
            {
                ("weakest recorded", MeasuredMinSwing, MeasuredMinSpinDeg),
                ("median", MeasuredMedianSwing, MeasuredMedianSpinDeg),
                ("hard (p95/p90)", MeasuredP95Swing, MeasuredP90SpinDeg),
                ("STRONGEST recorded", MeasuredMaxSwing, MeasuredMaxSpinDeg),
            };

            foreach (var (label, swing, spin) in cases)
            {
                var r = Simulate(s, swing, spin, LaunchTiltDeg);
                var rev = Game.SpinTransfer.ToStoneSpinRadPerSec(spin, s.Radius) / (2f * Mathf.PI);
                Console.WriteLine($"  {label,-22}{swing * s.SpeedScale,13:F2}{rev,13:F2}"
                                  + $"{r.Skips,8}{r.Distance,10:F1}");
            }
        }

        private static int Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "envelope";
            var s = Baseline();

            // Optional overrides so a sweep can drive this without a rebuild:
            //   envelope [radiusCm] [massG] [speedScale]
            if (args.Length > 1 && mode != "trace") s.Radius = float.Parse(args[1], CultureInfo.InvariantCulture) / 100f;
            if (args.Length > 2) s.Mass = float.Parse(args[2], CultureInfo.InvariantCulture) / 1000f;
            if (args.Length > 3) s.SpeedScale = float.Parse(args[3], CultureInfo.InvariantCulture);
            if (args.Length > 1 && mode != "trace") s.Thickness = s.Radius * 0.22f; // aspect ratio 9.1 ([N])

            // Prove the shim shares Unity's conventions BEFORE trusting a single number.
            UnityEngine.Quaternion.SelfTest();

            Console.WriteLine("=".PadRight(70, (char)61));
            Console.WriteLine("HEADLESS SKIP SOLVER — real DiscContact/RigidDisc, no Unity");
            Console.WriteLine("=".PadRight(70, (char)61));
            Console.WriteLine($"target: strongest throw = {WorldRecordSkips} skips, weakest = sinks");
            Console.WriteLine();

            if (mode == "envelope")
            {
                PrintEnvelope(s);
                return 0;
            }

            if (mode == "sweep")
            {
                Console.WriteLine($"  {"R cm",8}{"M g",9}{"scale",8}{"weak",7}{"median",8}{"hard",7}{"STRONG",8}");
                Console.WriteLine("  " + new string('-', 55));
                foreach (var radiusCm in new[] { 5f, 4f, 3f, 2f, 1.5f, 1.0f })
                {
                    var r = radiusCm / 100f;
                    var thick = r * 0.22f;
                    var mass = 2700f * Mathf.PI * r * r * thick; // granite, sp.gr. 2.7 ([N])
                    foreach (var scale in new[] { 0.15f, 0.19f, 0.23f })
                    {
                        var t = s;
                        t.Radius = r; t.Thickness = thick; t.Mass = mass; t.SpeedScale = scale;
                        var weak = Simulate(t, MeasuredMinSwing, MeasuredMinSpinDeg, LaunchTiltDeg);
                        var med = Simulate(t, MeasuredMedianSwing, MeasuredMedianSpinDeg, LaunchTiltDeg);
                        var hard = Simulate(t, MeasuredP95Swing, MeasuredP90SpinDeg, LaunchTiltDeg);
                        var strong = Simulate(t, MeasuredMaxSwing, MeasuredMaxSpinDeg, LaunchTiltDeg);
                        Console.WriteLine($"  {radiusCm,8:F1}{mass * 1000f,9:F1}{scale,8:F2}"
                                          + $"{weak.Skips,7}{med.Skips,8}{hard.Skips,7}{strong.Skips,8}");
                    }
                }
                return 0;
            }

            if (mode == "trace")
            {
                // Bocquet's own worked examples, in SI, bypassing phone units entirely.
                // [B] §VI.A: 5 m/s -> 6 bounces, 8 m/s -> 17, 12 m/s -> 38 (world record),
                // for his standard stone with enough spin not to be the binding limit.
                var t = Baseline();
                t.SpeedScale = 1f;
                if (args.Length > 1) t.LiftCoefficient = float.Parse(args[1], CultureInfo.InvariantCulture);
                Console.WriteLine($"C_l = {t.LiftCoefficient:F0}\n");

                foreach (var (speed, spinRev, expected) in new[]
                         {
                             (5f, 14f, 6), (8f, 14f, 17), (12f, 14f, 38),
                         })
                {
                    Console.WriteLine($"--- {speed:F0} m/s, {spinRev:F0} rev/s "
                                      + $"(Bocquet expects ~{expected} bounces) ---");
                    Console.WriteLine($"   {"skip",4}{"",0}{"gap m",9}{"speed",10}"
                                      + $"{"contact ms",11}{"tilt deg",10}{"rev/s",10}");
                    _trace = true;
                    var r = SimulateDirect(t, speed, spinRev * 2f * Mathf.PI, LaunchTiltDeg);
                    _trace = false;
                    Console.WriteLine($"   => {r.Skips} skips, {r.Distance:F1} m, ended: {r.EndReason}");
                    Console.WriteLine();
                }
                return 0;
            }

            if (mode == "calib")
            {
                // C_l is the ONE coefficient the literature says is genuinely uncertain.
                // [B] note 6: "C_l ~ h/a ... the proportionality constant is expected to be
                // sufficiently large"; [N] fits its own inflection parameter for the same
                // reason. Everything else here is a measured length, a density, or g — so
                // C_l is the only honest place to absorb the difference between Bocquet's
                // V² closed forms and the (v·n)² law that actually produces a magic angle.
                Console.WriteLine("Calibrating C_l against Bocquet's worked bounce counts");
                Console.WriteLine("([B] §VI.A: 5 m/s -> 6, 8 m/s -> 17, 12 m/s -> 38)\n");
                Console.WriteLine($"  {"C_l",8}{"5 m/s",9}{"8 m/s",9}{"12 m/s",9}   (want 6 / 17 / 38)");
                Console.WriteLine("  " + new string('-', 50));

                foreach (var cl in new[] { 1f, 2f, 4f, 8f, 12f, 16f, 24f, 32f, 48f, 64f })
                {
                    var t = Baseline();
                    t.SpeedScale = 1f;
                    t.LiftCoefficient = cl;
                    var a = SimulateDirect(t, 5f, 14f * 2f * Mathf.PI, LaunchTiltDeg);
                    var b = SimulateDirect(t, 8f, 14f * 2f * Mathf.PI, LaunchTiltDeg);
                    var c = SimulateDirect(t, 12f, 14f * 2f * Mathf.PI, LaunchTiltDeg);
                    Console.WriteLine($"  {cl,8:F0}{a.Skips,9}{b.Skips,9}{c.Skips,9}");
                }

                // Independent check: whatever C_l lands on, the magic angle must survive.
                // It is a real experimental result; the bounce counts are model output.
                Console.WriteLine("\nMagic-angle check — tilt minimising the speed needed to bounce");
                Console.WriteLine($"  {"C_l",8}{"best tilt",12}{"v_min m/s",12}   (want ~20 deg)");
                Console.WriteLine("  " + new string('-', 46));
                foreach (var cl in new[] { 1f, 4f, 16f, 32f, 64f })
                {
                    var t = Baseline();
                    t.SpeedScale = 1f;
                    t.LiftCoefficient = cl;
                    var bestTilt = 0f;
                    var bestSpeed = float.MaxValue;
                    for (var tiltDeg = 5f; tiltDeg <= 60f; tiltDeg += 2.5f)
                    {
                        var lo = 0.1f;
                        var hi = 30f;
                        if (SimulateDirect(t, hi, 14f * 2f * Mathf.PI, tiltDeg).Skips < 1) continue;
                        for (var i = 0; i < 16; i++)
                        {
                            var mid = 0.5f * (lo + hi);
                            if (SimulateDirect(t, mid, 14f * 2f * Mathf.PI, tiltDeg).Skips >= 1) hi = mid;
                            else lo = mid;
                        }
                        if (hi < bestSpeed) { bestSpeed = hi; bestTilt = tiltDeg; }
                    }
                    Console.WriteLine(bestSpeed < float.MaxValue
                        ? $"  {cl,8:F0}{bestTilt,12:F1}{bestSpeed,12:F2}"
                        : $"  {cl,8:F0}{"never bounces",24}");
                }
                return 0;
            }

            if (mode == "converge")
            {
                // R9. If halving the contact step changes the answer, the results are
                // integrator error, not physics. Angular accelerations in contact run to
                // ~1e6 rad/s^2, so a 20 us step changes omega by ~20 rad/s against a spin
                // of ~88 rad/s -- more than 20% per step, which would diverge.
                Console.WriteLine("Contact-step convergence at C_l=16, 12 m/s, 14 rev/s");
                Console.WriteLine();
                Console.WriteLine($"  {"step us",10}{"skips",8}{"distance m",13}{"end reason",26}");
                Console.WriteLine("  " + new string('-', 57));
                foreach (var stepUs in new[] { 20f, 10f, 5f, 2f, 1f, 0.5f, 0.25f, 0.1f })
                {
                    var t = Baseline();
                    t.SpeedScale = 1f;
                    t.LiftCoefficient = 16f;
                    t.ContactStep = stepUs * 1e-6f;
                    var r = SimulateDirect(t, 12f, 14f * 2f * Mathf.PI, LaunchTiltDeg);
                    Console.WriteLine($"  {stepUs,10:F2}{r.Skips,8}{r.Distance,13:F1}  {r.EndReason,-24}");
                }
                return 0;
            }

            if (mode == "notorque") { _noTorque = true; mode = "target"; }

            if (mode == "target")
            {
                // The actual goal: the STRONGEST throw the phone can produce lands on the
                // world record, and the WEAKEST sinks. Everything here is either measured
                // (the phone envelope, the stone's dimensions) or constrained by the
                // magic-angle check — C_l and SpeedScale are the two free values.
                Console.WriteLine("Searching for: strongest recorded throw = 38 skips, weakest = 0\n");
                Console.WriteLine($"  {"C_l",6}{"scale",8}{"R cm",7}{"weak",6}{"med",6}{"hard",6}{"STRONG",8}");
                Console.WriteLine("  " + new string('-', 47));

                var bestErr = int.MaxValue;
                (float Cl, float Scale, float R) best = default;

                foreach (var cl in new[] { 8f, 12f, 16f, 24f, 32f })
                foreach (var radiusCm in new[] { 5f, 4f, 3f })
                foreach (var scale in new[] { 0.15f, 0.19f, 0.23f, 0.35f, 0.5f, 0.75f, 1f })
                {
                    var t = Baseline();
                    t.LiftCoefficient = cl;
                    t.SpeedScale = scale;
                    t.Radius = radiusCm / 100f;
                    t.Thickness = t.Radius * 0.22f;
                    t.Mass = 2700f * Mathf.PI * t.Radius * t.Radius * t.Thickness;

                    var weak = Simulate(t, MeasuredMinSwing, MeasuredMinSpinDeg, LaunchTiltDeg);
                    var med = Simulate(t, MeasuredMedianSwing, MeasuredMedianSpinDeg, LaunchTiltDeg);
                    var hard = Simulate(t, MeasuredP95Swing, MeasuredP90SpinDeg, LaunchTiltDeg);
                    var strong = Simulate(t, MeasuredMaxSwing, MeasuredMaxSpinDeg, LaunchTiltDeg);

                    Console.WriteLine($"  {cl,6:F0}{scale,8:F2}{radiusCm,7:F1}"
                                      + $"{weak.Skips,6}{med.Skips,6}{hard.Skips,6}{strong.Skips,8}");

                    // Score: strongest must hit 38, weakest must be 0. Weight the failure
                    // to sink heavily — a weak throw that still skips misses the whole point.
                    var err = Math.Abs(strong.Skips - WorldRecordSkips) + weak.Skips * 20;
                    if (err < bestErr) { bestErr = err; best = (cl, scale, radiusCm); }
                }

                Console.WriteLine($"\n  BEST: C_l={best.Cl:F0}  SpeedScale={best.Scale:F2}  "
                                  + $"R={best.R:F1} cm   (miss score {bestErr})");
                return 0;
            }

            Console.Error.WriteLine($"unknown mode '{mode}' — use 'envelope', 'sweep', 'trace', 'calib' or 'target'");
            return 1;
        }
    }
}
