using HarmonyLib;

namespace ForgeRuntime.GameBindings;

/// <summary>The two host patches GTFO-API 0.5.0 has no event for. <c>OnBuildStart</c> and <c>OnLevelCleanup</c> say
/// nothing about a checkpoint, and this version exposes neither <c>EventAPI.OnCheckpointReloaded</c> nor
/// <c>EventAPI.OnGameStateChanged</c>, so the detours stay. The save side captures the variables and `g-once`
/// latches of the world being saved, and the reload side puts them back and re-arms the plans; the runtime keeps
/// running, because a checkpoint reload continues the same expedition.
///
/// <c>CheckpointManager.StoreCheckpoint(Vector3)</c> is the game's save and <c>CheckpointManager.ReloadCheckpoint()</c>
/// its reload; <c>OnStateChange</c> fires for both and for a state that is not a save, which is why neither detour
/// hangs on it any more.</summary>
[HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.StoreCheckpoint))]
internal static class FrameworkCheckpointSave
{
    [HarmonyPrefix]
    private static void Prefix() => GameRuntimeBridge.Guard(GameRuntimeBridge.CaptureCheckpoint);
}

[HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.ReloadCheckpoint))]
internal static class FrameworkCheckpointRestore
{
    [HarmonyPrefix]
    private static void Prefix() => GameRuntimeBridge.Guard(GameRuntimeBridge.RestoreCheckpoint);
}
