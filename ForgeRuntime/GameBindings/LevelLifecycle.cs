using GTFO.API;

namespace ForgeRuntime.GameBindings;

/// <summary>Level lifecycle through GTFO-API instead of the host's own Harmony patches. Trigger points, verified
/// against the shipped GTFO-API 0.5.0's own patch set: <c>OnBuildStart</c> and <c>OnBuildDone</c> are postfixes of
/// <c>LevelGeneration.Builder.Build</c> and <c>BuildDone</c>, and only the start is subscribed — the host needs the new
/// world before the game builds into it, not a build-complete signal; <c>OnEnterLevel</c> reaches the API through
/// <c>RundownManager.OnExpeditionGameplayStarted</c> → <c>EventAPI.OnExpeditionStarted</c> and fires once the player can
/// move; <c>OnLevelCleanup</c> is the postfix of <c>Global.OnLevelCleanup</c>. That version exposes no
/// <c>EventAPI.OnGameStateChanged</c>, so the previous raw <c>GameStateManager.DoChangeState</c> patch has no event
/// equivalent and was removed rather than duplicated. Checkpoint restore and host migration keep their patches: there is
/// no checkpoint-reload or migration event to subscribe to.</summary>
internal static class LevelLifecycle
{
    internal static void Subscribe()
    {
        LevelAPI.OnBuildStart += BuildStarted;
        LevelAPI.OnEnterLevel += LevelEntered;
        LevelAPI.OnLevelCleanup += LevelCleanedUp;
    }

    internal static void Unsubscribe()
    {
        LevelAPI.OnBuildStart -= BuildStarted;
        LevelAPI.OnEnterLevel -= LevelEntered;
        LevelAPI.OnLevelCleanup -= LevelCleanedUp;
    }

    private static void BuildStarted() => GameRuntimeBridge.Guard(GameRuntimeBridge.BeginGeneration);
    private static void LevelEntered() => GameRuntimeBridge.Guard(GameRuntimeBridge.EnterLevel);
    private static void LevelCleanedUp() => GameRuntimeBridge.Guard(GameRuntimeBridge.LeaveLevel);
}
