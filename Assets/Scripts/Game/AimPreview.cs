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
        [Tooltip("Floor on the power the arc is drawn at, for a player holding still to " +
                 "aim. Winding up raises it above this; it is never drawn below it.")]
        [Range(0f, 1f)] [SerializeField] private float previewPower = 0.45f;

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

        /// <summary>
        /// How hard the phone is being swung right now, 0..1, and the smoothed value the
        /// arc is drawn at.
        /// </summary>
        /// <remarks>
        /// The same quantity the throw sends, normalised the same way — see the live
        /// tracker in ThrowController. An earlier version sent a separate `windup` derived
        /// from peak acceleration while the throw derived power from swing speed, so the
        /// arc and the stone were driven by two different notions of "how hard" and
        /// disagreed by construction.
        /// </remarks>
        private float _targetPower, _curPower;

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

            _curPower = Mathf.Lerp(
                _curPower, _targetPower, 1f - Mathf.Exp(-attitudeSmoothing * Time.deltaTime));

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

                // Snap to the new player's pose rather than sweeping across from wherever
                // the last one left the ghost.
                _curPower = 0f;
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

            _targetPower = Mathf.Clamp01(aim.power);
            _lastAimTime = Time.time;
        }

        private void UpdateArc()
        {
            // Drawn at how hard the phone is being swung right now, floored at the nominal.
            //
            // The floor is what makes this usable. Real power is genuinely unknowable
            // before release, so a player holding still to line a shot up reads as zero
            // effort — and an arc that collapses to a two-metre dribble every time they
            // stop moving is not an aiming guide. So the arc shows at least a nominal
            // throw, and winding up extends it from there.
            //
            // It used to be pinned at the nominal outright, which made the HUD's
            // "predicted N skips over M m" mathematically incapable of responding to how
            // hard anyone threw. That was the 13-skip ceiling.
            var drawPower = Mathf.Max(previewPower, _curPower);

            // Zero spin: it is visual only and has no effect whatsoever on the path, so
            // feeding the arc a spin rate would imply it mattered.
            var launch = SkipPhysics.CreateLaunch(
                throwOrigin.position, drawPower, _curAttitude, 0f, tuning);

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

            // The phone IS the rock. Nothing is added on top.
            //
            // There used to be an accumulated spin angle here, and it was wrong twice over.
            // It double-counted: _curAttitude already contains every rotation the phone
            // made, wrist twist included, so spinning the phone turned the ghost twice. And
            // it only ever grew, off an unsigned three-axis rotation magnitude, so merely
            // turning to aim ratcheted the ghost's yaw permanently and it never came back.
            // The stone appeared to increment rather than to match the phone.
            //
            // The thrown stone still spins, because once it leaves the hand there is no
            // phone left to track — see RockController.
            _ghost.rotation = _curAttitude;
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
            var rock = Resources.Load<GameObject>("RockObject");
            var ghost = Instantiate(rock);
            ghost.name = "GhostRock";
            Destroy(ghost.GetComponent<Collider>());
            var grey = Color.gray3;
            ghost.GetComponent<MeshRenderer>().material =
                PlaceholderArt.Create(grey, PlaceholderArt.StoneQueue);

            _ghost = ghost.transform;
            _ghost.SetParent(transform, false);
        }

        private void BuildPlaceholderScene()
        {
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "PlaceholderWater";
            water.transform.position = new Vector3(0f, tuning.WaterY, 60f);
            water.transform.localScale = new Vector3(200f, 1f, 200f);
            Destroy(water.GetComponent<Collider>());

            
            var waterMaterial = Resources.Load<Material>("RockSkipping_WaterRipple");
            if (waterMaterial == null)
            {
                waterMaterial = PlaceholderArt.CreateWater(new Color(0.06f, 0.16f, 0.28f));
            }
            water.GetComponent<MeshRenderer>().material = waterMaterial;

            // Handed the material directly rather than looked up — this method is the only
            // thing that ever holds the water, and it drops the reference on exit.
            water.AddComponent<WaterRipples>().Bind(waterMaterial, tuning);

            // Parented like the arc and the ghost so it survives with this component,
            // which Bootstrap marks DontDestroyOnLoad. Left at the scene root it was
            // destroyed by the first scene load and never rebuilt — Awake only runs once.
            water.transform.SetParent(transform, true);

            var camera = Camera.main;
            if (camera == null) return;

            // One colour for the empty sky and for what the lake fades into, so the two
            // cannot disagree — see the fog block below, where the hard horizon is fixed.
            var horizon = new Color(0.05f, 0.09f, 0.15f);

            camera.transform.position = new Vector3(0, 2f, -3);
            camera.transform.LookAt(new Vector3(0f, 0f, 4f));
            camera.backgroundColor = horizon;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.farClipPlane = 1000f;

            // The lake is 2000 units across (a 10-unit Plane scaled by 200) but the far
            // clip is 1000, so the far half is cut off by the frustum and the cut shows as
            // a hard line across the middle of the screen with flat background above it.
            //
            // Fogging to the background colour rather than moving the clip plane out:
            // pushing the far plane past 2000 would double the depth range and spend real
            // depth precision on water nobody can make out anyway, and the plane would
            // still end in a hard edge — just further away. Fading it into the same colour
            // the sky is cleared to means the edge is gone rather than relocated.
            //
            // Ends well inside the clip plane so the fade is complete before the geometry
            // is cut, which is the whole point.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = horizon;
            RenderSettings.fogStartDistance = 120f;
            RenderSettings.fogEndDistance = 600f;
        }
    }
}
