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

        /// <summary>
        /// How far the stone must travel between contacts, in disc radii, for the hop to
        /// count as a skip rather than surface chatter.
        /// </summary>
        /// <remarks>
        /// Two disc diameters. The separation it has to make is not subtle: measured chatter
        /// gaps are ~0.02 m on a 0.05 m stone while real hops are 0.5-6 m against Bocquet's
        /// `X_0 = 2·v_x·|v_z|/g ~ 5.9 m`, so anything from about one diameter to one metre
        /// picks out the same runs. Expressed in radii so it scales with the stone rather
        /// than being a length typed in for one size of rock.
        /// </remarks>
        private const float MinimumHopRadii = 4f;

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

        /// <summary>Diagnostic: hide the disc's rotation from the PRESSURE law only, so the
        /// contact behaves as it did before the pointwise rotating-pressure integration went
        /// in. The torque is still applied. Isolates the damping term's own effect.</summary>
        private static bool _noRotationalPressure;

        /// <summary>Diagnostic: leave out the added rotational inertia of the water the
        /// stone drags while tumbling. Isolates that term from the damping.</summary>
        private static bool _noAddedMass;

        /// <summary>Diagnostic: print substep detail through the first contact. R3 says a
        /// bounce lasts milliseconds; when it does not, this says what the stone is doing
        /// instead of guessing from the summary.</summary>
        private static bool _contactTrace;

        private static int _contactSamples;

        /// <summary>Release height above the water, m. Bocquet throws flat and lets gravity
        /// supply the incidence angle, so this sets the incidence at first contact and with
        /// it the vertical velocity every bounce has to reverse.</summary>
        private static float _releaseHeight = 0.30f;

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
            var position = new Vector3(0f, _releaseHeight, 0f);
            var velocity = new Vector3(0f, 0f, releaseSpeed);

            var angularVelocity = normal * spinRad;

            var inertia = Game.RigidDisc.ComputeInertia(s.Mass, s.Radius, s.Thickness);

            var result = new RunResult();
            var wasInContact = false;
            var lastContactZ = 0f;
            var pendingExitZ = 0f;
            var hasPendingExit = false;
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
                    height, n, velocity,
                    _noRotationalPressure ? Vector3.zero : angularVelocity,
                    s.Radius, s.WaterDensity,
                    s.LiftCoefficient, s.DragCoefficient);

                var inContact = forces.WettedArea > 0f;
                var dt = inContact ? s.ContactStep : s.FlightStep;

                if (_contactTrace && inContact && result.Skips == 0)
                {
                    // Every 250 substeps is every 5 ms at the default contact step.
                    if (_contactSamples++ % 250 == 0)
                    {
                        var dNow = Game.DiscContact.SignedWaterDistance(height, n);
                        var tiltNow = Mathf.Acos(Mathf.Clamp(n.y, -1f, 1f)) * Mathf.Rad2Deg;
                        var vn = Vector3.Dot(velocity, n);
                        Console.WriteLine(
                            $"   {(elapsed - contactStart) * 1000f,9:F2}{height * 1000f,9:F1}"
                            + $"{dNow / s.Radius,8:F2}{forces.WettedArea / (Mathf.PI * s.Radius * s.Radius),8:F2}"
                            + $"{velocity.z,8:F2}{velocity.y,8:F2}{vn,8:F2}"
                            + $"{forces.Lift.magnitude,10:F1}{tiltNow,8:F1}");
                    }
                }

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
                // Fold in the water the stone drags while tumbling. Only matters in
                // contact, and scales with how much of the face is wet.
                var wettedFraction = forces.WettedArea / (Mathf.PI * s.Radius * s.Radius);
                var effectiveInertia = _noAddedMass
                    ? inertia
                    : Game.RigidDisc.WithAddedMass(inertia, s.WaterDensity, s.Radius, wettedFraction);

                Game.RigidDisc.Integrate(ref attitude, ref angularVelocity,
                    inContact && !_noTorque ? forces.Torque : Vector3.zero, effectiveInertia, dt);

                // A skip is completed when the stone leaves the water going up AND then
                // actually FLIES — see MinimumHopRadii. The hop can only be measured once
                // the stone comes back down, so the exit is held pending and resolved at the
                // next contact.
                //
                // The distance floor is what keeps surface chatter out of the count: runs of
                // "skips" 2 cm apart while the stone sat vibrating on the surface, which is
                // what inflated every skip count in docs/Findings-RealSkipPhysics.md §7
                // (see §8.3).
                if (wasInContact && !inContact && velocity.y > 0f)
                {
                    pendingExitZ = position.z;
                    hasPendingExit = true;
                }
                else if (!wasInContact && inContact && hasPendingExit)
                {
                    // Airborne distance between leaving the water and touching it again.
                    // This is the hop; the old code measured position.z minus the CONTACT
                    // ENTRY z, which is the length of the contact itself, not the flight.
                    var gap = position.z - pendingExitZ;
                    hasPendingExit = false;

                    // This branch consumes the contact-entry event, so it owes the same
                    // bookkeeping the plain entry branch below does.
                    lastContactZ = position.z;
                    var previousContactStart = contactStart;
                    contactStart = elapsed;

                    if (gap >= MinimumHopRadii * s.Radius)
                    {
                        result.Skips++;
                        if (firstGap < 0f) firstGap = gap;
                        lastGap = gap;
                    }

                    if (_trace && result.Skips > 0)
                    {
                        // Tilt of the face away from level: this is the tumble that ends runs.
                        var tiltNow = Mathf.Acos(Mathf.Clamp(n.y, -1f, 1f)) * Mathf.Rad2Deg;
                        var spinNow = Vector3.Dot(angularVelocity, n) / (2f * Mathf.PI);
                        Console.WriteLine($"   {result.Skips,4}{contactStart - elapsed + elapsed,0:F0}"
                                          + $"{gap,9:F2}{velocity.magnitude,10:F2}"
                                          + $"{(contactStart - previousContactStart) * 1000f,11:F2}"
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

        /// <summary>
        /// Check the rotating-pressure quadrature against the closed-form segment moments
        /// at zero rotation, and check that rotation only ever removes rotational energy.
        /// </summary>
        /// <remarks>
        /// Two properties the whole damping model rests on, both cheap to check and both
        /// catastrophic to get wrong:
        ///
        /// 1. **At ω = 0 the quadrature must equal the closed form.** If it does not, the
        ///    strip integration is wrong and the magic angle (R1) — measured at fixed
        ///    attitude, where ω plays no part — would move for a purely numerical reason.
        /// 2. **The moment must oppose rotation.** For a pitch rate `b`, the `v̂` component
        ///    of the hydrodynamic moment must carry the opposite sign to `b`. This is the
        ///    damping, and a sign error here inverts the stone rather than settling it.
        /// </remarks>
        private static bool PressureSelfTest()
        {
            const float radius = 0.05f;
            var ok = true;

            Console.WriteLine("1. quadrature vs closed form at omega = 0");
            Console.WriteLine($"   {"d/R",6}{"quad P",14}{"exact P",14}{"rel",10}{"quad Mx",14}{"exact Mx",14}{"rel",10}");
            foreach (var k in new[] { -0.9f, -0.6f, -0.3f, 0f, 0.3f, 0.6f, 0.9f })
            {
                var d = radius * k;
                const float vn = -3.5f;
                Game.DiscContact.RotatingPressureIntegrals(radius, d, vn, 0f, 0f,
                    out var p, out var along, out _);

                var m = Game.DiscContact.Moments(radius, d);
                var exactP = vn * vn * m.Area;
                var exactAlong = vn * vn * m.AlongFirst;

                var relP = Mathf.Abs(p - exactP) / Mathf.Max(Mathf.Abs(exactP), 1e-12f);

                // Mx is normalised by `radius * P`, NOT by itself. Mx is a moment arm times
                // a pressure, and the arm passes through zero as the segment approaches the
                // full disc (a full disc's centroid is its own centre), so a self-relative
                // error there is a small number divided by a smaller one and says nothing.
                // What matters physically is the error in the arm as a fraction of the
                // disc's own radius, which is exactly this.
                var relA = Mathf.Abs(along - exactAlong) / Mathf.Max(radius * Mathf.Abs(exactP), 1e-12f);
                if (relP > 0.005f || relA > 0.005f) ok = false;

                Console.WriteLine($"   {k,6:F1}{p,14:E4}{exactP,14:E4}{relP,10:E2}"
                                  + $"{along,14:E4}{exactAlong,14:E4}{relA,10:E2}");
            }

            Console.WriteLine();
            Console.WriteLine("2. the pitch-rate-dependent moment opposes pitch (the damping)");
            Console.WriteLine("   NB the TOTAL moment is dominated by the static restoring term that");
            Console.WriteLine("   exists at b = 0; damping is what the pitch rate ADDS to it.");

            // The v-hat component of the moment is -alongMoment (see Evaluate).
            Game.DiscContact.RotatingPressureIntegrals(radius, 0f, -3.5f, 0f, 0f,
                out _, out var alongAtRest, out _);
            var momentAtRest = -alongAtRest;

            Console.WriteLine($"   {"b rad/s",10}{"total moment",18}{"damping part",18}{"opposes?",10}");
            foreach (var b in new[] { -400f, -100f, -20f, 20f, 100f, 400f })
            {
                Game.DiscContact.RotatingPressureIntegrals(radius, 0f, -3.5f, 0f, b,
                    out _, out var along, out _);
                var moment = -along;
                var damping = moment - momentAtRest;
                var opposes = damping * b < 0f;
                if (!opposes) ok = false;
                Console.WriteLine($"   {b,10:F0}{moment,18:E4}{damping,18:E4}{(opposes ? "yes" : "NO"),10}");
            }

            Console.WriteLine();
            Console.WriteLine($"SELF TEST: {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        private static int Main(string[] args)
        {
            // Pull diagnostic flags out FIRST, so the positional numeric arguments below
            // never see them. They are named, so they can sit anywhere on the line.
            _noRotationalPressure = Array.IndexOf(args, "norotpressure") >= 0;
            _noTorque = Array.IndexOf(args, "notorque") >= 0;
            _noAddedMass = Array.IndexOf(args, "noaddedmass") >= 0;
            args = Array.FindAll(args,
                a => a != "norotpressure" && a != "notorque" && a != "noaddedmass");

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

            if (mode == "spin")
            {
                // Spin is what is supposed to hold the attitude through contact. If the full
                // model only matches the frozen-attitude run at implausible spin rates, the
                // stabilisation is too weak and that is a modelling problem, not a tuning one.
                var t = Baseline();
                if (args.Length > 1) t.LiftCoefficient = float.Parse(args[1], CultureInfo.InvariantCulture);
                Console.WriteLine($"C_l = {t.LiftCoefficient:F0}, 12 m/s, tilt {LaunchTiltDeg:F0} deg");
                Console.WriteLine("   Bocquet's stability floor is sqrt(g/R) ~ 14 rad/s ~ 2.2 rev/s;");
                Console.WriteLine("   a good human throw is 5-14 rev/s (docs section 6: measured max ~3.8 rev/s at the phone).");
                Console.WriteLine($"   {"rev/s",8}{"rad/s",9}{"skips",8}{"dist m",9}{"first gap m",13}{"end",22}");
                foreach (var rev in new[] { 2f, 5f, 10f, 14f, 25f, 50f, 100f, 200f })
                {
                    var r = SimulateDirect(t, 12f, rev * 2f * Mathf.PI, LaunchTiltDeg);
                    Console.WriteLine($"   {rev,8:F0}{rev * 2f * Mathf.PI,9:F0}{r.Skips,8}"
                                      + $"{r.Distance,9:F1}{r.FirstGapMetres,13:F2}  {r.EndReason,-20}");
                }
                return 0;
            }

            if (mode == "tilt")
            {
                // Is skip count set by the tilt alone? A contact force that only acts along
                // the face normal removes horizontal momentum in fixed proportion to the
                // vertical momentum it reverses -- the ratio is tan(tilt) -- so if that is
                // what caps the run, skips should scale like 1/tan(tilt) and no coefficient
                // should move it.
                var t = Baseline();
                if (args.Length > 1) t.LiftCoefficient = float.Parse(args[1], CultureInfo.InvariantCulture);
                Console.WriteLine($"C_l = {t.LiftCoefficient:F0}, 12 m/s");
                Console.WriteLine($"   {"tilt",6}{"tan",8}{"1/tan",8}{"skips",8}{"dist m",9}{"first gap m",13}");
                foreach (var tiltDeg in new[] { 30f, 20f, 15f, 10f, 7f, 5f, 3f, 2f })
                {
                    var r = SimulateDirect(t, 12f, 14f * 2f * Mathf.PI, tiltDeg);
                    // UnityShim's Mathf has no Tan; the physics files never needed one.
                    var rad = tiltDeg * Mathf.Deg2Rad;
                    var tan = Mathf.Sin(rad) / Mathf.Cos(rad);
                    Console.WriteLine($"   {tiltDeg,6:F0}{tan,8:F3}{1f / tan,8:F1}"
                                      + $"{r.Skips,8}{r.Distance,9:F1}{r.FirstGapMetres,13:F2}");
                }
                return 0;
            }

            if (mode == "incidence")
            {
                // How much of the shortfall is the launch condition rather than the model?
                // Release height sets the incidence angle at first contact.
                var t = Baseline();
                if (args.Length > 1) t.LiftCoefficient = float.Parse(args[1], CultureInfo.InvariantCulture);
                Console.WriteLine($"C_l = {t.LiftCoefficient:F0}, 12 m/s, launch tilt {LaunchTiltDeg:F0} deg");
                Console.WriteLine($"   {"h m",8}{"beta deg",10}{"skips",8}{"dist m",9}{"end",24}");
                foreach (var h in new[] { 0.30f, 0.15f, 0.08f, 0.04f, 0.02f, 0.01f, 0.005f })
                {
                    _releaseHeight = h;
                    var vy = Mathf.Sqrt(2f * t.Gravity * h);
                    var beta = Mathf.Atan2(vy, 12f) * Mathf.Rad2Deg;
                    var r = SimulateDirect(t, 12f, 14f * 2f * Mathf.PI, LaunchTiltDeg);
                    Console.WriteLine($"   {h,8:F3}{beta,10:F1}{r.Skips,8}{r.Distance,9:F1}  {r.EndReason,-24}");
                }
                _releaseHeight = 0.30f;
                return 0;
            }

            if (mode == "contact")
            {
                var t = Baseline();
                if (args.Length > 1) t.LiftCoefficient = float.Parse(args[1], CultureInfo.InvariantCulture);
                var speed = args.Length > 2 ? float.Parse(args[2], CultureInfo.InvariantCulture) : 12f;
                Console.WriteLine($"first contact, {speed:F0} m/s, C_l = {t.LiftCoefficient:F0}, "
                                  + $"launch tilt {LaunchTiltDeg:F0} deg");
                Console.WriteLine($"   {"t ms",9}{"h mm",9}{"d/R",8}{"wet",8}"
                                  + $"{"vz",8}{"vy",8}{"v.n",8}{"lift N",10}{"tilt",8}");
                _contactTrace = true;
                var r = SimulateDirect(t, speed, 14f * 2f * Mathf.PI, LaunchTiltDeg);
                _contactTrace = false;
                Console.WriteLine($"   => {r.Skips} skips, {r.Distance:F1} m, {r.EndReason}");
                return 0;
            }

            if (mode == "selftest")
            {
                return PressureSelfTest() ? 0 : 1;
            }

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
                if (_noRotationalPressure)
                    Console.WriteLine("DIAGNOSTIC: rotation hidden from the pressure law");
                if (_noTorque)
                    Console.WriteLine("DIAGNOSTIC: contact torque disabled entirely");
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
