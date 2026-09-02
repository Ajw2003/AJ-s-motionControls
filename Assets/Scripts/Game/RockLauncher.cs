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

        /// <summary>
        /// Latched when the camera starts heading home, so <see cref="_cameraVelocity"/> is
        /// cleared exactly once at the handover rather than every frame of the return.
        /// </summary>
        private bool _returningHome = true;

        public RockController ActiveRock => _active;

        /// <summary>A flat stone, sized once so the rock and the aim ghost cannot differ.</summary>

        private void Update()
        {
            EnsureBound();

            // Unconditional, where this used to be gated on _active != null. The rock is
            // destroyed at the end of its despawn delay, and that used to cut the camera's
            // return off wherever it had got to — leaving the shot subtly wrong until the
            // next throw yanked it away. FollowActiveRock handles a null rock by heading
            // home, so letting it run every frame is what actually finishes the move.
            FollowActiveRock();
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
                _preview.ThrowOrigin.position, message.swingSpeed, attitude, message.spinRate, constants, tuning);

            _active = CreateRockObject(rock, constants, message.playerId);
            _active.Launch(launch, constants, tuning, message.playerId);
        }

        private void FollowActiveRock()
        {
            if (_camera == null) return;

            // IsSinking as well as IsFlying, deliberately. Gating on IsFlying alone meant
            // the camera turned for home on the exact frame the stone stopped moving — so
            // the sink it exists to show played entirely off-frame, and a failed throw read
            // as the screen glitching rather than as a stone going under.
            if (_active != null && (_active.IsFlying || _active.IsSinking))
            {
                _returningHome = false;

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

            // The chase leaves _cameraVelocity holding the full follow speed — tens of m/s
            // down +Z on a long throw. Feeding that into the return as its initial velocity
            // carried the camera FORWARD before it doubled back: the "jerk". Zeroing it once
            // on the handover is the whole fix, and it must be once rather than every frame
            // or SmoothDamp can never build up a return.
            if (!_returningHome)
            {
                _returningHome = true;
                _cameraVelocity = Vector3.zero;
            }

            _camera.transform.position = Vector3.SmoothDamp(
                _camera.transform.position, _cameraHome, ref _cameraVelocity, cameraFollowSmoothing * 2f);

            // Matched to the position curve rather than a raw Time.deltaTime * k slerp,
            // which is framerate-dependent and settles on a different schedule — the camera
            // used to arrive facing the wrong way for a moment after it had stopped moving.
            _camera.transform.rotation = Quaternion.Slerp(
                _camera.transform.rotation, _cameraHomeRotation,
                1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(cameraFollowSmoothing * 2f, 0.0001f)));
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
        private static RockController CreateRockObject(RockDefinition rock, SkipConstants constants, string playerId)
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

            var baseTint = rock != null ? rock.Tint : Color.gray3;
            var playerColour = PlayerColours.HasInstance ? PlayerColours.Instance.For(playerId) : Color.white;

            // Taken as a parameter and applied here rather than re-tinted after the object
            // exists, so the stone is never visibly the wrong colour for a frame between
            // spawning and being claimed — see PlayerColours.StoneColourFor, the same blend
            // the shore stone and the aim ghost use.
            var tint = PlayerColours.HasInstance
                ? PlayerColours.Instance.StoneColourFor(playerId, baseTint)
                : baseTint;

            go.GetComponent<MeshRenderer>().material =
                PlaceholderArt.Create(tint, PlaceholderArt.StoneQueue);

            var controller = go.AddComponent<RockController>();

            // After the controller, because the trail reads IsFlying and Speed off it. The
            // same constants the throw is about to be simulated with are handed over so the
            // trail's speed thresholds cannot drift from the launch speed they scale
            // against — see TrailTuning's remarks on why they are fractions. The player's
            // full colour goes to the trail rather than the blended stone tint — see
            // RockTrail.ApplySpeedTint's remarks on why the trail has nothing to preserve.
            RockTrail.Attach(go, constants, playerColour);

            return controller;
        }
    }
}
