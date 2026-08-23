using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// Materials for the runtime placeholder scene. Central because the draw order
    /// between water, stone and arc is one decision, not three.
    /// </summary>
    /// <remarks>
    /// It used to be three, and that was the bug: each caller ran its own Shader.Find
    /// chain and landed on Sprites/Default, which is Queue=Transparent with ZWrite Off.
    /// With every object depth-write disabled the depth buffer stays empty, so depth
    /// testing arbitrates nothing and Unity falls back to sorting transparent renderers
    /// by distance from the camera to each renderer's bounds centre.
    ///
    /// The water is a 200-unit plane sorted by a single point at its middle (z = 60).
    /// The camera trails the stone by ~8.8 units, so the moment the stone passed that
    /// midpoint the water became the nearer object, flipped to drawing last, and painted
    /// over everything — and at alpha 1 it obliterates rather than tints. The stone and
    /// the aim arc did not fade out, they vanished.
    ///
    /// Fixed by pinning an explicit queue per role. Unity honours renderQueue before any
    /// distance sorting, so the order is now deterministic no matter where the camera is.
    /// That pinning is the only thing in this file worth keeping — it is a bug fix, not
    /// decoration.
    ///
    /// The queues are pinned under URP too, and that is deliberate rather than an
    /// oversight. URP picks the opaque or transparent pass by render queue, so a stone at
    /// 3100 lands in the transparent pass and draws over the water without depth testing
    /// against it — which is exactly what is wanted here. The stone's lower half is below
    /// the water plane at every bounce (SkipPhysics clamps its centre to the surface), and
    /// letting the water occlude that half honestly reads as a hard flat cut across the
    /// stone. Drawing it over the top hides a geometry problem that is not worth solving
    /// properly. Sinking is handled as a deliberate effect at the end of a run instead.
    ///
    /// Shader selection is pipeline-aware, and that part is not optional. Under URP,
    /// Standard is not an error — it is magenta.
    /// </remarks>
    public static class PlaceholderArt
    {
        /// <summary>Drawn first, so everything else lands on top of it.</summary>
        public const int WaterQueue = 2000;

        /// <summary>The stone and its aiming ghost, above the water.</summary>
        public const int StoneQueue = 3100;

        /// <summary>The aim arc, above both — it is a guide and must never be hidden.</summary>
        public const int ArcQueue = 3200;

        private static readonly int ZTestId = Shader.PropertyToID("_ZTest");

        /// <summary>
        /// Whether this material is allowed to be hidden by things already in the depth
        /// buffer. Off means it draws over the scene regardless of what is in front of it.
        /// </summary>
        /// <remarks>
        /// This, not the render queue, is what decides whether the water can cut the stone
        /// in half — and the distinction cost a round of debugging, so it is worth stating
        /// plainly. The queue constants above control draw ORDER only. A stone at
        /// StoneQueue really does draw after the water, but URP/Lit still runs ZTest
        /// LEqual, so its submerged fragments fail against the depth the water already
        /// wrote and are discarded anyway. Reordering cannot fix an occlusion problem.
        ///
        /// Under the Built-in pipeline this never came up because the water ran ZWrite Off
        /// — there was nothing in the depth buffer to lose to. URP's water is opaque and
        /// writes depth, so the test finally has something to bite on.
        ///
        /// SkipPhysics clamps the stone's centre to the water plane, so its lower half is
        /// genuinely below the surface at every bounce. Rather than move the geometry, the
        /// stone simply ignores depth while it is in play, and is handed back to the depth
        /// buffer at the end of a run so it can be seen to sink.
        /// </remarks>
        public static void SetDepthTested(Material material, bool depthTested)
        {
            if (material == null) return;

            material.SetInt(ZTestId, (int)(depthTested
                ? CompareFunction.LessEqual
                : CompareFunction.Always));
        }

        /// <summary>
        /// An opaque material for the placeholder scene, pinned to an explicit draw order.
        /// </summary>
        /// <param name="renderQueue">One of the queue constants above.</param>
        public static Material Create(Color colour, int renderQueue)
        {
            var shader = FindOpaqueShader();

            // Material.color rather than an explicit property name, and that still works
            // across both pipelines: it binds to whichever property carries [MainColor],
            // which is _Color on Standard and _BaseColor on URP/Lit. Naming either one
            // directly here would break on the other.
            return new Material(shader) { color = colour, renderQueue = renderQueue };
        }

        /// <summary>
        /// Water, with the ripple shader if it is available and flat colour if it is not.
        /// </summary>
        /// <remarks>
        /// Resources.Load rather than Shader.Find: a project shader is not in Always
        /// Included Shaders, but everything under a Resources folder ships with the build.
        /// Shader.Find on one works in the editor and comes out magenta in a player build —
        /// the worst way to find out. Falling back to flat water keeps a broken or missing
        /// shader playable instead of leaving the lake invisible.
        ///
        /// The URP graph is tried first and the Built-in surface shader is kept as the
        /// fallback, so this stays playable on both pipelines for the length of the
        /// migration. They are named differently on purpose: two assets called WaterRipple
        /// in the same Resources folder would make Resources.Load ambiguous. Once URP is
        /// confirmed, the old shader goes and the graph takes the plain name.
        /// </remarks>
        public static Material CreateWater(Color colour)
        {
            var shader = Resources.Load<Shader>("WaterRippleURP")
                      ?? Resources.Load<Shader>("WaterRipple");

            if (shader == null)
            {
                Debug.LogWarning("[PlaceholderArt] No ripple shader found — flat water.");
                return Create(colour, WaterQueue);
            }

            return new Material(shader) { color = colour, renderQueue = WaterQueue };
        }

        /// <summary>
        /// The best available opaque lit shader for the active pipeline.
        /// </summary>
        /// <remarks>
        /// URP first, because under URP "Standard" resolves to a shader that cannot render
        /// and the lake comes out magenta. Standard second, so the Built-in pipeline is
        /// unaffected by any of this. The unlit tail is a last resort only — the water
        /// shader's own remarks record that going unlit visibly darkened the scene, because
        /// ambient mode is Skybox and a lit shader picks up skybox SH on the diffuse plus a
        /// grazing-angle specular that no unlit fallback reproduces.
        ///
        /// Sprites/Default stays at the end because it is the one entry guaranteed to be in
        /// Always Included Shaders, so the chain can never come back null in a build.
        /// </remarks>
        private static Shader FindOpaqueShader()
        {
            // Not cached: the pipeline asset can be swapped at runtime through a quality
            // level change, and this runs a handful of times at scene build, never per frame.
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                var urp = Shader.Find("Universal Render Pipeline/Lit")
                       ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                       ?? Shader.Find("Universal Render Pipeline/Unlit");

                if (urp != null) return urp;

                Debug.LogWarning("[PlaceholderArt] A scriptable render pipeline is active but no " +
                                 "URP shader resolved. Falling back to Built-in shaders, which " +
                                 "will render magenta.");
            }

            return Shader.Find("Standard")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default");
        }
    }
}
