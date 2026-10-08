using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    // Each role gets an explicit render queue so draw order never falls back to distance sorting; queues do not fix
    // occlusion, and Standard renders magenta under URP. doc-ref 3e8b docs/4-systems/Presentation.md
    public static class PlaceholderArt
    {
        // The shore's queue must sit below the water's, because the depth-less water cannot occlude anything drawn after
        // it; a new role gets a new queue. doc-ref 355b docs/4-systems/Presentation.md
        public const int ShoreQueue = 1900;

        /// <summary>Drawn after the shore, so the far bank sits behind the lake.</summary>
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

        // Emission needs both the colour and the _EMISSION keyword, and the HasProperty check catches
        // shader fallbacks that have no emission at all. doc-ref ca3a docs/4-systems/Presentation.md
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

        // Only the water can toggle depth writing: render queue and the stone's _ZTest were both dead
        // ends, since URP Lit exposes no _ZTest. doc-ref 8c9c docs/4-systems/Presentation.md
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

        // The water loads its shader via Resources, not Shader.Find, which turns magenta in a player
        // build; flat colour is the fallback. doc-ref cc0b docs/4-systems/Presentation.md
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

        // The trail material uses Sprites/Default, the one shader with alpha, vertex colour and a texture needing no
        // setup; one ignoring vertex colour draws flat white. doc-ref 1058 docs/4-systems/Presentation.md
        public static Material CreateTrail() => CreateVertexColoured(TrailQueue, "trail");

        // The arc needs vertex colour too: URP/Lit has no COLOR semantic, so the arc stayed white until 2026-08-25; unlit
        // also keeps the verdict colour independent of lighting. doc-ref f8c0 docs/4-systems/Presentation.md
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

        // The opaque shader chain tries URP first, then Standard, with Sprites/Default last as the one
        // guaranteed to be in Always Included Shaders. doc-ref 21db docs/4-systems/Presentation.md
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
