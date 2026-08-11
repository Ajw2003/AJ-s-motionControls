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

        [Tooltip("Skip re-predicting the arc while the aim has moved less than this, in " +
                 "degrees. Cheap when the player is holding steady, per-frame when they " +
                 "are not — which is the only time smoothness is visible.")]
        [SerializeField] private float arcRedrawThresholdDeg = 0.005f;

        [Tooltip("How fast the arc adapts to how hard this player actually throws. 0 keeps " +
                 "it at the nominal power for everyone, which is why the line used to " +
                 "consistently over- or undershoot depending on who was holding the phone.")]
        [Range(0f, 1f)] [SerializeField] private float learningRate = 0.45f;
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

        // LineRenderer.SetPositions wants an array, and List.ToArray() allocates one every
        // time. At 60fps with a 500-step prediction that was roughly 360 KB/s of garbage,
        // which shows up as periodic collection pauses — the "choppy sometimes" that no
        // amount of network smoothing could fix, because it was never the network.
        private Vector3[] _pathBuffer = new Vector3[0];

        private float _drawnAim, _drawnAttack, _drawnPower;

        /// <summary>
        /// What a given player's throws actually look like, so the arc can be drawn at
        /// their strength rather than a fixed nominal one.
        /// </summary>
        /// <remarks>
        /// Power genuinely cannot be known before the swing — it comes from peak
        /// acceleration, which has not happened yet. So the arc can never be exact. But
        /// drawing it at a constant 0.7 for everybody meant it was reliably wrong in a
        /// direction that depended on the player: a hard thrower always overshot the
        /// line, a gentle one always fell short, and neither could learn anything from
        /// it. Their own recent throws are a far better estimate than a constant, and
        /// converge within two or three attempts.
        ///
        /// Kept per player because a match has several, and they differ a lot.
        /// </remarks>
        private struct ThrowProfile
        {
            public float Power;
            public float Timing;
            public float Spin;
        }

        private readonly Dictionary<string, ThrowProfile> _profiles = new();

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
            _client.ThrowReceived -= OnThrow;
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

            if (!HasSignal)  return;

            UpdateGhost();

            // Every frame while anything is moving. An earlier version capped this at
            // 30Hz to save work, which traded the garbage-collection stutter for a
            // steady one — a line redrawn at half the display rate steps visibly. The
            // allocation was the real cost, so the fix is to skip only genuinely
            // redundant work: when the smoothed values have settled, the arc cannot
            // have changed, so there is nothing to recompute.
            if (HasAimMoved()) UpdateArc();
        }

        private bool HasAimMoved()
        {
            if (Mathf.Abs(Mathf.DeltaAngle(_curAim, _drawnAim)) > arcRedrawThresholdDeg) return true;
            if (Mathf.Abs(Mathf.DeltaAngle(_curAttack, _drawnAttack)) > arcRedrawThresholdDeg) return true;
            // Also redraw when the player changes, or after they throw and the profile moves.
            return _line.positionCount == 0 ||
                   !Mathf.Approximately(_drawnPower, ProfileForActivePlayer().Power);
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

        /// <summary>
        /// Learn what this player's throws are actually like. Read-only as far as the game
        /// is concerned — this never launches anything, it only informs the next preview.
        /// </summary>
        private void OnThrow(ThrowMessage thrown)
        {
            if (string.IsNullOrEmpty(thrown.playerId) || learningRate <= 0f) return;

            var known = _profiles.TryGetValue(thrown.playerId, out var profile);
            var rate = known ? learningRate : 1f; // first throw replaces the guess outright

            _profiles[thrown.playerId] = new ThrowProfile
            {
                Power = Mathf.Lerp(known ? profile.Power : previewPower, thrown.power, rate),
                Timing = Mathf.Lerp(known ? profile.Timing : 1f, thrown.timingQuality, rate),
                Spin = Mathf.Lerp(known ? profile.Spin : previewSpin, thrown.spinRate, rate),
            };
        }

        /// <summary>This player's learned throw, or the nominal one until they have thrown.</summary>
        private ThrowProfile ProfileForActivePlayer()
        {
            var active = TurnManager.HasInstance ? TurnManager.Instance.ActivePlayer : null;

            if (!string.IsNullOrEmpty(active) && _profiles.TryGetValue(active, out var profile))
            {
                return profile;
            }

            return new ThrowProfile { Power = previewPower, Timing = 1f, Spin = previewSpin };
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

            // Raw, always. An earlier version froze these once windup passed a threshold,
            // on the theory that a swinging phone no longer means "where I am aiming" —
            // but the throw now fires on the phone stopping, and a throwing motion ends
            // pointing where it was thrown. The stop sample IS the aim, so the preview
            // tracking live orientation is what agrees with the stone.
            _targetAim = aim.aimDeg;
            _targetAttack = aim.attackAngleDeg;
            _targetRoll = aim.rollDeg;
            _targetWindup = aim.windup;
            _lastAimTime = Time.time;
        }

        private void UpdateArc()
        {
            // Drawn at what this player actually throws like, not a fixed nominal — see
            // ThrowProfile. Timing matters as much as power here: SkipPhysics scales launch
            // speed by both, so assuming perfect timing overshoots every real throw.
            var profile = ProfileForActivePlayer();

            _drawnAim = _curAim;
            _drawnAttack = _curAttack;
            _drawnPower = profile.Power;

            var launch = SkipPhysics.CreateLaunch(
                throwOrigin.position, Vector3.forward,
                profile.Power, profile.Timing,
                -_curAim, -_curAttack, profile.Spin,
                RockStats.Default, tuning);

            var final = SkipPhysics.Predict(launch, RockStats.Default, tuning, _path);

            PredictedSkips = final.SkipCount;
            PredictedDistance = Vector3.Distance(throwOrigin.position, final.Position);

            // Exact length, because SetPositions reads the whole array. Reallocating only
            // when the point count actually changes turns a per-frame allocation into a
            // rare one — arcs of the same shape reuse the same buffer.
            if (_pathBuffer.Length != _path.Count) _pathBuffer = new Vector3[_path.Count];
            _path.CopyTo(_pathBuffer);

            _line.positionCount = _path.Count;
            _line.SetPositions(_pathBuffer);

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
            _line.material = PlaceholderArt.Create(Color.white, PlaceholderArt.ArcQueue);
            _line.enabled = false;
        }

        private void BuildGhost()
        {
            var ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ghost.name = "GhostRock";
            Destroy(ghost.GetComponent<Collider>());
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

            var waterMaterial = PlaceholderArt.CreateWater(new Color(0.06f, 0.16f, 0.28f));
            water.GetComponent<MeshRenderer>().material = waterMaterial;

            // Handed the material directly rather than looking it up — this method is the
            // only thing that ever holds the water, and it drops the reference on exit.
            water.AddComponent<WaterRipples>().Bind(waterMaterial);

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
