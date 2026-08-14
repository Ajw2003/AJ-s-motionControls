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
    /// That pinning is the only thing in this file worth keeping — it is a bug fix, not
    /// decoration.
    ///
    /// Still Sprites/Default rather than an opaque shader, deliberately. Opaque would
    /// restore real depth occlusion, but Shader.Find only resolves shaders included in
    /// the build, and only Sprites/Default is in Always Included Shaders (see
    /// ProjectSettings/GraphicsSettings.asset). Unlit/Color would look right in the
    /// editor and come out magenta in a player build — the worst way to find out.
    ///
    /// There were custom Stone and WaterRipple shaders here. They existed to make a
    /// scaled sphere's rotation legible and to draw impact rings; the stone is a flat
    /// box now, which shows its own rotation from every angle, so both are gone.
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
            var shader = Shader.Find("Standard")
                      ?? Shader.Find("Unlit/Color")
                      ?? Shader.Find("Sprites/Default");

            return new Material(shader) { color = colour, renderQueue = renderQueue };
        }
    }
}
