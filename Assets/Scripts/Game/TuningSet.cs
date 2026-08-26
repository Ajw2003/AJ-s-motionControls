namespace Game
{
    /// <summary>
    /// Which of the two per-rock tuning sets is live.
    /// </summary>
    /// <remarks>
    /// Two sets exist on purpose, and only one of them will survive. The question they
    /// answer is whether a rock's feel should be authored or fall out of its geometry:
    ///
    /// <b>Authored</b> — six hand-tuned archetypes. Guaranteed to separate, because
    /// separating them is what a person sat down and did. Says nothing about whether the
    /// stone that looks flattest is the one that skips best.
    ///
    /// <b>Derived</b> — six tunings generated from each mesh's own bounds. Honest by
    /// construction: the flat one really is the skimmer. May well fail to separate at all,
    /// because six meshes modelled to look like rocks were never modelled to span a
    /// gameplay range.
    ///
    /// Both are built and both are switchable at runtime from the match settings panel, so
    /// the answer comes from played matches rather than from argument. See
    /// Verification/SkipCurve for the offline half and Verification/RockTelemetry for the
    /// real-throw half.
    /// </remarks>
    public enum TuningSet
    {
        /// <summary>Hand-tuned archetypes.</summary>
        Authored,

        /// <summary>Generated from mesh bounds.</summary>
        Derived,
    }
}
