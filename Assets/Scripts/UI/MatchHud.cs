using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using EventTypes.SkipEvents;
using Managers;
using UnityEngine;

namespace Ui
{
    /// <summary>
    /// The in-match TV readout: the current or last throw, top-centre, and a slim leaderboard
    /// of every player's last throw and total, top-left.
    /// </summary>
    /// <remarks>
    /// IMGUI, like every other TV screen, by the owner's call on 2026-09-30: usable now, ported
    /// with the rest when docs/plans/presentation-and-ui.md D3 (UI Toolkit) happens. This is a
    /// first cut of D4's elements 1 and 3 — the counter and the scoreboard strip — without the
    /// punch-scaling and fly-in, which need a framework that animates.
    ///
    /// Numbers only, no words: "124m   12sk   1350pts". The format lives in <see cref="Separator"/>
    /// and the two Format methods, so changing how it reads is one place.
    ///
    /// Visible only while Playing. The lobby and the mid-match re-pick are the shore's screen,
    /// and Results has its own.
    ///
    /// Reads, never computes: skips and distance come off the throw's own events and points
    /// off ScoreManager, which the project's scoring rule says is the only owner of points.
    /// </remarks>
    public class MatchHud : MonoBehaviour
    {
        private const string Separator = "   ";

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

        // The throw the top-centre readout is about: live while in flight, held after it lands
        // until the next stone is launched, so the room has the whole next player's aim to read it.
        private string _readoutPlayer;
        private int _liveSkips;
        private bool _inFlight;
        private ThrowResult? _readoutResult;

        private TurnManager _turns;
        private readonly List<string> _sortedPlayers = new();

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
        }

        private void OnSkipOccurred(SkipOccurredEvent e)
        {
            if (_inFlight) _liveSkips++;
        }

        private void OnThrowCompleted(ThrowCompletedEvent e)
        {
            var result = new ThrowResult(e.Distance, e.TotalSkips);

            if (!string.IsNullOrEmpty(e.PlayerId))
            {
                _lastThrows[e.PlayerId] = result;
                _readoutPlayer = e.PlayerId;
            }

            _inFlight = false;
            _readoutResult = result;
        }

        private void OnPhaseChanged(MatchPhaseChangedEvent e)
        {
            // A fresh match starts from Lobby; a re-pick pause (Choosing) keeps the match going,
            // so only leaving the lobby clears last match's throws.
            if (e.From != MatchPhase.Lobby || e.To != MatchPhase.Playing) return;

            _lastThrows.Clear();
            _readoutPlayer = null;
            _readoutResult = null;
            _inFlight = false;
        }

        private void Update()
        {
            if (_turns == null && TurnManager.HasInstance) _turns = TurnManager.Instance;
            if (_turns == null || _turns.Phase != MatchPhase.Playing) return;

            // Once per frame, not per OnGUI pass — OnGUI runs several times a frame and this sorts.
            _sortedPlayers.Clear();
            _sortedPlayers.AddRange(_turns.Players);
            _sortedPlayers.Sort((a, b) => ScoreOf(b).CompareTo(ScoreOf(a)));
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
            string text;
            if (_inFlight) text = $"{_liveSkips}sk";
            else if (_readoutResult.HasValue) text = FormatThrow(_readoutResult.Value);
            else return;

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

            foreach (var playerId in _sortedPlayers)
            {
                var previous = GUI.color;
                GUI.color = ColourOf(playerId);
                GUI.DrawTexture(new Rect(panel.x + margin * 0.5f, y + (rowHeight - dot) * 0.5f, dot, dot), _dotTexture);
                GUI.color = previous;

                var nameX = panel.x + margin * 0.5f + dot * 1.6f;
                var name = PlayerNames.HasInstance ? PlayerNames.Instance.DisplayNameFor(playerId) : playerId;
                GUI.Label(new Rect(nameX, y, width * 0.4f, rowHeight), name, _row);

                var stats = _lastThrows.TryGetValue(playerId, out var last)
                    ? $"{FormatThrow(last)}{Separator}{ScoreOf(playerId)}pts"
                    : $"{ScoreOf(playerId)}pts";

                GUI.Label(new Rect(nameX, y, panel.xMax - nameX - margin * 0.5f, rowHeight), stats, _rowRight);

                y += rowHeight;
            }
        }

        private static string FormatThrow(ThrowResult result) =>
            $"{Mathf.RoundToInt(result.Distance)}m{Separator}{result.Skips}sk";

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
