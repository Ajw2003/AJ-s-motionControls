using System;
using EventTypes.AudioEvents;
using UnityEngine;

namespace Audio
{
    // What each SoundId actually sounds like. doc-ref 79d1 docs/4-systems/Audio.md
    [CreateAssetMenu(menuName = "RockSkipping/Audio Bank", fileName = "AudioBank")]
    public class AudioBank : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public SoundId Id;

            [Tooltip("Round-robin, never the same one twice running. Leave empty to use the " +
                     "synthesised placeholder for this sound.")]
            public AudioClip[] Variants;

            [Range(0f, 1f)] public float Volume = 1f;

            [Header("Pitch")]
            public float BasePitch = 1f;

            [Tooltip("Random pitch spread per shot. What stops a repeated sound from " +
                     "machine-gunning. Only works because every shot gets its own pooled " +
                     "source — pitch is a property of the source, not the shot.")]
            [Range(0f, 0.5f)] public float PitchJitter = 0.03f;

            [Tooltip("Semitones added per step of a run. About 1.0 for a skip ladder; 0 for " +
                     "anything with no ladder.")]
            [Range(0f, 4f)] public float SemitonesPerStep;

            [Tooltip("Where the ladder stops climbing. Cap it: a 30-skip run without a cap " +
                     "is a dog whistle by the end. Past the cap, hold the top note and let " +
                     "volume and variant carry the rest.")]
            [Range(0, 40)] public int StepCap = 14;

            [Header("Routing")]
            public AudioChannel Channel = AudioChannel.Sfx;

            [Header("Space")]
            [Tooltip("3D-positioned. Off for UI and anything with no place in the world.")]
            public bool Spatial = true;

            [Tooltip("Full volume inside this radius, in metres.")]
            public float MinDistance = 5f;

            [Tooltip("Silent past this radius. Wide, because a long throw ends well over " +
                     "100 m from the camera and should still be audible.")]
            public float MaxDistance = 150f;
        }

        public Entry[] Entries = Array.Empty<Entry>();

        /// <summary>The entry for a sound, or null if the bank does not describe it.</summary>
        /// <remarks>
        /// Linear scan over a handful of entries, called once per sound played. A dictionary
        /// would be faster and would also need rebuilding whenever the asset is edited in
        /// play mode, which is exactly when this is being tuned.
        /// </remarks>
        public Entry Find(SoundId id)
        {
            if (Entries == null) return null;

            foreach (var entry in Entries)
            {
                if (entry != null && entry.Id == id) return entry;
            }

            return null;
        }
    }
}
