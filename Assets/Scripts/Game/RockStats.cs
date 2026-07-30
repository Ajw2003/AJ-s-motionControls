namespace Game
{
    /// <summary>
    /// Per-rock physical properties, rolled once at spawn and then immutable.
    /// </summary>
    /// <remarks>
    /// Produced from a RockDefinition ScriptableObject by rolling each stat between
    /// its configured min and max, so two rocks of the same type still feel slightly
    /// different. Immutable because SkipPhysics.Step must be pure — the preview runs
    /// the same stats speculatively every frame and must not mutate anything.
    ///
    /// RockDefinition and the spawner land in M2; Default covers everything until then.
    /// </remarks>
    public readonly struct RockStats
    {
        /// <summary>Face angle, in degrees from horizontal, that skips best. ~20 in reality.</summary>
        public readonly float OptimalAngleDeg;

        /// <summary>How far from optimal the stone can be and still skip.</summary>
        public readonly float BaseToleranceDeg;

        /// <summary>Scales the bounce energy. Higher bounces further.</summary>
        public readonly float Restitution;

        /// <summary>Horizontal speed retained per bounce.</summary>
        public readonly float Drag;

        /// <summary>Spin retained per bounce. Higher holds the stone stable for longer.</summary>
        public readonly float SpinDecay;

        /// <summary>Below this speed the stone stops skipping and sinks.</summary>
        public readonly float MinSkipSpeed;

        public readonly string DisplayName;

        public RockStats(float optimalAngleDeg, float baseToleranceDeg, float restitution,
                         float drag, float spinDecay, float minSkipSpeed, string displayName)
        {
            OptimalAngleDeg = optimalAngleDeg;
            BaseToleranceDeg = baseToleranceDeg;
            Restitution = restitution;
            Drag = drag;
            SpinDecay = spinDecay;
            MinSkipSpeed = minSkipSpeed;
            DisplayName = displayName;
        }

        /// <summary>A plain, forgiving pebble. Used until the spawner exists.</summary>
        public static RockStats Default => new(
            optimalAngleDeg: 20f,
            baseToleranceDeg: 12f,
            restitution: 0.9f,
            drag: 0.94f,
            spinDecay: 0.88f,
            minSkipSpeed: 3.5f,
            displayName: "Pebble");
    }
}
