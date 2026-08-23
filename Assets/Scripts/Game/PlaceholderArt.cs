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
    ///
    /// <b>Under URP that fix is not just unnecessary, it is actively harmful — see
    /// <see cref="Create"/>.</b> URP chooses the opaque or the transparent pass by render
    /// queue, so pinning 3100 on an opaque material moves it into the transparent pass,
    /// where depth writes are off. That reproduces the original bug exactly: the queue
    /// pinning was a workaround for an empty depth buffer, and applying it under URP is
    /// what empties the depth buffer. So the queues are honoured under the Built-in
    /// pipeline and deliberately ignored under URP, except where something genuinely
    /// wants to draw over the top.
    ///
    /// Shader selection is pipeline-aware for the same reason. Under URP, Standard is not
    /// an error — it is magenta.
    /// </remarks>
    public static class PlaceholderArt
    {
        /// <summary>Drawn first, so everything else lands on top of it.</summary>
        public const int WaterQueue = 2000;

        /// <summary>The stone and its aiming ghost, above the water.</summary>
        public const int StoneQueue = 3100;

        /// <summary>The aim arc, above both — it is a guide and must never be hidden.</summary>
        public const int ArcQueue = 3200;

        /// <summary>
        /// True when a scriptable render pipeline (URP, here) is driving rendering, rather
        /// than the Built-in pipeline.
        /// </summary>
        /// <remarks>
        /// Not cached. The pipeline asset can be swapped at runtime through quality level
        /// changes, and this is called a handful of times at scene build — never per frame.
        /// </remarks>
        private static bool UsingScriptableRenderPipeline =>
            GraphicsSettings.currentRenderPipeline != null;

        /// <summary>
        /// An opaque material for the placeholder scene.
        /// </summary>
        /// <param name="renderQueue">
        /// One of the queue constants above. Honoured under the Built-in pipeline, where it
        /// is the documented draw-order fix. Ignored under URP unless
        /// <paramref name="alwaysOnTop"/> is set — see the class remarks.
        /// </param>
        /// <param name="alwaysOnTop">
        /// For things that must never be occluded no matter what is in front of them. Only
        /// the aim arc wants this: it is a guide, and an arc hidden by the water it is
        /// arcing over teaches nothing. Everything else should take real depth sorting.
        /// </param>
        public static Material Create(Color colour, int renderQueue, bool alwaysOnTop = false)
        {
            var shader = FindOpaqueShader();

            // Material.color rather than an explicit property name, and that still works
            // across both pipelines: it binds to whichever property carries [MainColor],
            // which is _Color on Standard and _BaseColor on URP/Lit. Naming either one
            // directly here would break on the other.
            var material = new Material(shader) { color = colour };

            // Under URP, leaving the queue alone is the whole point: URP/Lit defaults to
            // the opaque queue with depth writes on, which is what makes depth sorting
            // arbitrate properly and retires the workaround this class was built around.
            if (alwaysOnTop || !UsingScriptableRenderPipeline)
            {
                material.renderQueue = renderQueue;
            }

            return material;
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

            var material = new Material(shader) { color = colour };
            if (!UsingScriptableRenderPipeline) material.renderQueue = WaterQueue;
            return material;
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
            if (UsingScriptableRenderPipeline)
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
