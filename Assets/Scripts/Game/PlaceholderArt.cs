using UnityEngine;

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
    /// Still Sprites/Default rather than an opaque shader, deliberately. Opaque would
    /// restore real depth occlusion, but Shader.Find only resolves shaders included in
    /// the build, and only Sprites/Default is in Always Included Shaders (see
    /// ProjectSettings/GraphicsSettings.asset). Unlit/Color would look right in the
    /// editor and come out magenta in a player build — the worst way to find out.
    ///
    /// Replace the lot when the real lake and its water shader land.
    /// </remarks>
    public static class PlaceholderArt
    {
        /// <summary>Drawn first, so everything else lands on top of it.</summary>
        public const int WaterQueue = 2000;

        /// <summary>The stone and its aiming ghost, above the water.</summary>
        public const int StoneQueue = 3100;

        /// <summary>The aim arc, above both — it is a guide and must never be hidden.</summary>
        public const int ArcQueue = 3200;

        public static Material Create(Color colour, int renderQueue)
        {
            var shader = Shader.Find("Sprites/Default")
                      ?? Shader.Find("Unlit/Color")
                      ?? Shader.Find("Standard");

            return new Material(shader) { color = colour, renderQueue = renderQueue };
        }

        /// <summary>
        /// The stone and its aiming ghost, shaded and carrying a rotation-legible marking.
        /// </summary>
        /// <remarks>
        /// Not Create(): Sprites/Default is unlit with no texture bound, so it discards
        /// the mesh's normals and UVs and renders one flat silhouette. Nothing on it moves
        /// when the stone yaws, which is why no spin rate ever looked like spin. See
        /// Stone.shader. Falls back to flat colour so a broken shader is dull, not absent.
        /// </remarks>
        public static Material CreateStone(Color colour)
        {
            var shader = Resources.Load<Shader>("Stone");
            if (shader == null)
            {
                Debug.LogWarning("[PlaceholderArt] Stone shader not found — flat, unreadable spin.");
                return Create(colour, StoneQueue);
            }

            return new Material(shader) { color = colour, renderQueue = StoneQueue };
        }

        /// <summary>
        /// Water, with the ripple shader if it is available and flat colour if it is not.
        /// </summary>
        /// <remarks>
        /// Resources.Load rather than Shader.Find, for the reason above: a project shader
        /// is not in Always Included Shaders, but everything under a Resources folder ships
        /// with the build. Falling back to flat water keeps a broken or missing shader
        /// playable instead of leaving the lake invisible.
        /// </remarks>
        public static Material CreateWater(Color colour)
        {
            var shader = Resources.Load<Shader>("WaterRipple");
            if (shader == null)
            {
                Debug.LogWarning("[PlaceholderArt] WaterRipple shader not found — flat water.");
                return Create(colour, WaterQueue);
            }

            return new Material(shader) { color = colour, renderQueue = WaterQueue };
        }
    }
}
