using System.Collections;
using EventSystems;
using EventTypes.SkipEvents;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// One rock in flight. Drives the pure solver forward and turns its results into
    /// everything observable: movement, spin, and gameplay events.
    /// </summary>
    /// <remarks>
    /// This is the only place skip events are published. SkipPhysics.Step deliberately
    /// publishes nothing, because the aim preview runs it speculatively every frame for
    /// a throw that never happened — if the solver published, the preview would score
    /// imaginary throws. Keep every side effect on this side of the line.
    /// </remarks>
    public class RockController : MonoBehaviour
    {
        [Tooltip("Fraction of spin remaining by the final skip (0..1)")]
        [SerializeField] private float targetFinalSpinRetention = 0.001f;

        [SerializeField] private float despawnDelay = 2.5f;

        [Header("Sink")]
        [Tooltip("How long the stone takes to slip under once the run has ended.")]
        [SerializeField] private float sinkDuration = 0.6f;

        [Tooltip("How far it travels down. Only needs to be far enough to be out of sight — " +
                 "the water occludes it well before it gets there.")]
        [SerializeField] private float sinkDepth = 1.5f;

        /// <summary>
        /// Ceiling on how fast the stone is drawn turning, in deg/s. Shared with the aim
        /// ghost so the two cannot spin differently.
        /// </summary>
        /// <remarks>
        /// Not a tuning field, because it is not a taste decision — it is the frame rate.
        /// The silhouette of a flat box repeats every half turn, so at 60fps anything past
        /// about 2700 deg/s aliases into a wagon-wheel that appears to turn backwards, and
        /// it looks wrong well before that. A hard wrist flick genuinely measures 1500-2500
        /// deg/s on a phone, so this is reachable in normal play rather than theoretical.
        ///
        /// Clamping rather than rescaling: below the ceiling the stone turns at exactly the
        /// rate the phone measured, which is the point. Only the flicks that could not be
        /// drawn honestly anyway are capped.
        /// </remarks>
        internal const float MaxVisualSpinDegPerSec = 1200f;

        /// <summary>
        /// The rate a measured wrist spin is drawn at, signed. See the constant above.
        /// </summary>
        internal static float VisualSpin(float spinRate) =>
            Mathf.Clamp(spinRate, -MaxVisualSpinDegPerSec, MaxVisualSpinDegPerSec);

        private SkipState _state;
        private SkipTuning _tuning;
        private string _playerId;
        private Vector3 _origin;
        private float _accumulator;

        /// <summary>
        /// Accumulated turn about the stone's own face normal. Only ever added to, never
        /// assigned — the rate may change, but the angle must not jump, or a stone that
        /// was turning steadily appears to flick.
        /// </summary>
        private float _spinAngle;
        private float _spinRate;
        private float _perBounceSpinRetention = 1.0f;

        public bool IsFlying { get; private set; }
        public int SkipCount => _state.SkipCount;
        public float Distance => Vector3.Distance(_origin, _state.Position);

        public void Launch(SkipState state, SkipTuning tuning, string playerId)
        {
            _state = state;
            _tuning = tuning;
            _playerId = playerId;
            _origin = state.Position;
            _spinRate = (state.Spin);

            // Derive spin decay per bounce based on predicted skips
            var path = new System.Collections.Generic.List<Vector3>();
            var finalState = Game.SkipPhysics.Predict(state, tuning, path);
            int totalSkips = finalState.SkipCount;
            
            if (totalSkips > 0)
            {
                _perBounceSpinRetention = Mathf.Pow(targetFinalSpinRetention, 1.0f / totalSkips);
            }
            else
            {
                _perBounceSpinRetention = 1.0f;
            }

            transform.position = state.Position;
            transform.rotation = state.Attitude;
            IsFlying = true;
        }

        private void Update()
        {
            if (IsFlying)
            {
                // Fixed-step accumulator so the simulation is frame-rate independent —
                // a bounce must not depend on where a frame boundary happened to land.
                _accumulator += Time.deltaTime;

                var guard = 0;
                while (_accumulator >= _tuning.FixedStep && IsFlying && guard++ < 16)
                {
                    _accumulator -= _tuning.FixedStep;

                    var step = SkipPhysics.Step(_state, _tuning, _tuning.FixedStep);
                    _state = step.State;

                    if (step.Bounced)
                    {
                        // Lose spin on contact with the water
                        _spinRate *= _perBounceSpinRetention;

                        // _state is already step.State, and SkipPhysics clamps a bounce to
                        // the water plane, so this position is exactly the contact point.
                        EventManager.Instance?.Publish(
                            new SkipOccurredEvent(_state.SkipCount, step.ImpactSpeed, _state.Position));
                    }

                    if (step.Sank)
                    {
                        Finish();
                        break;
                    }
                }

                // Only while flying, deliberately. Once the run is over this stops writing
                // position at all, which is what leaves the transform free for a sink
                // coroutine to own without Update fighting it back every frame.
                transform.position = _state.Position;
            }

            if (!IsFlying)
            {
                // Coast to a stop smoothly over ~0.8 seconds while underwater
                _spinRate *= Mathf.Exp(-Time.deltaTime / 0.8f);
            }

            _spinAngle += _spinRate * Time.deltaTime;

            // Attitude on the left, spin on the right: the stone turns about its own face
            // normal, the way a skipping stone does, rather than about the world's up.
            // Attitude comes straight from the phone and is never adjusted — the ~20
            // degrees the water wants lives in SkipPhysics, not here.
            transform.rotation = _state.Attitude * Quaternion.Euler(0f, _spinAngle, 0f);
        }

        private void Finish()
        {
            IsFlying = false;

            EventManager.Instance?.Publish(
                new ThrowCompletedEvent(_playerId, _state.SkipCount, Distance));

            Debug.Log($"[Rock] {_playerId}: {_state.SkipCount} skips over {Distance:F1} m");

            // Scheduled here rather than at the end of the sink, so the object's lifetime
            // stays decoupled from the effect. despawnDelay comfortably outlasts
            // sinkDuration, and anything hanging off ThrowCompletedEvent still gets its
            // full window whether the sink runs, is retuned, or is switched off entirely.
            Destroy(gameObject, despawnDelay);

            StartCoroutine(SinkRoutine());
        }

        /// <summary>
        /// Drops the stone under the surface once its run is over.
        /// </summary>
        /// <remarks>
        /// The whole effect is the first line. While the stone is in play it ignores depth
        /// so the water cannot cut off the half of it that is below the surface (see
        /// PlaceholderArt.SetDepthTested); handing it back to the depth buffer here is what
        /// lets the water swallow it as it descends. The translation on its own would just
        /// slide a fully-visible stone downwards.
        ///
        /// Safe to own the transform because Update only writes position while IsFlying,
        /// and IsFlying is already false by the time this starts. Rotation is still driven
        /// there, so the stone keeps spinning down as it goes under.
        /// </remarks>
        private IEnumerator SinkRoutine()
        {
            var renderer = GetComponent<MeshRenderer>();
            if (renderer != null) PlaceholderArt.SetDepthTested(renderer.material, true);

            var start = transform.position;
            var end = start + Vector3.down * sinkDepth;

            var elapsed = 0f;
            while (elapsed < sinkDuration)
            {
                elapsed += Time.deltaTime;

                // Cubic ease-out: it drops away quickly and then slows, the way a stone
                // losing the last of its momentum into water does. Linear reads as a lift.
                var t = Mathf.Clamp01(elapsed / Mathf.Max(sinkDuration, 0.0001f));
                transform.position = Vector3.Lerp(start, end, 1f - Mathf.Pow(1f - t, 3f));

                yield return null;
            }
        }
    }
}
