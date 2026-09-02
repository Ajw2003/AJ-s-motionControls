using UnityEngine;

namespace Game
{
    /// <summary>
    /// Every number behind the shore-pointing rock picker: layout, hysteresis and colour.
    /// One asset, edited in the inspector, no recompile.
    /// </summary>
    /// <remarks>
    /// A ScriptableObject at Resources/RockSelectTuning for the same reason
    /// AimFeedbackTuning is one: <see cref="ShoreRocks"/> is built by AimPreview with
    /// AddComponent, so a [SerializeField] on it would sit at its C# field initializer
    /// forever. See AimPreview.Awake's own note about having been caught by that once.
    ///
    /// The asset is optional — <see cref="CreateDefault"/> is a fully playable fallback,
    /// the same load-or-warn pattern every other tuning asset in this project uses. Create
    /// the asset from the menu (RockSkipping/Rock Select Tuning) only once these numbers
    /// are worth hand-tuning against a real phone.
    /// </remarks>
    [CreateAssetMenu(menuName = "RockSkipping/Rock Select Tuning", fileName = "RockSelectTuning")]
    public class RockSelectTuning : ScriptableObject
    {
        [Header("Layout")]
        [Tooltip("Half-width of the fan the stones are spread across, in degrees either " +
                 "side of centre. A phone's identity quaternion — however it was held at " +
                 "the tap — is yaw 0, so this is centred on the player, not the TV.")]
        [Range(5f, 90f)] public float FanDegrees = 38f;

        [Tooltip("Distance from the throw origin the stones sit at.")]
        public float ShoreRadius = 6f;

        [Tooltip("Height above the throw origin the stones and pointers sit at.")]
        public float ShoreHeight = 0f;

        [Header("Pointer")]
        [Tooltip("Degrees the pointer must cross past a boundary before the highlight " +
                 "moves. Without this, a pointer resting on a boundary flickers between " +
                 "two stones at 20Hz.")]
        [Range(0f, 15f)] public float StickyMarginDeg = 3f;

        [Tooltip("How fast the on-screen pointer chases a new AIM frame, same shape as " +
                 "AimFeedbackTuning.ColourSmoothing — exponential, frame-rate independent.")]
        [Range(0.5f, 40f)] public float PointerSmoothing = 12f;

        [Tooltip("A player's pointer disappears after this long with no AIM frames — " +
                 "surfaces a dropped phone rather than leaving a stale marker on the shore.")]
        public float PointerStaleSeconds = 1.5f;

        [Header("Look")]
        [Tooltip("Scale multiplier on whichever stone is currently highlighted.")]
        [Range(1f, 1.5f)] public float HighlightScale = 1.12f;

        [Tooltip("Tint blended onto a stone while at least one pointer is on it.")]
        public Color HighlightColour = new(0.95f, 0.85f, 0.35f);

        [Tooltip("Tint blended onto a stone once a player has confirmed it.")]
        public Color ConfirmedColour = new(0.4f, 0.9f, 0.5f);

        /// <summary>
        /// Last-resort fallback so the shore still lays out and picks still work if the
        /// asset is missing. Reaching this means these numbers are back to being a
        /// recompile, so callers should warn the same way SkipTuning/AimFeedbackTuning do.
        /// </summary>
        public static RockSelectTuning CreateDefault()
        {
            var tuning = CreateInstance<RockSelectTuning>();
            tuning.name = "RockSelectTuning (runtime default)";
            return tuning;
        }
    }
}
