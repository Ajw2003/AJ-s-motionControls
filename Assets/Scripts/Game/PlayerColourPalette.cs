using UnityEngine;

namespace Game
{
    // The palette is a Resources asset, not a component, so a human can edit it; colour is keyed by
    // player identity alone through PlayerColours. doc-ref 6272 docs/4-systems/Presentation.md
    [CreateAssetMenu(menuName = "RockSkipping/Player Colour Palette", fileName = "PlayerColourPalette")]
    public class PlayerColourPalette : ScriptableObject
    {
        [Header("Palette")]
        [Tooltip("The default colours, handed out in join order — furthest-from-everyone " +
                 "first, so eight players get eight distinct colours without anyone " +
                 "picking. A phone may override its own with any colour it likes; these " +
                 "are also the quick-pick swatches its picker offers.\n\n" +
                 "Chosen to read against the lake and against each other at pointer-dot " +
                 "size. Adding more is fine; the assignment does not assume eight.")]
        public Color[] Swatches =
        {
            new(0.95f, 0.35f, 0.35f), new(0.35f, 0.75f, 0.95f), new(0.95f, 0.85f, 0.35f),
            new(0.55f, 0.9f, 0.55f), new(0.85f, 0.5f, 0.95f), new(0.95f, 0.6f, 0.3f),
            new(0.6f, 0.8f, 0.9f), new(0.9f, 0.9f, 0.9f),
        };

        [Header("Guards on a phone's pick")]
        [Tooltip("How different two connected players' colours must be, as a distance in " +
                 "hue/saturation/value space. A pick closer than this to somebody else's " +
                 "is rotated away in hue rather than refused — the player still gets " +
                 "something close to what they asked for, and the two dots stay tellable " +
                 "apart on a TV across the room.\n\n" +
                 "0 disables the check and lets two players be identical.\n\n" +
                 "Default 0.08 because every swatch above must itself be a legal pick: the " +
                 "closest pair in the shipped palette (yellow and orange) sits at 0.094, so " +
                 "a higher threshold would quietly rotate a player off the swatch they just " +
                 "tapped. Raise this only alongside a palette spaced to match.")]
        [Range(0f, 0.5f)] public float MinSeparation = 0.08f;

        [Tooltip("Floor on a picked colour's brightness. Below this a dot reads as a hole " +
                 "in the water rather than as a player — the lake is already dark.")]
        [Range(0f, 1f)] public float MinValue = 0.45f;

        [Tooltip("Floor on a picked colour's saturation, so a near-grey pick is still " +
                 "identifiably somebody's colour. White is a legitimate choice, which is " +
                 "why this floor is gentle rather than absolute.")]
        [Range(0f, 1f)] public float MinSaturation = 0.25f;

        [Header("Materials")]
        [Tooltip("How far a stone is pulled toward the colour of the player it belongs to: " +
                 "0 leaves the rock's own tint alone, 1 paints it flat in their colour. " +
                 "Partway keeps the stone looking like stone while still answering " +
                 "'whose is that'.\n\n" +
                 "One knob for all three stones — the claimed stone on the shore, the stone " +
                 "in flight, and the aim ghost — because the rock you picked has to look " +
                 "like the rock you threw. See PlayerColours.StoneColourFor.")]
        [Range(0f, 1f)] public float ConfirmedBlend = 0.65f;

        // No highlight knob here on purpose: the pointed-at (not yet confirmed) stone is
        // still RockSelectTuning.HighlightColour, because a highlight belongs to whoever is
        // pointing and is gone the moment they look away — it is not a player's colour. A
        // knob here that nothing reads would only look like one.

        /// <summary>The palette entry furthest from every colour already taken.</summary>
        /// <remarks>
        /// Furthest-first rather than next-index: the index would depend on join order
        /// surviving, which is exactly the assumption the old ShoreRocks code made and the
        /// reason colours moved around between rounds. This depends only on which colours
        /// are currently in use, so it gives the same answer however the roster got here.
        /// </remarks>
        public Color FurthestFrom(System.Collections.Generic.ICollection<Color> taken)
        {
            if (Swatches == null || Swatches.Length == 0) return Color.white;
            if (taken == null || taken.Count == 0) return Swatches[0];

            var best = Swatches[0];
            var bestDistance = -1f;

            foreach (var candidate in Swatches)
            {
                var nearest = float.MaxValue;
                foreach (var used in taken) nearest = Mathf.Min(nearest, Distance(candidate, used));

                if (nearest <= bestDistance) continue;
                bestDistance = nearest;
                best = candidate;
            }

            return best;
        }

        /// <summary>
        /// How far apart two colours look, 0 (identical) upward. Hue is weighted hardest
        /// because at dot size that is what the eye actually reads.
        /// </summary>
        public static float Distance(Color a, Color b)
        {
            Color.RGBToHSV(a, out var ha, out var sa, out var va);
            Color.RGBToHSV(b, out var hb, out var sb, out var vb);

            // Hue wraps: red at 0.98 and red at 0.02 are neighbours, not opposites.
            var dh = Mathf.Abs(ha - hb);
            if (dh > 0.5f) dh = 1f - dh;

            // A grey has no meaningful hue, so hue only counts as far as both colours are
            // saturated enough for it to be visible.
            var hueWeight = Mathf.Min(sa, sb);

            return Mathf.Sqrt(
                Mathf.Pow(dh * 2f * hueWeight, 2f) +
                Mathf.Pow(sa - sb, 2f) +
                Mathf.Pow(va - vb, 2f));
        }

        /// <summary>Last-resort fallback so colours still work if the asset is missing.</summary>
        public static PlayerColourPalette CreateDefault()
        {
            var palette = CreateInstance<PlayerColourPalette>();
            palette.name = "PlayerColourPalette (runtime default)";
            return palette;
        }

        /// <summary>The authored asset, or a runtime default with a warning.</summary>
        public static PlayerColourPalette LoadOrWarn()
        {
            var palette = Resources.Load<PlayerColourPalette>("PlayerColourPalette");
            if (palette != null) return palette;

            Debug.LogWarning("[PlayerColourPalette] No Resources/PlayerColourPalette asset — " +
                             "running on code defaults, which cannot be tuned without a recompile.");
            return CreateDefault();
        }
    }
}
