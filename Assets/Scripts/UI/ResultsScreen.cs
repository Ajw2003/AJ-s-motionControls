using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using EventTypes.SkipEvents;
using Managers;
using UnityEngine;

namespace Ui
{
    // Reads standings straight off TurnManager; the Results scene transition is deliberately not wired
    // yet. doc-ref ef90 docs/4-systems/Presentation.md
    public class ResultsScreen : MonoBehaviour
    {
        [SerializeField] private KeyCode toggleKey = KeyCode.F3;
        [SerializeField] private bool isVisible = true;

        private TurnManager _turns;
        // Our own copy, refreshed only with the cached texts below. RebuildStandings hands
        // back TurnManager's internal list, which EndMatch also rebuilds; holding that list
        // let its row count change under texts built for the old count.
        private readonly List<PlayerStanding> _standings = new();

        private MatchHud _hud;

        // Rebuilt together, only when a standings-affecting event raised the flag: the standings
        // sort, plus every string OnGUI draws, so OnGUI formats nothing.
        private bool _dirty = true;
        private string _subtitleText, _tieText;
        private readonly List<string> _nameTexts = new();
        private readonly List<string> _numberTexts = new();

        private GUIStyle _title, _subtitle, _row, _winnerRow, _rowRight, _winnerRowRight, _hint;
        private Texture2D _panelTexture, _dotTexture;

        private void Start()
        {
            if (TurnManager.HasInstance) _turns = TurnManager.Instance;
            else Debug.LogWarning("[ResultsScreen] No TurnManager at Start; the results will never show.");

            // Optional: absent when Bootstrap's showMatchHud is off, so not logged.
            _hud = FindFirstObjectByType<MatchHud>();

            EventManager.Instance?.Subscribe<MatchPhaseChangedEvent>(this, OnStandingsChanged);
            EventManager.Instance?.Subscribe<ScoreAwardedEvent>(this, OnStandingsChanged);
            EventManager.Instance?.Subscribe<PlayerJoinedEvent>(this, OnStandingsChanged);
            EventManager.Instance?.Subscribe<PlayerLeftEvent>(this, OnStandingsChanged);
            EventManager.Instance?.Subscribe<PlayerNameChangedEvent>(this, OnStandingsChanged);
            // The best-throw text comes from MatchHud, which updates on this event.
            EventManager.Instance?.Subscribe<ThrowCompletedEvent>(this, OnStandingsChanged);
        }

        /// <summary>
        /// Only raises the flag; the rebuild is in Update, after every other subscriber (scores,
        /// roster, MatchHud's best throws) has settled, so handler order does not matter.
        /// </summary>
        private void OnStandingsChanged<T>(T e) where T : IEvent
        {
            _dirty = true;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey)) isVisible = !isVisible;

            if (_turns == null || _turns.Phase != MatchPhase.Results) return;

            // Only when an event said something changed — not every frame. Raised on entering
            // Results too (MatchPhaseChangedEvent), so the first Results frame builds it.
            if (!_dirty) return;
            _dirty = false;

            _standings.Clear();
            _standings.AddRange(_turns.RebuildStandings());
            RebuildTexts();
        }

        private void RebuildTexts()
        {
            var rounds = _turns.RoundsPerMatch;
            _subtitleText = $"after {rounds} round{(rounds == 1 ? "" : "s")}";
            _tieText = IsTied() ? $"Tied at {_standings[0].Score} — no winner" : null;

            _nameTexts.Clear();
            _numberTexts.Clear();
            for (var i = 0; i < _standings.Count; i++)
            {
                var standing = _standings[i];

                var displayName = PlayerNames.HasInstance
                    ? PlayerNames.Instance.DisplayNameFor(standing.PlayerId)
                    : standing.PlayerId;
                _nameTexts.Add($"{i + 1}.  {displayName}");

                var best = _hud != null ? _hud.BestThrowText(standing.PlayerId) : null;
                _numberTexts.Add(best != null
                    ? $"{best}{MatchHud.Separator}{standing.Score}pts"
                    : $"{standing.Score}pts");
            }
        }

        private void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);

            if (_panelTexture != null) Destroy(_panelTexture);
            if (_dotTexture != null) Destroy(_dotTexture);
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
            _rowRight = new GUIStyle(_row) { alignment = TextAnchor.MiddleRight };
            _winnerRowRight = new GUIStyle(_winnerRow) { alignment = TextAnchor.MiddleRight };
            _hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(24 * scale),
                alignment = TextAnchor.MiddleCenter,
            };

            _panelTexture = new Texture2D(1, 1);
            _panelTexture.SetPixel(0, 0, new Color(0.04f, 0.06f, 0.10f, 0.95f));
            _panelTexture.Apply();

            _dotTexture = new Texture2D(1, 1);
            _dotTexture.SetPixel(0, 0, Color.white);
            _dotTexture.Apply();
        }

        /// <remarks>
        /// Centred and sized to its rows rather than a fixed 80%-tall panel, so one player's
        /// result is not a small line in a big empty box and the lake shows around it. Rows
        /// match MatchHud's leaderboard — colour dot, name, numbers — and add each player's
        /// best throw of the match beside the total. Keyboard hints are gone: Space and F4
        /// still work for the host, but the screen speaks to the phones.
        /// </remarks>
        private void OnGUI()
        {
            if (!isVisible) return;
            if (_turns == null || _turns.Phase != MatchPhase.Results) return;

            EnsureStyles();

            var rowCount = _standings == null || _standings.Count == 0 ? 1 : _standings.Count;
            var tied = IsTied();

            var pad = Mathf.Round(_row.fontSize * 1.2f);
            var titleHeight = _title.fontSize * 1.4f;
            var subtitleHeight = _subtitle.fontSize * 1.6f;
            var rowHeight = _winnerRow.fontSize * 1.5f;
            var tieHeight = tied ? subtitleHeight : 0f;
            var hintHeight = _hint.fontSize * 2.2f;

            var width = Mathf.Min(Screen.width * 0.6f, _row.fontSize * 26f);
            var height = pad + titleHeight + subtitleHeight + pad * 0.5f + rowHeight * rowCount +
                         tieHeight + pad * 0.5f + hintHeight + pad * 0.5f;
            var panel = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

            GUI.DrawTexture(panel, _panelTexture);

            var inner = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, panel.height - pad * 2);
            var y = inner.y;

            GUI.Label(new Rect(inner.x, y, inner.width, titleHeight), "FINAL SCORES", _title);
            y += titleHeight;
            GUI.Label(new Rect(inner.x, y, inner.width, subtitleHeight),
                      _subtitleText, _subtitle);
            y += subtitleHeight + pad * 0.5f;

            y = DrawStandings(inner, y, rowHeight, tied);

            if (tied)
            {
                GUI.Label(new Rect(inner.x, y, inner.width, tieHeight),
                          _tieText, _subtitle);
                y += tieHeight;
            }

            y += pad * 0.5f;
            GUI.Label(new Rect(inner.x, y, inner.width, hintHeight), "Tap PLAY AGAIN on your phone", _hint);
        }

        /// <summary>
        /// A shared top score means no winner. Highlighting the first row anyway would
        /// silently pick one arbitrarily, off nothing but sort order.
        /// </summary>
        private bool IsTied() =>
            _standings != null && _standings.Count > 1 && _standings[1].Score == _standings[0].Score;

        /// <returns>The y just below the last row drawn.</returns>
        private float DrawStandings(Rect inner, float y, float rowHeight, bool tied)
        {
            if (_standings == null || _standings.Count == 0)
            {
                GUI.Label(new Rect(inner.x, y, inner.width, rowHeight), "Everybody left before the end.", _subtitle);
                return y + rowHeight;
            }

            for (var i = 0; i < _standings.Count; i++)
            {
                var standing = _standings[i];
                var isWinner = !tied && i == 0;
                var nameStyle = isWinner ? _winnerRow : _row;
                var numbersStyle = isWinner ? _winnerRowRight : _rowRight;

                var dot = nameStyle.fontSize * 0.55f;
                var previous = GUI.color;
                GUI.color = PlayerColours.HasInstance ? PlayerColours.Instance.For(standing.PlayerId) : Color.white;
                GUI.DrawTexture(new Rect(inner.x, y + (rowHeight - dot) * 0.5f, dot, dot), _dotTexture);

                GUI.color = isWinner ? new Color(1f, 0.85f, 0.4f) : previous;

                var textX = inner.x + dot * 1.8f;
                GUI.Label(new Rect(textX, y, inner.width * 0.45f, rowHeight), _nameTexts[i], nameStyle);

                GUI.Label(new Rect(textX, y, inner.xMax - textX, rowHeight), _numberTexts[i], numbersStyle);

                GUI.color = previous;
                y += rowHeight;
            }

            return y;
        }
    }
}
