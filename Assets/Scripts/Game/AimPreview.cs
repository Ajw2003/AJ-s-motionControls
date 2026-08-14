using System.Collections.Generic;
using Managers;
using Net;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Live trajectory preview on the TV while a player aims.
    /// </summary>
    /// <remarks>
    /// Two things on screen: a ghost stone that matches the phone's rotation exactly, and
    /// an arc showing roughly where a throw from that angle would go.
    ///
    /// Smoothing is not polish here, it is what makes the feature work: AIM frames arrive
    /// at only ~20Hz over a jittery connection, so applying them raw looks choppy. One
    /// Slerp toward the last received attitude every frame is the whole of it — it used
    /// to be four SmoothDampAngle calls over four separate scalars, which is what a
    /// three-angle wire format forces you into.
    ///
    /// The arc comes from SkipPhysics.Predict — the same solver the live throw uses, so
    /// what a player sees is what they get. It runs at a nominal power because the real
    /// power is unknown until release; the arc teaches the angle, not an exact landing
    /// spot. There used to be a per-player learned-power model here trying to close that
    /// gap; it made the arc's meaning depend on throw history, which is worse than it
    /// simply being approximate and obviously so.
    ///
    /// This never publishes gameplay events. Prediction is speculative by definition.
    /// </remarks>
    public class AimPreview : MonoBehaviour
    {
        [Header("Setup")]
        [Tooltip("Leave empty to use a runtime default.")]
        [SerializeField] private SkipTuning tuning;
        [SerializeField] private Transform throwOrigin;

        [Tooltip("Spawn placeholder water and frame the camera. Scaffolding until the real lake exists.")]
        [SerializeField] private bool createPlaceholderScene = true;

        [Header("Feel")]
        [Tooltip("Power the arc is drawn at. Real power is unknown until release.")]
        [Range(0f, 1f)] [SerializeField] private float previewPower = 0.7f;

        [Tooltip("How long the ghost takes to catch up to a new aim frame. This is the " +
                 "20Hz-to-smooth conversion; it is not meant to lag perceptibly.")]
        [SerializeField] private float attitudeSmoothing = 18f;

        [Tooltip("Hide the preview after this long with no aim frames — surfaces a dropped phone.")]
        [SerializeField] private float staleAfterSeconds = 0.5f;

        public int PredictedSkips { get; private set; }
        public float PredictedDistance { get; private set; }
        public bool HasSignal { get; private set; }

        /// <summary>Shared with RockLauncher so preview and live throw use one rig.</summary>
        public Transform ThrowOrigin => throwOrigin;
        public SkipTuning Tuning => tuning;

        private string _lastAimPlayer;

        private RelayClient _client;
        private RockLauncher _launcher;
        private LineRenderer _line;
        private Transform _ghost;

        // Sized from the tuning in Awake rather than left at a literal: PredictSeconds /
        // FixedStep is about 1100 points, so a hardcoded 512 quietly grew and reallocated
        // on the first arc of every session.
        private List<Vector3> _path = new(512);

        // LineRenderer.SetPositions wants an array, and List.ToArray() allocates one every
        // time. At 60fps with a 1100-step prediction that is a lot of garbage, which shows
        // up as periodic collection pauses — the "choppy sometimes" that no amount of
        // network smoothing could fix, because it was never the network.
        private Vector3[] _pathBuffer = new Vector3[0];

        private int _lastSeq = -1;
        private float _lastAimTime = -999f;

        /// <summary>Where the phone is now, and where the ghost has smoothed to.</summary>
        private Quaternion _targetAttitude = Quaternion.identity;
        private Quaternion _curAttitude = Quaternion.identity;

        private float _targetSpin, _curSpin;

        /// <summary>
        /// The ghost's accumulated turn. Kept as an angle that only ever grows, never
        /// assigned from the spin — the rate may jump when a packet lands, but the angle
        /// must not, or a stone that was turning steadily appears to flick.
        /// </summary>
        private float _ghostSpinAngle;

        private void Awake()
        {
            // Serialized field, then the authored asset, then a bare runtime instance.
            // The middle step is the one that does the work: Bootstrap builds this
            // component with AddComponent, so nothing was ever dropped into the field
            // above and the whole game ran on C# field initializers — the numbers were
            // only reachable by recompiling.
            if (tuning == null) tuning = Resources.Load<SkipTuning>("SkipTuning");

            if (tuning == null)
            {
                tuning = SkipTuning.CreateDefault();
                Debug.LogWarning("[AimPreview] No SkipTuning at Resources/SkipTuning — " +
                                 "running on code defaults, which cannot be tuned without a recompile.");
            }

            _path = new List<Vector3>(tuning.MaxPredictSteps + 1);

            if (createPlaceholderScene) BuildPlaceholderScene();
            if (throwOrigin == null) throwOrigin = CreateOrigin();

            BuildLine();
            BuildGhost();
        }

        private void OnDestroy()
        {
            if (_client == null) return;
            _client.AimReceived -= OnAim;
        }

        private void Update()
        {
            EnsureBound();

            HasSignal = Time.time - _lastAimTime <= staleAfterSeconds;

            // Smooth every frame regardless of packet arrival — this is what turns
            // 20Hz of network data into something that reads as continuous motion.
            // Exponential rather than a fixed step so it is frame-rate independent.
            _curAttitude = Quaternion.Slerp(
                _curAttitude, _targetAttitude, 1f - Mathf.Exp(-attitudeSmoothing * Time.deltaTime));

            _curSpin = Mathf.Lerp(
                _curSpin, _targetSpin, 1f - Mathf.Exp(-attitudeSmoothing * Time.deltaTime));

            // Nothing to predict while a stone is already in the air, and leaving the arc
            // up through the throw means the line the player was aiming with is still
            // drawn over the result. Both the tooltip above and the debug HUD used to
            // claim this happened; it did not, because _line.enabled was set true
            // unconditionally just before the early return below.
            var flying = _launcher != null && _launcher.ActiveRock != null && _launcher.ActiveRock.IsFlying;
            var visible = HasSignal && !flying;

            _line.enabled = visible;
            _ghost.gameObject.SetActive(visible);

            if (!visible) return;

            UpdateGhost();
            UpdateArc();
        }

        private void EnsureBound()
        {
            if (_launcher == null) _launcher = GetComponent<RockLauncher>();

            // HasInstance, not Instance — Instance would create a RelayClient, and a
            // preview that conjures its own data source hides a real failure.
            if (_client != null || !RelayClient.HasInstance) return;

            _client = RelayClient.Instance;
            _client.AimReceived += OnAim;
        }

        private void OnAim(AimMessage aim)
        {
            // Only the active player steers the preview; a stray frame from someone
            // else's phone must not yank the arc around.
            var active = TurnManager.HasInstance ? TurnManager.Instance.ActivePlayer : null;
            if (!string.IsNullOrEmpty(active) && aim.playerId != active) return;

            if (aim.playerId != _lastAimPlayer)
            {
                _lastAimPlayer = aim.playerId;
                _lastSeq = -1;

                // Start the new player's stone from rest rather than wherever the last
                // player's happened to have turned to.
                _ghostSpinAngle = 0f;
                _curSpin = 0f;
                _curAttitude = PhoneAttitude.ToUnity(aim.qw, aim.qx, aim.qy, aim.qz);
            }

            // Cheap ordering guard: TCP preserves order per socket, but a reconnect can
            // interleave, and a stale frame overwriting a newer one looks like a stutter.
            if (aim.seq <= _lastSeq) return;
            _lastSeq = aim.seq;

            // Raw, always. An earlier version froze these once the phone started moving,
            // on the theory that a swinging phone no longer means "where I am aiming" —
            // but the throw now fires on the phone stopping, and a throwing motion ends
            // pointing where it was thrown. The stop sample IS the aim, so the preview
            // tracking live orientation is what agrees with the stone.
            _targetAttitude = PhoneAttitude.ToUnity(aim.qw, aim.qx, aim.qy, aim.qz);

            // Through the same clamp the live stone uses, so a flick fast enough to alias
            // is drawn at the same rate in the preview as it will be in flight.
            _targetSpin = RockController.VisualSpin(aim.spinRate);
            _lastAimTime = Time.time;
        }

        private void UpdateArc()
        {
            var launch = SkipPhysics.CreateLaunch(
                throwOrigin.position, previewPower, _curAttitude, _curSpin, tuning);

            var final = SkipPhysics.Predict(launch, tuning, _path);

            PredictedSkips = final.SkipCount;
            PredictedDistance = Vector3.Distance(throwOrigin.position, final.Position);

            // Exact length, because SetPositions reads the whole array. Reallocating only
            // when the point count actually changes turns a per-frame allocation into a
            // rare one — arcs of the same shape reuse the same buffer.
            if (_pathBuffer.Length != _path.Count) _pathBuffer = new Vector3[_path.Count];
            _path.CopyTo(_pathBuffer);

            _line.positionCount = _path.Count;
            _line.SetPositions(_pathBuffer);
        }

        private void UpdateGhost()
        {
            _ghost.position = throwOrigin.position;

            // Turned by how fast the player is actually rotating the phone right now, so
            // winding up harder visibly spins the stone faster and holding steady leaves
            // it still.
            _ghostSpinAngle += _curSpin * Time.deltaTime;

            // Attitude on the left, spin on the right — the same composition the live
            // stone uses, so the ghost and the rock cannot turn differently. And the same
            // unmodified phone rotation: hold the phone flat and this lies flat, even
            // though the water wants ~20 degrees. That offset belongs in SkipPhysics.
            _ghost.rotation = _curAttitude * Quaternion.Euler(0f, _ghostSpinAngle, 0f);
        }

        // ---------------------------------------------------------------- scaffolding

        private Transform CreateOrigin()
        {
            var origin = new GameObject("ThrowOrigin").transform;
            origin.position = new Vector3(0f, 0f, 0f);
            return origin;
        }

        private void BuildLine()
        {
            var go = new GameObject("AimArc");
            go.transform.SetParent(transform, false);

            _line = go.AddComponent<LineRenderer>();
            _line.widthMultiplier = 0.12f;
            _line.numCapVertices = 4;
            _line.useWorldSpace = true;
            _line.material = PlaceholderArt.Create(Color.white, PlaceholderArt.ArcQueue);

            var colour = new Color(0.55f, 0.85f, 1f);
            _line.startColor = colour;
            _line.endColor = new Color(colour.r, colour.g, colour.b, 0.15f);
            _line.enabled = false;
        }

        private void BuildGhost()
        {
            // Same primitive and scale as the live stone — see RockLauncher for why a box
            // rather than a scaled sphere.
            var ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ghost.name = "GhostRock";
            Destroy(ghost.GetComponent<Collider>());
            ghost.transform.localScale = RockLauncher.StoneScale;
            ghost.GetComponent<MeshRenderer>().material =
                PlaceholderArt.Create(new Color(0.9f, 0.92f, 1f), PlaceholderArt.StoneQueue);

            _ghost = ghost.transform;
            _ghost.SetParent(transform, false);
        }

        private void BuildPlaceholderScene()
        {
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "PlaceholderWater";
            water.transform.position = new Vector3(0f, tuning.WaterY, 60f);
            water.transform.localScale = new Vector3(40f, 1f, 40f);
            Destroy(water.GetComponent<Collider>());

            water.GetComponent<MeshRenderer>().material =
                PlaceholderArt.Create(new Color(0.06f, 0.16f, 0.28f), PlaceholderArt.WaterQueue);

            // Parented like the arc and the ghost so it survives with this component,
            // which Bootstrap marks DontDestroyOnLoad. Left at the scene root it was
            // destroyed by the first scene load and never rebuilt — Awake only runs once.
            water.transform.SetParent(transform, true);

            var camera = Camera.main;
            if (camera == null) return;

            camera.transform.position = new Vector3(0, 4.5f, -5);
            camera.transform.LookAt(new Vector3(0f, 0f, 14f));
            camera.backgroundColor = new Color(0.05f, 0.09f, 0.15f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.farClipPlane = 500f;
        }
    }
}
