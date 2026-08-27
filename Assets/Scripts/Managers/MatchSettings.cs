using System.Collections.Generic;
using Game;
using Singleton;

namespace Managers
{
    /// <summary>
    /// Host-editable match settings, browsable as a generic list so a new setting is one entry
    /// rather than a screen edit. Owns no gameplay state itself — each <see cref="Setting"/>
    /// reads/writes a property that already lives on <see cref="RockSelection"/> or
    /// <see cref="TurnManager"/>, per the deviation note on <see cref="RockSelection"/> saying
    /// this class should read/write those properties rather than duplicate them.
    /// </summary>
    /// <remarks>
    /// Created in Bootstrap after both RockSelection and TurnManager — Awake needs both
    /// instances to already exist so the values loaded from <see cref="MatchSettingsStore"/>
    /// have somewhere to land.
    /// </remarks>
    public class MatchSettings : SingletonBase<MatchSettings>
    {
        public List<Setting> All { get; } = new();

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            var loaded = MatchSettingsStore.Load();
            if (RockSelection.HasInstance)
            {
                RockSelection.Instance.RandomRocks = loaded.RandomRocks;
                RockSelection.Instance.ActiveTuningSet = loaded.TuningSet;
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

            All.Add(new EnumSetting<TuningSet>(
                "tuningSet", "Tuning set",
                "Authored (hand-tuned) or Derived (from mesh bounds) — the comparison switch.",
                get: () => RockSelection.HasInstance ? RockSelection.Instance.ActiveTuningSet : TuningSet.Authored,
                set: value =>
                {
                    if (RockSelection.HasInstance) RockSelection.Instance.ActiveTuningSet = value;
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
            var tuningSet = RockSelection.HasInstance ? RockSelection.Instance.ActiveTuningSet : TuningSet.Authored;
            var roundsPerMatch = TurnManager.HasInstance ? TurnManager.Instance.RoundsPerMatch : 5;

            MatchSettingsStore.Save(randomRocks, tuningSet, roundsPerMatch);
        }
    }
}
