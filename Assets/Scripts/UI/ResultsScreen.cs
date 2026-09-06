using System.Collections.Generic;
using EventTypes.MatchEvents;
using Managers;
using UnityEngine;

namespace Ui
{
    /// <summary>
    /// Final standings at the end of a match, with a prompt to play again.
    /// </summary>
    /// <remarks>
    /// Scaffolding in the same spirit as JoinScreen — OnGUI, no Canvas, no prefab — so it
    /// works in whatever scene is loaded. PLAN.md calls for a dedicated Results scene;
    /// that transition is deliberately not wired yet, for the same reason Bootstrap does
    /// not auto-load the Lobby: the scene is empty, so loading it would look like the game
    /// had closed rather than like a results screen.
    ///
    /// Reads standings straight off TurnManager, so there is one source of truth on
    /// screen. There used to be a MatchEndedEvent carrying a duplicate snapshot; nothing
    /// ever subscribed to it, so it was deleted rather than left as a second answer.
    /// </remarks>
    public class ResultsScreen : MonoBehaviour
    {
        [SerializeField] private KeyCode toggleKey = KeyCode.F3;
        [SerializeField] private bool isVisible = true;

        private TurnManager _turns;
        private IReadOnlyList<PlayerStanding> _standings;

        private GUIStyle _title, _subtitle, _row, _winnerRow, _hint;
        private Texture2D _panelTexture;

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey)) isVisible = !isVisible;

            if (_turns == null && TurnManager.HasInstance) _turns = TurnManager.Instance;
            if (_turns == null || _turns.Phase != MatchPhase.Results) return;

            // Once per frame, not once per OnGUI — OnGUI runs several times a frame and
            // this allocates a sort.
            _standings = _turns.RebuildStandings();
        }

        private void OnDestroy()
        {
            if (_panelTexture != null) Destroy(_panelTexture);
        }

        private void EnsureStyles()
        {
            if (_title != null) return;

            var scale = Mathf.Clamp(Screen.height / 1080f, 0.5f, 3f);

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(64 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _subtitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(28 * scale),
                alignment = TextAnchor.MiddleCenter,
            };
            _row = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(34 * scale),
                alignment = TextAnchor.MiddleLeft,
            };
            _winnerRow = new GUIStyle(_row)
            {
                fontSize = Mathf.RoundToInt(44 * scale),
                fontStyle = FontStyle.Bold,
            };
            _hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(24 * scale),
                alignment = TextAnchor.MiddleCenter,
            };

            _panelTexture = new Texture2D(1, 1);
            _panelTexture.SetPixel(0, 0, new Color(0.04f, 0.06f, 0.10f, 0.95f));
            _panelTexture.Apply();
        }

        private void OnGUI()
        {
            if (!isVisible) return;
            if (_turns == null || _turns.Phase != MatchPhase.Results) return;

            EnsureStyles();

            var panel = new Rect(Screen.width * 0.18f, Screen.height * 0.10f,
                                 Screen.width * 0.44f, Screen.height * 0.80f);

            GUI.DrawTexture(panel, _panelTexture);
            GUILayout.BeginArea(new Rect(panel.x + 30, panel.y + 24, panel.width - 60, panel.height - 48));

            GUILayout.Label("FINAL SCORES", _title);
            GUILayout.Label($"after {_turns.RoundsPerMatch} round{(_turns.RoundsPerMatch == 1 ? "" : "s")}", _subtitle);
            GUILayout.Space(24);

            DrawStandings();

            GUILayout.FlexibleSpace();
            GUILayout.Label($"Press {_turns.StartMatchKey} — or tap PLAY AGAIN on a phone", _hint);
            GUILayout.Label("Press F4 for match settings", _hint);
            GUILayout.Label($"{toggleKey} hides this screen", _hint);

            GUILayout.EndArea();
        }

        private void DrawStandings()
        {
            if (_standings == null || _standings.Count == 0)
            {
                GUILayout.Label("Everybody left before the end.", _subtitle);
                return;
            }

            // A shared top score means no winner. Highlighting the first row anyway would
            // silently pick one arbitrarily, off nothing but sort order.
            var top = _standings[0].Score;
            var tied = _standings.Count > 1 && _standings[1].Score == top;

            for (var i = 0; i < _standings.Count; i++)
            {
                var standing = _standings[i];
                var isWinner = !tied && i == 0;
                var style = isWinner ? _winnerRow : _row;

                var previous = GUI.color;
                if (isWinner) GUI.color = new Color(1f, 0.85f, 0.4f);

                GUILayout.BeginHorizontal();
                var displayName = PlayerNames.HasInstance
                    ? PlayerNames.Instance.DisplayNameFor(standing.PlayerId)
                    : standing.PlayerId;
                GUILayout.Label($"{i + 1}.  {displayName}", style);
                GUILayout.FlexibleSpace();
                GUILayout.Label(standing.Score.ToString(), style);
                GUILayout.EndHorizontal();

                GUI.color = previous;
            }

            if (tied)
            {
                GUILayout.Space(12);
                GUILayout.Label($"Tied at {top} — no winner", _subtitle);
            }
        }
    }
}
