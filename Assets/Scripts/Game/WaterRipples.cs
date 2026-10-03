using System.Collections.Generic;
using EventSystems;
using EventTypes.SkipEvents;
using UnityEngine;

namespace Game
{
    // Ripples hang off SkipOccurredEvent, which only RockController publishes, so the aim preview can
    // never produce them. doc-ref 029b docs/4-systems/Presentation.md
    public class WaterRipples : MonoBehaviour
    {
        /// <summary>Must match MAX_RIPPLES in WaterRipple.hlsl (and WaterRipple.shader, while it lasts).</summary>
        public const int MaxRipples = 16;

        [Tooltip("Seconds a ring takes to fade out entirely.")]
        [SerializeField] private float rippleLifeSeconds = 2.5f;
        [Tooltip("How fast the leading edge expands, in world units per second.")]
        [SerializeField] private float rippleSpeed = 6f;

        [Header("Impact response")]
        [Tooltip("Faintest and strongest ring, as fractions of SkipConstants.ReferenceSpeed. " +
                 "Derived rather than absolute because these were hardcoded at 4 and 25 " +
                 "against a fixed MaxSpeed of 26 — raise the launch speed and every impact " +
                 "clamps to full strength, so the ripple stops responding to anything at all.")]
        [Range(0f, 1f)] [SerializeField] private float minImpactSpeedFraction = 0.15f;
        [Range(0f, 1f)] [SerializeField] private float maxImpactSpeedFraction = 0.96f;

        private float _minImpactSpeed = 4f;
        private float _maxImpactSpeed = 25f;

        private static readonly int RipplesId = Shader.PropertyToID("_Ripples");
        private static readonly int RippleCountId = Shader.PropertyToID("_RippleCount");
        private static readonly int RippleLifeId = Shader.PropertyToID("_RippleLife");
        private static readonly int RippleSpeedId = Shader.PropertyToID("_RippleSpeed");

        private readonly List<Ripple> _ripples = new(MaxRipples);

        // Always pushed at full length. Material.SetVectorArray fixes the array size on
        // its first call, and a shorter array later is silently ignored — so unused slots
        // are zeroed rather than trimmed, and _RippleCount says how many are meaningful.
        private readonly Vector4[] _packed = new Vector4[MaxRipples];

        private Material _material;

        private struct Ripple
        {
            public Vector2 Origin;   // world x/z
            public float StartTime;
            public float Strength;
        }

        /// <summary>
        /// Hand over the water's material. Passed in rather than looked up because the
        /// builder already has it, and Awake runs once — this reference is good for the
        /// whole session.
        /// </summary>
        public void Bind(Material waterMaterial, SkipConstants constants)
        {
            _material = waterMaterial;

            // Off by default, and this is what stops the water cutting the stone in half.
            // SkipPhysics clamps the stone's centre to the surface, so its lower half is
            // genuinely below the water at every bounce; with nothing in the depth buffer
            // there is nothing to occlude it and it draws whole. RockController turns this
            // back on for the length of a sink. See PlaceholderArt.SetWaterDepthWrite for
            // why the render queue could never have done this job.
            SetDepthWriting(false);

            if (constants != null)
            {
                _minImpactSpeed = constants.ReferenceSpeed * minImpactSpeedFraction;
                _maxImpactSpeed = constants.ReferenceSpeed * maxImpactSpeedFraction;
            }

            PushTuning();
        }

        /// <summary>
        /// Lets the water hide things drawn after it, or not. Off during play so the stone
        /// is never cut by the surface; on while a stone is sinking, so the water can
        /// swallow it.
        /// </summary>
        public void SetDepthWriting(bool writesDepth) =>
            PlaceholderArt.SetWaterDepthWrite(_material, writesDepth);

        private void Awake()
        {
            EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, OnSkipOccurred);

            // The impact that ENDS a run used to be the only one that left no ring on the
            // water, which is precisely backwards: it is the one the player is looking at.
            EventManager.Instance?.Subscribe<RockSunkEvent>(this, OnRockSunk);
        }

        private void OnDestroy()
        {
            // Mandatory, not tidiness: subscriptions are held weakly but a live component
            // that forgot would keep receiving after the water is gone.
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
        }

        // True once an all-zero ripple array has been uploaded; see LateUpdate.
        private bool _publishedEmpty;

        private void OnSkipOccurred(SkipOccurredEvent e) => AddRipple(e.Position, e.Speed);

        /// <summary>
        /// The final contact. Rung at full strength regardless of how slowly the stone
        /// arrived — a run usually ends at walking pace, so speed-scaling this the way a
        /// skip is scaled would produce the faintest ring of the whole throw for the one
        /// moment that needs to read as an ending.
        /// </summary>
        private void OnRockSunk(RockSunkEvent e) => AddRipple(e.Position, _maxImpactSpeed);

        private void AddRipple(Vector3 position, float speed)
        {
            // Oldest out first once full, so a long run of skips keeps the newest rings
            // rather than refusing new ones.
            if (_ripples.Count >= MaxRipples) _ripples.RemoveAt(0);

            _ripples.Add(new Ripple
            {
                Origin = new Vector2(position.x, position.z),
                StartTime = Time.time,
                Strength = Mathf.InverseLerp(_minImpactSpeed, _maxImpactSpeed, speed),
            });
        }

        private void LateUpdate()
        {
            // No _material guard. The ripple array is pushed as a global rather than a
            // material property — see the remarks on the Shader.SetGlobalVectorArray call
            // below — so this no longer needs the material at all. With nothing bound
            // there is no water to read the globals either, so publishing them is free.

            // Idle lake: the empty array is already on the GPU, so there is nothing to
            // re-upload until a ripple is added (#66).
            if (_ripples.Count == 0 && _publishedEmpty) return;

            // Drop what has expired, then pack the survivors into the front of the array.
            // Compacting is what lets the fragment loop run zero iterations while idle.
            var live = 0;
            for (var i = _ripples.Count - 1; i >= 0; i--)
            {
                if (Time.time - _ripples[i].StartTime >= rippleLifeSeconds) _ripples.RemoveAt(i);
            }

            for (var i = 0; i < _ripples.Count && i < MaxRipples; i++)
            {
                var ripple = _ripples[i];
                var age = Time.time - ripple.StartTime;

                // Age is resolved here rather than from _Time in the shader, so the shader
                // keeps no clock that could drift from the game's.
                _packed[live++] = new Vector4(ripple.Origin.x, ripple.Origin.y, age, ripple.Strength);
            }

            for (var i = live; i < MaxRipples; i++) _packed[i] = Vector4.zero;

            // The ripple array is set as a global because Shader Graph arrays cannot live in the SRP Batcher CBUFFER; its
            // length is fixed by the first call. doc-ref a765 docs/4-systems/Presentation.md
            Shader.SetGlobalVectorArray(RipplesId, _packed);
            Shader.SetGlobalInt(RippleCountId, live);
            _publishedEmpty = live == 0;
        }

        private void PushTuning()
        {
            if (_material == null) return;

            _material.SetFloat(RippleLifeId, rippleLifeSeconds);
            _material.SetFloat(RippleSpeedId, rippleSpeed);
        }

#if UNITY_EDITOR
        /// <summary>Lets the ring timing be dialled in from the inspector during play.</summary>
        private void OnValidate() => PushTuning();
#endif
    }
}
