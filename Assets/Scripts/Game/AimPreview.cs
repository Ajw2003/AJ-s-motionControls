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
    /// Smoothing is not polish here, it is what makes the feature work: AIM frames
    /// arrive at only ~20Hz over a jittery connection, so applying them raw looks
    /// choppy. Values are smoothed toward their targets every frame instead.
    ///
    /// The arc comes from SkipPhysics.Predict — the same solver the live throw uses,
    /// so what a player sees is what they get. It runs at a nominal power because the
    /// real power is unknown until release; the arc teaches the ~20 degree angle
    /// rather than promising an exact landing spot.
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

        [Tooltip("Nominal spin for the arc, same reasoning as preview power — actual spin " +
                 "is only known at release. Must be roughly what real throws produce: spin " +
                 "buys angle tolerance, so predicting with zero spin draws a far harsher " +
                 "throw than the player will actually get. Measured throws land near 1000.")]
        [SerializeField] private float previewSpin = 800f;
        [SerializeField] private float smoothTime = 0.08f;
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
        private LineRenderer _line;
        private Transform _ghost;

        private readonly List<Vector3> _path = new(512);

        private int _lastSeq = -1;
        private float _lastAimTime = -999f;

        private float _targetAim, _targetAttack, _targetRoll, _targetWindup;
        private float _curAim, _curAttack, _curRoll, _curWindup;
        private float _velAim, _velAttack, _velRoll, _velWindup;

        private void Awake()
        {
            if (tuning == null) tuning = SkipTuning.CreateDefault();
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
            _curAim = Mathf.SmoothDampAngle(_curAim, _targetAim, ref _velAim, smoothTime);
            _curAttack = Mathf.SmoothDampAngle(_curAttack, _targetAttack, ref _velAttack, smoothTime);
            _curRoll = Mathf.SmoothDampAngle(_curRoll, _targetRoll, ref _velRoll, smoothTime);
            _curWindup = Mathf.SmoothDamp(_curWindup, _targetWindup, ref _velWindup, smoothTime);

            _line.enabled = true;
            _ghost.gameObject.SetActive(HasSignal);

            if (!HasSignal) return;

            UpdateGhost();
            UpdateArc();
        }

        private void EnsureBound()
        {
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

            // Cheap ordering guard: TCP preserves order per socket, but a reconnect can
            // interleave, and a stale frame overwriting a newer one looks like a stutter.
            
            if (aim.playerId != _lastAimPlayer)
            {
                _lastAimPlayer = aim.playerId;
                _lastSeq = -1;
            }
            if (aim.seq <= _lastSeq) return;
            _lastSeq = aim.seq;

            _targetAim = aim.aimDeg;
            _targetAttack = aim.attackAngleDeg;
            _targetRoll = aim.rollDeg;
            _targetWindup = aim.windup;
            _lastAimTime = Time.time;
        }

        private void UpdateArc()
        {
            var launch = SkipPhysics.CreateLaunch(
                throwOrigin.position, Vector3.forward,
                previewPower, 1f,
                -_curAim, -_curAttack, previewSpin,
                RockStats.Default, tuning);

            var final = SkipPhysics.Predict(launch, RockStats.Default, tuning, _path);

            PredictedSkips = final.SkipCount;
            PredictedDistance = Vector3.Distance(throwOrigin.position, final.Position);

            _line.positionCount = _path.Count;
            _line.SetPositions(_path.ToArray());

            // Green when the angle is working, red when the stone is going to plough in.
            var health = Mathf.Clamp01(PredictedSkips / 5f);
            var colour = Color.Lerp(new Color(1f, 0.3f, 0.35f), new Color(0.35f, 1f, 0.6f), health);
            _line.startColor = colour;
            _line.endColor = new Color(colour.r, colour.g, colour.b, 0.15f);
        }

        private void UpdateGhost()
        {
            _ghost.position = throwOrigin.position;
            // Pitch by the stone's face angle, yaw by aim, roll by wrist tilt.
            _ghost.rotation = Quaternion.Euler(-_curAttack, -_curAim, _curRoll);

            var scale = Mathf.Lerp(1f, 1.35f, _curWindup);
            _ghost.localScale = new Vector3(0.45f * scale, 0.08f * scale, 0.45f * scale);
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
            _line.material = CreateUnlitMaterial(Color.white);
            _line.enabled = false;
        }

        private void BuildGhost()
        {
            var ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ghost.name = "GhostRock";
            Destroy(ghost.GetComponent<Collider>());
            ghost.GetComponent<MeshRenderer>().material = CreateUnlitMaterial(new Color(0.9f, 0.92f, 1f));
            _ghost = ghost.transform;
            _ghost.SetParent(transform, false);
        }

        private void BuildPlaceholderScene()
        {
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "PlaceholderWater";
            water.transform.position = new Vector3(0f, tuning.WaterY, 60f);
            water.transform.localScale = new Vector3(12f, 1f, 20f);
            Destroy(water.GetComponent<Collider>());
            water.GetComponent<MeshRenderer>().material = CreateUnlitMaterial(new Color(0.06f, 0.16f, 0.28f));

            var camera = Camera.main;
            if (camera == null) return;

            camera.transform.position = new Vector3(0, 4.5f, -5);
            camera.transform.LookAt(new Vector3(0f, 0f, 14f));
            camera.backgroundColor = new Color(0.05f, 0.09f, 0.15f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.farClipPlane = 500f;
        }

        /// <summary>Shader.Find is editor-friendly; these are scaffolding visuals only.</summary>
        private static Material CreateUnlitMaterial(Color colour)
        {
            var shader = Shader.Find("Sprites/Default")
                      ?? Shader.Find("Unlit/Color")
                      ?? Shader.Find("Standard");

            return new Material(shader) { color = colour };
        }
    }
}
