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

        [Tooltip("On-screen relay/throw readout. M1 scaffolding; turn off for real play.")]
        [SerializeField] private bool showDebugHud = true;

        [Tooltip("Live trajectory arc plus placeholder water. Scaffolding until the real lake scene exists.")]
        [SerializeField] private bool showAimPreview = true;

        private void Awake()
        {
            EventManager.DebugLoggingEnabled = eventDebugLogging;

            // Touching Instance is what creates each singleton. Every manager listed
            // here sets persistBetweenScenes = true in its own Awake, so each one
            // survives from here to Results.
            _ = EventManager.Instance;

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

            // After RelayClient so it can subscribe before any phone joins, and after
            // ScoreManager for the delivery-order reason above.
            _ = TurnManager.Instance;

            if (showJoinScreen) CreateJoinScreen();
            if (showResultsScreen) CreateResultsScreen();
            if (showDebugHud) CreateDebugHud();
            if (showAimPreview) CreateAimPreview();
        }

        /// <summary>
        /// Persistent because the room code stays valid across every scene — a latecomer
        /// scanning during the Game scene must still be able to get in.
        /// </summary>
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
