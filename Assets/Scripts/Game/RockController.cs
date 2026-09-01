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

        [Tooltip("Beat held after the stone is under, before the turn is allowed to move on. " +
                 "This is the difference between a throw that ENDED and a throw that glitched: " +
                 "without it the score banks and the next player is up ~16 ms after the stone " +
                 "touches the water, on top of the sink it is supposed to be reacting to.")]
        [SerializeField] private float settleHold = 1.2f;

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
        private SkipConstants _constants;
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
        /// True from the moment the run ends until the settle beat is over. The camera
        /// follows on <see cref="IsFlying"/> OR this, so it stays with the stone through
        /// the descent instead of leaving on the frame it stops moving.
        /// </summary>
        public bool IsSinking { get; private set; }

        /// <summary>Set once the run has ended, whether or not the settle has finished.</summary>
        private bool _finished;

        /// <summary>Guards ThrowCompletedEvent to exactly one publish. See PublishCompleted.</summary>
        private bool _completed;

        public bool IsFlying { get; private set; }
        public int SkipCount => _state.SkipCount;
        public float Distance => Vector3.Distance(_origin, _state.Position);

        /// <summary>
        /// How fast the stone is travelling right now, in m/s. Read-only, for presentation.
        /// </summary>
        /// <remarks>
        /// Exposed rather than derived from the transform by whoever wants it: position is
        /// only written while IsFlying, so a frame-delta reading would report a stone that
        /// had stopped dead the instant a run ended and then report the sink descent as
        /// speed. This is the solver's own number and stays correct in both.
        ///
        /// A property, not an event, and that keeps it on the right side of the line this
        /// class documents. Reading state publishes nothing and cannot fire on a throw that
        /// never happened.
        /// </remarks>
        public float Speed => _state.Velocity.magnitude;

        public void Launch(SkipState state, SkipConstants constants, SkipTuning tuning, string playerId)
        {
            _state = state;
            _constants = constants;
            _tuning = tuning;
            _playerId = playerId;
            _origin = state.Position;
            _spinRate = (state.Spin);

            // Derive spin decay per bounce based on predicted skips
            var path = new System.Collections.Generic.List<Vector3>();
            var finalState = Game.SkipPhysics.Predict(state, constants, tuning, path);
            int totalSkips = finalState.SkipCount;
            
            if (totalSkips > 0)
            {
                _perBounceSpinRetention = Mathf.Pow(targetFinalSpinRetention, 1.0f / totalSkips);
            }
            else
            {
                _perBounceSpinRetention = 1.0f;
            }

            // In case a previous sink was interrupted — by a despawn landing mid-descent,
            // or a match reset — rather than trusting the last one to have tidied up.
            var water = FindFirstObjectByType<WaterRipples>();
            if (water != null) water.SetDepthWriting(false);

            transform.position = state.Position;
            transform.rotation = state.Attitude;
            IsFlying = true;

            // Last, so anything reacting to a launch finds the stone already placed,
            // oriented and flying rather than mid-construction.
            //
            // Both recovered from the launch velocity rather than passed in — see
            // RockLaunchedEvent.SwingSpeed for why that is exact, not approximate.
            var swingSpeed = state.Velocity.magnitude / Mathf.Max(constants.SpeedScale, 0.0001f);
            var power = state.Velocity.magnitude / Mathf.Max(constants.ReferenceSpeed, 0.0001f);

            EventManager.Instance?.Publish(new RockLaunchedEvent(
                _origin, swingSpeed, power, totalSkips, state.FaceAngleDeg, state.RollDeg, state.Spin));
        }

        private void Update()
        {
            if (IsFlying)
            {
                // Fixed-step accumulator so the simulation is frame-rate independent —
                // a bounce must not depend on where a frame boundary happened to land.
                _accumulator += Time.deltaTime;

                var guard = 0;
                while (_accumulator >= _constants.FixedStep && IsFlying && guard++ < 16)
                {
                    _accumulator -= _constants.FixedStep;

                    var step = SkipPhysics.Step(_state, _constants, _tuning, _constants.FixedStep);
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
                        // Before Finish, and the order is the point. Finish publishes
                        // ThrowCompletedEvent, which banks the score and rotates the turn
                        // synchronously — so publishing the splash first means the stone's
                        // own moment is handled before the match moves on top of it.
                        EventManager.Instance?.Publish(
                            new RockSunkEvent(_state.Position, step.ImpactSpeed, _state.SkipCount));

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
            IsSinking = true;
            _finished = true;

            Debug.Log($"[Rock] {_playerId}: {_state.SkipCount} skips over {Distance:F1} m");

            StartCoroutine(SettleRoutine());
        }

        /// <summary>
        /// Sink, hold, then let the match move on.
        /// </summary>
        /// <remarks>
        /// The ordering here is the whole fix for "a failed throw reads as a glitch".
        /// ThrowCompletedEvent banks the score and rotates the turn synchronously
        /// (TurnManager.OnThrowCompleted), so publishing it on the sinking step — which is
        /// what used to happen — meant the next player was up before the stone had drawn a
        /// single frame of its descent.
        ///
        /// Destroy is scheduled AFTER the publish rather than alongside Finish. It used to
        /// be set at Finish time on the reasoning that despawnDelay comfortably outlasts
        /// sinkDuration; that stops being true the moment someone lengthens the settle, and
        /// the failure mode is the object dying before it publishes, which strands the match
        /// on a turn that never ends. Publishing first makes that unrepresentable.
        /// </remarks>
        private IEnumerator SettleRoutine()
        {
            yield return SinkRoutine();

            if (settleHold > 0f) yield return new WaitForSeconds(settleHold);

            IsSinking = false;
            PublishCompleted();

            Destroy(gameObject, despawnDelay);
        }

        /// <summary>
        /// Publishes ThrowCompletedEvent exactly once.
        /// </summary>
        /// <remarks>
        /// Called from the settle routine normally, and from OnDestroy as a backstop: a rock
        /// that finished its run but was destroyed mid-settle (scene change, match reset)
        /// would otherwise leave TurnManager waiting for a turn that can never complete.
        /// Gated on _finished so a rock destroyed while still FLYING stays silent — that is
        /// a teardown, not a throw, and publishing there would advance the turn spuriously.
        /// </remarks>
        private void PublishCompleted()
        {
            if (_completed || !_finished) return;
            _completed = true;

            EventManager.Instance?.Publish(
                new ThrowCompletedEvent(_playerId, _state.SkipCount, Distance));
        }

        private void OnDestroy() => PublishCompleted();

        /// <summary>
        /// Drops the stone under the surface once its run is over.
        /// </summary>
        /// <remarks>
        /// The descent on its own would just slide a fully-visible stone downwards. What
        /// makes it read as sinking is the first line: during play the water writes no
        /// depth, so nothing can occlude the stone and it is never cut by the surface;
        /// switching depth writing on lets the water swallow it as it goes down.
        ///
        /// That toggle lives on the WATER rather than the stone, and not by preference —
        /// URP's Lit shader exposes no _ZTest to turn off, and a render queue only decides
        /// draw order, not occlusion. PlaceholderArt.SetWaterDepthWrite records both dead
        /// ends in full.
        ///
        /// Safe to own the transform because Update only writes position while IsFlying,
        /// and IsFlying is already false by the time this starts. Rotation is still driven
        /// there, so the stone keeps spinning down as it goes under.
        /// </remarks>
        private IEnumerator SinkRoutine()
        {
            var water = FindFirstObjectByType<WaterRipples>();
            if (water != null) water.SetDepthWriting(true);

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

            // Handed back before this object is destroyed. Left on, the next throw's stone
            // would be cut by the surface again — and the cause would be a coroutine that
            // finished on a rock that no longer exists, which is a miserable thing to find.
            if (water != null) water.SetDepthWriting(false);
        }

    }
}
