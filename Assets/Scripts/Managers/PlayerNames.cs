using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using Net;
using Singleton;
using UnityEngine;

namespace Managers
{
    // Owns what display name every player shows as. doc-ref 2c28 docs/4-systems/ScoringAndTurns.md
    public class PlayerNames : SingletonBase<PlayerNames>
    {
        private RelayClient _relay;
        private readonly Dictionary<string, string> _names = new();

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            EventManager.Instance?.Subscribe<PlayerJoinedEvent>(this, OnPlayerJoined);
            EventManager.Instance?.Subscribe<PlayerLeftEvent>(this, OnPlayerLeft);
        }

        /// <summary>
        /// Subscribes straight to RelayClient, bound once in Start, after Bootstrap's
        /// Awake has created every manager.
        /// </summary>
        private void Start()
        {
            if (!RelayClient.HasInstance)
            {
                Debug.LogWarning("[PlayerNames] No RelayClient at Start; phone messages will not be received.");
                return;
            }

            _relay = RelayClient.Instance;
            _relay.SetNameReceived += OnSetName;
        }

        private void OnSetName(SetNameMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.playerId)) return;

            // An empty/whitespace name is dropped rather than accepted: it would make the
            // roster line unreadable, and the player still has whatever name they had
            // before (their fallback label, at worst) rather than a blank row.
            var trimmed = message.name?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;

            if (_names.TryGetValue(message.playerId, out var current) && current == trimmed) return;

            _names[message.playerId] = trimmed;
            EventManager.Instance?.Publish(new PlayerNameChangedEvent(message.playerId, trimmed));
        }

        /// <summary>
        /// This player's display name: whatever SET_NAME last set, else the fallback label
        /// assigned when they joined, else the raw id for a player nobody has heard of yet.
        /// </summary>
        public string DisplayNameFor(string playerId)
        {
            if (!string.IsNullOrEmpty(playerId) && _names.TryGetValue(playerId, out var name))
            {
                return name;
            }

            return playerId ?? string.Empty;
        }

        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            // A reconnect fires this again for a player who already has a name — leaving it
            // is the point, same as PlayerColours.OnPlayerJoined. The device's own SET_NAME
            // (sent on every JOINED, not just a first join) overwrites this immediately if
            // the player actually has a name; this only covers the gap before it arrives.
            if (_names.ContainsKey(e.PlayerId)) return;

            _names[e.PlayerId] = e.PlayerId;
        }

        private void OnPlayerLeft(PlayerLeftEvent e)
        {
            // Deliberately not removed: a dropped phone is usually seconds from coming
            // back, and its name must survive the round trip. Cleared only once nobody is
            // left, the same point PlayerColours and TurnManager treat as session end.
            if (TurnManager.HasInstance && TurnManager.Instance.Players.Count > 0) return;

            _names.Clear();
        }

        protected override void OnDestroy()
        {
            if (_relay != null) _relay.SetNameReceived -= OnSetName;
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }
    }
}
