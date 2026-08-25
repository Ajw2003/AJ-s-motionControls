using EventSystems;
using EventTypes.AudioEvents;
using EventTypes.MatchEvents;
using EventTypes.SkipEvents;
using Game;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// Turns gameplay events into sounds. The only thing that decides WHICH sound a moment
    /// deserves; AudioManager decides what that sound is made of.
    /// </summary>
    /// <remarks>
    /// A standalone subscriber that adds no lines to gameplay code, exactly as WaterRipples
    /// is. That is what inherits the protection SkipPhysics states twice: the aim preview
    /// runs the solver hundreds of times a frame for a throw that never happened, and only
    /// RockController publishes the events this listens to, so aiming cannot machine-gun
    /// skip sounds. The safety is inherited, not re-implemented.
    ///
    /// Created after ScoreManager and TurnManager — see Bootstrap. EventManager delivers in
    /// subscription order, so by the time this hears anything the score is banked and the
    /// phase has settled, and it can read final state to decide what to play.
    /// </remarks>
    public class SfxDirector : MonoBehaviour
    {
        [Header("Skip response")]
        [Tooltip("Quietest and loudest skip, as fractions of SkipTuning.MaxSpeed.")]
        [Range(0f, 1f)] [SerializeField] private float minImpactSpeedFraction = 0.15f;
        [Range(0f, 1f)] [SerializeField] private float maxImpactSpeedFraction = 0.96f;

        [Tooltip("Skips at which a brighter accent is layered over the normal skip sound.")]
        [SerializeField] private int[] milestones = { 5, 10, 15, 20 };

        [Header("End-of-throw scheduling")]
        [Tooltip("Delay on the score tally, in seconds. See the remarks on the pile-up.")]
        [SerializeField] private float scoreDelay = 0.35f;

        [Tooltip("Delay on the turn sting or the match fanfare, in seconds.")]
        [SerializeField] private float turnDelay = 1.2f;

        private float _minImpactSpeed = 4f;
        private float _maxImpactSpeed = 25f;

        /// <summary>
        /// Whether the throw that just ended ever skipped. Read on ThrowCompleted to tell a
        /// run that died from one that never started.
        /// </summary>
        private int _lastSinkSkipCount;

        private void Awake()
        {
            // Fractions of MaxSpeed, never absolute speeds. WaterRipples carries the note
            // explaining why: its thresholds were hardcoded at 4 and 25 against a MaxSpeed
            // of 26, so raising the launch speed clamped every impact to full strength and
            // the ripple stopped responding to anything. Skip volume fails the same way.
            var tuning = Resources.Load<SkipTuning>("SkipTuning");
            if (tuning != null)
            {
                _minImpactSpeed = tuning.MaxSpeed * minImpactSpeedFraction;
                _maxImpactSpeed = tuning.MaxSpeed * maxImpactSpeedFraction;
            }

            var events = EventManager.Instance;
            if (events == null) return;

            events.Subscribe<RockLaunchedEvent>(this, OnLaunched);
            events.Subscribe<SkipOccurredEvent>(this, OnSkip);
            events.Subscribe<RockSunkEvent>(this, OnSunk);
            events.Subscribe<ScoreAwardedEvent>(this, OnScored);
            events.Subscribe<TurnStartedEvent>(this, OnTurnStarted);
            events.Subscribe<MatchEndedEvent>(this, OnMatchEnded);
            events.Subscribe<MatchPhaseChangedEvent>(this, OnPhaseChanged);
            events.Subscribe<PlayerJoinedEvent>(this, OnPlayerJoined);
        }

        private void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
        }

        // ------------------------------------------------------------------ the throw

        private void OnLaunched(RockLaunchedEvent e)
        {
            Play(SoundId.ThrowRelease, e.Origin, intensity: Mathf.Lerp(0.5f, 1f, e.Power));
        }

        /// <summary>
        /// The skip sound, its pitch ladder, its score tick, and the milestone accent.
        /// </summary>
        /// <remarks>
        /// SkipIndex goes to Step and the ladder itself lives on the bank entry, so how many
        /// semitones a skip is worth is tuned in the inspector rather than here.
        ///
        /// The score tick rides on this event rather than on a scoring one, and that is not
        /// a shortcut. ScoreManager banks points per skip and commits the total on throw
        /// completion, so there is no per-skip scoring event to hang it off — and a tick
        /// that arrived separately would drift against the skip it belongs to. It shares the
        /// skip's step so the two climb together and read as one sound.
        /// </remarks>
        private void OnSkip(SkipOccurredEvent e)
        {
            var intensity = Mathf.InverseLerp(_minImpactSpeed, _maxImpactSpeed, e.Speed);

            Play(SoundId.Skip, e.Position, intensity, e.SkipIndex);
            Play(SoundId.ScoreTick, e.Position, intensity * 0.6f, e.SkipIndex);

            if (IsMilestone(e.SkipIndex))
            {
                // Layered over the skip, not instead of it, so a milestone reads as the same
                // contact with something added rather than as a different event.
                Play(SoundId.SkipMilestone, e.Position, intensity, e.SkipIndex);
            }
        }

        private bool IsMilestone(int skipIndex)
        {
            if (milestones == null) return false;

            foreach (var milestone in milestones)
            {
                if (skipIndex == milestone) return true;
            }

            return false;
        }

        /// <summary>
        /// The sink, and the failure case that shares its moment.
        /// </summary>
        /// <remarks>
        /// A throw that never skipped does not get a second event of its own — the failure
        /// IS the sink, at the same instant. Splitting them would mean either two sounds on
        /// a bad throw or an event that fires only sometimes.
        /// </remarks>
        private void OnSunk(RockSunkEvent e)
        {
            _lastSinkSkipCount = e.SkipCount;

            var intensity = Mathf.InverseLerp(_minImpactSpeed, _maxImpactSpeed, e.ImpactSpeed);

            Play(e.SkipCount == 0 ? SoundId.ThrowFailed : SoundId.Sink, e.Position, intensity);
        }

        /// <summary>
        /// The score tally, held back so it is not buried by the splash.
        /// </summary>
        /// <remarks>
        /// This is the pile-up Plan-SfxVfx.md section 7.1 describes, and it is real: when a
        /// rock sinks, RockController publishes RockSunkEvent and then Finish publishes
        /// ThrowCompletedEvent, which banks the score and rotates the turn — all
        /// synchronously in one frame. Played on receipt, the splash, the tally and the turn
        /// sting land together as mush, and the tally, which is the rewarding one, loses.
        ///
        /// So the ordering is authored rather than inherited from event delivery: splash at
        /// t+0, tally at t+scoreDelay, sting at t+turnDelay. The delay rides on the event and
        /// AudioManager schedules it, so when the fuller queue arrives in M6 nothing here has
        /// to change.
        ///
        /// A failed throw is silent here on purpose. Zero skips still awards distance points,
        /// but a ka-ching after the dead plunk of a stone that never skipped rewards the one
        /// outcome the game is trying to make disappointing.
        /// </remarks>
        private void OnScored(ScoreAwardedEvent e)
        {
            if (_lastSinkSkipCount == 0) return;

            Play(SoundId.ThrowScored, null, intensity: 1f, step: 0, delay: scoreDelay);
        }

        // ------------------------------------------------------------------ the match

        private void OnTurnStarted(TurnStartedEvent e)
        {
            // Delayed for the same reason the tally is, and by more, so the order across the
            // frame is splash, then reward, then whose turn it is next.
            Play(SoundId.TurnStart, null, intensity: 1f, step: 0, delay: turnDelay);
        }

        /// <summary>
        /// The fanfare, or the thing that is deliberately not a fanfare.
        /// </summary>
        /// <remarks>
        /// TurnManager refuses to name a winner when the top two scores are level and says
        /// why: claiming one would be a lie the results screen then has to contradict. A
        /// victory fanfare over a tie is that same lie, so a tie gets its own shorter,
        /// unresolved sting instead of a quieter version of the win.
        /// </remarks>
        private void OnMatchEnded(MatchEndedEvent e)
        {
            Play(e.IsTie ? SoundId.MatchTie : SoundId.MatchWin,
                null, intensity: 1f, step: 0, delay: turnDelay);
        }

        private void OnPhaseChanged(MatchPhaseChangedEvent e)
        {
            // Only the start of a match. Results is announced by the fanfare, and the return
            // to the lobby is a cleanup rather than a moment.
            if (e.To == Managers.MatchPhase.Playing && e.From == Managers.MatchPhase.Lobby)
            {
                Play(SoundId.MatchStart, null);
            }
        }

        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            Play(SoundId.PlayerJoined, null);
        }

        // ------------------------------------------------------------------ plumbing

        private static void Play(
            SoundId id, Vector3? position, float intensity = 1f, int step = 0, float delay = 0f)
        {
            EventManager.Instance?.Publish(new PlaySoundEvent(id, position, intensity, step, delay));
        }
    }
}
