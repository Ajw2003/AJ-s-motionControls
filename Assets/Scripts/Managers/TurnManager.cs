using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using EventTypes.SkipEvents;
using Net;
using Singleton;
using UnityEngine;

namespace Managers
{
    /// <summary>
    /// Owns the player roster and whose turn it is, and is the only thing that tells
    /// a phone to show its throw screen.
    /// </summary>
    /// <remarks>
    /// Publishes match events; deliberately computes no score — that lives solely in
    /// ScoreManager, so scoring can be retuned without touching turn logic.
    ///
    /// M1 scope: the first player to join gets the turn immediately so the motion
    /// pipeline can be exercised end to end, and N advances manually. From M2 the
    /// turn advances on ThrowCompletedEvent instead, which is already wired below.
    /// </remarks>
    public class TurnManager : SingletonBase<TurnManager>
    {
        [Tooltip("Give the turn to the first player who joins. Useful while testing; " +
                 "turn off once there is a real lobby with a start button.")]
        [SerializeField] private bool autoStartFirstTurn = true;

        [Tooltip("Editor shortcut to hand the turn to the next player.")]
        [SerializeField] private KeyCode nextTurnKey = KeyCode.N;

        private readonly List<string> _players = new();
        private int _current = -1;
        private int _round;

        public string ActivePlayer =>
            _current >= 0 && _current < _players.Count ? _players[_current] : null;

        public IReadOnlyList<string> Players => _players;

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            EventManager.Instance?.Subscribe<PlayerJoinedEvent>(this, OnPlayerJoined);
            EventManager.Instance?.Subscribe<PlayerLeftEvent>(this, OnPlayerLeft);
            EventManager.Instance?.Subscribe<ThrowCompletedEvent>(this, _ => NextTurn());
        }

        private void Update()
        {
            if (Input.GetKeyDown(nextTurnKey)) NextTurn();
        }

        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            if (!_players.Contains(e.PlayerId)) _players.Add(e.PlayerId);
            Debug.Log($"[TurnManager] {e.PlayerId} joined ({_players.Count} players)");

            if (_current < 0 && autoStartFirstTurn) StartTurn(0);
            else SendTurnStates(); // a late joiner still needs telling it is not their turn
        }

        private void OnPlayerLeft(PlayerLeftEvent e)
        {
            var index = _players.IndexOf(e.PlayerId);
            if (index < 0) return;

            var wasActive = index == _current;
            _players.RemoveAt(index);

            if (_players.Count == 0)
            {
                _current = -1;
                return;
            }

            // Removing someone before the active player shifts the index under it.
            if (index < _current) _current--;

            if (wasActive) StartTurn(_current);
            else SendTurnStates();
        }

        public void NextTurn()
        {
            if (_players.Count == 0) return;
            if (_current + 1 >= _players.Count) _round++;
            StartTurn(_current + 1);
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
            Debug.Log($"[TurnManager] Round {_round}: {ActivePlayer}'s turn");
        }

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
                ScoreManager.Instance?.Scores));
        }

        protected override void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }
    }
}
