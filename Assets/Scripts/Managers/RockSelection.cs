using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using Game;
using Singleton;
using UnityEngine;

namespace Managers
{
    /// <summary>
    /// Owns which rock every player is playing with. The only source of truth for "what
    /// stone is this player's" — everything downstream (the ghost mesh, the live throw,
    /// the telemetry row) reads through here rather than re-deriving a pick.
    /// </summary>
    /// <remarks>
    /// <b>Random rocks / active tuning set live here, not on a MatchSettings singleton.</b>
    /// The rock-choosing plan (docs/plans/rock-choosing-system.md, Part 5) designs a
    /// generic settings-list panel that would own both — but that panel doesn't exist
    /// yet, and this class cannot function without somewhere to read them from. They sit
    /// here as plain fields until Part 5 lands; that panel should read/write these
    /// properties rather than duplicate the state in a second place.
    ///
    /// <b>PICK_ROCK carries no rock id</b> (see Messages.cs) — a tap means "confirm
    /// whatever is highlighted", and only the shore layout (Part 3's ShoreRocks, not
    /// built yet) knows what that is. So this class exposes <see cref="Pick"/> for
    /// whatever owns that highlight to call; it does not itself subscribe to
    /// RelayClient.PickRockReceived, because it has nothing to resolve that event into
    /// without ShoreRocks. Wiring that up is Part 3's job.
    /// </remarks>
    public class RockSelection : SingletonBase<RockSelection>
    {
        [Tooltip("When on, every player is assigned a rock at match start instead of using " +
                 "their pointed pick. Phone picks are ignored while this is on.")]
        [SerializeField] private bool randomRocks;

        [Tooltip("Which of a rock's two tunings — Authored or Derived — decides how it plays.")]
        [SerializeField] private TuningSet activeTuningSet = TuningSet.Authored;

        private RockCatalog _catalog;
        private readonly Dictionary<string, string> _picks = new();

        public bool RandomRocks
        {
            get => randomRocks;
            set => randomRocks = value;
        }

        public TuningSet ActiveTuningSet
        {
            get => activeTuningSet;
            set => activeTuningSet = value;
        }

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
            else if (e.To == MatchPhase.Lobby)
            {
                // Every new match re-picks from scratch. For() degrades gracefully for
                // anyone still connected — it falls back to the catalog default the
                // instant their entry is gone, same as before they ever picked.
                _picks.Clear();
            }
        }

        /// <summary>
        /// Records a confirmed pick. Refused once the match has started — a PICK_ROCK
        /// arriving mid-match is stale, mirroring the Lobby-only guard on THROW — and
        /// while random mode is on, since a pointed pick would otherwise silently win
        /// back control the moment the player confirms.
        /// </summary>
        public void Pick(string playerId, string rockId)
        {
            if (string.IsNullOrEmpty(playerId) || randomRocks) return;
            if (TurnManager.HasInstance && TurnManager.Instance.Phase != MatchPhase.Lobby) return;

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

        public SkipTuning TuningFor(string playerId) => For(playerId)?.Tuning(activeTuningSet);

        /// <summary>
        /// Assigns every currently-picked player a fresh rock from a logged seed, so a
        /// surprising match can be reproduced. The seed is only logged for now —
        /// Part 6's RockTelemetry is what will actually record it per match.
        /// </summary>
        private void AssignRandomRocks()
        {
            var seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            var rng = new System.Random(seed);

            Debug.Log($"[RockSelection] Random rocks — seed {seed}");

            foreach (var player in new List<string>(_picks.Keys))
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
