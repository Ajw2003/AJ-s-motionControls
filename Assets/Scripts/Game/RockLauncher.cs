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

        /// <summary>A flat stone, sized once so the rock and the aim ghost cannot differ.</summary>
        internal static readonly Vector3 StoneScale = new(0.25f, 0.08f, 0.25f);

        private void Update()
        {
            EnsureBound();
            if (_active != null)
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

            // No negations. The phone's rotation becomes a Unity rotation exactly once, in
            // PhoneAttitude, and everything downstream uses the result as-is — see that
            // file for why the sign fixes that used to live on this line were a symptom.
            var attitude = PhoneAttitude.ToUnity(message.qw, message.qx, message.qy, message.qz);

            var launch = SkipPhysics.CreateLaunch(
                _preview.ThrowOrigin.position, message.power, attitude, message.spinRate, tuning);

            _active = CreateRockObject();
            _active.Launch(launch, tuning, message.playerId);
        }

        private void FollowActiveRock()
        {
            if (_camera == null) return;

            if (_active != null && _active.IsFlying)
            {
                // Trail behind the rock on X and Z, but lock Y to the camera's home height
                var target = new Vector3(
                    _active.transform.position.x - 2f, 
                    _cameraHome.y, // Locks vertical tracking to prevent bouncing
                    _active.transform.position.z - 3f
                );

                _camera.transform.position = Vector3.SmoothDamp(_camera.transform.position, target, ref _cameraVelocity, cameraFollowSmoothing);

                // Smoothly rotate to look at the active rock
                var lookDirection = _active.transform.position - _camera.transform.position;
                
                // Optional: If the camera tilting up/down to watch the rock skip is ALSO 
                // causing motion sickness, uncomment the line below to lock the look pitch:
                // lookDirection.y = 0f; 

                _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation, Quaternion.LookRotation(lookDirection), Time.deltaTime * 5f);
                return;
            }

            _camera.transform.position = Vector3.SmoothDamp(_camera.transform.position, _cameraHome, ref _cameraVelocity, cameraFollowSmoothing * 2f);
            _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation, _cameraHomeRotation, Time.deltaTime * 3f);
        }

        /// <summary>
        /// A flat box.
        /// </summary>
        /// <remarks>
        /// A box, not a scaled sphere, and that is the whole reason the custom Stone
        /// shader and the welded-on chip cube are gone. A scaled sphere is a surface of
        /// revolution: it renders identically at every yaw, so no amount of spin was ever
        /// visible on it, and both the shader and the chip existed to fake an asymmetry
        /// back in. A box has flat faces and hard edges, so it shows its own rotation
        /// from every angle for free — which is what the stone has to do now that it
        /// tracks the phone one-to-one.
        /// </remarks>
        private static RockController CreateRockObject()
        {
            var rock = Resources.Load<GameObject>("RockObject");
            var go = Instantiate(rock);
            go.name = "Rock";
            Destroy(go.GetComponent<Collider>()); // the custom solver owns collision
            go.transform.localScale = StoneScale;

            go.GetComponent<MeshRenderer>().material =
                PlaceholderArt.Create(new Color(0.85f, 0.87f, 0.95f), PlaceholderArt.StoneQueue);

            return go.AddComponent<RockController>();
        }
    }
}
