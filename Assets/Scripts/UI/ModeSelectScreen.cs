using Managers;
using Net;
using UnityEngine;

// Namespace is `Ui`, not `UI` — see JoinScreen.cs for why.
namespace Ui
{
    /// <summary>
    /// Lets the host pick LAN or Hotspot mode before a match starts. Blocks on true first run
    /// (nothing saved yet); on every later launch the saved choice applies immediately and this
    /// only shows a small corner affordance to reopen the picker.
    /// </summary>
    /// <remarks>
    /// Same OnGUI/no-Canvas scaffolding as JoinScreen and RelayDebugHud, for the same reason —
    /// it needs to work in whatever scene is loaded, with no prefab.
    ///
    /// Changing mode away from the one active this session never takes effect live — see
    /// NetworkModeGate.Choose(). Picking a different mode here only persists it for next launch
    /// and says so; there is no live teardown/rebuild of a running relay, certificate, or
    /// hotspot.
    /// </remarks>
    public class ModeSelectScreen : MonoBehaviour
    {
        [SerializeField] private KeyCode toggleKey = KeyCode.F3;

        [Tooltip("Hide the corner affordance once the match is under way, so the lake is visible.")]
        [SerializeField] private bool compactDuringMatch = true;

        private NetworkModeGate _gate;
        private bool _openedManually;
        private string _restartNotice;

        private GUIStyle _title, _status, _body, _hint, _button;
        private Texture2D _panelTexture;

        private void Update()
        {
            if (_gate == null && NetworkModeGate.HasInstance) _gate = NetworkModeGate.Instance;
            if (_gate == null) return;

            var pickerVisible = !_gate.Resolved || _openedManually;

            if (Input.GetKeyDown(toggleKey) && _gate.Resolved) _openedManually = !_openedManually;

            if (pickerVisible)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1)) Choose(NetworkMode.Lan);
                else if (Input.GetKeyDown(KeyCode.Alpha2)) Choose(NetworkMode.Hotspot);
            }
        }

        private void Choose(NetworkMode mode)
        {
            var wasRunningDifferentMode = _gate.Resolved && mode != _gate.Mode;

            _gate.Choose(mode);

            if (wasRunningDifferentMode)
            {
                _restartNotice = $"Saved. Restart the game to switch to {Label(mode)} mode.";
            }
            else
            {
                _restartNotice = null;
                _openedManually = false; // first run: picking a mode is the whole point of the screen
            }
        }

        private static string Label(NetworkMode mode) => mode == NetworkMode.Lan ? "LAN" : "Hotspot";

        // ------------------------------------------------------------------ drawing

        private void EnsureStyles()
        {
            if (_title != null) return;

            var scale = Mathf.Clamp(Screen.height / 1080f, 0.5f, 3f);

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(42 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _status = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(26 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };
            _body = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(22 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };
            _hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(18 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };
            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(28 * scale),
                fixedHeight = 64 * scale,
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
            if (_gate == null) return;

            EnsureStyles();

            if (!_gate.Resolved) DrawPicker(blocking: true);
            else if (_openedManually) DrawPicker(blocking: false);
            else DrawCornerAffordance();
        }

        private void DrawPicker(bool blocking)
        {
            var panel = new Rect(Screen.width * 0.5f - Screen.width * 0.25f, Screen.height * 0.25f,
                                 Screen.width * 0.5f, Screen.height * 0.5f);

            GUI.DrawTexture(panel, _panelTexture);
            GUILayout.BeginArea(new Rect(panel.x + 30, panel.y + 30, panel.width - 60, panel.height - 60));

            GUILayout.Label(blocking ? "Choose how guests join" : "Network mode", _title);
            GUILayout.Space(10);
            GUILayout.Label(blocking
                ? "LAN — guests on the same Wi-Fi or router.\n" +
                  "Hotspot — this PC creates its own network. No router or internet needed."
                : $"Currently running: {Label(_gate.Mode)}", _status);
            GUILayout.Space(20);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("LAN  [1]", _button, GUILayout.Width(panel.width * 0.35f)))
                Choose(NetworkMode.Lan);
            GUILayout.Space(20);
            if (GUILayout.Button("Hotspot  [2]", _button, GUILayout.Width(panel.width * 0.35f)))
                Choose(NetworkMode.Hotspot);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(16);

            if (!string.IsNullOrEmpty(_restartNotice))
            {
                var previous = GUI.color;
                GUI.color = new Color(1f, 0.85f, 0.4f);
                GUILayout.Label(_restartNotice, _body);
                GUI.color = previous;
            }

            if (!blocking) GUILayout.Label($"{toggleKey} closes this", _hint);

            GUILayout.EndArea();
        }

        private void DrawCornerAffordance()
        {
            var compact = compactDuringMatch && TurnManager.HasInstance &&
                          TurnManager.Instance.Phase != MatchPhase.Lobby;
            if (compact) return;

            var width = 260f;
            var panel = new Rect(20, Screen.height - 60, width, 40);

            GUI.DrawTexture(panel, _panelTexture);
            GUILayout.BeginArea(new Rect(panel.x + 10, panel.y + 6, panel.width - 20, panel.height - 12));
            GUILayout.Label($"Mode: {Label(_gate.Mode)}  ({toggleKey} to change)", _hint);
            GUILayout.EndArea();
        }
    }
}
