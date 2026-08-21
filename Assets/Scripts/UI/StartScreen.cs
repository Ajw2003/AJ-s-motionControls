using Net;
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
    /// standing at it deliberately presses Start. The mode picker here writes straight to
    /// NetworkModeStore with no "restart to apply" caveat, because nothing has started yet to
    /// need one; every later visit to this screen (there is no other way back into Bootstrap)
    /// shows the same picker again, so it is never harder than one click to change.
    /// </remarks>
    public class StartScreen : MonoBehaviour
    {
        [SerializeField] private KeyCode lanKey = KeyCode.Alpha1;
        [SerializeField] private KeyCode hotspotKey = KeyCode.Alpha2;
        [SerializeField] private KeyCode startKey = KeyCode.Return;

        private NetworkMode _mode;

        private GUIStyle _title, _sectionLabel, _modeButton, _startButton, _hint;
        private Texture2D _panelTexture;

        private void Awake()
        {
            _mode = NetworkModeStore.Load();
        }

        private void Update()
        {
            if (Input.GetKeyDown(lanKey)) SetMode(NetworkMode.Lan);
            else if (Input.GetKeyDown(hotspotKey)) SetMode(NetworkMode.Hotspot);
            else if (Input.GetKeyDown(startKey)) BeginMatch();
        }

        private void SetMode(NetworkMode mode)
        {
            _mode = mode;
            NetworkModeStore.Save(mode);
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
            _modeButton = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(22 * scale),
                fixedHeight = 64 * scale,
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

            GUILayout.Label("How should guests join?", _sectionLabel);
            GUILayout.Space(12);

            GUILayout.BeginHorizontal();
            DrawModeOption(NetworkMode.Lan, $"LAN  [{lanKey}]\nsame Wi-Fi or router");
            GUILayout.Space(16);
            DrawModeOption(NetworkMode.Hotspot, $"Hotspot  [{hotspotKey}]\nno router or internet needed");
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("Remembered for next time — change it here whenever you like.", _hint);

            GUILayout.Space(40);

            if (GUILayout.Button($"Start  [{startKey}]", _startButton)) BeginMatch();

            GUILayout.FlexibleSpace();
            GUILayout.EndArea();
        }

        private void DrawModeOption(NetworkMode mode, string label)
        {
            var previousColor = GUI.backgroundColor;
            if (_mode == mode) GUI.backgroundColor = new Color(0.55f, 0.85f, 0.6f);

            if (GUILayout.Button(label, _modeButton)) SetMode(mode);

            GUI.backgroundColor = previousColor;
        }
    }
}
