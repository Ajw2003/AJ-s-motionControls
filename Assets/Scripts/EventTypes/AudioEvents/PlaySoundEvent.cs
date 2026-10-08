namespace EventTypes.AudioEvents
{
    using EventSystems;
    using UnityEngine;

    /// <summary>Play a sound by identity. Callers never hold clips — the bank resolves them.</summary>
    /// <remarks>
    /// The only route into AudioManager that gameplay or presentation should use. See
    /// SoundId for why naming beats referencing here.
    /// </remarks>
    public class PlaySoundEvent : IEvent
    {
        public SoundId Id { get; }

        /// <summary>
        /// Where it happened, or null for a 2D sound with no place in the world.
        /// </summary>
        /// <remarks>
        /// Worth carrying rather than playing everything flat. Skips happen from zero to
        /// past 100 m from the camera, and a single 2D source plays every one of them at the
        /// same volume in the same place, so a long run reads flat. The contact point is
        /// already on SkipOccurredEvent.Position, so this costs nothing to supply.
        /// </remarks>
        public Vector3? Position { get; }

        /// <summary>0..1. Scales volume — how hard this instance of the sound was.</summary>
        public float Intensity { get; }

        /// <summary>
        /// Position in a run, driving the pitch ladder. Zero for sounds with no ladder.
        /// </summary>
        /// <remarks>
        /// The step, not the pitch. How many semitones a step is worth, and where the ladder
        /// stops climbing, are properties of the SOUND and live on its bank entry — so the
        /// ladder can be retuned without recompiling whatever counted the steps.
        /// </remarks>
        public int Step { get; }

        // Seconds to wait before it sounds. doc-ref 6215 docs/4-systems/Audio.md
        public float Delay { get; }

        public PlaySoundEvent(
            SoundId id, Vector3? position = null, float intensity = 1f, int step = 0, float delay = 0f)
        {
            Id = id;
            Position = position;
            Intensity = Mathf.Clamp01(intensity);
            Step = Mathf.Max(0, step);
            Delay = Mathf.Max(0f, delay);
        }
    }
}
