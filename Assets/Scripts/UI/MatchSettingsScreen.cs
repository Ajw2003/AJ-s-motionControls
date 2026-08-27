using Managers;
using UnityEngine;

namespace Ui
{
    /// <summary>
    /// Host settings panel: Random rocks, Tuning set, Rounds per match — see
    /// docs/plans/rock-choosing-system.md Part 5. Renders <see cref="MatchSettings.All"/> in a
    /// loop, so a fourth setting is one entry there, not a screen edit.
    /// </summary>
    /// <remarks>
    /// Scaffolding in the same spirit as JoinScreen/ResultsScreen — OnGUI, no Canvas, no
    /// prefab. Reachable whenever <see cref="MatchSettings.CanEdit"/> is true (Lobby, Choosing,
    /// Results — anything but Playing), so it opens both from the main lobby before a match and
    /// from the existing mid-match re-pick pause without any new phase logic.
    /// </remarks>
    public class MatchSettingsScreen : MonoBehaviour
    {
        [SerializeField] private KeyCode toggleKey = KeyCode.F4;

        private static MatchSettingsScreen _instance;

        /// <summary>Whether the modal has focus right now — JoinScreen checks this to suppress its start prompt.</summary>
        public static bool IsOpen => _instance != null && _instance._visible;

        private bool _visible;
        private int _cursor;

        private GUIStyle _title, _row, _selectedRow, _help, _hint;
        private Texture2D _panelTexture;

        private void Awake() => _instance = this;

        private void Update()
        {
            var settings = MatchSettings.HasInstance ? MatchSettings.Instance : null;

            if (Input.GetKeyDown(toggleKey))
            {
                if (_visible) _visible = false;
                else if (settings != null && settings.CanEdit) _visible = true;
            }

            // The match can start (or another host action can leave CanEdit) while the panel
            // happens to be open — force-close rather than keep drawing a modal over gameplay.
            if (_visible && (settings == null || !settings.CanEdit)) _visible = false;

            if (!_visible || settings == null || settings.All.Count == 0) return;

            if (Input.GetKeyDown(KeyCode.DownArrow)) _cursor = (_cursor + 1) % settings.All.Count;
            if (Input.GetKeyDown(KeyCode.UpArrow)) _cursor = (_cursor - 1 + settings.All.Count) % settings.All.Count;
            if (Input.GetKeyDown(KeyCode.RightArrow)) settings.All[_cursor].Next();
            if (Input.GetKeyDown(KeyCode.LeftArrow)) settings.All[_cursor].Prev();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_panelTexture != null) Destroy(_panelTexture);
        }

        private void EnsureStyles()
        {
            if (_title != null) return;

            var scale = Mathf.Clamp(Screen.height / 1080f, 0.5f, 3f);

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(40 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _row = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(28 * scale),
                alignment = TextAnchor.MiddleLeft,
            };
            _selectedRow = new GUIStyle(_row) { fontStyle = FontStyle.Bold };
            _help = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(18 * scale),
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
            };
            _hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(20 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };

            _panelTexture = new Texture2D(1, 1);
            _panelTexture.SetPixel(0, 0, new Color(0.04f, 0.06f, 0.10f, 0.96f));
            _panelTexture.Apply();
        }

        private void OnGUI()
        {
            if (!_visible) return;
            var settings = MatchSettings.HasInstance ? MatchSettings.Instance : null;
            if (settings == null) return;

            EnsureStyles();

            var panel = new Rect(Screen.width * 0.5f - Screen.width * 0.2f, Screen.height * 0.5f - Screen.height * 0.28f,
                                 Screen.width * 0.4f, Screen.height * 0.56f);

            GUI.DrawTexture(panel, _panelTexture);
            GUILayout.BeginArea(new Rect(panel.x + 24, panel.y + 20, panel.width - 48, panel.height - 40));

            GUILayout.Label("MATCH SETTINGS", _title);
            GUILayout.Space(16);

            for (var i = 0; i < settings.All.Count; i++)
            {
                var setting = settings.All[i];
                var selected = i == _cursor;
                var style = selected ? _selectedRow : _row;

                var previous = GUI.color;
                if (selected) GUI.color = new Color(0.6f, 1f, 0.7f);

                GUILayout.BeginHorizontal();
                GUILayout.Label($"{(selected ? "> " : "  ")}{setting.Label}", style);
                GUILayout.FlexibleSpace();
                GUILayout.Label(setting.Display, style);
                GUILayout.EndHorizontal();

                GUI.color = previous;

                if (selected) GUILayout.Label(setting.Help, _help);

                GUILayout.Space(8);
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("Up/Down to select, Left/Right to change", _hint);
            GUILayout.Label($"{toggleKey} closes this panel", _hint);

            GUILayout.EndArea();
        }
    }
}
