using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using EventTypes.SkipEvents;
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
        [SerializeField] private int roundsPerMatch = 3;

        [Tooltip("Starts the match from the lobby, and starts the next one from the results screen.")]
        [SerializeField] private KeyCode startMatchKey = KeyCode.Space;

        [Tooltip("Editor shortcut to hand the turn to the next player. Useful when a " +
                 "test client cannot throw for itself.")]
        [SerializeField] private KeyCode nextTurnKey = KeyCode.N;

        private readonly List<string> _players = new();
        private readonly List<PlayerStanding> _standings = new();
        private int _current = -1;
        private int _round;

        public MatchPhase Phase { get; private set; } = MatchPhase.Lobby;

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
        }

        private void OnThrowCompleted(ThrowCompletedEvent e)
        {
            // Only the active player's rock advances the turn. When the active player
            // disconnects mid-flight, OnPlayerLeft has already handed the turn on —
            // the orphaned rock landing later must not advance it a second time and
            // cost the newly-promoted player their throw.
            if (Phase != MatchPhase.Playing) return;
            if (!string.IsNullOrEmpty(e.PlayerId) && e.PlayerId != ActivePlayer) return;

            NextTurn();
        }

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

            _round = 0;
            Phase = MatchPhase.Playing;

            Debug.Log($"[TurnManager] Match started — {_players.Count} players, {roundsPerMatch} rounds");
            StartTurn(0);
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
            Phase = MatchPhase.Results;
            _current = -1;

            RebuildStandings();

            // A tie at the top has no single winner, and claiming one would be a lie the
            // results screen then has to render.
            var winner = _standings.Count > 0 &&
                         (_standings.Count == 1 || _standings[0].Score > _standings[1].Score)
                ? _standings[0].PlayerId
                : null;

            Debug.Log($"[TurnManager] Match over after {_round} rounds — winner: {winner ?? "tied"}");

            SendTurnStates();
            EventManager.Instance?.Publish(
                new MatchEndedEvent(_standings.ToArray(), winner, _round));
        }

        /// <summary>Back to the join screen with scores cleared, ready for another match.</summary>
        public void ReturnToLobby()
        {
            Phase = MatchPhase.Lobby;
            _current = -1;
            _round = 0;

            ScoreManager.Instance?.ResetScores();
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
            EventManager.Instance?.Publish(new TurnStartedEvent(ActivePlayer, _round));
            Debug.Log($"[TurnManager] Round {_round + 1}/{roundsPerMatch}: {ActivePlayer}'s turn");
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

        /// <summary>Match state for the idle phones. Cheap enough to call on any change.</summary>
        public void BroadcastSpectate(int liveSkips = 0, float liveDistance = 0f, string rockName = "")
        {
            RelayClient.Instance?.Send(OutboundMessages.BuildSpectate(
                ActivePlayer ?? string.Empty, rockName, liveSkips, liveDistance,
                ScoreManager.Instance?.Scores,
                Phase.ToString().ToUpperInvariant(), _round + 1, roundsPerMatch));
        }

        protected override void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }
    }
}
