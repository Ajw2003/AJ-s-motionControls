using UnityEngine;

namespace Game
{
    /// <summary>
    /// Every number that shapes the ribbon behind a stone. One asset, edited in the
    /// inspector, no recompile.
    /// </summary>
    /// <remarks>
    /// A ScriptableObject at Resources/TrailTuning for the same reason SkipTuning is one,
    /// and it is not a style preference. The rock is built at runtime by
    /// RockLauncher.CreateRockObject, so anything holding trail numbers as [SerializeField]
    /// on a component would sit at its C# field initializer forever — the exact trap
    /// AimPreview.Awake documents having already been caught by once, where "the whole game
    /// ran on C# field initializers and the numbers were only reachable by recompiling."
    ///
    /// Speed-derived values are stored as FRACTIONS of SkipConstants.ReferenceSpeed rather
    /// than absolutes. WaterRipples carries the note explaining why: its impact thresholds
    /// were hardcoded at 4 and 25 against a fixed MaxSpeed of 26, so raising the launch
    /// speed clamped every impact to full strength and the ripple stopped responding to
    /// anything at all. A trail tinted by absolute speed fails the same way and just as
    /// quietly.
    /// </remarks>
    [CreateAssetMenu(menuName = "RockSkipping/Trail Tuning", fileName = "TrailTuning")]
    public class TrailTuning : ScriptableObject
    {
        /// <summary>
        /// What the ribbon is made of. Only the material and texture mode differ between
        /// these — every other field below still applies as authored.
        /// </summary>
        public enum TrailStyle
        {
            /// <summary>No trail at all. The stone flies bare.</summary>
            None,

            /// <summary>A solid tapering ribbon. The default.</summary>
            Ribbon,

            /// <summary>
            /// Broken into dashes along its length, which reads as speed. The dash texture
            /// is generated in code — see RockTrail.DashTexture — so this needs no asset and
            /// cannot come out magenta in a build.
            /// </summary>
            Dashed,

            /// <summary>
            /// Wide and faint, like disturbed water rather than a drawn line. Uses
            /// <see cref="WakeWidthScale"/> and <see cref="WakeAlphaScale"/> on top of the
            /// authored width and gradient.
            /// </summary>
            Wake,
        }

        [Header("Style")]
        public TrailStyle Style = TrailStyle.Ribbon;

        [Header("Shape")]
        [Tooltip("Seconds of trail behind the stone.\n\n" +
                 "Kept short deliberately. A long throw runs about ten seconds and the " +
                 "trail is rebuilt every frame it is alive, so this is the cost knob as " +
                 "well as the look knob — it is a length cap, not a lifetime.")]
        [Range(0.05f, 4f)] public float Time = 0.55f;

        [Tooltip("Width at the stone, tapering along the curve below.")]
        [Range(0f, 2f)] public float WidthMultiplier = 0.18f;

        [Tooltip("Width along the trail: time 0 is at the stone, time 1 at the tail. " +
                 "Set by Reset() to a linear taper to zero.")]
        public AnimationCurve WidthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);

        [Tooltip("Distance the stone must travel before a new trail vertex is laid down. " +
                 "Higher is cheaper and chunkier. At 40 m/s a value of 0.15 is about 270 " +
                 "vertices per second.")]
        [Range(0.01f, 2f)] public float MinVertexDistance = 0.15f;

        [Header("Colour")]
        [Tooltip("Colour head-to-tail. Alpha does the fading, so the tail should end at " +
                 "zero alpha or the ribbon will stop dead instead of dissolving.")]
        public Gradient Colour = DefaultColour();

        [Tooltip("Tint the whole trail by how much speed the stone has left, so a dying run " +
                 "visibly dims. Multiplies into Colour rather than replacing it.")]
        public bool ColourBySpeed = true;

        [Tooltip("Slowest and fastest tint points, as fractions of SkipConstants.ReferenceSpeed. " +
                 "Fractions rather than absolute speeds on purpose — see the remarks above.")]
        [Range(0f, 1f)] public float MinSpeedFraction = 0.15f;
        [Range(0f, 1f)] public float MaxSpeedFraction = 0.96f;

        [Tooltip("Tint at MinSpeedFraction and below.")]
        public Color SlowTint = new(0.55f, 0.62f, 0.70f, 0.55f);

        [Tooltip("Tint at MaxSpeedFraction and above.")]
        public Color FastTint = Color.white;

        [Header("Wake style")]
        [Tooltip("Width multiplier applied on top of WidthMultiplier when Style is Wake.")]
        [Range(1f, 12f)] public float WakeWidthScale = 4f;

        [Tooltip("Alpha multiplier applied on top of Colour when Style is Wake.")]
        [Range(0f, 1f)] public float WakeAlphaScale = 0.35f;

        [Header("Dashed style")]
        [Tooltip("World units of texture repeat along the trail when Style is Dashed. " +
                 "Smaller means denser dashes.")]
        [Range(0.05f, 5f)] public float DashLength = 0.6f;

        [Tooltip("Fraction of each repeat that is solid. 0.5 is even dash and gap.")]
        [Range(0.05f, 0.95f)] public float DashDutyCycle = 0.5f;

        [Header("Sink")]
        [Tooltip("Stop laying down new trail the moment the run ends, so the ribbon fades " +
                 "in place instead of following the stone under the surface.\n\n" +
                 "Leave this on. Post-URP it is not only a look question: RockController " +
                 "turns water depth writing ON for the length of a sink, so a trail still " +
                 "emitting into that window is CUT by the surface rather than fading. " +
                 "Plan-SfxVfx.md section 1.2 has the detail.")]
        public bool StopEmittingOnSink = true;

        /// <summary>
        /// Head-to-tail default: bright and opaque at the stone, transparent at the tail.
        /// </summary>
        /// <remarks>
        /// A method rather than a field initializer literal because Gradient has no
        /// constructor that takes keys, and an unassigned Gradient is opaque black — which
        /// on a fresh asset would look like the trail rendering solid black rather than
        /// like a default nobody set.
        /// </remarks>
        private static Gradient DefaultColour()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(0.75f, 0.85f, 0.95f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0.85f, 0f),
                    new GradientAlphaKey(0f, 1f),
                });

            return gradient;
        }

        /// <summary>
        /// Re-applies the defaults above. Runs when the asset is first created and on
        /// Reset in the inspector.
        /// </summary>
        private void Reset()
        {
            WidthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            Colour = DefaultColour();
        }

        /// <summary>
        /// Last-resort fallback so a stone still gets a trail if the asset is missing.
        /// The authored asset at Resources/TrailTuning is what should normally load — see
        /// RockTrail.Attach. Reaching this means trail tuning is back to being a recompile,
        /// so it warns rather than silently living with it, the same way SkipTuning does.
        /// </summary>
        public static TrailTuning CreateDefault()
        {
            var tuning = CreateInstance<TrailTuning>();
            tuning.name = "TrailTuning (runtime default)";
            return tuning;
        }
    }
}
