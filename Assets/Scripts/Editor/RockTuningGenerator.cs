using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game;
using UnityEditor;
using UnityEngine;

namespace EditorTools
{
    /// <summary>
    /// Writes the two per-rock SkipTuning sets the catalog compares: one derived from each
    /// mesh's bounds, one seeded for hand tuning.
    /// </summary>
    /// <remarks>
    /// Both sets start from Resources/SkipTuning.asset, so neither is an invention — they are
    /// perturbations of numbers already known to pass SkipCurve's acceptance checks. That
    /// matters more than it sounds: the tuning has been recomputed by hand twice before and
    /// both times against a throwaway port of the solver rather than the solver itself, which
    /// is how a bug that zeroed bounce quality on every real throw survived a sweep that
    /// looked healthy. Starting from the shipped asset means the worst case is a set that
    /// plays like today, not one that plays like nothing.
    ///
    /// <b>Derived is overwritten every run. Authored is written once and then left alone.</b>
    /// That asymmetry is the whole point — derived is a function of the mesh and should track
    /// it, authored is a person's opinion and regenerating it would erase the opinion.
    ///
    /// Neither set may touch the fields RockCatalog.SharedFieldNames lists; CopySerialized
    /// brings them across from the baseline unchanged and the catalog's own validation
    /// catches it if a hand edit ever moves one.
    /// </remarks>
    public static class RockTuningGenerator
    {
        private const string BaselinePath = "Assets/Resources/SkipTuning.asset";
        private const string CatalogPath = "Assets/Resources/RockCatalog.asset";
        private const string AuthoredFolder = "Assets/Rocks/Tuning/Authored";
        private const string DerivedFolder = "Assets/Rocks/Tuning/Derived";

        /// <summary>What a mesh's bounds say about it, normalised across the catalog.</summary>
        private readonly struct Shape
        {
            /// <summary>0 = a ball, 1 = a wafer. Drives retention, tolerance and exit angle.</summary>
            public readonly float Flat;

            /// <summary>0 = the smallest stone in the set, 1 = the largest. Drives min skip speed.</summary>
            public readonly float Size;

            /// <summary>0 = round in plan, 1 = a long splinter. Nudges the best face angle.</summary>
            public readonly float Elongation;

            public Shape(float flat, float size, float elongation)
            {
                Flat = flat;
                Size = size;
                Elongation = elongation;
            }
        }

        // ------------------------------------------------------------------ menu items

        [MenuItem("RockSkipping/Rocks/Seed Authored Tunings")]
        public static void SeedAuthored()
        {
            if (!Load(out var baseline, out var catalog)) return;

            Directory.CreateDirectory(AuthoredFolder);
            AssetDatabase.Refresh();

            var created = 0;
            var kept = 0;

            foreach (var rock in catalog.Rocks.Where(r => r != null))
            {
                var path = $"{AuthoredFolder}/{rock.Id}.asset";
                var existing = AssetDatabase.LoadAssetAtPath<SkipTuning>(path);

                if (existing != null)
                {
                    // Never overwritten. This is somebody's hand tuning and it is half the
                    // experiment — see the remarks on this class.
                    rock.AuthoredTuning = existing;
                    kept++;
                }
                else
                {
                    rock.AuthoredTuning = CreateFrom(baseline, path, rock.DisplayName);
                    created++;
                }

                EditorUtility.SetDirty(rock);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[RockTuningGenerator] Authored set: {created} seeded from the baseline, " +
                      $"{kept} left untouched.\n" +
                      "Now open them and tune. Leave one rock exactly as seeded — an unchanged " +
                      "control is what makes the authored-vs-derived report readable, because " +
                      "one row is then guaranteed to be today's game.");

            WarnOnAngleSplit(catalog);
        }

        [MenuItem("RockSkipping/Rocks/Generate Derived Tunings")]
        public static void GenerateDerived()
        {
            if (!Load(out var baseline, out var catalog)) return;

            var rocks = catalog.Rocks.Where(r => r != null && r.Mesh != null).ToList();
            if (rocks.Count == 0)
            {
                Debug.LogError("[RockTuningGenerator] No rocks with meshes — run " +
                               "RockSkipping/Rocks/Build Catalog from FBX first.");
                return;
            }

            Directory.CreateDirectory(DerivedFolder);
            AssetDatabase.Refresh();

            var shapes = MeasureShapes(rocks, out var spreadReport);
            var report = new List<string>();

            for (var i = 0; i < rocks.Count; i++)
            {
                var rock = rocks[i];
                var path = $"{DerivedFolder}/{rock.Id}.asset";

                var tuning = AssetDatabase.LoadAssetAtPath<SkipTuning>(path);
                if (tuning == null)
                {
                    tuning = CreateFrom(baseline, path, rock.DisplayName);
                }
                else
                {
                    // CopySerialized rather than a fresh asset, so the GUID survives and the
                    // RockDefinition's reference to it does not break on every regeneration.
                    EditorUtility.CopySerialized(baseline, tuning);
                    tuning.name = Path.GetFileNameWithoutExtension(path);
                }

                Apply(tuning, baseline, shapes[i], rock.DisplayName);

                EditorUtility.SetDirty(tuning);
                rock.DerivedTuning = tuning;
                EditorUtility.SetDirty(rock);

                report.Add($"  {rock.Id}  {rock.DisplayName,-8} " +
                           $"flat {shapes[i].Flat:F2}  size {shapes[i].Size:F2}  elong {shapes[i].Elongation:F2}  ->  " +
                           $"keep {tuning.MinRetention:F2}..{tuning.MaxRetention:F2}  " +
                           $"tol {tuning.ToleranceDeg:F0}  exit {tuning.SkipExitAngleDeg:F1}  " +
                           $"minSkip {tuning.MinSkipSpeed:F1}  rest {tuning.Restitution:F2}");
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[RockTuningGenerator] Derived set: {rocks.Count} tunings written from " +
                      $"mesh bounds.\n{spreadReport}\n" + string.Join("\n", report) +
                      "\nRun RockSkipping/Verify/Print Skip Curve to see whether these six actually " +
                      "separate — a narrow raw spread above means they cannot.");

            RockCatalogBuilder.ReportValidation(catalog);
        }

        // ------------------------------------------------------------------ measurement

        /// <summary>
        /// Reads flatness, size and elongation off every mesh and normalises each across the set.
        /// </summary>
        /// <remarks>
        /// Bounds only. RocksRemeshed.fbx.meta has isReadable: 0, so mesh.vertices is not
        /// safely available and anything wanting real volume would need the Read/Write box
        /// ticked — which costs a permanent duplicate of every mesh in memory for a number
        /// this does not need.
        ///
        /// Normalised against the catalog's own min and max rather than against absolute
        /// thresholds, because the six were modelled to look like rocks and not to span a
        /// gameplay range. The raw spread is reported alongside for exactly that reason: if
        /// the six meshes are nearly identical in shape, this stretches a hair's-breadth
        /// difference across the full tuning range and the derived set is amplified noise
        /// rather than physics. That is a finding, not a failure — but it has to be visible.
        /// </remarks>
        private static Shape[] MeasureShapes(List<RockDefinition> rocks, out string spreadReport)
        {
            var flat = rocks.Select(r => RockCatalogBuilder.Flatness(r.Mesh)).ToArray();

            var size = rocks
                .Select(r => Mathf.Max(r.Mesh.bounds.size.x, r.Mesh.bounds.size.z))
                .ToArray();

            var elongation = rocks.Select(r =>
            {
                var s = r.Mesh.bounds.size;
                var widest = Mathf.Max(s.x, s.z);
                return widest < 0.0001f ? 0f : Mathf.Abs(s.x - s.z) / widest;
            }).ToArray();

            spreadReport =
                $"  raw spread — flatness {flat.Min():F3}..{flat.Max():F3}, " +
                $"footprint {size.Min():F2}..{size.Max():F2}, " +
                $"elongation {elongation.Min():F3}..{elongation.Max():F3}";

            if (flat.Max() - flat.Min() < 0.08f)
            {
                spreadReport += "\n  WARNING: the six meshes are within 0.08 flatness of each " +
                                "other. The derived set is stretching near-identical shapes " +
                                "across the full tuning range, so its differences are noise.";
            }

            var shapes = new Shape[rocks.Count];
            for (var i = 0; i < rocks.Count; i++)
            {
                shapes[i] = new Shape(
                    Normalise(flat, i), Normalise(size, i), Normalise(elongation, i));
            }
            return shapes;
        }

        /// <summary>
        /// Where one value sits between the set's own min and max. Mid-range when they match,
        /// so an identical set produces identical tunings rather than a divide by zero.
        /// </summary>
        private static float Normalise(float[] values, int index)
        {
            var min = values.Min();
            var max = values.Max();

            return Mathf.Approximately(min, max) ? 0.5f : Mathf.InverseLerp(min, max, values[index]);
        }

        // ------------------------------------------------------------------ the mapping

        /// <summary>
        /// The physical claim this whole set is testing, written as code.
        /// </summary>
        /// <remarks>
        /// Every line is a statement about real stones that a played match can disagree with:
        ///
        /// A <b>flatter</b> stone presents more surface to the water, so it keeps more speed
        /// per bounce, forgives more angle error, and leaves the surface shallower.
        ///
        /// A <b>bigger</b> stone needs more speed to stay up at all, and slaps harder when it
        /// fails to — more restitution, so a bad throw skids rather than stopping dead.
        ///
        /// An <b>elongated</b> stone wants a slightly different attitude, so the whole angle
        /// window shifts. BestFaceAngleDeg and OptimalAngle shift TOGETHER by the same offset:
        /// they govern the two halves of one bounce (per-throw retention and per-bounce
        /// quality respectively) and moving one without the other is how they came apart in
        /// the shipped asset — see WarnOnAngleSplit.
        ///
        /// Nothing here touches the launch speed range. See RockCatalog.SharedFieldNames.
        /// </remarks>
        private static void Apply(SkipTuning tuning, SkipTuning baseline, Shape shape, string displayName)
        {
            tuning.DisplayName = displayName;

            // Flatter keeps more speed. Capped short of 1 — at full retention the stone never
            // slows down and the run ends only when the prediction horizon does.
            tuning.MinRetention = Mathf.Clamp(
                Mathf.Lerp(baseline.MinRetention - 0.08f, baseline.MinRetention + 0.04f, shape.Flat),
                0.4f, 0.98f);

            tuning.MaxRetention = Mathf.Clamp(
                Mathf.Lerp(baseline.MaxRetention - 0.06f, baseline.MaxRetention + 0.03f, shape.Flat),
                tuning.MinRetention + 0.01f, 0.985f);

            // Flatter forgives more. The floor stays wide on purpose: the throw fires the
            // instant the phone stops, which is exactly when the wrist has pitched over, so
            // even the least forgiving stone has to absorb a systematically off-neutral
            // release. See SkipTuning.ToleranceDeg.
            tuning.ToleranceDeg = Mathf.Lerp(
                baseline.ToleranceDeg * 0.65f, baseline.ToleranceDeg * 1.25f, shape.Flat);

            // Flatter leaves the water shallower. Hop LENGTH is (2 tan(angle) / g) * speed^2,
            // so this sets the whole shape of a run — a steep exit reads as mortar fire.
            tuning.SkipExitAngleDeg = Mathf.Lerp(
                baseline.SkipExitAngleDeg * 1.8f, baseline.SkipExitAngleDeg * 0.6f, shape.Flat);

            tuning.OpeningHopAngleDeg = Mathf.Max(
                tuning.SkipExitAngleDeg,
                Mathf.Lerp(baseline.OpeningHopAngleDeg * 1.6f,
                           baseline.OpeningHopAngleDeg * 0.8f, shape.Flat));

            // Bigger needs more speed to stay up, and slaps harder when it does not.
            tuning.MinSkipSpeed = Mathf.Lerp(
                baseline.MinSkipSpeed * 0.6f, baseline.MinSkipSpeed * 1.5f, shape.Size);

            tuning.Restitution = Mathf.Clamp(
                Mathf.Lerp(baseline.Restitution * 0.7f, baseline.Restitution * 1.4f, shape.Size),
                0.05f, 0.9f);

            var offset = Mathf.Lerp(-3f, 4f, shape.Elongation);
            tuning.BestFaceAngleDeg = baseline.BestFaceAngleDeg + offset;
            tuning.OptimalAngle = baseline.OptimalAngle + offset;
        }

        // ------------------------------------------------------------------ plumbing

        private static bool Load(out SkipTuning baseline, out RockCatalog catalog)
        {
            baseline = AssetDatabase.LoadAssetAtPath<SkipTuning>(BaselinePath);
            catalog = AssetDatabase.LoadAssetAtPath<RockCatalog>(CatalogPath);

            if (baseline == null)
            {
                Debug.LogError($"[RockTuningGenerator] No baseline tuning at {BaselinePath}. " +
                               "Both sets are perturbations of it, so there is nothing to start from.");
                return false;
            }

            if (catalog == null || catalog.IsEmpty)
            {
                Debug.LogError($"[RockTuningGenerator] No populated catalog at {CatalogPath} — " +
                               "run RockSkipping/Rocks/Build Catalog from FBX first.");
                return false;
            }

            return true;
        }

        private static SkipTuning CreateFrom(SkipTuning baseline, string path, string displayName)
        {
            var tuning = Object.Instantiate(baseline);
            tuning.name = Path.GetFileNameWithoutExtension(path);
            tuning.DisplayName = displayName;

            AssetDatabase.CreateAsset(tuning, path);
            return tuning;
        }

        /// <summary>
        /// Says out loud when a rock's two "best angle" fields disagree.
        /// </summary>
        /// <remarks>
        /// The shipped baseline has BestFaceAngleDeg 5 and OptimalAngle 10. They govern the
        /// two halves of one bounce — OptimalAngle sets the per-throw speed retention in
        /// CreateLaunch, BestFaceAngleDeg sets the per-bounce quality in Step — and both
        /// divide by the same ToleranceDeg. Five degrees apart means the angle that gives the
        /// best retention is not the angle that gives the best bounce, so no throw can be
        /// fully right and the miss is invisible.
        ///
        /// Not corrected here. Closing the gap changes how today's game plays, and that is a
        /// decision rather than a cleanup — but a hand-tuned set can easily widen it by
        /// accident, so it gets named every time.
        /// </remarks>
        private static void WarnOnAngleSplit(RockCatalog catalog)
        {
            var split = new List<string>();

            foreach (var rock in catalog.Rocks.Where(r => r?.AuthoredTuning != null))
            {
                var t = rock.AuthoredTuning;
                var gap = Mathf.Abs(t.OptimalAngle - t.BestFaceAngleDeg);
                if (gap > 0.01f) split.Add($"{rock.DisplayName}: {t.BestFaceAngleDeg} vs {t.OptimalAngle}");
            }

            if (split.Count == 0) return;

            Debug.LogWarning(
                "[RockTuningGenerator] BestFaceAngleDeg and OptimalAngle disagree on " +
                $"{split.Count} authored rock(s): {string.Join(", ", split)}.\n" +
                "They govern the two halves of one bounce, so a gap means no throw angle can " +
                "be fully right. Inherited from the shipped baseline — left alone deliberately, " +
                "but worth deciding on rather than drifting further.");
        }
    }
}
