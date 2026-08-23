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

        /// <summary>
        /// How far above the solved position the stone is drawn, so it rests ON the water
        /// rather than half inside it. Defaults to half the stone's height.
        /// </summary>
        /// <remarks>
        /// SkipPhysics clamps the stone's CENTRE to the water plane, so its lower half has
        /// always been below the surface at every bounce. Under the Built-in pipeline that
        /// was invisible: everything ran ZWrite Off behind hand-pinned render queues, so
        /// the stone drew over the water regardless of where it actually was. URP/Lit is
        /// opaque and writes depth, so the water now correctly occludes the submerged half
        /// and it reads as a hard straight cut across the stone.
        ///
        /// So this is a presentation fix for a geometry bug the renderer started telling
        /// the truth about — not a change to where the stone goes. It lives here rather
        /// than in SkipPhysics deliberately: the solver is pure and owns the maths, and an
        /// offset applied in there would move the real contact point and change the bounce.
        /// </remarks>
        [Tooltip("Draws the stone resting on the surface instead of half-submerged. " +
                 "Leave at 0 to measure it off the mesh — see RockLauncher.StoneFloatOffset. " +
                 "Set a value only to override that.")]
        [SerializeField] private float visualFloatOffset;

        [Header("Sink")]
        [Tooltip("How long the stone takes to disappear under the surface once it has sunk.")]
        [SerializeField] private float sinkDuration = 0.7f;

        [Tooltip("How far below the waterline it ends up. Only needs to be deep enough to " +
                 "be hidden — the water occludes it long before it gets there.")]
        [SerializeField] private float sinkDepth = 1.4f;

        [Tooltip("Degrees the stone tips over as it goes under, so it drops like a stone " +
                 "rather than descending like a lift.")]
        [SerializeField] private float sinkTumbleDeg = 55f;

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

        /// <summary>
        /// Seconds since the stone sank. Zero while flying, which is what keeps the sink
        /// descent out of the airborne path entirely.
        /// </summary>
        private float _sinkElapsed;

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

            // Measured once, here, rather than hardcoded: the launcher builds this object
            // from Rock.fbx, so the mesh is the only thing that knows where its own bottom
            // is. Zero means "not overridden in the inspector".
            if (visualFloatOffset <= 0f)
            {
                visualFloatOffset = RockLauncher.StoneFloatOffset(gameObject);
            }

            // Offset here too, not just in Update, so the stone does not appear at the
            // solved position for one frame and then jump up to its drawn one.
            transform.position = state.Position + Vector3.up * visualFloatOffset;
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
            }

            if (!IsFlying)
            {
                // Coast to a stop smoothly over ~0.8 seconds while underwater
                _spinRate *= Mathf.Exp(-Time.deltaTime / 0.8f);
                _sinkElapsed += Time.deltaTime;
            }

            _spinAngle += _spinRate * Time.deltaTime;

            // How far under the surface the stone has settled, and how far it has tipped
            // over on the way down. Both are zero while it is flying, so the airborne path
            // is exactly what it always was.
            //
            // Cubic ease-out: it drops away quickly and then slows, which is what a stone
            // losing the last of its momentum into water actually does. Linear read as a
            // lift descending.
            var sink = 0f;
            var tumble = 0f;
            if (_sinkElapsed > 0f)
            {
                var t = Mathf.Clamp01(_sinkElapsed / Mathf.Max(sinkDuration, 0.0001f));
                var eased = 1f - Mathf.Pow(1f - t, 3f);
                sink = sinkDepth * eased;
                tumble = sinkTumbleDeg * eased;
            }

            // Position is written once, here, for both the flying and the sinking case —
            // it used to be set inside the IsFlying branch, which left no single place the
            // float offset could be applied without being overwritten a frame later.
            transform.position = _state.Position + Vector3.up * (visualFloatOffset - sink);

            // Attitude on the left, spin on the right: the stone turns about its own face
            // normal, the way a skipping stone does, rather than about the world's up.
            // Attitude comes straight from the phone and is never adjusted — the ~20
            // degrees the water wants lives in SkipPhysics, not here.
            //
            // The sink tumble goes on the far left, in world space, so it tips the whole
            // stone over regardless of which way the phone happened to be held. Applied on
            // the right it would have been about the stone's own axis, which for a flat
            // stone lying face-up is barely visible.
            transform.rotation =
                Quaternion.AngleAxis(tumble, Vector3.right) *
                _state.Attitude * Quaternion.Euler(0f, _spinAngle, 0f);
        }

        private void Finish()
        {
            IsFlying = false;

            EventManager.Instance?.Publish(
                new ThrowCompletedEvent(_playerId, _state.SkipCount, Distance));

            Debug.Log($"[Rock] {_playerId}: {_state.SkipCount} skips over {Distance:F1} m");
            Destroy(gameObject, despawnDelay);
        }
    }
}
