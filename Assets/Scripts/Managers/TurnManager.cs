using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using EventTypes.SkipEvents;
using Game;
using Net;
using Singleton;
using UnityEngine;

namespace Managers
{
    /// <summary>Where the match is in its lifecycle. Drives what every screen shows.</summary>
    public enum MatchPhase
    {
        /// <summary>Players are joining. Nobody may throw yet.</summary>
        Lobby,

        /// <summary>Turns are rotating. Exactly one player may throw at a time.</summary>
        Playing,

        /// <summary>Final standings are up. Throws are refused until the next match.</summary>
        Results,
    }

    /// <summary>
    /// Owns the player roster, whose turn it is, and where the match is in its lifecycle.
    /// The only thing that tells a phone to show its throw screen.
    /// </summary>
    /// <remarks>
    /// Publishes match events; deliberately computes no score — that lives solely in
    /// ScoreManager, so scoring can be retuned without touching turn logic.
    ///
    /// The match runs a fixed number of rounds, each giving every player one throw, and
    /// then ends. Before this existed turns rotated forever and the round counter was
    /// incremented but never read, so a session had no way to finish.
    /// </remarks>
    public class TurnManager : SingletonBase<TurnManager>
    {
        [Header("Match")]
        [Tooltip("Each round gives every player one throw.")]
        [SerializeField] private int roundsPerMatch = 5;

        [Tooltip("Starts the match from the lobby, and starts the next one from the results screen.")]
        [SerializeField] private KeyCode startMatchKey = KeyCode.Space;

        [Tooltip("Editor shortcut to hand the turn to the next player. Useful when a " +
                 "test client cannot throw for itself.")]
        [SerializeField] private KeyCode nextTurnKey = KeyCode.N;

        private readonly List<string> _players = new();
        private readonly List<PlayerStanding> _standings = new();
        private int _current = -1;
        private int _round;

        // The throw that just finished, carried on the next broadcast. Kept here rather
        // than streamed while the rock flies: phones are not a spectator screen in a couch
        // game, so the only moment the numbers matter is once they are final.
        private string _lastPlayer = string.Empty;
        private int _lastSkips;
        private float _lastDistance;

        public MatchPhase Phase { get; private set; } = MatchPhase.Lobby;

        /// <summary>
        /// The one place Phase is ever assigned. Announces real changes and swallows
        /// redundant ones.
        /// </summary>
        /// <remarks>
        /// A setter rather than three assignments with a Publish next to each, because the
        /// duplicate that eventually gets missed is the bug: a phase change that moves the
        /// field without announcing it leaves music and screens on the previous phase with
        /// nothing in the log to say why.
        ///
        /// The equality check is not tidiness either. ReturnToLobby is reachable from the
        /// lobby — OnPlayerLeft calls it whenever the roster empties, and the guard there is
        /// on Phase != Lobby, but StartMatch with no players and other paths can still land
        /// here with nothing to change. Announcing that would restart lobby music every time
        /// the last player disconnects.
        ///
        /// Published after the assignment, so a subscriber reading Phase sees the new value
        /// rather than the one it is being told is over.
        /// </remarks>
        private void SetPhase(MatchPhase phase)
        {
            if (Phase == phase) return;

            var from = Phase;
            Phase = phase;

            EventManager.Instance?.Publish(new MatchPhaseChangedEvent(from, phase));
        }

        /// <summary>Null outside the Playing phase — nobody may throw in a lobby.</summary>
        public string ActivePlayer =>
            Phase == MatchPhase.Playing && _current >= 0 && _current < _players.Count
                ? _players[_current]
                : null;

        public IReadOnlyList<string> Players => _players;
        public int RoundsPerMatch => roundsPerMatch;

        /// <summary>Zero-based index of the round in progress.</summary>
        public int CurrentRound => _round;

        public bool CanStartMatch => Phase != MatchPhase.Playing && _players.Count > 0;
        public KeyCode StartMatchKey => startMatchKey;

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            EventManager.Instance?.Subscribe<PlayerJoinedEvent>(this, OnPlayerJoined);
            EventManager.Instance?.Subscribe<PlayerLeftEvent>(this, OnPlayerLeft);
            EventManager.Instance?.Subscribe<ThrowCompletedEvent>(this, OnThrowCompleted);

            // Otherwise a confirmed pick sits invisible until some unrelated event (another
            // player joining, say) happens to trigger the next SPECTATE — a lobby where
            // everyone has already joined could confirm rocks all match and nobody's phone
            // would ever show it.
            EventManager.Instance?.Subscribe<RockPickedEvent>(this, OnRockPicked);
        }

        private void OnRockPicked(RockPickedEvent e) => BroadcastSpectate();

        private void Update()
        {
            if (Input.GetKeyDown(startMatchKey) && CanStartMatch) StartMatch();
            if (Input.GetKeyDown(nextTurnKey) && Phase == MatchPhase.Playing) NextTurn();
        }

        // ------------------------------------------------------------------ roster

        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            if (!_players.Contains(e.PlayerId)) _players.Add(e.PlayerId);
            Debug.Log($"[TurnManager] {e.PlayerId} joined ({_players.Count} players)");

            // Never auto-starts. A player walking in mid-lobby should not begin the match
            // for everyone else, and a late joiner mid-match just waits for their turn.
            SendTurnStates();
        }

        private void OnPlayerLeft(PlayerLeftEvent e)
        {
            var index = _players.IndexOf(e.PlayerId);
            if (index < 0) return;

            var wasActive = index == _current && Phase == MatchPhase.Playing;
            _players.RemoveAt(index);

            if (_players.Count == 0)
            {
                // Nobody left to play for. Reset rather than stranding the match
                // mid-round with an empty roster.
                _current = -1;
                if (Phase != MatchPhase.Lobby) ReturnToLobby();
                return;
            }

            // Removing someone before the active player shifts the index under it.
            if (index < _current) _current--;

            if (wasActive) StartTurn(_current);
            else SendTurnStates();
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Begins a fresh match: scores cleared, round 0, first player up.</summary>
        public void StartMatch()
        {
            if (_players.Count == 0)
            {
                Debug.LogWarning("[TurnManager] Cannot start a match with no players.");
                return;
            }

            ScoreManager.Instance?.ResetScores();
            ClearLastThrow();

            _round = 0;
            SetPhase(MatchPhase.Playing);

            Debug.Log($"[TurnManager] Match started — {_players.Count} players, {roundsPerMatch} rounds");
            StartTurn(0);
        }

        /// <summary>
        /// A rock has sunk. Keeps the result, then advances the turn — which broadcasts on
        /// the way through, carrying these numbers with it.
        /// </summary>
        /// <remarks>
        /// ScoreManager subscribes to this event before TurnManager does, and EventManager
        /// delivers in subscription order, so the points are already banked by the time the
        /// broadcast below reads them. See Bootstrap, where that ordering is set deliberately.
        /// </remarks>
        private void OnThrowCompleted(ThrowCompletedEvent e)
        {
            _lastPlayer = e.PlayerId ?? string.Empty;
            _lastSkips = e.TotalSkips;
            _lastDistance = e.Distance;

            NextTurn();
        }

        /// <summary>Forgets the last throw, so a new match does not open showing the old one.</summary>
        private void ClearLastThrow()
        {
            _lastPlayer = string.Empty;
            _lastSkips = 0;
            _lastDistance = 0f;
        }

        /// <summary>
        /// Advances to the next player, or ends the match if the last round is done.
        /// Called on ThrowCompletedEvent, so a sunk rock is what drives the match forward.
        /// </summary>
        public void NextTurn()
        {
            if (Phase != MatchPhase.Playing || _players.Count == 0) return;

            if (_current + 1 >= _players.Count)
            {
                _round++;
                if (_round >= roundsPerMatch)
                {
                    EndMatch();
                    return;
                }
            }

            StartTurn(_current + 1);
        }

        private void EndMatch()
        {
            SetPhase(MatchPhase.Results);
            _current = -1;

            RebuildStandings();

            // A tie at the top has no single winner, and claiming one would be a lie the
            // results screen then has to render.
            var winner = _standings.Count > 0 &&
                         (_standings.Count == 1 || _standings[0].Score > _standings[1].Score)
                ? _standings[0].PlayerId
                : null;

            Debug.Log($"[TurnManager] Match over after {_round} rounds — winner: {winner ?? "tied"}");

            // The computed winner is passed through rather than recomputed downstream, so
            // the tie rule above stays the single definition of who won. Null here means a
            // tie and is expected — see MatchEndedEvent.
            EventManager.Instance?.Publish(new MatchEndedEvent(winner));

            SendTurnStates();
        }

        /// <summary>Back to the join screen with scores cleared, ready for another match.</summary>
        public void ReturnToLobby()
        {
            SetPhase(MatchPhase.Lobby);
            _current = -1;
            _round = 0;

            ScoreManager.Instance?.ResetScores();
            ClearLastThrow();
            SendTurnStates();

            Debug.Log("[TurnManager] Back in the lobby");
        }

        private void StartTurn(int index)
        {
            if (_players.Count == 0)
            {
                _current = -1;
                return;
            }

            // Positive modulo so a negative index wraps to the end rather than throwing.
            _current = ((index % _players.Count) + _players.Count) % _players.Count;

            SendTurnStates();
            Debug.Log($"[TurnManager] Round {_round + 1}/{roundsPerMatch}: {ActivePlayer}'s turn");

            // After _current is set, so ActivePlayer resolves to the player this announces.
            // After SendTurnStates too, so the phone that is now up has already been told
            // before anything on the TV starts celebrating the handover.
            EventManager.Instance?.Publish(
                new TurnStartedEvent(ActivePlayer, _round, roundsPerMatch));
        }

        // ------------------------------------------------------------------ standings

        /// <summary>
        /// Current standings, highest first. Rebuilt on demand rather than cached per
        /// frame — the results screen calls this once per Update, not once per OnGUI.
        /// </summary>
        public IReadOnlyList<PlayerStanding> RebuildStandings()
        {
            _standings.Clear();

            var scores = ScoreManager.HasInstance ? ScoreManager.Instance : null;
            foreach (var player in _players)
            {
                _standings.Add(new PlayerStanding(player, scores?.GetScore(player) ?? 0));
            }

            _standings.Sort((a, b) => b.Score.CompareTo(a.Score));
            return _standings;
        }

        // ------------------------------------------------------------------ outbound

        /// <summary>Every phone is told explicitly, so nobody is left on a stale screen.</summary>
        private void SendTurnStates()
        {
            var active = ActivePlayer;
            foreach (var player in _players)
            {
                RelayClient.Instance?.Send(OutboundMessages.BuildTurn(player, player == active));
            }
            BroadcastSpectate();
        }

        /// <summary>
        /// What the phones call a player's current rock. No longer cached — a global cache
        /// stopped being valid the moment the rock became per-player rather than one
        /// tuning asset for the whole game. "Pebble" on a missing catalog/pick matches the
        /// same fallback RockSelection.For itself degrades to.
        /// </summary>
        private static string RockDisplayNameFor(string playerId)
        {
            var name = RockSelection.HasInstance ? RockSelection.Instance.For(playerId)?.DisplayName : null;
            return string.IsNullOrEmpty(name) ? "Pebble" : name;
        }

        /// <summary>Every connected player's rock, for the shore/lobby readout on idle phones.</summary>
        private IEnumerable<(string PlayerId, string RockId, string RockName)> BuildPlayerRocks()
        {
            foreach (var player in _players)
            {
                var rock = RockSelection.HasInstance ? RockSelection.Instance.For(player) : null;
                yield return (player, rock?.Id ?? string.Empty, RockDisplayNameFor(player));
            }
        }

        /// <summary>
        /// Match state for the idle phones, sent on change only.
        /// </summary>
        /// <remarks>
        /// Private, and worth keeping that way. This took live skip/distance parameters so
        /// RockLauncher could push flight values in four times a second — the most expensive
        /// traffic in the game, feeding a readout nobody looks at during a couch match. With
        /// the parameters gone and no caller outside this class, there is no route back to a
        /// per-frame broadcast from gameplay code.
        /// </remarks>
        private void BroadcastSpectate()
        {
            var active = ActivePlayer;
            RelayClient.Instance?.Send(OutboundMessages.BuildSpectate(
                active ?? string.Empty, active != null ? RockDisplayNameFor(active) : "Pebble",
                _lastPlayer, _lastSkips, _lastDistance,
                ScoreManager.Instance?.Scores,
                Phase.ToString().ToUpperInvariant(), _round + 1, roundsPerMatch,
                BuildPlayerRocks()));
        }

        protected override void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }
    }
}
