using UnityEngine;
using UnityEngine.SceneManagement;
using Utils;

// Namespace is `Ui`, not `UI` — see JoinScreen.cs for why.
namespace Ui
{
    /// <summary>
    /// The first thing a player sees, in its own scene ahead of Bootstrap. Nothing
    /// network-related exists yet at this point — LocalServices, RelayClient and
    /// HotspotService are all created in the Bootstrap scene, which this screen only loads
    /// once Start is pressed.
    /// </summary>
    /// <remarks>
    /// That scene boundary is the whole gate: no relay spawns, no certificate is fetched, and
    /// in particular no hotspot starts — nothing happens on this PC's network — until a person
    /// standing at it deliberately presses Start.
    ///
    /// The LAN/Hotspot picker that used to live here is gone — see
    /// docs/plans/magic-domain-replacement.md Phase 3. LAN mode is deprecated (its
    /// certificate no longer validates, now that local-ip.sh's wildcard is replaced by a
    /// single-name certificate; see NetworkMode.Lan's remarks), so this screen shows no
    /// choice and writes nothing to network-mode.txt at all — LocalServices' call to
    /// NetworkModeStore.Load() is what decides the mode, falling back to Hotspot on a fresh
    /// install. LAN stays reachable only by hand-editing network-mode.txt to the literal
    /// string "Lan" before launching; this screen leaves that file alone either way.
    /// </remarks>
    public class StartScreen : MonoBehaviour
    {
        [SerializeField] private KeyCode startKey = KeyCode.Return;

        private GUIStyle _title, _sectionLabel, _startButton, _hint;
        private Texture2D _panelTexture;

        private void Awake()
        {
            // Deliberately does not touch network-mode.txt at all any more — writing Hotspot
            // here unconditionally would stomp a hand-edited "Lan" the moment this scene
            // loads, before Bootstrap even reads it, which is exactly the escape hatch Phase 3
            // is supposed to leave open. NetworkModeStore.Load() (called by LocalServices in
            // Bootstrap) is what actually decides the mode, and its fallback — used only when
            // the file is missing or unreadable — is Hotspot, so a fresh install still gets
            // Hotspot with nothing written here.
        }

        private void Update()
        {
            if (Input.GetKeyDown(startKey)) BeginMatch();
        }

        private void BeginMatch() => SceneManager.LoadScene(SceneNames.Bootstrap);

        // ------------------------------------------------------------------ drawing

        private void EnsureStyles()
        {
            if (_title != null) return;

            var scale = Mathf.Clamp(Screen.height / 1080f, 0.5f, 3f);

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(56 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _sectionLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(24 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };
            _startButton = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(32 * scale),
                fontStyle = FontStyle.Bold,
                fixedHeight = 80 * scale,
            };
            _hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(18 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };

            _panelTexture = new Texture2D(1, 1);
            _panelTexture.SetPixel(0, 0, new Color(0.05f, 0.07f, 0.12f, 0.92f));
            _panelTexture.Apply();
        }

        private void OnDestroy()
        {
            if (_panelTexture != null) Destroy(_panelTexture);
        }

        private void OnGUI()
        {
            EnsureStyles();

            var panel = new Rect(Screen.width * 0.5f - Screen.width * 0.22f, Screen.height * 0.15f,
                                 Screen.width * 0.44f, Screen.height * 0.7f);

            GUI.DrawTexture(panel, _panelTexture);
            GUILayout.BeginArea(new Rect(panel.x + 30, panel.y + 30, panel.width - 60, panel.height - 60));

            GUILayout.FlexibleSpace();
            GUILayout.Label("ROCK SKIPPING", _title);
            GUILayout.Space(40);

            GUILayout.Label("Guests join over a Wi-Fi hotspot this PC creates —\n" +
                             "no router or internet needed.", _sectionLabel);
            GUILayout.Space(12);
            GUILayout.Label("(LAN mode is deprecated; hand-edit network-mode.txt to use it.)", _hint);

            GUILayout.Space(40);

            if (GUILayout.Button($"Start  [{startKey}]", _startButton)) BeginMatch();

            GUILayout.FlexibleSpace();
            GUILayout.EndArea();
        }
    }
}
