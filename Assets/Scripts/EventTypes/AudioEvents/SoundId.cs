namespace EventTypes.AudioEvents
{
    /// <summary>Every sound the game can ask for, by identity rather than by asset.</summary>
    /// <remarks>
    /// An enum rather than an AudioClip reference, and that is the whole design. FactoryTM's
    /// AudioClipEvent carries a clip, which means the caller has to hold one — a
    /// [SerializeField] AudioClip on RockController or ScoreManager. Every manager in this
    /// project is built with new GameObject() + AddComponent by Bootstrap, so a serialized
    /// field on one has never been through an inspector and sits at its C# field initializer
    /// forever: null. AimPreview.Awake documents the project already losing time to exactly
    /// that, with the tuning asset.
    ///
    /// Naming a sound instead means gameplay code holds no assets at all, and swapping what
    /// a skip sounds like is an edit to the bank asset rather than to a gameplay class.
    /// </remarks>
    public enum SoundId
    {
        /// <summary>A stone touching the water and coming back off it.</summary>
        Skip,

        /// <summary>Layered on top of Skip at 5, 10, 15 — a run turning into an event.</summary>
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
