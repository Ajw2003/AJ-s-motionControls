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
        [Tooltip("How long the stone keeps turning once it is under, in seconds. A water-drag " +
                 "time constant, not a target — spin is otherwise entirely the solver's " +
                 "(SkipConstants.SpinRetention), so this is the only visual-only spin number left.")]
        [SerializeField] private float underwaterSpinDamping = 0.8f;

        [SerializeField] private float despawnDelay = 2.5f;

        [Header("Sink")]
        [Tooltip("How long the stone takes to slip under after a HARD arrival — it is going " +
                 "fast, so it goes quickly.")]
        [SerializeField] private float fastSinkDuration = 0.45f;

        [Tooltip("How long it takes after a run simply ends — no momentum left, so it settles " +
                 "rather than knifing, and needs longer on screen to read as settling instead " +
                 "of vanishing.")]
        [SerializeField] private float slowSinkDuration = 1f;

        [Tooltip("How far a hard arrival drives under. Only needs to beat the point where the " +
                 "water occludes it.")]
        [SerializeField] private float fastSinkDepth = 2.2f;

        [Tooltip("How far a spent stone slips under.")]
        [SerializeField] private float slowSinkDepth = 0.9f;

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

        /// <summary>
        /// How fast the stone was travelling when it went in, m/s. Shapes the sink: a hard
        /// arrival drives deep and quickly, a spent one settles shallow and slowly.
        /// </summary>
        private float _sinkImpactSpeed;

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

        /// <summary>
        /// The stone's own renderer, switched off the moment it is under. Cached rather
        /// than fetched in the coroutine so the lookup cannot fail late. Deliberately just
        /// this one — the trail is a separate renderer and is still fading out.
        /// </summary>
        private MeshRenderer _renderer;

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
            // Seeded from the solver's own spin, and from here on the solver keeps it.
            //
            // This used to run a full SkipPhysics.Predict of the entire throw purely to
            // learn the skip count, then back-solve a per-bounce decay that would land spin
            // on an authored 0.001 exactly as the run ended. Nothing about that was
            // physical: the stone's spin was not decaying BECAUSE of anything, it was being
            // driven to a predetermined value on a predetermined schedule. It also made the
            // ending look wrong in a way that depended on the skip count — a throw that
            // scored zero kept every degree of its spin (the divide-by-zero branch returned
            // retention 1.0) and knifed under beautifully, while a good throw arrived with
            // 0.1% of its spin and sat there like a deleted object.
            //
            // SkipPhysics already decays spin honestly, by SkipConstants.SpinRetention per
            // bounce, because spin is load-bearing there: it is the gyroscopic stability
            // holding the stone's attack angle. Reading that is free, correct, and means
            // what the player sees is the same number the bounce is judged on.
            _spinRate = state.Spin;

            // In case a previous sink was interrupted — by a despawn landing mid-descent,
            // or a match reset — rather than trusting the last one to have tidied up.
            var water = FindFirstObjectByType<WaterRipples>();
            if (water != null) water.SetDepthWriting(false);

            // Cached for the sink to switch off. Re-enabled here as well as cached, so a
            // pooled or re-launched stone cannot inherit the hidden state of the last one.
            _renderer = GetComponent<MeshRenderer>();
            if (_renderer != null) _renderer.enabled = true;

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
                _origin, swingSpeed, power, state.FaceAngleDeg, state.RollDeg, state.Spin));
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
                        // Spin comes off the solver, which already lost some to the water on
                        // this bounce (SkipConstants.SpinRetention). No second, visual-only
                        // decay on top — one spin, physically derived, drawn as it is.
                        _spinRate = _state.Spin;

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

                        _sinkImpactSpeed = step.ImpactSpeed;
                        Finish();
                        break;
                    }
                }

                // Only while flying, deliberately. Once the run is over this stops writing
                // position at all, which is what leaves the transform free for a sink
                // coroutine to own without Update fighting it back every frame.
                //
                // Drawn BETWEEN simulation steps, not on them. The solver advances in fixed
                // increments but frames do not land on those boundaries, so writing the raw
                // solver position pins the stone to the step rate: at FixedStep 0.02 that is
                // 50 position updates a second against a 60-144 Hz display, which reads as
                // stutter however good the trajectory is. _accumulator is exactly the time
                // already elapsed past the last step, so carrying the stone forward by it
                // puts the stone where it actually is at draw time.
                //
                // This is why smoothness should not be bought by shrinking FixedStep: that
                // costs the aim preview 4x the work (MaxPredictSteps = PredictSeconds /
                // FixedStep) for a problem that is not in the physics.
                var render = _state.Position + _state.Velocity * _accumulator;

                // Never draw it under the surface between steps. The extrapolation is a
                // straight line but the real path bounces, so on the frames either side of a
                // contact the line would dip through the water and the stone would flicker
                // below it.
                if (render.y < _constants.WaterY) render.y = _constants.WaterY;

                transform.position = render;
            }

            if (!IsFlying)
            {
                // Water drag once it is under. The only visual-only spin constant left, and
                // it is a drag time constant rather than a target: the stone slows because
                // it is in water, not so that it will have stopped by some particular moment.
                _spinRate *= Mathf.Exp(-Time.deltaTime / underwaterSpinDamping);
            }

            // Clamped here rather than at the source, so the physics keeps the real rate and
            // only the DRAWING is limited. Past MaxVisualSpinDegPerSec a flat stone aliases
            // into a wagon-wheel that appears to turn backwards. This clamp existed but was
            // never actually applied to the angle — the raw rate was being integrated.
            _spinAngle += VisualSpin(_spinRate) * Time.deltaTime;

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
        /// the sink; that stops being true the moment someone lengthens the settle, and
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

            // Shaped by how hard it arrived, because the two endings are genuinely different
            // events and looked wrong sharing one shape. A stone that plows in still has all
            // its speed and should be driven deep and fast; a stone whose run simply expired
            // has none, and hurrying it under is what made a good throw end by vanishing.
            var hardness = Mathf.Clamp01(_sinkImpactSpeed / Mathf.Max(_constants.ReferenceSpeed, 0.0001f));
            var duration = Mathf.Lerp(slowSinkDuration, fastSinkDuration, hardness);
            var depth = Mathf.Lerp(slowSinkDepth, fastSinkDepth, hardness);

            var start = transform.position;
            var end = start + Vector3.down * depth;

            var elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;

                // Cubic ease-out: it drops away quickly and then slows, the way a stone
                // losing the last of its momentum into water does. Linear reads as a lift.
                var t = Mathf.Clamp01(elapsed / Mathf.Max(duration, 0.0001f));
                transform.position = Vector3.Lerp(start, end, 1f - Mathf.Pow(1f - t, 3f));

                yield return null;
            }

            // Gone the instant it is under, and BEFORE depth writing is handed back. The
            // stone is not destroyed here — the settle beat and ThrowCompletedEvent still
            // have to run, and they run on this object's coroutine — so it is hidden
            // instead and torn down later.
            //
            // The order is the bug. The only thing concealing a sunk stone is the water
            // writing depth; switch that off with the stone still alive 1.5 m down and it
            // reappears, whole, hanging under the surface. It did that before too, but the
            // camera had already left, so nobody watched it happen. Holding the shot on
            // the sink put it on screen.
            if (_renderer != null) _renderer.enabled = false;

            // Handed back only once nothing is left to conceal. Left on, the next throw's
            // stone would be cut by the surface — and the cause would be a coroutine that
            // finished on a rock that no longer exists, which is a miserable thing to find.
            if (water != null) water.SetDepthWriting(false);
        }

    }
}
