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
        [Tooltip("Lag deliberately traded against jitter. SmoothDamp trails its target by " +
                 "roughly this long, which during a fast swing is degrees of error — so " +
                 "this wants to be only just large enough to hide packet jitter, no more. " +
                 "Sized against a 60Hz aim stream; raise it if the arc looks noisy.")]
        [SerializeField] private float smoothTime = 0.035f;
        [Tooltip("Hide the preview after this long with no aim frames — surfaces a dropped phone. " +
                 "Also how long the arc lingers after a throw, which is the only chance the " +
                 "player gets to compare the line they were shown against the rock that left.")]
        [SerializeField] private float staleAfterSeconds = 1f;

        public int PredictedSkips { get; private set; }
        public float PredictedDistance { get; private set; }
        public bool HasSignal { get; private set; }

        /// <summary>Shared with RockLauncher so preview and live throw use one rig.</summary>
        public Transform ThrowOrigin => throwOrigin;
        public SkipTuning Tuning => tuning;

        /// <summary>
        /// Which way "straight ahead" points, at aimDeg = 0.
        /// </summary>
        /// <remarks>
        /// Derived from the aiming camera rather than being world +Z, because straight
        /// has to mean straight *to the player looking at the TV*. With a hardcoded
        /// forward, a camera framed even slightly off-axis makes a dead-centre throw
        /// visibly drift to one side, and no amount of correct phone aiming fixes it —
        /// the player just learns the game is lying to them.
        ///
        /// Flattened onto the horizontal plane: only the heading matters here, since
        /// the launch pitch comes from SkipTuning.
        ///
        /// Captured once, from the aiming view. Reading it live would swing the arc
        /// around as the follow camera chases a rock downrange.
        /// </remarks>
        public Vector3 ThrowForward { get; private set; } = Vector3.forward;

        private RelayClient _client;
        private LineRenderer _line;
        private Transform _ghost;

        private readonly List<Vector3> _path = new(512);

        private int _lastSeq = -1;
        private string _lastAimPlayer;
        private float _lastAimTime = -999f;

        // Set while the arc is showing a throw that already happened rather than a
        // prediction of one that has not.
        private bool _showingThrow;
        private float _thrownPower, _thrownTiming, _thrownSpin;

        private float _targetAim, _targetAttack, _targetRoll, _targetWindup;
        private float _curAim, _curAttack, _curRoll, _curWindup;
        private float _velAim, _velAttack, _velRoll, _velWindup;

        private void Awake()
        {
            if (tuning == null) tuning = SkipTuning.CreateDefault();
            if (createPlaceholderScene) BuildPlaceholderScene();
            if (throwOrigin == null) throwOrigin = CreateOrigin();

            // After BuildPlaceholderScene, which is what frames the camera.
            RefreshThrowForward();

            BuildLine();
            BuildGhost();
        }

        private void OnDestroy()
        {
            if (_client == null) return;
            _client.AimReceived -= OnAim;
            _client.ThrowReceived -= OnThrow;
        }

        /// <summary>
        /// Redraw the arc from the throw that actually happened, and hold it there while
        /// it fades.
        /// </summary>
        /// <remarks>
        /// The throw is computed from the last sensor sample before release, while the
        /// arc was drawn from the most recent AIM frame smoothed over several more — so
        /// at the instant of release they are close but not identical. Snapping here
        /// means the final thing the player sees is the line the rock is on, not the
        /// line it was very nearly on, and it makes a mismatch between preview and
        /// throw visible instead of something to squint at frame by frame.
        /// </remarks>
        private void OnThrow(ThrowMessage message)
        {
            _targetAim = _curAim = message.aimDeg;
            _targetAttack = _curAttack = message.attackAngleDeg;
            _targetRoll = _curRoll = message.rollDeg;
            _velAim = _velAttack = _velRoll = 0f;

            _thrownPower = message.power;
            _thrownTiming = message.timingQuality;
            _thrownSpin = message.spinRate;
            _showingThrow = true;

            _lastAimTime = Time.time;
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

            _line.enabled = HasSignal;
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
            _client.ThrowReceived += OnThrow;
        }

        private void OnAim(AimMessage aim)
        {
            // Only the active player steers the preview; a stray frame from someone
            // else's phone must not yank the arc around.
            var active = TurnManager.HasInstance ? TurnManager.Instance.ActivePlayer : null;
            if (!string.IsNullOrEmpty(active) && aim.playerId != active) return;

            // The sequence counter is per-phone and only advances while that phone is
            // gripping, so counts from different players are not comparable. Comparing
            // them against one shared value meant whoever had gripped the most locked
            // everyone else out: their successors' frames all arrived with a lower seq
            // and were silently dropped, so only one player in a session ever got an
            // aim preview. Reset the guard whenever the aiming player changes.
            if (aim.playerId != _lastAimPlayer)
            {
                _lastAimPlayer = aim.playerId;
                _lastSeq = -1;
            }

            // Within one player, this is still the ordering guard it was meant to be:
            // TCP preserves order per socket, but a reconnect can interleave, and a
            // stale frame overwriting a newer one looks like a stutter.
            if (aim.seq <= _lastSeq) return;
            _lastSeq = aim.seq;

            // First frame of a new aim, rather than a continuation of one in progress.
            // A throw still on screen also counts: the next gesture has begun, so the
            // arc must stop showing the previous throw and start tracking again.
            var reacquired = _showingThrow || Time.time - _lastAimTime > staleAfterSeconds;
            _showingThrow = false;

            _targetAim = aim.aimDeg;
            _targetAttack = aim.attackAngleDeg;
            _targetRoll = aim.rollDeg;
            _targetWindup = aim.windup;
            _lastAimTime = Time.time;

            if (!reacquired) return;

            // Nothing was carried over from the last throw, so start exactly on the new
            // values instead of easing across from them. Smoothing in from a stale
            // target meant every turn opened by sweeping the arc over from wherever the
            // PREVIOUS player had been aiming, and spent the first fraction of a second
            // — a real share of the time the preview is on screen at all — showing a
            // line nobody had asked for.
            _curAim = _targetAim;
            _curAttack = _targetAttack;
            _curRoll = _targetRoll;
            _curWindup = _targetWindup;
            _velAim = _velAttack = _velRoll = _velWindup = 0f;
        }

        private void UpdateArc()
        {
            // While predicting, power and timing are unknown, so the arc is drawn at a
            // nominal effort and promises a line rather than a landing spot. Once the
            // throw has actually happened they are known exactly, so the fading arc
            // switches to the real thing and traces the rock's own path.
            var launch = SkipPhysics.CreateLaunch(
                throwOrigin.position, ThrowForward,
                _showingThrow ? _thrownPower : previewPower,
                _showingThrow ? _thrownTiming : 1f,
                _curAim, _curAttack,
                _showingThrow ? _thrownSpin : previewSpin,
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

            // Built on ThrowForward, not on world axes, so the ghost tilts about the
            // same heading the arc leaves along. Then pitch by the stone's face angle,
            // yaw by aim, roll by wrist tilt.
            _ghost.rotation = Quaternion.LookRotation(ThrowForward, Vector3.up)
                            * Quaternion.Euler(-_curAttack, _curAim, _curRoll);

            var scale = Mathf.Lerp(1f, 1.35f, _curWindup);
            _ghost.localScale = new Vector3(0.45f * scale, 0.08f * scale, 0.45f * scale);
        }

        // ---------------------------------------------------------------- scaffolding

        /// <summary>
        /// Re-reads "straight ahead" from the main camera. Call after moving the aiming
        /// camera; the real lake scene will want this once its framing is authored.
        /// </summary>
        public void RefreshThrowForward()
        {
            var camera = Camera.main;
            if (camera == null) return;

            var flattened = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);

            // A camera pointing straight down has no heading to inherit; keep the last
            // good value rather than collapsing the aim to a zero vector.
            if (flattened.sqrMagnitude < 1e-4f) return;

            ThrowForward = flattened.normalized;
        }

        private Transform CreateOrigin()
        {
            var origin = new GameObject("ThrowOrigin").transform;
            origin.position = new Vector3(0f, 1.2f, 0f);
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
            // Square, and starting at the player's feet rather than floating ahead of
            // them. A Unity plane is 10 units per unit of scale, so this is 200x200.
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "PlaceholderWater";
            water.transform.position = new Vector3(0f, tuning.WaterY, 100f);
            water.transform.localScale = new Vector3(20f, 1f, 20f);
            Destroy(water.GetComponent<Collider>());
            water.GetComponent<MeshRenderer>().material = CreateUnlitMaterial(new Color(0.06f, 0.16f, 0.28f));

            var camera = Camera.main;
            if (camera == null) return;

            // Square on to the lake, looking straight down +Z with no yaw, pitch or
            // roll. Any rotation here is a rotation the player has to unconsciously
            // correct for: a camera even a few degrees off-axis makes a dead-centre
            // throw visibly drift, and ThrowForward inherits whatever tilt is left in.
            // Identity rotation is what makes "straight" mean the same thing to the
            // solver, the preview, and the person holding the phone.
            camera.transform.SetPositionAndRotation(
                new Vector3(0f, 3.5f, -9f), Quaternion.identity);

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
