// Minimal stand-in for the GTFO-API surface the host subscribes to. The real assembly ships in BepInExPack_GTFO and
// cannot load here; this double reproduces the exact member names and signatures the bridge uses and lets the harness
// drive the level lifecycle the way GTFO-API's own patches do.
namespace GTFO.API
{
    public static class LevelAPI
    {
        public static event Action? OnBuildStart;
        public static event Action? OnBuildDone;
        public static event Action? OnEnterLevel;
        public static event Action? OnLevelCleanup;

        internal static void RaiseBuildStart() => OnBuildStart?.Invoke();
        internal static void RaiseBuildDone() => OnBuildDone?.Invoke();
        internal static void RaiseEnterLevel() => OnEnterLevel?.Invoke();
        internal static void RaiseLevelCleanup() => OnLevelCleanup?.Invoke();
    }

    public static class EventAPI
    {
        public static event Action? OnExpeditionStarted;
        internal static void RaiseExpeditionStarted() => OnExpeditionStarted?.Invoke();
    }
}
