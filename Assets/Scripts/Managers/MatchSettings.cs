using System.Collections.Generic;
using Game;
using Singleton;

namespace Managers
{
    // Host-editable match settings, browsable as a generic list so a new setting is one entry rather than a scree... doc-ref 52b4 docs/4-systems/ScoringAndTurns.md
    public class MatchSettings : SingletonBase<MatchSettings>
    {
        public List<Setting> All { get; } = new();

        private bool _shareInternet = true;

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            var loaded = MatchSettingsStore.Load();
            _shareInternet = loaded.ShareInternet;
            if (RockSelection.HasInstance)
            {
                RockSelection.Instance.RandomRocks = loaded.RandomRocks;
            }
            if (TurnManager.HasInstance)
            {
                TurnManager.Instance.RoundsPerMatch = loaded.RoundsPerMatch;
            }

            BuildSettingsList();
        }

        private void BuildSettingsList()
        {
            All.Add(new ToggleSetting(
                "randomRocks", "Random rocks",
                "Assign every player a rock at match start instead of using their pointed pick.",
                get: () => RockSelection.HasInstance && RockSelection.Instance.RandomRocks,
                set: value =>
                {
                    if (RockSelection.HasInstance) RockSelection.Instance.RandomRocks = value;
                    Save();
                }));

            All.Add(new IntSetting(
                "roundsPerMatch", "Rounds per match",
                "Each round gives every player one throw.",
                get: () => TurnManager.HasInstance ? TurnManager.Instance.RoundsPerMatch : 5,
                set: value =>
                {
                    if (TurnManager.HasInstance) TurnManager.Instance.RoundsPerMatch = value;
                    Save();
                },
                min: 1, max: 10));

            All.Add(new ToggleSetting(
                "shareInternet", "Share internet",
                "Players get this PC's internet, so phones rejoin the game's Wi-Fi by themselves. Takes effect the next time the game starts its hotspot.",
                get: () => _shareInternet,
                set: value =>
                {
                    _shareInternet = value;
                    Save();
                }));
        }

        /// <summary>
        /// Whether the panel may be opened/edited right now. Anything but Playing — a
        /// mid-match tuning-set swap would make half a match's telemetry unattributable, per
        /// the original plan text. Lobby and Choosing are covered so the panel is reachable
        /// both before a match starts and at the existing mid-match re-pick pause; Results is
        /// included too since pressing the start key there returns to Lobby rather than
        /// jumping into a match.
        /// </summary>
        public bool CanEdit => !TurnManager.HasInstance || TurnManager.Instance.Phase != MatchPhase.Playing;

        private void Save()
        {
            var randomRocks = RockSelection.HasInstance && RockSelection.Instance.RandomRocks;
            var roundsPerMatch = TurnManager.HasInstance ? TurnManager.Instance.RoundsPerMatch : 5;

            MatchSettingsStore.Save(randomRocks, roundsPerMatch, _shareInternet);
        }
    }
}
