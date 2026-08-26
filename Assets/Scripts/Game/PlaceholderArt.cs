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
    /// The queues are pinned under URP too, and they still only buy draw ORDER. They do
    /// NOT stop the water hiding the stone — depth testing does that, and it is settled
    /// separately by SetWaterDepthWrite below. Assuming a later queue was enough to draw
    /// over the water cost a debugging round, so it is spelled out here as well.
    ///
    /// Shader selection is pipeline-aware, and that part is not optional. Under URP,
    /// Standard is not an error — it is magenta.
    /// </remarks>
    public static class PlaceholderArt
    {
        /// <summary>Drawn first, so everything else lands on top of it.</summary>
        public const int WaterQueue = 2000;

        /// <summary>
        /// The stone's trail: above the water, below the stone that is drawing it.
        /// </summary>
        /// <remarks>
        /// Below StoneQueue on purpose. The trail is emitted from the stone's own centre,
        /// so its newest segment and the stone occupy the same space — drawn after, a
        /// translucent ribbon washes over the near face of the thing it is meant to be
        /// trailing and the stone reads as fogged.
        /// </remarks>
        public const int TrailQueue = 3050;

        /// <summary>The stone and its aiming ghost, above the water.</summary>
        public const int StoneQueue = 3100;

        /// <summary>The aim arc, above both — it is a guide and must never be hidden.</summary>
        public const int ArcQueue = 3200;

        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
        private static readonly int EmissionColourId = Shader.PropertyToID("_EmissionColor");

        /// <summary>
        /// Turns emission on for a material and reports whether it took, so a caller can
        /// stop driving a property that does not exist.
        /// </summary>
        /// <remarks>
        /// Two steps because emission needs both, and missing either one is silent. The
        /// property carries the colour; the _EMISSION keyword is what compiles the shader
        /// variant that reads it. Set the colour without the keyword and the value lands
        /// correctly on a material that renders as though it were black — which looks like
        /// the colour being wrong rather than the variant being absent.
        ///
        /// The HasProperty check is not defensive padding. FindOpaqueShader's tail can
        /// resolve Unlit/Color or Sprites/Default, and neither has _EmissionColor at all, so
        /// on a machine that fell down that chain every emission write would be a no-op with
        /// no error — the same failure SetWaterDepthWrite records losing a debugging round
        /// to on the stone's _ZTest. Returning a bool lets the caller find that out once at
        /// build time instead of writing into nothing every frame.
        ///
        /// GI flags are cleared because this is meant to be animated per frame. Left at the
        /// default the value is treated as a baked lighting contribution, which it cannot be
        /// when it changes sixty times a second.
        /// </remarks>
        public static bool PrepareEmission(Material material)
        {
            if (material == null || !material.HasProperty(EmissionColourId)) return false;

            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            material.SetColor(EmissionColourId, Color.black);
            return true;
        }

        /// <summary>
        /// The emissive colour on a material already through <see cref="PrepareEmission"/>.
        /// Safe to call every frame — it is one property write and no keyword churn.
        /// </summary>
        public static void SetEmission(Material material, Color colour)
        {
            if (material == null) return;
            material.SetColor(EmissionColourId, colour);
        }

        /// <summary>
        /// Whether the water writes depth, and so whether it is able to hide anything
        /// drawn after it.
        /// </summary>
        /// <remarks>
        /// This is the only lever available for the stone-cut-in-half problem, and finding
        /// that out took two wrong turns worth recording.
        ///
        /// The render queue was the first. Queues control draw ORDER only: a stone at
        /// StoneQueue really does draw after the water, but it still runs ZTest LEqual, so
        /// its submerged fragments fail against the depth the water already wrote and are
        /// discarded regardless of when they are drawn. Reordering cannot fix occlusion.
        ///
        /// Setting _ZTest on the STONE was the second, and it silently did nothing.
        /// URP's hand-written Lit.shader has no ZTest line in its ForwardLit pass and
        /// declares no _ZTest property, so it is hardwired to LEqual and SetInt lands on a
        /// property that does not exist. (Shader Graph materials DO expose _ZTest, which is
        /// what made this look plausible — the water material has one. The stone is not a
        /// Shader Graph material.)
        ///
        /// So the toggle has to live on the water, which IS a Shader Graph material with
        /// material override enabled, and therefore really does emit ZWrite[_ZWrite]. With
        /// depth writing off there is nothing in the buffer to occlude the stone and it
        /// draws whole; turning it back on lets the water swallow the stone as it sinks.
        ///
        /// Safe now in a way it was not before URP, because the queues above are explicit
        /// and distinct. The original draw-order bug came from every object sharing one
        /// Transparent queue and falling back to sorting by distance to bounds centre;
        /// with water, stone and arc pinned to 2000/3100/3200 that cannot recur.
        /// </remarks>
        public static void SetWaterDepthWrite(Material waterMaterial, bool writesDepth)
        {
            if (waterMaterial == null) return;
            waterMaterial.SetInt(ZWriteId, writesDepth ? 1 : 0);
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
        /// A transparent, vertex-coloured material for a <see cref="TrailRenderer"/>.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Create"/> because a trail needs two things an opaque lit
        /// material does not have: alpha blending, and respect for vertex colour. A
        /// TrailRenderer's gradient reaches the shader as vertex colour and nothing else —
        /// on a shader that ignores it the ribbon comes out flat white and the authored fade
        /// silently does nothing, which looks like the gradient failing to apply rather than
        /// the material being wrong.
        ///
        /// The tint is left white here. Colour is the gradient's job; multiplying a second
        /// colour in underneath would mean two places to edit one look.
        ///
        /// Sprites/Default first here, and that inverts FindOpaqueShader's URP-first rule
        /// on purpose rather than by oversight. Three things have to be true at once for a
        /// trail — alpha blending, vertex colour, and a sampled main texture for the dashed
        /// style — and Sprites/Default is the only name that has all three with no setup and
        /// is guaranteed to be in Always Included Shaders, so it cannot come back null in a
        /// player build. It renders correctly under URP; it is simply unlit.
        ///
        /// URP's Particles/Unlit would be the richer choice but defaults to an OPAQUE
        /// surface, so using it means setting _Surface, _Blend, _SrcBlend, _DstBlend, ZWrite
        /// and the _SURFACE_TYPE_TRANSPARENT keyword by hand from code. Every one of those is
        /// a silent no-op if the property name is wrong — which is exactly the failure mode
        /// SetWaterDepthWrite records losing a debugging round to on the stone's _ZTest.
        /// Worth revisiting deliberately if the trail ever needs to receive light; not worth
        /// carrying untested for a ribbon that is meant to read as spray.
        /// </remarks>
        public static Material CreateTrail() => CreateVertexColoured(TrailQueue, "trail");

        /// <summary>
        /// A transparent, vertex-coloured material for the aim arc's <see cref="LineRenderer"/>.
        /// </summary>
        /// <remarks>
        /// The arc needs exactly what the trail needs, and for exactly the reason CreateTrail
        /// states: LineRenderer.startColor and endColor reach a shader as VERTEX COLOUR and by
        /// no other route, so a shader that does not read vertex colour discards them in
        /// silence and draws the material's flat tint instead.
        ///
        /// This is not hypothetical. The arc was built with <see cref="Create"/> — which is
        /// URP/Lit, whose vertex Attributes struct declares positionOS, normalOS, tangentOS
        /// and three UV sets and NO COLOR semantic at all — so it could not read them even in
        /// principle. The arc rendered plain white from the URP migration until 2026-08-25,
        /// including the blue it was originally authored with, and the quality tint added in
        /// Plan-SfxVfx.md section 6.1 computed correctly every frame and went nowhere. It
        /// presented as "the colour feature does not work" rather than as "the material is
        /// wrong", which is why it survived a round of widening the tuning ranges.
        ///
        /// Unlit is an improvement here rather than the compromise it is for the trail. The
        /// arc is a red/amber/green readout of predicted quality, and a lit material would
        /// multiply scene lighting into the verdict — the same angle would report a different
        /// colour at a different time of day. Sprites/Default also carries ZWrite Off, which
        /// agrees with what ArcQueue was already pinned for: the guide must never be hidden,
        /// including by the water's depth-write window (section 1.2).
        /// </remarks>
        public static Material CreateArc() => CreateVertexColoured(ArcQueue, "aim arc");

        /// <summary>
        /// The shared body of <see cref="CreateTrail"/> and <see cref="CreateArc"/>: one
        /// alpha-blended, vertex-colour-respecting material, pinned to a given queue.
        /// </summary>
        /// <param name="role">Named only so a failure says which renderer went dark.</param>
        private static Material CreateVertexColoured(int renderQueue, string role)
        {
            var shader = Shader.Find("Sprites/Default")
                      ?? Shader.Find("Universal Render Pipeline/Particles/Unlit");

            if (shader == null)
            {
                Debug.LogWarning($"[PlaceholderArt] No vertex-coloured shader resolved — " +
                                 $"the {role} will not draw.");
                return null;
            }

            // Tint left white. Colour is the gradient's job, and a second colour multiplied
            // in underneath would mean two places to edit one look.
            return new Material(shader)
            {
                color = Color.white,
                renderQueue = renderQueue,
            };
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
