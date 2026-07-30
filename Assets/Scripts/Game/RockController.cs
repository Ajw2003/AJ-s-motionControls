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
        [SerializeField] private float visualSpinDegPerSec = 720f;
        [SerializeField] private float despawnDelay = 2.5f;

        private SkipState _state;
        private RockStats _stats;
        private SkipTuning _tuning;
        private string _playerId;
        private Vector3 _origin;
        private float _accumulator;
        private float _visualSpin;

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

            transform.position = state.Position;
            IsFlying = true;
        }

        private void Update()
        {
            if (!IsFlying) return;

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

            _visualSpin += visualSpinDegPerSec * Time.deltaTime;
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
