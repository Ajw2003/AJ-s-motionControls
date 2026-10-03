using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using Managers;
using Net;
using UnityEngine;

namespace Game
{
    // Ghost follows the phone with one Slerp per frame; the arc comes from SkipPhysics.Predict at a
    // nominal power and never publishes events. doc-ref caf5 docs/4-systems/Presentation.md
    public class AimPreview : MonoBehaviour
    {
        [Header("Setup")]
        [Tooltip("Leave empty to use a runtime default.")]
        [SerializeField] private SkipTuning tuning;

        [Tooltip("Global simulation/forgiveness constants. Leave empty to use a runtime default.")]
        [SerializeField] private SkipConstants constants;

        [Tooltip("Arc colour ramp. Leave empty to use a runtime default.")]
        [SerializeField] private AimFeedbackTuning feedback;

        [SerializeField] private Transform throwOrigin;

        [Tooltip("Spawn placeholder water and frame the camera. Scaffolding until the real lake exists.")]
        [SerializeField] private bool createPlaceholderScene = true;

        [Header("Feel")]
        [Tooltip("Floor on the raw swing speed (m/s) the arc is drawn at, for a player " +
                 "holding still to aim. Winding up raises it above this; it is never drawn " +
                 "below it.")]
        [SerializeField] private float previewSwingSpeed = 2f;

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

        /// <summary>
        /// The active player's tuning, refreshed on TurnStartedEvent rather than resolved
        /// per frame. Falls back to whatever Awake resolved (SerializeField, then
        /// Resources/SkipTuning, then a bare default) until the first turn starts, and
        /// stays there if RockSelection or the catalog cannot resolve one — see OnTurnStarted.
        /// </summary>
        public SkipTuning Tuning => tuning;

        /// <summary>Global simulation/forgiveness constants — see the class field.</summary>
        public SkipConstants Constants => constants;

        /// <summary>
        /// The active player's rock, alongside <see cref="Tuning"/> — RockLauncher reads
        /// both from here rather than re-resolving, so the ghost and the live throw can
        /// never land on different rocks for the same turn.
        /// </summary>
        public RockDefinition CurrentRock { get; private set; }

        private string _lastAimPlayer;

        private RelayClient _client;
        private RockLauncher _launcher;
        private LineRenderer _line;
        private Transform _ghost;

        // Held rather than looked back off the renderers. Renderer.material INSTANCES on
        // read, so fetching it per frame to tint it would leak a fresh material every
        // frame — and the arc's material is written every frame for its colour.
        private Material _lineMaterial;
        private Material _ghostMaterial;

        /// <summary>
        /// Smoothed 0..1 arc quality, and a request to skip the smoothing for one frame.
        /// </summary>
        /// <remarks>
        /// The snap exists for the same reason the attitude snap in OnAim does: when the
        /// active player changes, sweeping from the last player's verdict to the new one
        /// draws a colour neither of them earned. It cannot be done in OnAim itself because
        /// the new player's quality is not known until the next prediction runs.
        /// </remarks>
        private float _curQuality;
        private bool _snapQuality = true;

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

        /// <summary>
        /// What <see cref="SetVisible"/> last wrote, so an unchanged frame writes nothing.
        /// </summary>
        /// <remarks>
        /// Starts false and BuildLine/BuildGhost both start hidden to match. A cache that
        /// disagrees with what is actually on screen is worse than no cache — it latches the
        /// wrong state permanently — so those two are the initialisation, not a coincidence.
        /// </remarks>
        private bool _isVisible;

        /// <summary>Rate-limits <see cref="LogRejectedAim"/> so a stuck rejection can't spam the Console.</summary>
        private float _nextRejectLogTime;

        /// <summary>Where the phone is now, and where the ghost has smoothed to.</summary>
        private Quaternion _targetAttitude = Quaternion.identity;
        private Quaternion _curAttitude = Quaternion.identity;

        /// <summary>
        /// Raw swing speed off the phone right now, m/s, and the smoothed value the arc is
        /// drawn at.
        /// </summary>
        /// <remarks>
        /// The same raw quantity the throw sends — see ThrowMessage.swingSpeed and
        /// SkipPhysics.CreateLaunch. No normalisation on either end: the phone measures a
        /// real speed and Unity converts it once, via SkipConstants.SpeedScale.
        /// </remarks>
        private float _targetSwingSpeed, _curSwingSpeed;

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

            if (constants == null) constants = SkipConstants.LoadOrWarn();

            // Same three steps for the same reason, and it matters more here rather than
            // less: these are look numbers, and a look number that needs a recompile does
            // not get tuned.
            if (feedback == null) feedback = Resources.Load<AimFeedbackTuning>("AimFeedbackTuning");

            if (feedback == null)
            {
                feedback = AimFeedbackTuning.CreateDefault();
                Debug.LogWarning("[AimPreview] No AimFeedbackTuning at Resources/AimFeedbackTuning — " +
                                 "arc colour is on code defaults, which cannot be tuned without a " +
                                 "recompile.");
            }

            // PredictSeconds is global now — every rock predicts the same horizon — so this
            // sizes once from SkipConstants rather than from the whole catalog's longest.
            _path = new List<Vector3>(constants.MaxPredictSteps + 1);

            if (createPlaceholderScene) BuildPlaceholderScene();
            if (throwOrigin == null) throwOrigin = CreateOrigin();

            BuildLine();
            BuildGhost();
            BuildShoreRocks();

            EventManager.Instance?.Subscribe<TurnStartedEvent>(this, OnTurnStarted);
            EventManager.Instance?.Subscribe<PlayerColourChangedEvent>(this, OnPlayerColourChanged);
            EventManager.Instance?.Subscribe<PlayerJoinedEvent>(this, OnPlayerJoined);
        }

        // Bound in Start, not Awake, because Bootstrap adds the launcher after this component; HasInstance, so a missing
        // RelayClient is loud rather than conjured. doc-ref 0fb4 docs/4-systems/Presentation.md
        private void Start()
        {
            _launcher = GetComponent<RockLauncher>();

            if (_launcher == null)
            {
                Debug.LogWarning("[AimPreview] No RockLauncher on this object — the arc will " +
                                 "stay up through a throw, drawn over its own result.");
            }

            if (!RelayClient.HasInstance)
            {
                Debug.LogError("[AimPreview] No RelayClient — no aim frames will ever arrive " +
                               "and the preview stays hidden for this session.");
                return;
            }

            _client = RelayClient.Instance;
            _client.AimReceived += OnAim;
        }

        private void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);

            // Before the unsubscribe, not after: the early return below is conditional and
            // anything placed under it is skipped for a preview that never bound a client.
            if (_lineMaterial != null) Destroy(_lineMaterial);
            if (_ghostMaterial != null) Destroy(_ghostMaterial);

            if (_client == null) return;
            _client.AimReceived -= OnAim;
        }

        // Only the ghost's mesh and scale change per turn; MeshRotation stays unapplied because rotation is overwritten
        // from the phone every frame. doc-ref 1a25 docs/4-systems/Presentation.md
        private void OnTurnStarted(TurnStartedEvent e)
        {
            if (!RockSelection.HasInstance) return;

            var selection = RockSelection.Instance;
            var rock = selection.For(e.PlayerId);
            if (rock == null) return; // empty catalog — keep the existing fallback tuning/ghost

            CurrentRock = rock;

            var resolved = selection.TuningFor(e.PlayerId);
            if (resolved != null) tuning = resolved;

            if (rock.Mesh == null || _ghost == null) return;

            var filter = _ghost.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = rock.Mesh;

            _ghost.localScale = rock.LocalScale;

            TintGhost(e.PlayerId);
        }

        /// <summary>
        /// Paints the ghost in the aiming player's colour — the same blend the shore stone
        /// and the thrown stone use, so the rock a player is about to throw already looks
        /// like the one that leaves their hand.
        /// </summary>
        private void TintGhost(string playerId)
        {
            if (_ghostMaterial == null || !PlayerColours.HasInstance) return;

            var tint = CurrentRock != null ? CurrentRock.Tint : Color.gray3;
            _ghostMaterial.color = PlayerColours.Instance.StoneColourFor(playerId, tint);
        }

        /// <summary>Re-tints a live ghost the instant its player's colour changes, rather
        /// than waiting for their next turn to start.</summary>
        /// <remarks>
        /// Checked against TurnManager.ActivePlayer, not _lastAimPlayer: the ghost is
        /// already tinted for the new player the moment their turn starts (see
        /// OnTurnStarted), before their first AIM frame has arrived to update
        /// _lastAimPlayer — using that instead would miss a colour change landing in
        /// that gap.
        /// </remarks>
        private void OnPlayerColourChanged(PlayerColourChangedEvent e)
        {
            if (TurnManager.HasInstance && e.PlayerId == TurnManager.Instance.ActivePlayer) TintGhost(e.PlayerId);
        }

        // A join must reset the ordering guard because the new phone's sequence counter restarts at zero; do not gate on
        // Reconnected, the relay reports a rejoin as a fresh join. doc-ref f6df docs/4-systems/Presentation.md
        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            if (e.PlayerId == _lastAimPlayer) _lastSeq = -1;
        }

        private void Update()
        {
            HasSignal = Time.time - _lastAimTime <= staleAfterSeconds;

            // Smooth every frame regardless of packet arrival — this is what turns
            // 20Hz of network data into something that reads as continuous motion.
            // Exponential rather than a fixed step so it is frame-rate independent.
            //
            // One Exp for both: the attitude and the swing speed smooth at the same rate,
            // so evaluating it twice was computing the same number twice per frame.
            var smoothing = 1f - Mathf.Exp(-attitudeSmoothing * Time.deltaTime);

            _curAttitude = Quaternion.Slerp(_curAttitude, _targetAttitude, smoothing);
            _curSwingSpeed = Mathf.Lerp(_curSwingSpeed, _targetSwingSpeed, smoothing);

            // Hide the preview until the stone has landed and CameraSettled, or a ghost slides across
            // the frame while the camera swings back. doc-ref 0c3d docs/4-systems/Presentation.md
            var launcherBusy = _launcher != null &&
                               ((_launcher.ActiveRock != null && _launcher.ActiveRock.IsFlying) ||
                                !_launcher.CameraSettled);

            var isVisible = HasSignal && !launcherBusy;

            SetVisible(isVisible);

            if (!isVisible) return;

            UpdateGhost();
            UpdateArc();
        }

        /// <summary>
        /// Shows or hides the arc and the ghost, writing only on an actual change.
        /// </summary>
        /// <remarks>
        /// Both setters cross into native code, and SetActive additionally walks the
        /// object's children and fires OnEnable/OnDisable — so it is not the free
        /// assignment it looks like. This value changes a handful of times per turn and
        /// was being written 60 times a second. Same shape as ShoreRocks.SetVisible.
        /// </remarks>
        private void SetVisible(bool isVisible)
        {
            if (isVisible == _isVisible) return;

            _isVisible = isVisible;
            _line.enabled = isVisible;
            _ghost.gameObject.SetActive(isVisible);
        }

        private void OnAim(AimMessage aim)
        {
            // React to AIM only while Playing; lobby AIM frames belong to ShoreRocks, and logging them
            // as rejected reads like lost input. doc-ref e8c1 docs/4-systems/Presentation.md
            if (!TurnManager.HasInstance || TurnManager.Instance.Phase != MatchPhase.Playing) return;

            var active = TurnManager.Instance.ActivePlayer;
            if (aim.playerId != active)
            {
                LogRejectedAim($"from '{aim.playerId}', active is '{active}'");
                return;
            }

            if (aim.playerId != _lastAimPlayer)
            {
                _lastAimPlayer = aim.playerId;
                _lastSeq = -1;

                // Snap to the new player's pose rather than sweeping across from wherever
                // the last one left the ghost. The arc's verdict is snapped the same way,
                // one frame later, once there is a prediction to snap it to.
                _curSwingSpeed = 0f;
                _curAttitude = PhoneAttitude.ToUnity(aim.qw, aim.qx, aim.qy, aim.qz);
                _snapQuality = true;
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

            // Raw swing speed, not a normalised power — the phone sends the same quantity
            // ThrowMessage does, and this is the only place it is converted (via
            // SkipConstants.SpeedScale, inside SkipPhysics.CreateLaunch).
            _targetSwingSpeed = Mathf.Max(0f, aim.swingSpeed);
            _lastAimTime = Time.time;
        }

        /// <summary>
        /// Diagnostic only — surfaces which of OnAim's two guards is dropping frames when
        /// the ghost/arc unexpectedly fail to show, rather than that staying a mystery.
        /// Rate-limited to once a second so a turn stuck on one reason can't flood the
        /// Console.
        /// </summary>
        private void LogRejectedAim(string reason)
        {
            if (Time.time < _nextRejectLogTime) return;
            _nextRejectLogTime = Time.time + 1f;
            Debug.Log($"[AimPreview] Rejected AIM frame — {reason}");
        }

        private void UpdateArc()
        {
            // The arc is drawn at live swing speed floored at a nominal throw, so holding still still
            // shows a usable arc. doc-ref 460e docs/4-systems/Presentation.md
            var drawSwingSpeed = Mathf.Max(previewSwingSpeed, _curSwingSpeed);

            // Zero spin: real spin is measured at throw release, not while aiming, and
            // feeding the arc one anyway would move it before a throw actually happened.
            var launch = SkipPhysics.CreateLaunch(
                throwOrigin.position, drawSwingSpeed, _curAttitude, 0f, constants, tuning);

            var final = SkipPhysics.Predict(launch, constants, tuning, _path);

            PredictedSkips = final.SkipCount;
            PredictedDistance = Vector3.Distance(throwOrigin.position, final.Position);

            // Exact length, because SetPositions reads the whole array. Reallocating only
            // when the point count actually changes turns a per-frame allocation into a
            // rare one — arcs of the same shape reuse the same buffer.
            if (_pathBuffer.Length != _path.Count) _pathBuffer = new Vector3[_path.Count];
            _path.CopyTo(_pathBuffer);

            _line.positionCount = _path.Count;
            _line.SetPositions(_pathBuffer);

            UpdateArcColour();
        }

        // Arc colour runs red to green by predicted skips and is set on the LineRenderer's vertex
        // colour, not on the material. doc-ref acf8 docs/4-systems/Presentation.md
        private void UpdateArcColour()
        {
            var quality = feedback.QualityOf(PredictedSkips);

            // Exponential, frame-rate independent, same shape as the attitude smoothing
            // above — the rate is slower, and AimFeedbackTuning.ColourSmoothing says why.
            _curQuality = _snapQuality
                ? quality
                : Mathf.Lerp(_curQuality, quality,
                             1f - Mathf.Exp(-feedback.ColourSmoothing * Time.deltaTime));

            _snapQuality = false;

            var colour = feedback.ColourAt(_curQuality);

            _line.startColor = colour;
            _line.endColor = new Color(colour.r, colour.g, colour.b, feedback.TailAlpha);
        }

        private void UpdateGhost()
        {
            _ghost.position = throwOrigin.position;

            // The ghost's rotation is the phone's attitude verbatim; adding a spin angle on top
            // double-counted twist and ratcheted the yaw. doc-ref 478e docs/4-systems/Presentation.md
            _ghost.rotation = _curAttitude;
        }

        // ---------------------------------------------------------------- scaffolding

        private Transform CreateOrigin()
        {
            var origin = new GameObject("ThrowOrigin").transform;
            origin.position = new Vector3(0f, 0f, 1f);
            return origin;
        }

        private void BuildLine()
        {
            var go = new GameObject("AimArc");
            go.transform.SetParent(transform, false);

            // CreateArc, not Create. Create is URP/Lit, which reads no vertex colour, and
            // startColor/endColor are vertex colour and nothing else — see CreateArc's
            // remarks. Every tint written below and in UpdateArcColour was discarded by the
            // shader before this was changed.
            _lineMaterial = PlaceholderArt.CreateArc();

            _line = go.AddComponent<LineRenderer>();
            _line.widthMultiplier = 0.12f;
            _line.numCapVertices = 4;
            _line.useWorldSpace = true;
            _line.material = _lineMaterial;

            // Start colour only; UpdateArcColour owns both ends from the first prediction
            // on. This is what shows for the frame before one exists.
            var colour = feedback.ColourAt(0.5f);
            _line.startColor = colour;
            _line.endColor = new Color(colour.r, colour.g, colour.b, feedback.TailAlpha);
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

            // Neutral until OnTurnStarted paints it in the aiming player's colour — this
            // runs at Awake, before anyone's turn has started.
            _ghostMaterial = PlaceholderArt.Create(Color.gray3, PlaceholderArt.StoneQueue);
            ghost.GetComponent<MeshRenderer>().material = _ghostMaterial;

            _ghost = ghost.transform;
            _ghost.SetParent(transform, false);

            // Hidden to start, matching _line.enabled = false in BuildLine. Update's
            // visibility cache assumes this — see _isVisible — and there is nothing to show
            // anyway until an aim frame has arrived.
            ghost.SetActive(false);
        }

        /// <summary>
        /// The lobby's shore of six stones, sharing this component's throw origin so the
        /// fan is centred on the same rig the arc and the live throw use.
        /// </summary>
        private void BuildShoreRocks()
        {
            // Parented like the ghost, the line and the water: to this component's
            // transform, which Bootstrap marks DontDestroyOnLoad, rather than to
            // throwOrigin — CreateOrigin's object is a bare root GameObject with no such
            // marking of its own. Safe to treat this transform's local space as
            // origin-relative because throwOrigin is created at (0,0,0) by CreateOrigin
            // and nothing in this project ever assigns the [SerializeField] override.
            var shore = new GameObject("ShoreRocks");
            shore.transform.SetParent(transform, false);
            shore.AddComponent<ShoreRocks>().Bind(throwOrigin);
        }

        // Water size constants are shared with SkipCurve so its on-mesh check reads the real numbers;
        // Unity's Plane is 10 units before scaling. doc-ref b58d docs/4-systems/Presentation.md
        public const float WaterPlaneBaseSize = 10f;
        public const float WaterPlaneScale = 200f;
        public const float WaterCentreZ = 60f;

        /// <summary>
        /// How far the drawn water runs ahead of the throw origin, in metres. The origin is at
        /// (0, 0, 0) — see CreateOrigin — so this is the centre offset plus the half extent.
        /// Past this a throw is still skipping, because SkipPhysics bounces against WaterY (a
        /// plane equation) rather than against the mesh, but it is doing it over water nobody
        /// can see.
        /// </summary>
        public const float WaterReachFromOrigin =
            WaterCentreZ + WaterPlaneBaseSize * WaterPlaneScale * 0.5f;

        // The far bank sits at 320 m so enough of its colour survives the fog to read as sand, while
        // staying far outside a throw's ~50 m. doc-ref 8bec docs/4-systems/Presentation.md
        private const float ShoreDistance = 320f;

        private void BuildPlaceholderScene()
        {
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "PlaceholderWater";
            water.transform.position = new Vector3(0f, constants.WaterY, WaterCentreZ);
            water.transform.localScale = new Vector3(WaterPlaneScale, 1f, WaterPlaneScale);
            Destroy(water.GetComponent<Collider>());


            // A LakeLouiseEnvironment in the scene swaps in its own water, scenery and sky;
            // without one (the Bootstrap scene) nothing below changes.
            var environment = FindFirstObjectByType<LakeLouiseEnvironment>();

            var waterMaterial = environment != null ? environment.CreateWaterMaterial() : null;
            if (waterMaterial == null) waterMaterial = Resources.Load<Material>("RockSkipping_WaterRipple");
            if (waterMaterial == null)
            {
                waterMaterial = PlaceholderArt.CreateWater(new Color(0.06f, 0.16f, 0.28f));
            }
            water.GetComponent<MeshRenderer>().material = waterMaterial;

            // Handed the material directly rather than looked up — this method is the only
            // thing that ever holds the water, and it drops the reference on exit.
            water.AddComponent<WaterRipples>().Bind(waterMaterial, constants);

            // Parented like the arc and the ghost so it survives with this component,
            // which Bootstrap marks DontDestroyOnLoad. Left at the scene root it was
            // destroyed by the first scene load and never rebuilt — Awake only runs once.
            water.transform.SetParent(transform, true);

            var camera = Camera.main;
            if (camera == null) return;

            // The sky clear colour is derived from the water's base colour so the horizon has no hard
            // line and retuning the lake carries the sky. doc-ref 5850 docs/4-systems/Presentation.md
            var horizon = new Color(0.42f, 0.58f, 0.74f);

            // Owner-chosen framing (2026-10-01, #42): higher and steeper than the old 2 m /
            // horizon-at-a-third pitch, so the six shore rocks read larger on the TV.
            // RockLauncher captures this as its "home" and returns here after every throw.
            // z = -4.5, not the owner's first -3: at -3 the stone (ThrowOrigin, z = 1) sat 48°
            // below horizontal, past the frame's bottom edge at 45° (15° pitch + half the 60° FOV),
            // and was cut off. At -4.5 it sits ~39° down, inside the frame (owner: "a little too
            // close", 2026-10-01).
            camera.transform.position = new Vector3(0, 4.5f, -4.5f);
            camera.transform.rotation = Quaternion.Euler(15f, 0f, 0f);
            camera.backgroundColor = horizon;

            // Do not use a skybox: URP draws it after opaques and the depth-less water leaves nothing to test against, so
            // the sky paints over the lake. doc-ref 27c2 docs/4-systems/Presentation.md
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.farClipPlane = 1000f;

            var skyMaterial = BuildSkyBackdrop(camera, horizon);
            AngleTheSun();

            if (environment != null) environment.Build(transform, skyMaterial, constants.WaterY, camera, water.transform);
            else BuildFarShore();

            // Fog fades the lake into the sky colour before the far clip plane cuts it, rather than
            // pushing the clip plane out. doc-ref 3a02 docs/4-systems/Presentation.md
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = horizon;
            RenderSettings.fogStartDistance = 120f;
            RenderSettings.fogEndDistance = 600f;
        }

        /// <summary>
        /// How far in front of the camera the sky quad sits. Any value inside the far clip
        /// works — it writes no depth and is drawn before everything — so this is only large
        /// enough that near-plane clipping can never reach it.
        /// </summary>
        private const float SkyQuadDistance = 50f;

        // The backdrop is a Background-queue quad on the camera, with its gradient taken from view
        // direction, because a skybox hides the lake. doc-ref a26e docs/4-systems/Presentation.md
        private Material BuildSkyBackdrop(Camera camera, Color horizon)
        {
            var shader = Resources.Load<Shader>("SkyBackdrop");

            if (shader == null)
            {
                Debug.LogWarning("[AimPreview] Resources/SkyBackdrop.shader NOT FOUND — keeping " +
                                 "the flat sky. Expected at Assets/Resources/SkyBackdrop.shader.");
                return null;
            }

            if (!shader.isSupported)
            {
                Debug.LogWarning("[AimPreview] Resources/SkyBackdrop.shader FAILED TO COMPILE — " +
                                 "keeping the flat sky. The shader error is logged above this.");
                return null;
            }

            var material = new Material(shader) { renderQueue = 1000 };
            material.SetColor("_SkyTop", new Color(0.16f, 0.34f, 0.62f));
            material.SetColor("_SkyHorizon", horizon);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "SkyBackdrop";
            Destroy(quad.GetComponent<Collider>());

            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;

            // No shadows either way. It is a backdrop: casting would drop a rectangle across the
            // lake, and receiving would let the sun's angle darken the sky.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            quad.transform.SetParent(camera.transform, false);
            quad.transform.localPosition = new Vector3(0f, 0f, SkyQuadDistance);
            quad.transform.localRotation = Quaternion.identity;

            // Sized to the frustum at that distance, with margin. Height is the exact frustum
            // height; width comes from the aspect ratio — and the margin covers both rounding
            // and a window whose aspect is wider than the one this was measured at, either of
            // which would otherwise leave the clear colour showing in a corner.
            var height = 2f * SkyQuadDistance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var width = height * Mathf.Max(camera.aspect, 2.4f);
            quad.transform.localScale = new Vector3(width * 1.2f, height * 1.2f, 1f);
            return material;
        }

        // The bank goes at the far end, since anything behind the origin is behind the camera, and is
        // drawn below the water's queue. doc-ref 094c docs/4-systems/Presentation.md
        private static void BuildFarShore()
        {
            var bank = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bank.name = "FarShore";
            Destroy(bank.GetComponent<Collider>());

            // The bank is a flattened cube wide enough to cross the frustum and sized for its distance,
            // so it reads as land and not a hairline. doc-ref 4be6 docs/4-systems/Presentation.md
            bank.transform.position = new Vector3(0f, 1f, ShoreDistance);
            bank.transform.localScale = new Vector3(1600f, 18f, 140f);
            bank.GetComponent<MeshRenderer>().sharedMaterial = Sand();

            BuildDunes();
        }

        // The sand is far brighter than real sand because fog replaces about 40% of it; recheck it
        // whenever ShoreDistance moves. doc-ref 2612 docs/4-systems/Presentation.md
        private static Material Sand() =>
            PlaceholderArt.Create(new Color(0.86f, 0.74f, 0.52f), PlaceholderArt.ShoreQueue);

        // Mounds are squashed spheres sunk into the bank with a seeded Random, so the coastline reads
        // as terrain and is reproducible. doc-ref 2f4a docs/4-systems/Presentation.md
        private static void BuildDunes()
        {
            var sand = Sand();
            var random = new System.Random(20260908);

            for (var i = 0; i < 30; i++)
            {
                var dune = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                dune.name = $"Dune_{i:00}";
                Destroy(dune.GetComponent<Collider>());
                dune.GetComponent<MeshRenderer>().sharedMaterial = sand;

                var spread = (float)(random.NextDouble() * 1500.0 - 750.0);

                // BEHIND the bank's centre only, never in front of it. Allowing them either side
                // put one dune ~45 m nearer than the rest, and a capture showed it as a single
                // oversized blob dominating the horizon: at this distance, 45 m of depth is a
                // large change in apparent size. Keeping them all at or behind the bank line
                // means the spread reads as coastline rather than as one thing in the wrong place.
                var depth = ShoreDistance + (float)(random.NextDouble() * 60.0);

                // Narrow range for the same reason. Wide size variation plus depth variation
                // multiply into silhouettes that look unrelated to each other.
                var size = (float)(random.NextDouble() * 14.0 + 10.0);

                // Wide and low — dunes, not domes. The vertical squash stops a sphere reading as
                // a ball.
                //
                // Sat relative to the BANK TOP, not to the waterline, and that is the whole
                // trick. Placed at their own scale off y = 0 they finished around y = 7 while
                // the bank top is at y = 10, so every one of them was swallowed whole and the
                // shoreline came back as a perfectly flat band. They have to be lifted to the
                // bank's surface first, and only then can their size decide how far they crest
                // above it.
                const float bankTop = 10f;
                dune.transform.position = new Vector3(spread, bankTop - 1f + size * 0.15f, depth);
                dune.transform.localScale = new Vector3(size * 2.8f, size * 0.40f, size * 1.8f);
                dune.transform.rotation =
                    Quaternion.Euler(0f, (float)(random.NextDouble() * 360.0), 0f);
            }
        }

        // Raise the existing directional light high and swing it off-axis; a low sun misses the stones'
        // flat tops and lights only the rims. doc-ref 1a80 docs/4-systems/Presentation.md
        private static void AngleTheSun()
        {
            Light sun = null;
            foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type != LightType.Directional) continue;
                sun = light;
                break;
            }

            if (sun == null) return;

            // Warm, and not white: white reads as midday and flattens an already flat scene.
            // Mild enough that the blue water stays blue rather than turning green.
            sun.color = new Color(1f, 0.94f, 0.85f);
            sun.intensity = 1.3f;
            sun.transform.rotation = Quaternion.Euler(55f, 35f, 0f);
        }
    }
}
