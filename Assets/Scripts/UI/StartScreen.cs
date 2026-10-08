using UnityEngine;
using UnityEngine.SceneManagement;
using Utils;

// Namespace is `Ui`, not `UI` — see JoinScreen.cs for why.
namespace Ui
{
    // Start is a scene boundary ahead of Bootstrap, so nothing touches the network until a person
    // presses Start; hotspot is the only mode. doc-ref 3bb9 docs/4-systems/Presentation.md
    public class StartScreen : MonoBehaviour
    {
        [SerializeField] private KeyCode startKey = KeyCode.Return;

        private GUIStyle _title, _sectionLabel, _startButton;
        private Texture2D _panelTexture;

        private void Awake()
        {
            // Nothing to do here now that Hotspot is the only mode — there is no per-machine
            // choice left for this screen to read or write.
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

            GUILayout.Space(40);

            if (GUILayout.Button($"Start  [{startKey}]", _startButton)) BeginMatch();

            GUILayout.FlexibleSpace();
            GUILayout.EndArea();
        }
    }
}
