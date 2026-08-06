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
        [Header("Visual spin")]
        [Tooltip("Yaw rate once the stone's spin is spent. Never zero — a stone that has " +
                 "stopped turning reads as frozen rather than tired.")]
        [SerializeField] private float minVisualSpinDegPerSec = 180f;
        [Tooltip("Yaw rate at full spin. Kept modest on purpose: the stone is a square plate, " +
                 "so it looks identical every 90 degrees and a fast yaw aliases into a strobe.")]
        [SerializeField] private float maxVisualSpinDegPerSec = 900f;
        [Tooltip("How long a full-speed spin takes to wind down once the stone sinks. " +
                 "Stopping dead reads as a freeze-frame rather than a stone settling.")]
        [SerializeField] private float spinCoastDownSeconds = 1.2f;

        [SerializeField] private float despawnDelay = 2.5f;

        private SkipState _state;
        private RockStats _stats;
        private SkipTuning _tuning;
        private string _playerId;
        private Vector3 _origin;
        private float _accumulator;
        private float _visualSpin;
        private float _spinRate;
        private float _spinCap;

        public bool IsFlying { get; private set; }
        public int SkipCount => _state.SkipCount;
        public float Distance => Vector3.Distance(_origin, _state.Position);

        public void Launch(SkipState state, in RockStats stats, SkipTuning tuning, string playerId)
        {
            _state = state;
            _stats = stats;
            _tuning = tuning;
            _playerId = playerId;
            _origin = state.Position;

            // The spin at which the gyroscopic tolerance bonus saturates. Past it, more
            // spin buys no more stability, so it should not look faster either. Derived
            // rather than hardcoded so retuning SkipTuning can't drift the visual off it.
            _spinCap = tuning.SpinToleranceScale > 0.0001f
                ? tuning.MaxSpinToleranceBonus / tuning.SpinToleranceScale
                : 1f;

            transform.position = state.Position;
            IsFlying = true;
        }

        private void Update()
        {
            // Note the branch rather than an early return: the stone keeps turning after
            // it sinks, coasting down over the despawn delay. IsFlying is read once here,
            // so the frame that sinks still takes the flying path and seeds the rate from
            // the final spin — there is no gap before the coast starts.
            if (IsFlying)
            {
                // Fixed-step accumulator so the simulation is frame-rate independent —
                // a bounce must not depend on where a frame boundary happened to land.
                _accumulator += Time.deltaTime;

                var guard = 0;
                while (_accumulator >= _tuning.FixedStep && IsFlying && guard++ < 16)
                {
                    _accumulator -= _tuning.FixedStep;

                    var step = SkipPhysics.Step(_state, _stats, _tuning, _tuning.FixedStep);
                    _state = step.State;

                    if (step.Bounced)
                    {
                        EventManager.Instance?.Publish(new SkipOccurredEvent(_state.SkipCount, step.ImpactSpeed));
                    }

                    if (step.Sank)
                    {
                        Finish();
                        break;
                    }
                }

                transform.position = _state.Position;

                // Driven by the stone's actual spin, so a hard flick visibly turns faster
                // and every bounce visibly bleeds it away as SpinDecay eats it. Only the
                // rate steps at a bounce, never the angle, so it reads smoothly.
                var spin01 = Mathf.Clamp01(_state.Spin / _spinCap);
                _spinRate = Mathf.Lerp(minVisualSpinDegPerSec, maxVisualSpinDegPerSec, spin01);
            }
            else
            {
                // Sunk. The water drags the turn out of it rather than stopping it dead.
                // Linear, so the tunable is a wind-down time that can be reasoned about:
                // a full-speed spin takes all of it, a slower one proportionally less.
                _spinRate = Mathf.MoveTowards(
                    _spinRate, 0f, maxVisualSpinDegPerSec * Time.deltaTime / Mathf.Max(spinCoastDownSeconds, 0.0001f));
            }

            _visualSpin += _spinRate * Time.deltaTime;
            transform.rotation = Quaternion.Euler(-_state.AttitudeDeg, 0f, 0f) * Quaternion.Euler(0f, _visualSpin, 0f);
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
