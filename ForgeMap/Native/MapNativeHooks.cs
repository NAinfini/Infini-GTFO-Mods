using System;
using System.Collections.Generic;
using HarmonyLib;
using Player;

namespace ForgeMap.Native;

internal static class MapNativeHooks
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
        { typeof(PlayerSpawnedReadback), typeof(PlayerDespawnedReadback) });
}

// Both hooks are postfixes that only choose when to read. Parameters are never used: identity comes from
// PlayerManager.PlayerAgentsInLevel and each agent's linked SNet_Player after the native body returned.
// PlayerReplicationManager.OnSpawn / OnDeSpawn call these non-virtual bodies for local, remote and bot
// players; targets, RVAs and call edges are frozen in evidence/map5a-player-hooks.json.

[HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.OnPlayerSpawned))]
internal static class PlayerSpawnedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerManager __instance) => Plugin.Session?.Guard(module => module.Reconcile());
}

[HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.OnPlayerDespawned))]
internal static class PlayerDespawnedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerManager __instance) => Plugin.Session?.Guard(module => module.Reconcile());
}
