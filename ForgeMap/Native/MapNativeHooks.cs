using System;
using System.Collections.Generic;
using HarmonyLib;
using Player;

namespace ForgeMap.Native;

/// <summary>Every Harmony patch class the Map package installs. The player readbacks below, the map-object
/// readbacks in `MapObjectHooks`, the expedition readback in `ExpeditionHooks`, the player-life readbacks in
/// `PlayerLifeFacts`, the player-state readbacks in `PlayerStateHooks`, the player-event readbacks in
/// `PlayerEventHooks`, the door-terminal readbacks in `DoorTerminalHooks`, the level-object readbacks in
/// `LevelObjectHooks` and the overhead readbacks in `TeammateOverheadHooks` are the whole set; the plugin hands
/// this one list to the class processor, so a hook is installed exactly when it is listed here and nowhere
/// else.</summary>
internal static class MapNativeHooks
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(PlayerSpawnedReadback), typeof(PlayerDespawnedReadback),
        typeof(DoorStateReadback), typeof(TerminalStateReadback),
        typeof(ExpeditionEndedReadback),
        typeof(DownedStateReadback), typeof(SyncedDownedStateReadback), typeof(RevivedStateReadback),
        typeof(ReviveInteractionReadback), typeof(PlayerDiedReadback), typeof(PlayerWarpedReadback),
        typeof(PlayerRespawnedReadback),
        typeof(PlayerDamageAccepted), typeof(PlayerInfectionWritten),
        typeof(PlayerBulletDamageKind), typeof(PlayerProjectileDamageKind), typeof(PlayerMeleeDamageKind),
        typeof(PlayerExplosionDamageKind), typeof(PlayerFallDamageKind), typeof(PlayerFireDamageKind),
        typeof(PlayerStickyDamageKind), typeof(PlayerParasiteDamageKind), typeof(PlayerPushDamageKind),
        typeof(PlayerGameEventPosted), typeof(PlayerPingMarkerSet),
        typeof(TerminalCommandReadbackFixed), typeof(WeakLockBrokenReadback), typeof(WeakDoorAttackedReadback),
        typeof(WeakDoorBrokenReadback),
        typeof(ScanProgressReadback), typeof(ScanStateReadback), typeof(GeneratorCellReadback),
        typeof(GeneratorClusterStateReadback), typeof(ResourceContainerStateReadback), typeof(ItemPickupReadback),
        // The seven session/objective/zone/portal readbacks of the level-event half. The expedition start row is
        // the one that reports to every peer; the rest publish host-side, which the callback itself decides.
        typeof(ExpeditionStartedReadback), typeof(ReactorWaveReadback),
        typeof(HsuSampledReadback), typeof(CheckpointRestoredReadback),
        typeof(ZoneEnteredReadback), typeof(PortalWarpedReadback),
        // The one tick of the trigger-zone half. A hook is installed exactly when it is listed here, and the
        // judging module answers nothing until the session that owns it exists.
        typeof(TriggerZoneTick),
        // The one tick of the light-colour row: the frames a colour/intensity transition is spread over.
        typeof(LightColorTick),
        typeof(TeammateOverheadRender), typeof(TeammateOverheadRemoved), typeof(TeammateOverheadVisibility)
    });
}

// Both hooks are postfixes that only choose when to read. Parameters are never used: identity comes from
// PlayerManager.PlayerAgentsInLevel and each agent's linked SNet_Player after the native body returned.
// PlayerReplicationManager.OnSpawn / OnDeSpawn call these non-virtual bodies for local, remote and bot
// players; targets, RVAs and call edges are frozen in evidence/map-hooks.json.

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
