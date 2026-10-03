using UnityEngine;

namespace Game
{
    // The trail is presentation-only: it publishes and subscribes to nothing, stops off IsFlying, and
    // lives on a child because a rock holds one Renderer. doc-ref 9800 docs/4-systems/Presentation.md
    [RequireComponent(typeof(TrailRenderer))]
    public class RockTrail : MonoBehaviour
    {
        private TrailTuning _tuning;
        private RockController _rock;
        private TrailRenderer _trail;

        private Material _material;
        private Color _stoneColour = Color.white;
        private Texture2D _dashTexture;

        private float _minSpeed;
        private float _maxSpeed;

        // Euler(-90,0,0) lays the ribbon flat on the stone's face plane, and since its +Z is the
        // stone's local Y the spin cannot make it flicker. doc-ref 05ec docs/4-systems/Presentation.md
        private static readonly Quaternion FaceAlignedRotation = Quaternion.Euler(-90f, 0f, 0f);

        // A factory rather than inspector wiring because rocks are built at runtime; skip constants are
        // passed in so thresholds match the throw. doc-ref 186b docs/4-systems/Presentation.md
        public static RockTrail Attach(GameObject rock, SkipConstants skipConstants, Color stoneColour)
        {
            if (rock == null) return null;

            var tuning = Resources.Load<TrailTuning>("TrailTuning");
            if (tuning == null)
            {
                Debug.LogWarning("[RockTrail] No Resources/TrailTuning asset — using code " +
                                 "defaults. Trail tuning is a recompile until the asset exists " +
                                 "(Assets > Create > RockSkipping > Trail Tuning).");
                tuning = TrailTuning.CreateDefault();
            }

            if (tuning.Style == TrailTuning.TrailStyle.None) return null;

            var material = PlaceholderArt.CreateTrail();
            if (material == null) return null;

            // Zero local offset: the trail is emitted from the stone's centre, which is the
            // point SkipPhysics clamps to the water at every bounce, so the ribbon touches
            // the surface exactly where the ripple does.
            var go = new GameObject("RockTrail");
            go.transform.SetParent(rock.transform, false);
            go.transform.localRotation = FaceAlignedRotation;

            var self = go.AddComponent<RockTrail>();
            self.Init(tuning, skipConstants, material, rock.GetComponent<RockController>(), stoneColour, StoneWidth(rock));

            return self;
        }

        // Width is measured from mesh.bounds as the mean of the two face axes, falling back to 1 m when
        // there is no mesh. doc-ref ae7e docs/4-systems/Presentation.md
        private static float StoneWidth(GameObject rock)
        {
            var filter = rock.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return 1f;

            var size = filter.sharedMesh.bounds.size;
            var scale = rock.transform.lossyScale;

            return (Mathf.Abs(size.x * scale.x) + Mathf.Abs(size.z * scale.z)) * 0.5f;
        }

        private void Init(TrailTuning tuning, SkipConstants skipConstants, Material material, RockController rock, Color stoneColour, float stoneWidth)
        {
            _tuning = tuning;
            _rock = rock;
            _material = material;
            _stoneColour = stoneColour;
            _trail = GetComponent<TrailRenderer>();

            if (skipConstants != null)
            {
                _minSpeed = skipConstants.ReferenceSpeed * tuning.MinSpeedFraction;
                _maxSpeed = skipConstants.ReferenceSpeed * tuning.MaxSpeedFraction;
            }

            var wake = tuning.Style == TrailTuning.TrailStyle.Wake;

            _trail.time = tuning.Time;
            _trail.minVertexDistance = tuning.MinVertexDistance;
            // Divide the width by the transform's world scale: TransformZ multiplies width by it, which
            // made trails about 2.5x too wide (issue #24). doc-ref 06b0 docs/4-systems/Presentation.md
            var trailScale = Mathf.Max(Mathf.Abs(transform.lossyScale.x), 0.0001f);
            _trail.widthMultiplier = tuning.WidthMultiplier * stoneWidth / trailScale *
                                     (wake ? tuning.WakeWidthScale : 1f);
            _trail.widthCurve = tuning.WidthCurve;
            _trail.colorGradient = AlphaOnly(tuning.Colour, wake ? tuning.WakeAlphaScale : 1f);

            // Nothing here casts or receives — a translucent ribbon that shadows the lake
            // reads as a solid object, and the water is the only thing under it to catch it.
            _trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _trail.receiveShadows = false;

            // The ribbon is aligned to the face plane by TransformZ plus FaceAlignedRotation as a pair; edge-on views
            // read thin, so widen it if needed. doc-ref e996 docs/4-systems/Presentation.md
            _trail.alignment = LineAlignment.TransformZ;

            if (tuning.Style == TrailTuning.TrailStyle.Dashed) ApplyDash(tuning);

            // sharedMaterial, not material: assigning to .material makes Unity clone it and
            // hand back a copy, so the instance mutated in LateUpdate would not be the one
            // being drawn and the speed tint would silently do nothing. This material was
            // built fresh for this trail and is destroyed with it, so there is nothing to
            // share it with anyway.
            _trail.sharedMaterial = _material;

            // Set once here rather than left to ApplySpeedTint, which only runs when
            // ColourBySpeed is on — a trail with that style off must still be the stone's
            // colour, just without the speed-based dimming.
            _material.color = _stoneColour;
        }

        // The dash texture is generated in code so it cannot go missing, with point filtering to keep
        // hard edges. doc-ref 2481 docs/4-systems/Presentation.md
        private void ApplyDash(TrailTuning tuning)
        {
            const int width = 64;

            _dashTexture = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                name = "RockTrail dash",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Point,
            };

            var solid = Mathf.RoundToInt(width * Mathf.Clamp01(tuning.DashDutyCycle));

            for (var x = 0; x < width; x++)
            {
                // White with alpha, so the gradient and the speed tint both still multiply
                // through cleanly — the texture decides presence, never colour.
                _dashTexture.SetPixel(x, 0, new Color(1f, 1f, 1f, x < solid ? 1f : 0f));
            }

            _dashTexture.Apply();

            _material.mainTexture = _dashTexture;

            // Tile rather than Stretch: Stretch fits one copy of the texture to the whole
            // ribbon, so the dashes would stretch and shrink with the trail's length instead
            // of staying a fixed size in the world and streaming past.
            _trail.textureMode = LineTextureMode.Tile;
            _trail.textureScale = new Vector2(1f / Mathf.Max(tuning.DashLength, 0.01f), 1f);
        }

        private void LateUpdate()
        {
            if (_trail == null || _tuning == null) return;

            // Stop emitting when the run ends, because the sink turns water depth writing on and would
            // cut a live trail. doc-ref 6246 docs/4-systems/Presentation.md
            if (_tuning.StopEmittingOnSink && _rock != null && !_rock.IsFlying)
            {
                _trail.emitting = false;
                return;
            }

            if (_tuning.ColourBySpeed) ApplySpeedTint();
        }

        // The whole ribbon is dimmed by the stone's current speed, multiplied into the gradient rather
        // than replacing it. doc-ref 16ef docs/4-systems/Presentation.md
        private void ApplySpeedTint()
        {
            if (_material == null || _rock == null) return;

            var t = Mathf.InverseLerp(_minSpeed, _maxSpeed, _rock.Speed);

            // Multiplied, not replaced, so the speed fade survives: SlowTint is dim and
            // FastTint is white, so the product is the stone's own colour at full
            // brightness on a fast run and dimmed on a dying one — ColourBySpeed still reads.
            _material.color = Color.Lerp(_tuning.SlowTint, _tuning.FastTint, t) * _stoneColour;
        }

        // Copy the gradient with white colour keys so only alpha fades; editing the shared tuning asset
        // in place would persist to disk. doc-ref e884 docs/4-systems/Presentation.md
        private static Gradient AlphaOnly(Gradient source, float alphaScale)
        {
            var alphas = source.alphaKeys;
            for (var i = 0; i < alphas.Length; i++) alphas[i].alpha *= alphaScale;

            var result = new Gradient();
            result.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                alphas);

            return result;
        }

        /// <summary>
        /// Both of these were built with `new` for this trail alone, so nothing else can be
        /// holding them. Unity does not collect either one on its own, and a throw creates a
        /// rock every time — left alone this leaks a material and a texture per throw for the
        /// length of a session.
        /// </summary>
        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_dashTexture != null) Destroy(_dashTexture);
        }
    }
}
