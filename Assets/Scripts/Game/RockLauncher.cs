using Managers;
using Net;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Turns an inbound THROW into a real rock and follows it with the camera.
    /// </summary>
    /// <remarks>
    /// Shares the throw origin and tuning with AimPreview on purpose: if the preview
    /// and the live throw ever drift onto different rigs, the arc stops predicting the
    /// thing it claims to predict. Scaffolding — replace with authored scene content
    /// once the real lake exists.
    /// </remarks>
    public class RockLauncher : MonoBehaviour
    {
        [SerializeField] private float cameraFollowSmoothing = 0.25f;

        private RelayClient _client;
        private AimPreview _preview;
        private RockController _active;

        private Camera _camera;
        private Vector3 _cameraHome;
        private Quaternion _cameraHomeRotation;
        private Vector3 _cameraVelocity;

        public RockController ActiveRock => _active;

        private void Update()
        {
            EnsureBound();
            if (_active != null )
            {
                FollowActiveRock();
            }
        }

        private void OnDestroy()
        {
            if (_client != null) _client.ThrowReceived -= OnThrow;
        }

        private void EnsureBound()
        {
            if (_preview == null) _preview = FindFirstObjectByType<AimPreview>();

            if (_camera == null)
            {
                _camera = Camera.main;
                if (_camera != null)
                {
                    // Captured after AimPreview has framed the shot, so "home" is the
                    // aiming view rather than the scene's default camera placement.
                    _cameraHome = _camera.transform.position;
                    _cameraHomeRotation = _camera.transform.rotation;
                }
            }

            if (_client != null || !RelayClient.HasInstance) return;

            _client = RelayClient.Instance;
            _client.ThrowReceived += OnThrow;
        }

        private void OnThrow(ThrowMessage message)
        {
            // Only the player whose turn it is, and only while a match is running.
            // Both halves matter: a phone that never got a TURN update could otherwise
            // fling rocks into someone else's turn, and ActivePlayer is null outside the
            // Playing phase — so an "is it your turn" check alone would let anyone throw
            // across the lobby and the results screen.
            if (TurnManager.HasInstance)
            {
                var turns = TurnManager.Instance;
                if (turns.Phase != MatchPhase.Playing) return;
                if (message.playerId != turns.ActivePlayer) return;
            }

            if (_preview == null)
            {
                Debug.LogWarning("[RockLauncher] No AimPreview in the scene — nothing to launch from.");
                return;
            }

            if (_active != null && _active.IsFlying) return; // one rock at a time

            var tuning = _preview.Tuning;
            var stats = RockStats.Default;

            var launch = SkipPhysics.CreateLaunch(
                _preview.ThrowOrigin.position, Vector3.forward,
                message.power, message.timingQuality,
                -message.aimDeg, -message.attackAngleDeg, message.spinRate,
                stats, tuning);

            _active = CreateRockObject();
            _active.Launch(launch, stats, tuning, message.playerId);
        }

        private void FollowActiveRock()
        {
            if (_camera == null) return;

            if (_active != null && _active.IsFlying)
            {
                // Trail behind and above so the skips read clearly from the couch.
                var target = _active.transform.position + new Vector3(-4f, 3.5f, -7f);
                _camera.transform.position = Vector3.SmoothDamp(
                    _camera.transform.position, target, ref _cameraVelocity, cameraFollowSmoothing);
                _camera.transform.rotation = Quaternion.Slerp(
                    _camera.transform.rotation,
                    Quaternion.LookRotation(_active.transform.position - _camera.transform.position),
                    Time.deltaTime * 5f);
                return;
            }

            _camera.transform.position = Vector3.SmoothDamp(
                _camera.transform.position, _cameraHome, ref _cameraVelocity, cameraFollowSmoothing * 2f);
            _camera.transform.rotation = Quaternion.Slerp(
                _camera.transform.rotation, _cameraHomeRotation, Time.deltaTime * 3f);
        }

        private static RockController CreateRockObject()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Rock";
            Destroy(go.GetComponent<Collider>()); // the custom solver owns collision
            go.transform.localScale = new Vector3(0.65f, 0.08f, 0.55f);

            go.GetComponent<MeshRenderer>().material =
                PlaceholderArt.Create(new Color(0.85f, 0.87f, 0.95f), PlaceholderArt.StoneQueue);

            return go.AddComponent<RockController>();
        }
    }
}
