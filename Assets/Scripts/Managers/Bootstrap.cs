using EventSystems;
using UnityEngine;
using UnityEngine.SceneManagement;
using Utils;

namespace Managers
{
    /// <summary>
    /// First scene in the build order. Creates every manager that must outlive a
    /// scene transition, then hands off to the Lobby.
    /// </summary>
    public class Bootstrap : MonoBehaviour
    {
        [Tooltip("Scene to load once the persistent managers exist.")]
        [SerializeField] private string firstScene = SceneNames.Lobby;

        [Tooltip("Advance to firstScene on boot. Off until the Lobby has real content — " +
                 "jumping straight into an empty scene just looks like the game closed.")]
        [SerializeField] private bool autoLoadFirstScene;

        [Tooltip("Verbose EventManager logging. Turn on while wiring new events.")]
        [SerializeField] private bool eventDebugLogging;

        [Tooltip("Run a relay on this machine. Development only — hosts connect out to the " +
                 "deployed service. Also needs a relay-url.txt pointing RelayClient at it.")]
        [SerializeField] private bool startLocalServices;

        [Tooltip("TV screen showing the room code, join URL and QR. This is what guests read.")]
        [SerializeField] private bool showJoinScreen = true;

        [Tooltip("Final standings when a match ends.")]
        [SerializeField] private bool showResultsScreen = true;

        [Tooltip("F4 host panel for Random rocks / Tuning set / Rounds per match.")]
        [SerializeField] private bool showMatchSettingsScreen = true;

        [Tooltip("Append one row per throw to rock-throws.csv — rock, tuning set, power, face " +
                 "angle, skips, distance. The real-throw half of the Authored/Derived " +
                 "comparison; records only, changes nothing.")]
        [SerializeField] private bool recordRockTelemetry = true;

        [Tooltip("On-screen relay/throw readout. M1 scaffolding; turn off for real play.")]
        [SerializeField] private bool showDebugHud = true;

        [Tooltip("Live trajectory arc plus placeholder water. Scaffolding until the real lake scene exists.")]
        [SerializeField] private bool showAimPreview = true;

        [Tooltip("Sound. Off runs the game silently — nothing else changes, because every " +
                 "sound hangs off events rather than off gameplay code.")]
        [SerializeField] private bool enableAudio = true;

        [Tooltip("Carry aim and throw over a direct WebRTC channel to each phone instead of " +
                 "through the hosted relay. Falls back to the relay per-phone if it cannot " +
                 "connect, so turning it off only costs latency. HostingPlan.md §5.")]
        [SerializeField] private bool usePeerConnections = true;

        private void Awake()
        {
            EventManager.DebugLoggingEnabled = eventDebugLogging;

            // Touching Instance is what creates each singleton. Every manager listed
            // here sets persistBetweenScenes = true in its own Awake, so each one
            // survives from here to Results.
            _ = EventManager.Instance;

            // First of the real managers, and the ordering is a trap rather than a
            // preference. SingletonBase.Instance auto-creates a bare instance for whoever
            // asks first, so anything touching AudioManager.Instance before this line gets a
            // manager with no bank, no mixer and no voice pool — and the real one would then
            // destroy itself in Awake as the duplicate. Nothing below may be moved above it.
            if (enableAudio) _ = AudioManager.Instance;

            // Before TurnManager, and that ordering is load-bearing. Both subscribe to
            // ThrowCompletedEvent, and EventManager delivers in subscription order — so
            // ScoreManager banks the final throw's points before TurnManager sees the
            // same event, ends the match and reads the standings. Created the other way
            // round, the winning throw would be missing from the results screen.
            _ = ScoreManager.Instance;

            // Development only, and off by default — a normal host connects out to the
            // deployed service and runs nothing locally. Created before RelayClient so a
            // local relay is listening before the client tries it; the client retries with
            // backoff regardless, so the ordering only avoids a guaranteed first failure.
            if (startLocalServices) _ = Net.LocalServices.Instance;

            // RelayClient must be created here, never per-scene: a scene-local socket
            // is torn down on every transition and every phone bounces back to the
            // join screen. That reads as a networking bug but is a lifecycle one.
            _ = Net.RelayClient.Instance;

            // After RelayClient, whose signalling it rides and whose dispatch it feeds.
            // Failing to establish is not fatal — a phone without a peer connection keeps
            // sending over the relay, which is what makes this safe to enable by default.
            if (usePeerConnections) _ = Net.PeerLink.Instance;

            // Created unconditionally, like RelayClient — LocalServices.Mode isn't set until
            // its own Start() runs, so this can't read it synchronously here either. It waits
            // for LocalServices.Ready and self-selects out (HotspotState.NotApplicable)
            // whenever the mode turns out to be Lan, or LocalServices isn't running at all.
            _ = Net.HotspotService.Instance;

            // Before TurnManager, whose broadcast path will ask it for each player's rock
            // — see docs/plans/rock-choosing-system.md Part 2. Placed after RelayClient
            // rather than earlier so a future direct subscription to a RelayClient event
            // (Part 3's ShoreRocks needs one) can assume the instance already exists.
            _ = RockSelection.Instance;

            // Before TurnManager for the same reason RockSelection is: the broadcast path
            // asks it for every player's colour. After RelayClient because it subscribes
            // to PickColourReceived directly — a PICK_COLOUR carries its own payload, so
            // unlike PICK_ROCK there is no highlight owner to resolve it first.
            _ = PlayerColours.Instance;

            // After RelayClient so it can subscribe before any phone joins, and after
            // ScoreManager for the delivery-order reason above.
            _ = TurnManager.Instance;

            // After both RockSelection and TurnManager, never earlier — its Awake applies the
            // settings loaded from match-settings.json straight onto RockSelection.RandomRocks
            // and TurnManager.RoundsPerMatch, which needs both instances to already exist. See
            // docs/plans/rock-choosing-system.md Part 5.
            _ = MatchSettings.Instance;

            // Last of the subscribers, deliberately. EventManager delivers in subscription
            // order, so creating this after ScoreManager and TurnManager means that by the
            // time it hears a throw ending, the points are banked and the phase has settled
            // — it can read final state to decide what to play rather than racing it.
            if (enableAudio) CreateSfxDirector();

            // After ScoreManager and TurnManager for the same delivery-order reason: by the
            // time it hears a throw ending, the points are banked and the phase has settled,
            // so the row it writes describes a finished throw rather than one mid-tally.
            if (recordRockTelemetry) CreateRockTelemetry();

            if (showJoinScreen) CreateJoinScreen();
            if (showResultsScreen) CreateResultsScreen();
            if (showMatchSettingsScreen) CreateMatchSettingsScreen();
            if (showDebugHud) CreateDebugHud();
            if (showAimPreview) CreateAimPreview();
        }

        /// <summary>
        /// Persistent because the room code stays valid across every scene — a latecomer
        /// scanning during the Game scene must still be able to get in.
        /// </summary>
        /// <summary>
        /// Turns gameplay events into sounds. Not a singleton and not persistent by its own
        /// choice — it holds no state worth keeping and is cheap to rebuild, so it follows
        /// the screens rather than the managers.
        /// </summary>
        private static void CreateSfxDirector()
        {
            if (FindFirstObjectByType<Audio.SfxDirector>() != null) return;

            var director = new GameObject(nameof(Audio.SfxDirector));
            director.AddComponent<Audio.SfxDirector>();
            DontDestroyOnLoad(director);
        }

        /// <summary>
        /// Persistent like the managers, and for a blunter reason than the screens: a
        /// scene-local recorder would be destroyed and rebuilt on the Lobby-Game-Results
        /// transitions, and every throw either side of a rebuild would be missing from the
        /// corpus with nothing in the file to say so.
        /// </summary>
        private static void CreateRockTelemetry()
        {
            if (FindFirstObjectByType<Verification.RockTelemetry>() != null) return;

            var recorder = new GameObject(nameof(Verification.RockTelemetry));
            recorder.AddComponent<Verification.RockTelemetry>();
            DontDestroyOnLoad(recorder);
        }

        private static void CreateJoinScreen()
        {
            if (FindFirstObjectByType<Ui.JoinScreen>() != null) return;

            var screen = new GameObject("JoinScreen");
            screen.AddComponent<Ui.JoinScreen>();
            DontDestroyOnLoad(screen);
        }

        /// <summary>Persistent for the same reason as the rest: it outlives any one scene.</summary>
        private static void CreateResultsScreen()
        {
            if (FindFirstObjectByType<Ui.ResultsScreen>() != null) return;

            var screen = new GameObject("ResultsScreen");
            screen.AddComponent<Ui.ResultsScreen>();
            DontDestroyOnLoad(screen);
        }

        private static void CreateMatchSettingsScreen()
        {
            if (FindFirstObjectByType<Ui.MatchSettingsScreen>() != null) return;

            var screen = new GameObject("MatchSettingsScreen");
            screen.AddComponent<Ui.MatchSettingsScreen>();
            DontDestroyOnLoad(screen);
        }

        private static void CreateAimPreview()
        {
            if (FindFirstObjectByType<Game.AimPreview>() != null) return;

            var preview = new GameObject("AimPreview");
            preview.AddComponent<Game.AimPreview>();

            // Same object so the launcher can share the preview's throw origin and
            // tuning — a live throw that used a different rig would make the arc lie.
            preview.AddComponent<Game.RockLauncher>();

            DontDestroyOnLoad(preview);
        }

        /// <summary>
        /// The HUD gets its own persistent object rather than riding on RelayClient.
        /// It is the instrument used to diagnose the relay, so it must not disappear
        /// when the relay is the thing that's broken.
        /// </summary>
        private static void CreateDebugHud()
        {
            if (FindFirstObjectByType<Net.RelayDebugHud>() != null) return;

            var hud = new GameObject("RelayDebugHud");
            hud.AddComponent<Net.RelayDebugHud>();
            DontDestroyOnLoad(hud);
        }

        private void Start()
        {
            if (!autoLoadFirstScene)
            {
                Debug.Log("[Bootstrap] Managers up. Staying in Bootstrap — " +
                          "tick Auto Load First Scene once the Lobby has content.");
                return;
            }

            SceneManager.LoadScene(firstScene);
        }
    }
}
