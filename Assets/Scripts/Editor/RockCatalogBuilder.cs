using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game;
using UnityEditor;
using UnityEngine;

namespace EditorTools
{
    /// <summary>
    /// Turns the six meshes inside RocksRemeshed.fbx into six RockDefinition assets and a
    /// RockCatalog that lists them.
    /// </summary>
    /// <remarks>
    /// The FBX has held six meshes since it was imported and the game has only ever drawn
    /// one — RockObject.prefab pins a single mesh by fileID. Wiring the other five up by
    /// hand would mean dragging sub-assets out of an FBX into six inspectors and doing it
    /// again every time the model is re-exported from Blender, which is exactly the manual
    /// step that silently stops being done.
    ///
    /// <b>Idempotent.</b> Re-running refreshes the mesh reference and the scale, and leaves
    /// DisplayName, Blurb, Tint and both tuning references alone. Same contract as
    /// AudioBankBuilder: regeneration is safe, and losing hand tuning to it would make the
    /// tool something you avoid running.
    ///
    /// Tunings are not touched here — see RockTuningGenerator.
    /// </remarks>
    public static class RockCatalogBuilder
    {
        private const string FbxPath = "Assets/Resources/Fbx/RocksRemeshed.fbx";
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

        /// <summary>
        /// Names and flavour for first creation only. Hand edits win from then on.
        /// </summary>
        /// <remarks>
        /// Ordered to match the archetypes the authored tuning set is aimed at, so a fresh
        /// build lines the two up. The mesh-to-archetype assignment is arbitrary on the first
        /// run — the meshes import as NurbsCurve.001..007, leftover Blender curve names that
        /// say nothing about shape — so expect to reshuffle the names once the six are on
        /// screen and their silhouettes can be seen. That reshuffle is exactly why these are
        /// seeded rather than enforced.
        /// </remarks>
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

            var (footprint, flatten) = ReadReferenceStone();

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
                rock.LocalScale = NormalisedScale(mesh, footprint, flatten);

                EditorUtility.SetDirty(rock);
                definitions.Add(rock);

                var size = mesh.bounds.size;
                report.Add($"  {rock.Id}  {rock.DisplayName,-8}  mesh {mesh.name,-16} " +
                           $"raw {size.x:F2} x {size.y:F2} x {size.z:F2}  " +
                           $"scale {rock.LocalScale.x:F3}  flatness {Flatness(mesh):F2}");
            }

            var catalog = LoadOrCreateCatalog();
            catalog.Rocks = definitions.ToArray();
            EditorUtility.SetDirty(catalog);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[RockCatalogBuilder] {CatalogPath}: {definitions.Count} rocks " +
                      $"({created} created, {definitions.Count - created} refreshed), " +
                      $"normalised to a {footprint:F2} footprint at {flatten:F2} flatten.\n" +
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

        /// <summary>
        /// The footprint and flatten factor of the stone the game is currently balanced on.
        /// </summary>
        /// <remarks>
        /// Measured rather than declared. RockObject.prefab carries a hand-found
        /// (0.75, 0.39, 0.75) against one specific mesh, so the world size it produces is the
        /// only size any player has ever seen a stone at. Reading it back means the six new
        /// rocks land in the same visual range instead of near it.
        ///
        /// Flatten is that prefab's y-to-x ratio, kept as a catalog-wide squash so the set
        /// reads as flat as today's stone does. It is applied to the VISUAL only — the
        /// derived tunings measure flatness off the raw mesh bounds, and a uniform squash
        /// preserves their ordering, so the stone that looks flattest is still the one the
        /// generator calls flattest.
        /// </remarks>
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

        /// <summary>
        /// Scale that brings a mesh to the common footprint, squashed to the common flatness.
        /// </summary>
        /// <remarks>
        /// Uniform on x and z so the stone is not stretched — the meshes are irregular, and
        /// normalising each axis separately would round every one of them out into the same
        /// blob and throw away the elongation the derived tunings read.
        /// </remarks>
        private static Vector3 NormalisedScale(Mesh mesh, float footprint, float flatten)
        {
            var size = mesh.bounds.size;
            var widest = Mathf.Max(size.x, size.z);

            // A degenerate mesh would divide by zero and scale to infinity, which shows up as
            // the stone vanishing rather than as an error.
            if (widest < 0.0001f)
            {
                Debug.LogWarning($"[RockCatalogBuilder] {mesh.name} has no width — left at scale 1.");
                return Vector3.one;
            }

            var s = footprint / widest;
            return new Vector3(s, s * flatten, s);
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
                Debug.Log("[RockCatalogBuilder] Catalog validates — every tuning agrees on " +
                          string.Join(", ", RockCatalog.SharedFieldNames) + ".");
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
