namespace EventTypes.MatchEvents
{
    using EventSystems;

    /// <summary>Published once, when the last round is done and the match is over.</summary>
    /// <remarks>
    /// This class existed before and was deleted for having no subscribers — PlayerStanding
    /// still carries the note, because it had to be moved out to survive the deletion. It is
    /// back deliberately, for the victory sting in Plan-SfxVfx.md section 5.6, and it is
    /// back in a smaller shape than it left in.
    ///
    /// What is gone is the standings snapshot it used to carry. That was the part nobody
    /// read: ResultsScreen reads standings straight off TurnManager.RebuildStandings, so the
    /// copy on the event was a second version of the same list, free to drift and answering
    /// no question the first one could not. A sting needs to know whether somebody won, not
    /// who came fourth. Anything that later needs the full table should call
    /// RebuildStandings rather than growing this back.
    /// </remarks>
    public class MatchEndedEvent : IEvent
    {
        /// <summary>
        /// The winner, or null on a tie at the top.
        /// </summary>
        /// <remarks>
        /// Null is a real, expected value here and must be handled rather than defended
        /// against. TurnManager refuses to name a winner when the top two scores are level,
        /// and says why: "claiming one would be a lie the results screen then has to
        /// render." A fanfare that crowns somebody on a tie is that same lie in audio.
        /// </remarks>
        public string WinnerId { get; }

        /// <summary>True when the match ended level at the top and nobody won.</summary>
        public bool IsTie => string.IsNullOrEmpty(WinnerId);

        public MatchEndedEvent(string winnerId)
        {
            WinnerId = winnerId;
        }
    }
}
