namespace Utils
{
    /// <summary>Scene names, kept in one place so transitions can't be broken by a typo.</summary>
    /// <remarks>
    /// There is deliberately no MainMenu. The game boots straight to Bootstrap, which loads
    /// Lobby and puts a QR on the TV — a menu somebody has to walk over and click is the exact
    /// friction milestone 7 exists to remove. The scene and this constant were deleted together
    /// (docs/plans/presentation-and-ui.md, D14); the constant was never referenced by anything.
    ///
    /// Bootstrap must stay index 0 in the build settings, because it creates every persistent
    /// manager. MainMenu had drifted in AHEAD of it, so a built player would have loaded an
    /// orphaned scene first — the invariant was already broken and this is what restored it.
    /// Assets/Editor/ProjectSetup.cs holds the authoritative order.
    /// </remarks>
    public static class SceneNames
    {
        public const string Bootstrap = "Bootstrap";
        public const string Lobby = "Lobby";
        public const string Game = "Game";
        public const string Results = "Results";
    }
}
