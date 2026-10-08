using System;
using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using EventTypes.SkipEvents;
using Managers;
using UnityEngine;

namespace Ui
{
    // IMGUI by the owner's call on 2026-09-30; numbers only, visible only while Playing, reading events
    // and ScoreManager and never computing points. doc-ref 46c4 docs/4-systems/Presentation.md
    public class MatchHud : MonoBehaviour
    {
        internal const string Separator = "   ";

        private readonly struct ThrowResult
        {
            public readonly float Distance;
            public readonly int Skips;

            public ThrowResult(float distance, int skips)
            {
                Distance = distance;
                Skips = skips;
            }
        }

        private readonly Dictionary<string, ThrowResult> _lastThrows = new();

        // Most skips, distance breaking a tie — skips are the headline number. Read by
        // ResultsScreen, so the final standings can show each player's best alongside the total.
        private readonly Dictionary<string, ThrowResult> _bestThrows = new();

        // The throw the top-centre readout is about: live while in flight, held after it lands
        // until the next stone is launched, so the room has the whole next player's aim to read it.
        private string _readoutPlayer;
        private int _liveSkips;
        private bool _inFlight;
        private ThrowResult? _readoutResult;

        private TurnManager _turns;
        private readonly List<string> _sortedPlayers = new();

        // Leaderboard right-hand text, index-aligned with _sortedPlayers. Built with the sort so
        // OnGUI (several passes a frame) draws cached strings instead of formatting them.
        private readonly List<string> _statsRows = new();

        // Set by anything that can change order, membership or a row's numbers; cleared by the
        // rebuild in Update. Starts true so the first Playing frame builds the board.
        private bool _standingsDirty = true;

        // The top-centre text, rebuilt when the live skips or the landed result change. Null
        // means nothing to show.
        private string _readoutText;

        // Static and non-capturing: created once, so sorting allocates no delegate.
        private static readonly Comparison<string> ByScoreDescending =
            (a, b) => ScoreOf(b).CompareTo(ScoreOf(a));

        private GUIStyle _readout, _row, _rowRight;
        private Texture2D _panelTexture, _dotTexture;

        private void Start()
        {
            // Start, not Awake: Bootstrap creates EventManager before this, but Start guarantees
            // every Awake in the bootstrap pass has run first.
            EventManager.Instance?.Subscribe<RockLaunchedEvent>(this, OnRockLaunched);
            EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, OnSkipOccurred);
            EventManager.Instance?.Subscribe<ThrowCompletedEvent>(this, OnThrowCompleted);
            EventManager.Instance?.Subscribe<MatchPhaseChangedEvent>(this, OnPhaseChanged);
            EventManager.Instance?.Subscribe<ScoreAwardedEvent>(this, OnStandingsChanged);
            EventManager.Instance?.Subscribe<PlayerJoinedEvent>(this, OnStandingsChanged);
            EventManager.Instance?.Subscribe<PlayerLeftEvent>(this, OnStandingsChanged);
            EventManager.Instance?.Subscribe<TurnStartedEvent>(this, OnStandingsChanged);

            if (TurnManager.HasInstance) _turns = TurnManager.Instance;
            else Debug.LogWarning("[MatchHud] No TurnManager at Start; the HUD will never show.");
        }

        private void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            if (_panelTexture != null) Destroy(_panelTexture);
            if (_dotTexture != null) Destroy(_dotTexture);
        }

        private void OnRockLaunched(RockLaunchedEvent e)
        {
            // RockLaunchedEvent carries no player; the thrower is whoever's turn it is.
            _readoutPlayer = TurnManager.HasInstance ? TurnManager.Instance.ActivePlayer : null;
            _liveSkips = 0;
            _inFlight = true;
            _readoutResult = null;
            _readoutText = "0sk";
        }

        private void OnSkipOccurred(SkipOccurredEvent e)
        {
            if (!_inFlight) return;

            _liveSkips++;
            _readoutText = $"{_liveSkips}sk";
        }

        /// <summary>
        /// Only raises the flag; the sort happens in Update. Handler order between this and
        /// TurnManager/ScoreManager is therefore irrelevant — by the time Update runs, the
        /// roster and scores are settled.
        /// </summary>
        private void OnStandingsChanged<T>(T e) where T : IEvent
        {
            _standingsDirty = true;
        }

        private void OnThrowCompleted(ThrowCompletedEvent e)
        {
            var result = new ThrowResult(e.Distance, e.TotalSkips);

            if (!string.IsNullOrEmpty(e.PlayerId))
            {
                _lastThrows[e.PlayerId] = result;

                if (!_bestThrows.TryGetValue(e.PlayerId, out var best) ||
                    result.Skips > best.Skips ||
                    (result.Skips == best.Skips && result.Distance > best.Distance))
                {
                    _bestThrows[e.PlayerId] = result;
                }
                _readoutPlayer = e.PlayerId;
            }

            _inFlight = false;
            _readoutResult = result;
            _readoutText = FormatThrow(result);

            // The player's last-throw text changed (the score arrives via ScoreAwardedEvent).
            _standingsDirty = true;
        }

        private void OnPhaseChanged(MatchPhaseChangedEvent e)
        {
            // Every phase change, including into a re-pick: scores may have been reset and the
            // roster is only drawn while Playing, so refresh before it is next shown.
            _standingsDirty = true;

            // Any start of play is a fresh match except resuming from a re-pick pause (Choosing),
            // which keeps the match — and its throws — going.
            if (e.To != MatchPhase.Playing || e.From == MatchPhase.Choosing) return;

            _lastThrows.Clear();
            _bestThrows.Clear();
            _readoutPlayer = null;
            _readoutResult = null;
            _inFlight = false;
            _readoutText = null;
        }

        private void Update()
        {
            if (_turns == null || _turns.Phase != MatchPhase.Playing) return;

            // Only when an event said something changed — not every frame, and never in OnGUI.
            if (!_standingsDirty) return;
            _standingsDirty = false;

            _sortedPlayers.Clear();
            _sortedPlayers.AddRange(_turns.Players);
            _sortedPlayers.Sort(ByScoreDescending);

            _statsRows.Clear();
            foreach (var playerId in _sortedPlayers)
            {
                _statsRows.Add(_lastThrows.TryGetValue(playerId, out var last)
                    ? $"{FormatThrow(last)}{Separator}{ScoreOf(playerId)}pts"
                    : $"{ScoreOf(playerId)}pts");
            }
        }

        private static int ScoreOf(string playerId) =>
            ScoreManager.HasInstance ? ScoreManager.Instance.GetScore(playerId) : 0;

        private void OnGUI()
        {
            if (_turns == null || _turns.Phase != MatchPhase.Playing) return;

            EnsureStyles();
            DrawReadout();
            DrawLeaderboard();
        }

        /// <summary>Top-centre: skips counting up in flight, then distance and skips once it lands.</summary>
        /// <remarks>
        /// Kept to one line in the top tenth of the screen: the chase camera settles the stone at
        /// frame centre (presentation plan, camera fact), so the readout must never grow down to it.
        /// </remarks>
        private void DrawReadout()
        {
            var text = _readoutText;
            if (text == null) return;

            var rect = new Rect(0, Screen.height * 0.02f, Screen.width, _readout.fontSize * 1.4f);

            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.Label(new Rect(rect.x + 3, rect.y + 3, rect.width, rect.height), text, _readout);
            GUI.color = ColourOf(_readoutPlayer);
            GUI.Label(rect, text, _readout);
            GUI.color = previous;
        }

        /// <summary>Top-left: one row per player, highest total first.</summary>
        /// <remarks>
        /// Top-left because the late-join QR card (JoinScreen.DrawCompactCard) owns top-right
        /// for the whole match.
        /// </remarks>
        private void DrawLeaderboard()
        {
            if (_sortedPlayers.Count == 0) return;

            var margin = Mathf.Round(Screen.height * 0.025f);
            var rowHeight = _row.fontSize * 1.5f;
            var width = Mathf.Min(Screen.width * 0.3f, _row.fontSize * 22f);
            var panel = new Rect(margin, margin, width, rowHeight * _sortedPlayers.Count + margin);

            GUI.DrawTexture(panel, _panelTexture);

            var dot = _row.fontSize * 0.6f;
            var y = panel.y + margin * 0.5f;

            for (var i = 0; i < _sortedPlayers.Count; i++)
            {
                var playerId = _sortedPlayers[i];
                var previous = GUI.color;
                GUI.color = ColourOf(playerId);
                GUI.DrawTexture(new Rect(panel.x + margin * 0.5f, y + (rowHeight - dot) * 0.5f, dot, dot), _dotTexture);
                GUI.color = previous;

                var nameX = panel.x + margin * 0.5f + dot * 1.6f;
                var name = PlayerNames.HasInstance ? PlayerNames.Instance.DisplayNameFor(playerId) : playerId;
                GUI.Label(new Rect(nameX, y, width * 0.4f, rowHeight), name, _row);

                GUI.Label(new Rect(nameX, y, panel.xMax - nameX - margin * 0.5f, rowHeight), _statsRows[i], _rowRight);

                y += rowHeight;
            }
        }

        /// <summary>This match's best throw for a player, formatted, or null if they have not thrown.</summary>
        public string BestThrowText(string playerId) =>
            _bestThrows.TryGetValue(playerId, out var best) ? FormatThrow(best) : null;

        private static string FormatThrow(ThrowResult result) => FormatThrow(result.Distance, result.Skips);

        /// <summary>The one number format both TV screens use: "143m   9sk".</summary>
        internal static string FormatThrow(float distance, int skips) =>
            $"{Mathf.RoundToInt(distance)}m{Separator}{skips}sk";

        private static Color ColourOf(string playerId) =>
            !string.IsNullOrEmpty(playerId) && PlayerColours.HasInstance
                ? PlayerColours.Instance.For(playerId)
                : Color.white;

        private void EnsureStyles()
        {
            if (_readout != null) return;

            // Scaled off screen height, as JoinScreen and ResultsScreen are, so it reads from a
            // couch whether this is a small editor Game view or a TV.
            var scale = Mathf.Clamp(Screen.height / 1080f, 0.5f, 3f);

            _readout = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(64 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _row = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(26 * scale),
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
            };
            _rowRight = new GUIStyle(_row) { alignment = TextAnchor.MiddleRight };

            _panelTexture = new Texture2D(1, 1);
            _panelTexture.SetPixel(0, 0, new Color(0.04f, 0.06f, 0.10f, 0.7f));
            _panelTexture.Apply();

            _dotTexture = new Texture2D(1, 1);
            _dotTexture.SetPixel(0, 0, Color.white);
            _dotTexture.Apply();
        }
    }
}
