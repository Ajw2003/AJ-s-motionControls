namespace EventTypes.AudioEvents
{
    // Every sound the game can ask for, by identity rather than by asset. doc-ref a857 docs/4-systems/Audio.md
    public enum SoundId
    {
        /// <summary>A stone touching the water and coming back off it.</summary>
        Skip,

        /// <summary>
        /// Layered on top of Skip at the milestone counts — a run turning into an event.
        /// Which counts those are is SfxDirector.milestones; naming them here too just gave
        /// the two somewhere to disagree, and they did.
        /// </summary>
        SkipMilestone,

        /// <summary>A run that ended naturally. The stone goes under.</summary>
        Sink,

        /// <summary>The throw itself leaving the hand.</summary>
        ThrowRelease,

        /// <summary>A soft per-skip tick riding under the skip sound as points bank.</summary>
        ScoreTick,

        /// <summary>The tally once a throw's points are committed.</summary>
        ThrowScored,

        /// <summary>A stone that never skipped at all. Flat, dead, unrewarding on purpose.</summary>
        ThrowFailed,

        /// <summary>The turn passing to a player.</summary>
        TurnStart,

        /// <summary>A match beginning.</summary>
        MatchStart,

        /// <summary>Somebody won.</summary>
        MatchWin,

        /// <summary>
        /// Nobody won. Deliberately its own sound rather than a quieter MatchWin — see
        /// MatchEndedEvent on why a fanfare over a tie is a lie the results screen then has
        /// to contradict.
        /// </summary>
        MatchTie,

        /// <summary>A phone joining the lobby.</summary>
        PlayerJoined,

        /// <summary>Generic UI acknowledgement.</summary>
        UiConfirm,
    }
}
