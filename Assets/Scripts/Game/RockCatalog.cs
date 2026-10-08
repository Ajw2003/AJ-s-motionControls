using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    // Catalog order is load-bearing: it sets the shore layout and the random-assignment index, so treat
    // it as fixed once matches have been played. doc-ref d540 docs/4-systems/RockCatalogAndTuning.md
    [CreateAssetMenu(menuName = "RockSkipping/Rock Catalog", fileName = "RockCatalog")]
    public class RockCatalog : ScriptableObject
    {
        [Tooltip("In shore order, left to right. Built by Editor/RockCatalogBuilder.")]
        public RockDefinition[] Rocks = new RockDefinition[0];

        public int Count => Rocks?.Length ?? 0;
        public bool IsEmpty => Count == 0;

        /// <summary>Index-safe lookup. Wraps rather than throwing, so a stale index cannot crash a turn.</summary>
        public RockDefinition At(int index)
        {
            if (IsEmpty) return null;
            return Rocks[((index % Count) + Count) % Count];
        }

        public RockDefinition ById(string id)
        {
            if (string.IsNullOrEmpty(id) || IsEmpty) return null;

            foreach (var rock in Rocks)
            {
                if (rock != null && rock.Id == id) return rock;
            }
            return null;
        }

        public int IndexOf(RockDefinition rock)
        {
            if (rock == null || IsEmpty) return -1;

            for (var i = 0; i < Rocks.Length; i++)
            {
                if (Rocks[i] == rock) return i;
            }
            return -1;
        }

        // Validate checks slots, mesh, id and tuning; per-rock disagreement on shared fields is no longer representable,
        // so it is not checked. doc-ref 5bc6 docs/4-systems/RockCatalogAndTuning.md
        public List<string> Validate()
        {
            var problems = new List<string>();

            if (IsEmpty)
            {
                problems.Add("The catalog is empty — no rocks to choose from.");
                return problems;
            }

            var seenIds = new HashSet<string>();

            foreach (var rock in Rocks)
            {
                if (rock == null)
                {
                    problems.Add("A slot in Rocks is empty.");
                    continue;
                }

                if (rock.Mesh == null) problems.Add($"{rock.name}: no mesh.");
                if (!seenIds.Add(rock.Id)) problems.Add($"{rock.name}: duplicate Id '{rock.Id}'.");
                if (rock.Tuning == null) problems.Add($"{rock.name}: no tuning.");
            }

            return problems;
        }

        // CreateDefault is empty on purpose and callers fall back to Resources/SkipTuning; reaching it
        // means rock choosing is silently off. doc-ref ccf1 docs/4-systems/RockCatalogAndTuning.md
        public static RockCatalog CreateDefault()
        {
            var catalog = CreateInstance<RockCatalog>();
            catalog.name = "RockCatalog (runtime default, empty)";
            return catalog;
        }

        /// <summary>
        /// The authored asset, or an empty catalog with a warning.
        /// </summary>
        /// <remarks>
        /// The load-or-warn pattern AimPreview.Awake uses for SkipTuning, hoisted here because
        /// more than one system needs the catalog now and three copies of a three-step load
        /// is three places for the warning to go missing.
        /// </remarks>
        public static RockCatalog LoadOrWarn()
        {
            var catalog = Resources.Load<RockCatalog>("RockCatalog");
            if (catalog != null && !catalog.IsEmpty) return catalog;

            Debug.LogWarning(
                "[RockCatalog] No usable catalog at Resources/RockCatalog — rock choosing is " +
                "off and every throw will use Resources/SkipTuning. Run " +
                "RockSkipping/Rocks/Build Catalog from FBX.");

            return catalog != null ? catalog : CreateDefault();
        }
    }
}
