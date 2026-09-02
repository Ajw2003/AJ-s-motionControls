using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Every rock in the game, in a fixed order. Loaded by name from Resources/RockCatalog.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing twice over: it is the left-to-right layout of the stones on
    /// the shore, and it is the index the random assignment draws against. Reordering this
    /// array moves stones under players' pointers and changes what a recorded match seed
    /// replays as, so treat it as fixed once matches have been played.
    ///
    /// Only the catalog lives in Resources. The definitions and their tunings sit in
    /// Assets/Rocks and come along as asset dependencies — Resources stays a short list of
    /// things genuinely loaded by name, which is what it is for.
    /// </remarks>
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

        /// <summary>
        /// Checks the catalog is playable: every slot filled, every rock has a mesh, an id
        /// and a tuning, and no id repeats.
        /// </summary>
        /// <returns>Empty when the catalog is sound; one line per problem otherwise.</returns>
        /// <remarks>
        /// Used to also enforce that every rock's tuning agreed on shared fields like
        /// MaxSpeed and Gravity — see docs/plans/Plan-ThrowDrivenSkipModel.md. Those fields
        /// are not per-rock anymore; they live once, globally, on SkipConstants, so
        /// disagreement between rocks is no longer representable and there is nothing left
        /// to check there.
        /// </remarks>
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

        /// <summary>
        /// Last-resort empty catalog, so a missing asset degrades to the old single-rock game
        /// rather than taking the throw path down.
        /// </summary>
        /// <remarks>
        /// Same shape as SkipTuning.CreateDefault and for the same reason — but note what
        /// "default" means here. There is nothing sensible to invent: a catalog needs mesh
        /// references, and code cannot conjure those. So this is empty on purpose, and every
        /// caller treats an empty catalog as "fall back to Resources/SkipTuning". Reaching it
        /// means rock choosing is silently off, which is worth noticing rather than quietly
        /// living with.
        /// </remarks>
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
