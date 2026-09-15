using HarmonyLib;

namespace ForgeRuntime.GameBindings;

[HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.DoChangeState))]
internal static class FrameworkStateChanged
{
    [HarmonyPostfix]
    private static void Postfix() => GameRuntimeBridge.Guard(() => GameRuntimeBridge.StateChanged(GameStateManager.CurrentStateName));
}

[HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.OnLevelCleanup))]
internal static class FrameworkWorldCleanup
{
    [HarmonyPrefix]
    private static void Prefix() => GameRuntimeBridge.Guard(GameRuntimeBridge.EndWorld);
}

[HarmonyPatch(typeof(GameStateManager), nameof(GameStateManager.OnResetSession))]
internal static class FrameworkSessionReset
{
    [HarmonyPrefix]
    private static void Prefix() => GameRuntimeBridge.Guard(GameRuntimeBridge.EndWorld);
}

[HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.OnStateChange))]
internal static class FrameworkCheckpointRestore
{
    [HarmonyPrefix]
    private static void Prefix(pCheckpointState __1, bool __2)
    {
        if (__1.isReloadingCheckpoint || __2)
            GameRuntimeBridge.Guard(() => GameRuntimeBridge.Suspend("checkpoint-restore", "Checkpoint restore requires a fresh expedition; no old graph tasks or entity references are replayed.", true));
    }
}

