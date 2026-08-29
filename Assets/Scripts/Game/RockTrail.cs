using UnityEngine;

namespace Game
{
    /// <summary>
    /// The ribbon behind a stone in flight. Reads TrailTuning, drives a TrailRenderer, and
    /// stops emitting the moment the run ends.
    /// </summary>
    /// <remarks>
    /// Presentation only, and it adds nothing to gameplay. It publishes no events, is read
    /// by nothing, and holds no state the simulation can see — so the rule SkipPhysics
    /// states twice, that observable effects belong to the caller and never to the solver,
    /// is not at risk here. WaterRipples calls that safety "inherited, not re-implemented,"
    /// and this inherits it the same way: the trail exists only where a real stone does, and
    /// RockLauncher.CreateRockObject is the single place a real stone is made. The aim
    /// preview runs the same solver hundreds of times a frame for a throw that never
    /// happened and never touches this file.
    ///
    /// It also does not subscribe to anything. Stopping on sink hangs off
    /// RockController.IsFlying, which is already false by the time the sink coroutine
    /// starts, rather than off a RockSunkEvent — that event is M2 work and does not exist
    /// yet, and inventing it here would have coupled the trail to a milestone it does not
    /// need. Reading a sibling's public flag is the smaller dependency, and when
    /// RockSunkEvent does arrive this can move onto it without changing what it does.
    ///
    /// Lives on a CHILD of the rock, not on the rock itself. Unity permits one Renderer per
    /// GameObject and the rock already has a MeshRenderer, so AddComponent&lt;TrailRenderer&gt;
    /// on the stone is refused outright.
    /// </remarks>
    [RequireComponent(typeof(TrailRenderer))]
    public class RockTrail : MonoBehaviour
    {
        private TrailTuning _tuning;
        private RockController _rock;
        private TrailRenderer _trail;

        private Material _material;
        private Texture2D _dashTexture;

        private float _minSpeed;
        private float _maxSpeed;

        /// <summary>
        /// Local rotation that lays the ribbon flat in the stone's own face plane.
        /// </summary>
        /// <remarks>
        /// LineAlignment.TransformZ builds every trail quad facing the transform's local +Z,
        /// so making the ribbon flat WITH the stone is a question of pointing this child's
        /// +Z along the stone's face normal — and the face normal is local +Y, not +Z.
        /// RockController spins the stone with Quaternion.Euler(0, angle, 0) applied on the
        /// right, which is a turn about its own local Y, and calls that "about its own face
        /// normal." So the flat face is the local XZ plane and its normal is local up.
        ///
        /// Euler(-90, 0, 0) is the rotation that carries +Z onto +Y, and no other component
        /// is needed: the remaining freedom is a turn about the ribbon's own normal, which
        /// does not change the plane it lies in.
        ///
        /// It also cannot be shaken by the spin, which is the part worth having. The stone
        /// turns about local Y at up to MaxVisualSpinDegPerSec; this child's +Z IS local Y,
        /// and a rotation about an axis leaves that axis fixed — so the ribbon's plane is
        /// invariant under the spin and the trail cannot strobe or flicker as the stone
        /// whirls. Aligning to any other local axis would have it flipping edge-on twice per
        /// revolution.
        /// </remarks>
        private static readonly Quaternion FaceAlignedRotation = Quaternion.Euler(-90f, 0f, 0f);

        /// <summary>
        /// Hangs a configured trail off a rock, or returns null if the style is None or the
        /// material could not be built.
        /// </summary>
        /// <remarks>
        /// A factory rather than inspector wiring, because the rock is instantiated at
        /// runtime from Resources and nothing here ever passes through the inspector — the
        /// trap TrailTuning's remarks describe. The skip constants are passed in rather than
        /// looked up so the speed thresholds come from the same asset the throw was
        /// simulated with; two lookups could disagree after a retune.
        /// </remarks>
        public static RockTrail Attach(GameObject rock, SkipConstants skipConstants)
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
            self.Init(tuning, skipConstants, material, rock.GetComponent<RockController>());

            return self;
        }

        private void Init(TrailTuning tuning, SkipConstants skipConstants, Material material, RockController rock)
        {
            _tuning = tuning;
            _rock = rock;
            _material = material;
            _trail = GetComponent<TrailRenderer>();

            if (skipConstants != null)
            {
                _minSpeed = skipConstants.ReferenceSpeed * tuning.MinSpeedFraction;
                _maxSpeed = skipConstants.ReferenceSpeed * tuning.MaxSpeedFraction;
            }

            var wake = tuning.Style == TrailTuning.TrailStyle.Wake;

            _trail.time = tuning.Time;
            _trail.minVertexDistance = tuning.MinVertexDistance;
            _trail.widthMultiplier = tuning.WidthMultiplier * (wake ? tuning.WakeWidthScale : 1f);
            _trail.widthCurve = tuning.WidthCurve;
            _trail.colorGradient = wake ? Faded(tuning.Colour, tuning.WakeAlphaScale) : tuning.Colour;

            // Nothing here casts or receives — a translucent ribbon that shadows the lake
            // reads as a solid object, and the water is the only thing under it to catch it.
            _trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _trail.receiveShadows = false;

            // Flat in the stone's own face plane, not billboarded at the camera. The stone
            // tracks the phone one-to-one and its face angle is the variable the whole game
            // is about, so a ribbon lying in that plane shows the angle of every bounce —
            // whereas LineAlignment.View turns to meet the camera and reads the same width
            // whatever the stone is doing, which is the one thing it should not do here.
            //
            // FaceAlignedRotation on this child is the other half of this line and they only
            // work as a pair: TransformZ faces local +Z, and the rotation is what puts local
            // +Z on the stone's face normal. Changing either alone silently tilts the ribbon
            // off the face.
            //
            // Cost, and it is real: a plane seen edge-on is a line. The camera sits near the
            // waterline (RockLauncher.FollowActiveRock locks its Y to the home height) and a
            // well-thrown stone lies nearly flat, so the face plane is close to horizontal
            // and the view of it is close to grazing — the trail will read thin on exactly
            // the good throws. Widen it if so: WidthMultiplier, or the Wake style, which is
            // WakeWidthScale times wider for this reason.
            _trail.alignment = LineAlignment.TransformZ;

            if (tuning.Style == TrailTuning.TrailStyle.Dashed) ApplyDash(tuning);

            // sharedMaterial, not material: assigning to .material makes Unity clone it and
            // hand back a copy, so the instance mutated in LateUpdate would not be the one
            // being drawn and the speed tint would silently do nothing. This material was
            // built fresh for this trail and is destroyed with it, so there is nothing to
            // share it with anyway.
            _trail.sharedMaterial = _material;
        }

        /// <summary>
        /// Builds the dash pattern as a generated texture and tiles it along the trail.
        /// </summary>
        /// <remarks>
        /// Generated rather than authored, so the dashed style needs no texture asset. That
        /// is not laziness about art: PlaceholderArt.CreateWater's remarks record that a
        /// shader or texture reached by name rather than from Resources "would look right in
        /// the editor and come out magenta in a player build." A texture built in code
        /// cannot go missing.
        ///
        /// Point filtering on purpose. Bilinear on a 64-pixel strip smears each dash edge
        /// into a gradient, which at speed reads as a blurry ribbon rather than as dashes —
        /// and hard edges are what is wanted anyway, since soft-edged particles are off the
        /// table while the water's depth writing flips mid-throw (Plan-SfxVfx.md 1.2).
        /// </remarks>
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

            // Stop laying down new ribbon the moment the run ends. Two reasons, and the
            // second is the load-bearing one: the stone is about to be dragged 1.5 m
            // underwater by RockController.SinkRoutine, and that routine turns water depth
            // writing ON for its duration — so a trail still emitting into the sink is cut
            // by the surface rather than fading out behind it.
            //
            // The ribbon already laid down is untouched and fades over TrailTuning.Time.
            // RockController destroys the rock after despawnDelay (2.5s), comfortably
            // longer than any sane trail time, so the fade always completes.
            if (_tuning.StopEmittingOnSink && _rock != null && !_rock.IsFlying)
            {
                _trail.emitting = false;
                return;
            }

            if (_tuning.ColourBySpeed) ApplySpeedTint();
        }

        /// <summary>
        /// Dims the whole ribbon as the stone runs out of speed.
        /// </summary>
        /// <remarks>
        /// Whole-trail rather than per-vertex, and the difference is visible if you look for
        /// it: the tint is the stone's speed NOW, applied uniformly, so an old segment laid
        /// down while the stone was fast dims along with everything else instead of keeping
        /// the colour it was born with. Per-vertex colour would need the gradient rebuilt
        /// every frame from a speed history, which is a real cost for a ribbon that lives
        /// half a second. Revisit only if a dying run does not read.
        ///
        /// Multiplies into the gradient rather than replacing it — the material tint and the
        /// vertex colour are separate inputs to the shader, so the authored head-to-tail
        /// fade survives.
        /// </remarks>
        private void ApplySpeedTint()
        {
            if (_material == null || _rock == null) return;

            var t = Mathf.InverseLerp(_minSpeed, _maxSpeed, _rock.Speed);
            _material.color = Color.Lerp(_tuning.SlowTint, _tuning.FastTint, t);
        }

        /// <summary>A copy of a gradient with every alpha key scaled. Used by the Wake style.</summary>
        /// <remarks>
        /// A copy, because the source gradient belongs to the shared TrailTuning ASSET.
        /// Scaling it in place would edit the asset itself — which in the editor persists to
        /// disk, so every subsequent throw would come out fainter than the last until the
        /// wake vanished entirely and the asset on disk was quietly wrong.
        /// </remarks>
        private static Gradient Faded(Gradient source, float alphaScale)
        {
            var alphas = source.alphaKeys;
            for (var i = 0; i < alphas.Length; i++) alphas[i].alpha *= alphaScale;

            var faded = new Gradient();
            faded.SetKeys(source.colorKeys, alphas);

            return faded;
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
