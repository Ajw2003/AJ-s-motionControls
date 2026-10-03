using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using Game;
using Singleton;
using UnityEngine;

namespace Managers
{
    // Owns which rock every player is playing with. doc-ref a6f0 docs/4-systems/RockCatalogAndTuning.md
    public class RockSelection : SingletonBase<RockSelection>
    {
        [Tooltip("When on, every player is assigned a rock at match start instead of using " +
                 "their pointed pick. Phone picks are ignored while this is on.")]
        [SerializeField] private bool randomRocks;

        private RockCatalog _catalog;
        private readonly Dictionary<string, string> _picks = new();

        public bool RandomRocks
        {
            get => randomRocks;
            set => randomRocks = value;
        }

        // The seed the current match's rocks were drawn from, or null when players picked their own. doc-ref 0a57 docs/4-systems/RockCatalogAndTuning.md
        public int? LastRandomSeed { get; private set; }

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            _catalog = RockCatalog.LoadOrWarn();

            EventManager.Instance?.Subscribe<PlayerJoinedEvent>(this, OnPlayerJoined);
            EventManager.Instance?.Subscribe<PlayerLeftEvent>(this, OnPlayerLeft);
            EventManager.Instance?.Subscribe<MatchPhaseChangedEvent>(this, OnMatchPhaseChanged);
        }

        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            // A reconnect fires this again for a player who already has a pick — leave it.
            if (_picks.ContainsKey(e.PlayerId)) return;

            var seeded = _catalog.At(0);
            if (seeded == null) return; // Empty catalog — see the note on For().

            _picks[e.PlayerId] = seeded.Id;
            EventManager.Instance?.Publish(new RockPickedEvent(e.PlayerId, seeded.Id, 0));
        }

        private void OnPlayerLeft(PlayerLeftEvent e)
        {
            _picks.Remove(e.PlayerId);
        }

        private void OnMatchPhaseChanged(MatchPhaseChangedEvent e)
        {
            if (e.To == MatchPhase.Playing && randomRocks)
            {
                AssignRandomRocks();
            }
            else if (e.To == MatchPhase.Playing)
            {
                // Cleared, not left standing: a match played on pointed picks has no seed,
                // and carrying the previous random match's one into the telemetry would
                // label these throws as reproducible from a draw that never ran.
                LastRandomSeed = null;
            }
            else if (TurnManager.IsChoosingPhase(e.To))
            {
                // Every fresh Lobby AND every mid-match Choosing pause re-picks from
                // scratch — see TurnManager.IsChoosingPhase for why the two are treated
                // as one case here. For() degrades gracefully for anyone still connected —
                // it falls back to the catalog default the instant their entry is gone,
                // same as before they ever picked.
                _picks.Clear();
            }
        }

        /// <summary>
        /// Records a confirmed pick. Refused outside a choosing phase — a PICK_ROCK
        /// arriving mid-throw is stale, mirroring the Playing-only guard on THROW — and
        /// while random mode is on, since a pointed pick would otherwise silently win
        /// back control the moment the player confirms.
        /// </summary>
        public void Pick(string playerId, string rockId)
        {
            if (string.IsNullOrEmpty(playerId) || randomRocks) return;
            if (TurnManager.HasInstance && !TurnManager.IsChoosingPhase(TurnManager.Instance.Phase)) return;

            var rock = _catalog.ById(rockId);
            if (rock == null) return;

            _picks[playerId] = rock.Id;
            EventManager.Instance?.Publish(new RockPickedEvent(playerId, rock.Id, _catalog.IndexOf(rock)));
        }

        /// <summary>
        /// The rock this player is playing with: their pick, else the catalog's first
        /// entry. Null only when the catalog itself is empty — the same degenerate case
        /// every other rock-catalog reader in the project treats as "rock choosing is
        /// off", per RockCatalog.CreateDefault's own doc comment.
        /// </summary>
        public RockDefinition For(string playerId)
        {
            if (!string.IsNullOrEmpty(playerId) && _picks.TryGetValue(playerId, out var id))
            {
                var picked = _catalog.ById(id);
                if (picked != null) return picked;
            }

            return _catalog.At(0);
        }

        public SkipTuning TuningFor(string playerId) => For(playerId)?.Tuning;

        // Assigns every player in the match a fresh rock from a logged seed, so a surprising match can be reproduced. doc-ref ae91 docs/4-systems/RockCatalogAndTuning.md
        private void AssignRandomRocks()
        {
            var seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            var rng = new System.Random(seed);
            LastRandomSeed = seed;

            Debug.Log($"[RockSelection] Random rocks — seed {seed}");

            var players = TurnManager.HasInstance
                ? TurnManager.Instance.Players
                : (IReadOnlyList<string>)new List<string>(_picks.Keys);

            foreach (var player in players)
            {
                var index = rng.Next(_catalog.Count);
                var rock = _catalog.At(index);
                if (rock == null) continue;

                _picks[player] = rock.Id;
                EventManager.Instance?.Publish(new RockPickedEvent(player, rock.Id, index));
            }
        }

        protected override void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }
    }
}
