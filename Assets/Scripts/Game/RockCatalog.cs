using System.Collections.Generic;
using System.Text;
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
        /// The longest prediction any rock in the catalog can produce.
        /// </summary>
        /// <remarks>
        /// AimPreview sizes its path buffer from this rather than from one tuning's
        /// MaxPredictSteps. Sizing it from a single rock would be an allocation waiting to
        /// happen: the buffer would grow the first time a player picked a stone with a longer
        /// PredictSeconds, and that reallocation lands mid-aim on exactly the throws people
        /// care most about. The buffer is a few thousand Vector3s — sizing it for the worst
        /// case once costs nothing worth measuring.
        /// </remarks>
        public int MaxPredictSteps
        {
            get
            {
                var steps = 1;
                if (IsEmpty) return steps;

                foreach (var rock in Rocks)
                {
                    if (rock == null) continue;

                    foreach (var set in new[] { TuningSet.Authored, TuningSet.Derived })
                    {
                        var tuning = rock.Tuning(set);
                        if (tuning != null) steps = Mathf.Max(steps, tuning.MaxPredictSteps);
                    }
                }
                return steps;
            }
        }

        /// <summary>
        /// Why the launch speed range has to be the same for every rock.
        /// </summary>
        /// <remarks>
        /// WaterRipples.Bind, SfxDirector.Awake and RockTrail all express their thresholds as
        /// FRACTIONS of SkipTuning.MaxSpeed, and two of the three read the global
        /// Resources/SkipTuning once at startup and never look again. Let MaxSpeed vary per
        /// rock and those normalisers are quietly wrong for five rocks out of six — which is
        /// the exact failure WaterRipples already carries a note about, from when its
        /// thresholds were hardcoded at 4 and 25 against a MaxSpeed of 26 and every impact
        /// clamped to full strength.
        ///
        /// It also protects the rule SkipTuning states at the top: power is the only driver of
        /// distance. If rocks differed in top speed then "how hard did I throw" would mean a
        /// different thing per rock, and a player could not carry a sense of effort from one
        /// stone to the next.
        ///
        /// So rock character comes from retention, the angle window, exit angle, restitution,
        /// minimum skip speed and the opening hop — never from the speed range.
        /// </remarks>
        public static readonly string[] SharedFieldNames =
            { "Gravity", "WaterY", "MinSpeed", "MaxSpeed", "FixedStep" };

        /// <summary>
        /// Checks the catalog is playable and that every tuning agrees on the shared fields.
        /// </summary>
        /// <returns>Empty when the catalog is sound; one line per problem otherwise.</returns>
        /// <remarks>
        /// Public and runtime-callable rather than living in the editor tool, so the same
        /// rule is enforced by the thing that writes the assets AND readable by anything that
        /// loads them. A rule enforced only at author time is a rule that a hand-edited asset
        /// walks straight through.
        /// </remarks>
        public List<string> Validate()
        {
            var problems = new List<string>();

            if (IsEmpty)
            {
                problems.Add("The catalog is empty — no rocks to choose from.");
                return problems;
            }

            SkipTuning reference = null;
            string referenceLabel = null;
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

                if (rock.AuthoredTuning == null && rock.DerivedTuning == null)
                {
                    problems.Add($"{rock.name}: no tuning in either set.");
                    continue;
                }

                foreach (var set in new[] { TuningSet.Authored, TuningSet.Derived })
                {
                    var tuning = set == TuningSet.Derived ? rock.DerivedTuning : rock.AuthoredTuning;
                    if (tuning == null) continue;

                    var label = $"{rock.name} ({set})";

                    if (reference == null)
                    {
                        reference = tuning;
                        referenceLabel = label;
                        continue;
                    }

                    var mismatch = DescribeSharedFieldMismatch(reference, tuning);
                    if (mismatch != null)
                    {
                        problems.Add($"{label} disagrees with {referenceLabel} on {mismatch}. " +
                                     "See RockCatalog.SharedFieldNames for why these must match.");
                    }
                }
            }

            return problems;
        }

        /// <summary>The first shared field the two tunings disagree on, or null if they agree.</summary>
        private static string DescribeSharedFieldMismatch(SkipTuning a, SkipTuning b)
        {
            var differences = new StringBuilder();

            void Compare(string field, float left, float right)
            {
                if (Mathf.Approximately(left, right)) return;
                if (differences.Length > 0) differences.Append(", ");
                differences.Append($"{field} ({right} vs {left})");
            }

            Compare("Gravity", a.Gravity, b.Gravity);
            Compare("WaterY", a.WaterY, b.WaterY);
            Compare("MinSpeed", a.MinSpeed, b.MinSpeed);
            Compare("MaxSpeed", a.MaxSpeed, b.MaxSpeed);
            Compare("FixedStep", a.FixedStep, b.FixedStep);

            return differences.Length > 0 ? differences.ToString() : null;
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
