using System;
using Singleton;

namespace Net
{
    /// <summary>
    /// Resolves which NetworkMode this session runs under, and tells LocalServices when to
    /// stop waiting.
    /// </summary>
    /// <remarks>
    /// A saved preference is read in Awake(), before any Start() runs — the steady-state
    /// (returning player) path is fully resolved with no delay and no coroutine. Only true
    /// first run, with nothing saved yet, needs ModeSelectScreen to call Choose() before
    /// LocalServices can proceed.
    /// </remarks>
    public class NetworkModeGate : SingletonBase<NetworkModeGate>
    {
        public NetworkMode Mode { get; private set; }
        public bool Resolved { get; private set; }

        /// <summary>Fires the first time a mode is chosen this session (first run only).</summary>
        public event Action<NetworkMode> Chosen;

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            if (NetworkModeStore.HasSavedMode)
            {
                Mode = NetworkModeStore.Load();
                Resolved = true;
            }
        }

        /// <summary>
        /// Always persists. Only changes Mode/fires Chosen the first time this session —
        /// picking a different mode once already resolved just saves it for next launch,
        /// by design (no live teardown/rebuild of a running relay, certificate, or hotspot).
        /// </summary>
        public void Choose(NetworkMode mode)
        {
            NetworkModeStore.Save(mode);

            if (Resolved) return;

            Mode = mode;
            Resolved = true;
            Chosen?.Invoke(mode);
        }
    }
}
