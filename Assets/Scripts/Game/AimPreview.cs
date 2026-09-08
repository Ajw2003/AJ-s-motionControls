using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
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

        /// <summary>
        /// Resolves the new active player's rock and tuning once per turn, and swaps the
        /// ghost's mesh and scale to match — see the class remarks on <see cref="Tuning"/>.
        /// </summary>
        /// <remarks>
        /// Only sharedMesh and localScale are touched. RockDefinition.MeshRotation is
        /// deliberately left unapplied here — the ghost's rotation is overwritten every
        /// frame from the phone's live attitude (see UpdateGhost), and RockController does
        /// the same from the solver's attitude, so neither has anywhere to compose a
        /// per-mesh correction without also changing what the solver reads as the stone's
        /// face angle. Every mesh RockCatalogBuilder has produced so far imports flat, so
        /// this has not yet needed solving for real.
        /// </remarks>
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

        /// <summary>
        /// A phone that joins has a brand new ThrowController, whose sequence counter starts
        /// at zero again — so the ordering guard in <see cref="OnAim"/> must forget the number
        /// it was holding for that player, or every frame they send is older than what it
        /// already saw and the preview starves.
        /// </summary>
        /// <remarks>
        /// This is what left the ghost and arc hidden for the rest of a session after a player
        /// closed their tab and came back. The guard rejected each frame before
        /// <c>_lastAimTime</c> was updated, so HasSignal went stale and Update hid both.
        /// See docs/Findings-AimPreviewAfterRejoin.md.
        ///
        /// Deliberately NOT gated on <see cref="PlayerJoinedEvent.Reconnected"/>. The relay
        /// deletes a player from the room on socket close (relay/server.js, the 'close'
        /// handler), so closing a tab and coming back arrives as a fresh join with that flag
        /// false — which is precisely the case being fixed. Any join at all means a phone with
        /// a sequence counter back at zero, and that is the fact this depends on.
        ///
        /// Only the player the guard is currently tracking matters. For anyone else the reset
        /// already happens in OnAim, the moment the aiming player changes.
        /// </remarks>
        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            if (e.PlayerId == _lastAimPlayer) _lastSeq = -1;
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

            _curSwingSpeed = Mathf.Lerp(
                _curSwingSpeed, _targetSwingSpeed, 1f - Mathf.Exp(-attitudeSmoothing * Time.deltaTime));

            // Nothing to predict while a stone is already in the air, and leaving the arc
            // up through the throw means the line the player was aiming with is still
            // drawn over the result. Both the tooltip above and the debug HUD used to
            // claim this happened; it did not, because _line.enabled was set true
            // unconditionally just before the early return below.
            var flying = _launcher != null && _launcher.ActiveRock != null && _launcher.ActiveRock.IsFlying;
            var isVisible = HasSignal && !flying;

            _line.enabled = isVisible;
            _ghost.gameObject.SetActive(isVisible);

            if (!isVisible) return;

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
            // Playing only. TurnManager.ActivePlayer is null outside Playing, so the old
            // guard here (active-is-empty-or-matches) let every lobby AIM frame from every
            // phone through — latent until ShoreRocks started streaming AIM during
            // selection, at which point it would have yanked this ghost around a lobby it
            // has no business appearing in. See the plan's constraints section.
            if (!TurnManager.HasInstance || TurnManager.Instance.Phase != MatchPhase.Playing)
            {
                LogRejectedAim($"phase is {(TurnManager.HasInstance ? TurnManager.Instance.Phase.ToString() : "no TurnManager")}, not Playing");
                return;
            }

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
            // Drawn at how hard the phone is being swung right now, floored at the nominal.
            //
            // The floor is what makes this usable. Real speed is genuinely unknowable
            // before release, so a player holding still to line a shot up reads as zero
            // effort — and an arc that collapses to a two-metre dribble every time they
            // stop moving is not an aiming guide. So the arc shows at least a nominal
            // throw, and winding up extends it from there.
            //
            // It used to be pinned at the nominal outright, which made the HUD's
            // "predicted N skips over M m" mathematically incapable of responding to how
            // hard anyone threw. That was the 13-skip ceiling.
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

        /// <summary>
        /// Red through amber to green by how many skips the solver just predicted.
        /// </summary>
        /// <remarks>
        /// This is a readability fix rather than decoration. Arc LENGTH was the only signal
        /// a bad angle gave, and the angle-quality curve in SkipPhysics is a smooth gradient
        /// with no cliff in it — so a mediocre throw looks broadly like a good one right up
        /// until it is in the water. Colour makes that gradient legible while there is still
        /// time to correct.
        ///
        /// Tinting via startColor/endColor rather than the material, because the material is
        /// shared with nothing but is drawn with a gradient the LineRenderer applies as
        /// vertex colour — writing the material colour instead would multiply underneath and
        /// leave two places that decide one hue.
        /// </remarks>
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

        /// <summary>
        /// The placeholder water's geometry, named because more than one thing needs it.
        /// <see cref="Verification.SkipCurve"/> asserts that a full-power throw stays on the
        /// drawn mesh, and it can only do that honestly if it is reading the same numbers the
        /// mesh is built from — it runs in the Editor without entering Play mode, so there is
        /// no live plane for it to measure. Duplicating the values there instead would go
        /// stale silently the first time the water changed size, which is exactly the failure
        /// the check exists to catch.
        /// </summary>
        /// <remarks>
        /// Unity's Plane primitive is 10 x 10 units before scaling, hence the base size — a
        /// scale of 200 gives 2000 m across, not 200.
        /// </remarks>
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

        private void BuildPlaceholderScene()
        {
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "PlaceholderWater";
            water.transform.position = new Vector3(0f, constants.WaterY, WaterCentreZ);
            water.transform.localScale = new Vector3(WaterPlaneScale, 1f, WaterPlaneScale);
            Destroy(water.GetComponent<Collider>());

            
            var waterMaterial = Resources.Load<Material>("RockSkipping_WaterRipple");
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

            // One colour for the empty sky and for what the lake fades into, so the two
            // cannot disagree — see the fog block below, where the hard horizon is fixed.
            //
            // Derived from the water rather than picked independently. The water's authored
            // _BaseColor is a bright mid-blue (0.06, 0.36, 0.72); the sky used to be an almost
            // black (0.05, 0.09, 0.15), and a headless capture shows the two meeting as a hard
            // line across the frame — the fog cannot hide an edge when the colour on the far
            // side of it is nothing like the water's own.
            //
            // Lightened and desaturated from the water instead of set to a taste value, so the
            // relationship survives someone retuning the lake: change _BaseColor and the sky
            // follows it rather than drifting apart from it again.
            var horizon = new Color(0.42f, 0.58f, 0.74f);

            camera.transform.position = new Vector3(0, 2f, -3);
            camera.transform.LookAt(new Vector3(0f, 0f, 4f));
            camera.backgroundColor = horizon;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.farClipPlane = 1000f;

            AngleTheSun();

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

        /// <summary>
        /// Puts the scene's directional light at a low side angle so the stones have form.
        /// </summary>
        /// <remarks>
        /// Adjusts the light the scene already has rather than adding one — a second
        /// directional light would double the ambient contribution and wash out the very
        /// shading this is for.
        ///
        /// Found by type, not by name: Game.unity's is called "Directional Light", but a
        /// string lookup breaks silently the moment somebody renames it, leaving the scene lit
        /// by the old values with no error to follow.
        ///
        /// The angle is the point, and the obvious choice is the wrong one. A LOW raking light
        /// is what gives most scenes form, and it was tried first: a headless capture at 28
        /// degrees came back with the stones darker and flatter than Unity's default. These
        /// stones are flat and horizontal, so their large visible face is their TOP, and a low
        /// sun barely lands on it — it lights the thin rims instead, which is nothing.
        ///
        /// So the elevation goes up, not down, and the direction comes from the azimuth
        /// instead: high enough to actually reach the top faces, swung off-axis so the two
        /// halves of each stone are lit differently and the silhouettes separate.
        /// </remarks>
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
