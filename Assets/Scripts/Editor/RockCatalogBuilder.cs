using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game;
using UnityEditor;
using UnityEngine;

namespace EditorTools
{
    // Idempotent: re-running refreshes each rock's mesh and scale but never DisplayName, Blurb, Tint or
    // the tuning reference. doc-ref 9807 docs/4-systems/RockCatalogAndTuning.md
    public static class RockCatalogBuilder
    {
        private const string FbxPath = "Assets/Resources/Fbx/Rocks.fbx";
        private const string PrefabPath = "Assets/Resources/RockObject.prefab";
        private const string RockFolder = "Assets/Rocks";
        private const string CatalogPath = "Assets/Resources/RockCatalog.asset";

        /// <summary>
        /// Fallbacks for when RockObject.prefab cannot be read.
        /// </summary>
        /// <remarks>
        /// The real numbers are measured off the prefab, because the prefab IS the stone the
        /// game is currently balanced around — its (0.75, 0.39, 0.75) is a hand-found result,
        /// not a round number, and inventing a replacement would silently rescale every
        /// throw. These only apply if the prefab is missing, and the tool says so loudly when
        /// it falls back.
        /// </remarks>
        private const float FallbackFootprint = 0.75f;
        private const float FallbackFlatten = 0.52f;

        // One uniform 2.47 scale brings the 0.741 m authored Pebble to the 1.83 m stone players know; this is the only
        // place the game size lives. doc-ref 417e docs/4-systems/RockCatalogAndTuning.md
        private const float CatalogScale = 2.47f;

        // Names and flavour are seeded on first creation only, so hand edits always win over a rebuild.
        // doc-ref ffbf docs/4-systems/RockCatalogAndTuning.md
        private static readonly (string Name, string Blurb)[] Archetypes =
        {
            ("Skimmer", "Thin and fast. Rewards a clean angle, forgives nothing."),
            ("Slab",    "Broad and steady. Hard to throw badly."),
            ("Pebble",  "The one everyone learns on."),
            ("Nugget",  "Chunky. Big hops, short story."),
            ("Shard",   "Barely there. Skitters on long after it should have sunk."),
            ("Boulder", "Heavy going. Needs a real throw to stay up at all."),
        };

        [MenuItem("RockSkipping/Rocks/Build Catalog from FBX")]
        public static void Build()
        {
            var meshes = LoadMeshes();
            if (meshes == null) return;

            // Flatten is discarded now — see ReadReferenceStone's remarks: it only mattered
            // to NormalisedScale, which a uniform CatalogScale replaces.
            var (footprint, _) = ReadReferenceStone();

            Directory.CreateDirectory(RockFolder);
            AssetDatabase.Refresh();

            var definitions = new List<RockDefinition>();
            var created = 0;
            var report = new List<string>();

            for (var i = 0; i < meshes.Count; i++)
            {
                var mesh = meshes[i];
                var path = $"{RockFolder}/Rock_{i + 1:00}.asset";

                var rock = AssetDatabase.LoadAssetAtPath<RockDefinition>(path);
                var isNew = rock == null;

                if (isNew)
                {
                    rock = ScriptableObject.CreateInstance<RockDefinition>();
                    AssetDatabase.CreateAsset(rock, path);
                    created++;

                    // Seeded on creation only — see Archetypes.
                    var archetype = Archetypes[i % Archetypes.Length];
                    rock.DisplayName = archetype.Name;
                    rock.Blurb = archetype.Blurb;
                }

                // Id is stable across rebuilds and independent of the display name, because
                // it goes into the telemetry CSV and a renamed rock must not look like a new
                // one halfway through a comparison.
                rock.Id = $"rock_{i + 1:00}";
                rock.Mesh = mesh;

                // One scale for the whole catalog, never per rock — see CatalogScale. Unlike
                // the old NormalisedScale, it keeps each rock's size relative to the others.
                rock.LocalScale = Vector3.one * CatalogScale;

                EditorUtility.SetDirty(rock);
                definitions.Add(rock);

                // World size sits right next to the reference footprint so a rock built
                // too big or too small (a bad rocks.json value, a units mistake in Blender)
                // is visible in the Console rather than only on screen.
                var size = mesh.bounds.size * CatalogScale;
                report.Add($"  {rock.Id}  {rock.DisplayName,-8}  mesh {mesh.name,-16} " +
                           $"world {size.x:F2} x {size.y:F2} x {size.z:F2} m " +
                           $"(reference footprint {footprint:F2} m)  flatness {Flatness(mesh):F2}");
            }

            var catalog = LoadOrCreateCatalog();
            catalog.Rocks = definitions.ToArray();
            EditorUtility.SetDirty(catalog);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[RockCatalogBuilder] {CatalogPath}: {definitions.Count} rocks " +
                      $"({created} created, {definitions.Count - created} refreshed), " +
                      $"at catalog scale {CatalogScale:F2} (reference footprint {footprint:F2} m).\n" +
                      string.Join("\n", report));

            ReportValidation(catalog);
        }

        /// <summary>
        /// Every Mesh inside the FBX, in a stable order.
        /// </summary>
        /// <remarks>
        /// Ordinal sort by name, because the order is the shore layout and the index the
        /// random assignment draws against — a culture-sensitive sort could reorder the
        /// stones under players' pointers on a different machine.
        /// </remarks>
        private static List<Mesh> LoadMeshes()
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(FbxPath) == null)
            {
                Debug.LogError($"[RockCatalogBuilder] No model at {FbxPath}.");
                return null;
            }

            var meshes = AssetDatabase.LoadAllAssetsAtPath(FbxPath)
                .OfType<Mesh>()
                .OrderBy(m => m.name, System.StringComparer.Ordinal)
                .ToList();

            if (meshes.Count == 0)
            {
                Debug.LogError($"[RockCatalogBuilder] {FbxPath} contains no meshes.");
                return null;
            }

            if (meshes.Count < Archetypes.Length)
            {
                Debug.LogWarning($"[RockCatalogBuilder] {FbxPath} has {meshes.Count} meshes, " +
                                 $"fewer than the {Archetypes.Length} archetypes the authored " +
                                 "tuning set is aimed at.");
            }

            return meshes;
        }

        // Footprint of the original stone is kept only so Build's report can show a size regression;
        // Flatten is unused. doc-ref bf19 docs/4-systems/RockCatalogAndTuning.md
        private static (float Footprint, float Flatten) ReadReferenceStone()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var filter = prefab != null ? prefab.GetComponentInChildren<MeshFilter>() : null;

            if (filter == null || filter.sharedMesh == null)
            {
                Debug.LogWarning($"[RockCatalogBuilder] Could not measure {PrefabPath} — " +
                                 $"falling back to a {FallbackFootprint} footprint at " +
                                 $"{FallbackFlatten} flatten. Check the six rocks on screen " +
                                 "before trusting their size.");
                return (FallbackFootprint, FallbackFlatten);
            }

            var scale = filter.transform.localScale;
            var bounds = filter.sharedMesh.bounds.size;

            var footprint = Mathf.Max(bounds.x, bounds.z) * Mathf.Max(scale.x, scale.z);
            var flatten = Mathf.Approximately(scale.x, 0f) ? FallbackFlatten : scale.y / scale.x;

            return (footprint, flatten);
        }

        /// <summary>How flat a mesh is, 0..1, off its raw bounds. Shared with RockTuningGenerator.</summary>
        public static float Flatness(Mesh mesh)
        {
            var size = mesh.bounds.size;
            var widest = Mathf.Max(size.x, size.z);
            if (widest < 0.0001f) return 0f;

            return Mathf.Clamp01(1f - size.y / widest);
        }

        private static RockCatalog LoadOrCreateCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<RockCatalog>(CatalogPath);
            if (catalog != null) return catalog;

            // Resources, specifically — RockSelection loads this by name at runtime, and only
            // a Resources folder ships an asset nothing references directly.
            Directory.CreateDirectory("Assets/Resources");

            catalog = ScriptableObject.CreateInstance<RockCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);

            Debug.Log($"[RockCatalogBuilder] Created {CatalogPath}");
            return catalog;
        }

        /// <summary>
        /// Runs the catalog's own validation and reports it.
        /// </summary>
        /// <remarks>
        /// The rule lives on RockCatalog rather than here so the thing that writes the assets
        /// and the thing that loads them enforce the same one. A rule that only exists in an
        /// editor tool is a rule a hand-edited asset walks straight through.
        ///
        /// A missing tuning is expected right after this menu item and before the generator
        /// has run, so this reports rather than fails.
        /// </remarks>
        public static void ReportValidation(RockCatalog catalog)
        {
            var problems = catalog.Validate();
            if (problems.Count == 0)
            {
                Debug.Log("[RockCatalogBuilder] Catalog validates — every rock has a mesh, " +
                          "an id and a tuning.");
                return;
            }

            Debug.LogWarning($"[RockCatalogBuilder] {problems.Count} problem(s):\n  " +
                             string.Join("\n  ", problems));
        }

        [MenuItem("RockSkipping/Rocks/Validate Catalog")]
        public static void Validate()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<RockCatalog>(CatalogPath);
            if (catalog == null)
            {
                Debug.LogError($"[RockCatalogBuilder] No catalog at {CatalogPath}.");
                return;
            }

            ReportValidation(catalog);
        }
    }
}
