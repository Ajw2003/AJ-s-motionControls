using System.Collections.Generic;
using EventSystems;
using EventTypes.SkipEvents;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Feeds bounce positions to the water material so each skip leaves an expanding ring.
    /// </summary>
    /// <remarks>
    /// Lives on the water rather than inside SkipPhysics for the reason the solver spells
    /// out: observable effects belong to the caller, never in the solver. Ripples hang off
    /// SkipOccurredEvent, which only RockController publishes, so the aim preview — which
    /// runs the same solver hundreds of times a frame for a throw that never happened —
    /// cannot produce them. That safety is inherited, not re-implemented here.
    /// </remarks>
    public class WaterRipples : MonoBehaviour
    {
        /// <summary>Must match MAX_RIPPLES in WaterRipple.shader.</summary>
        public const int MaxRipples = 16;

        [Tooltip("Seconds a ring takes to fade out entirely.")]
        [SerializeField] private float rippleLifeSeconds = 2.5f;
        [Tooltip("How fast the leading edge expands, in world units per second.")]
        [SerializeField] private float rippleSpeed = 6f;

        [Header("Impact response")]
        [Tooltip("Faintest and strongest ring, as fractions of SkipTuning.MaxSpeed. Derived " +
                 "rather than absolute because these were hardcoded at 4 and 25 against a " +
                 "MaxSpeed of 26 — raise the launch speed and every impact clamps to full " +
                 "strength, so the ripple stops responding to anything at all.")]
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
        public void Bind(Material waterMaterial, SkipTuning tuning)
        {
            _material = waterMaterial;

            if (tuning != null)
            {
                _minImpactSpeed = tuning.MaxSpeed * minImpactSpeedFraction;
                _maxImpactSpeed = tuning.MaxSpeed * maxImpactSpeedFraction;
            }

            PushTuning();
        }

        private void Awake()
        {
            EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, OnSkipOccurred);
        }

        private void OnDestroy()
        {
            // Mandatory, not tidiness: subscriptions are held weakly but a live component
            // that forgot would keep receiving after the water is gone.
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
        }

        private void OnSkipOccurred(SkipOccurredEvent e)
        {
            // Oldest out first once full, so a long run of skips keeps the newest rings
            // rather than refusing new ones.
            if (_ripples.Count >= MaxRipples) _ripples.RemoveAt(0);

            _ripples.Add(new Ripple
            {
                Origin = new Vector2(e.Position.x, e.Position.z),
                StartTime = Time.time,
                Strength = Mathf.InverseLerp(_minImpactSpeed, _maxImpactSpeed, e.Speed),
            });
        }

        private void LateUpdate()
        {
            if (_material == null) return;

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

            _material.SetVectorArray(RipplesId, _packed);
            _material.SetInt(RippleCountId, live);
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
