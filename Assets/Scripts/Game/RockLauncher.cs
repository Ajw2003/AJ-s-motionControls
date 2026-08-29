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

            // Both read from AimPreview rather than re-resolved here, so the ghost this
            // player just aimed with and the stone that actually flies can never land on
            // different rocks for the same turn — see AimPreview.OnTurnStarted.
            var tuning = _preview.Tuning;
            var constants = _preview.Constants;
            var rock = _preview.CurrentRock;

            // No negations. The phone's rotation becomes a Unity rotation exactly once, in
            // PhoneAttitude, and everything downstream uses the result as-is — see that
            // file for why the sign fixes that used to live on this line were a symptom.
            var attitude = PhoneAttitude.ToUnity(message.qw, message.qx, message.qy, message.qz);

            var launch = SkipPhysics.CreateLaunch(
                _preview.ThrowOrigin.position, message.power, attitude, message.spinRate, constants, tuning);

            _active = CreateRockObject(rock, constants);
            _active.Launch(launch, constants, tuning, message.playerId);
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
        /// The live stone: <paramref name="rock"/>'s mesh, scale and tint when there is
        /// one, else the prefab's own plain box — the original single-rock look, kept as
        /// the fallback for a catalog that failed to load rather than taking the throw
        /// path down over it. See RockCatalog.CreateDefault's doc for why an empty
        /// catalog degrading to this is the deliberate behaviour project-wide.
        /// </summary>
        /// <remarks>
        /// A box was the original shape for a reason worth keeping in mind for the
        /// fallback: a scaled sphere is a surface of revolution and renders identically at
        /// every yaw, so a box was chosen so the stone shows its own rotation from every
        /// angle. RockCatalogBuilder's meshes carry that concern themselves now.
        /// </remarks>
        private static RockController CreateRockObject(RockDefinition rock, SkipConstants constants)
        {
            var prefab = Resources.Load<GameObject>("RockObject");
            var go = Instantiate(prefab);
            go.name = "Rock";
            Destroy(go.GetComponent<Collider>()); // the custom solver owns collision

            if (rock != null && rock.Mesh != null)
            {
                var filter = go.GetComponent<MeshFilter>();
                if (filter != null) filter.sharedMesh = rock.Mesh;
                go.transform.localScale = rock.LocalScale;
            }

            var tint = rock != null ? rock.Tint : Color.gray3;
            go.GetComponent<MeshRenderer>().material =
                PlaceholderArt.Create(tint, PlaceholderArt.StoneQueue);

            var controller = go.AddComponent<RockController>();

            // After the controller, because the trail reads IsFlying and Speed off it. The
            // same constants the throw is about to be simulated with are handed over so the
            // trail's speed thresholds cannot drift from the launch speed they scale
            // against — see TrailTuning's remarks on why they are fractions.
            RockTrail.Attach(go, constants);

            return controller;
        }
    }
}
