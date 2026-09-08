using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using EventTypes.SkipEvents;
using Singleton;
using UnityEngine;

namespace Managers
{
    /// <summary>
    /// Owns every point value in the game. Gameplay code publishes events describing
    /// what happened; this is the only place that decides what those are worth, so
    /// retuning scoring never touches physics or turn logic.
    /// </summary>
    public class ScoreManager : SingletonBase<ScoreManager>
    {
        [Header("Point values")]
        [SerializeField] private int pointsPerSkip = 10;
        [SerializeField] private int pointsPerMetre = 1;

        private readonly Dictionary<string, int> _scores = new();

        // Skips arrive mid-flight, before we know the throw completed. They're banked
        // here and committed to the player on ThrowCompletedEvent.
        private int _pendingThrowPoints;

        private int _currentSkips;

        public IReadOnlyDictionary<string, int> Scores => _scores;

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, OnSkipOccurred);
            EventManager.Instance?.Subscribe<ThrowCompletedEvent>(this, OnThrowCompleted);
            EventManager.Instance?.Subscribe<TurnStartedEvent>(this, OnTurnStarted);
        }

        private void OnTurnStarted(TurnStartedEvent e)
        {
            // The bucket is global, not per-player. A throw that never completes —
            // its player disconnected mid-flight, say — would otherwise leave banked
            // skip points to be silently credited to whoever throws next.
            _pendingThrowPoints = 0;
        }

        private void OnSkipOccurred(SkipOccurredEvent e)
        {
            _pendingThrowPoints += pointsPerSkip;
            _currentSkips++;
        }

        private void OnThrowCompleted(ThrowCompletedEvent e)
        {
            if (_currentSkips >= 1)
            {
                var total = _pendingThrowPoints + Mathf.RoundToInt(e.Distance * pointsPerMetre);
                _pendingThrowPoints = 0;

                if (string.IsNullOrEmpty(e.PlayerId))
                    return;

                _scores.TryGetValue(e.PlayerId, out var current);
                var updated = current + total;
                _scores[e.PlayerId] = updated;

                // After the dictionary is written, never before. TurnManager handles this same
                // ThrowCompletedEvent immediately after this method returns and reads the scores
                // to build standings and broadcast them — a subscriber woken here finds the
                // total already banked, matching what every other reader will see.
                //
                // Below the empty-id guard above, so a throw with no player attached scores
                // nothing and announces nothing rather than announcing points that went nowhere.
                EventManager.Instance?.Publish(new ScoreAwardedEvent(e.PlayerId, total, updated));
            }
            else
            {
                _pendingThrowPoints = 0;

                if (string.IsNullOrEmpty(e.PlayerId))
                    return;

                _scores.TryGetValue(e.PlayerId, out var current);
                var updated = current + _pendingThrowPoints;
                _scores[e.PlayerId] = updated;

                EventManager.Instance?.Publish(new ScoreAwardedEvent(e.PlayerId, _pendingThrowPoints, updated));
            }

            _currentSkips = 0;

        }

        public int GetScore(string playerId)
        {
            _scores.TryGetValue(playerId, out var score);
            return score;
        }

        public void ResetScores()
        {
            _scores.Clear();
            _pendingThrowPoints = 0;
        }

        protected override void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }
    }
}
