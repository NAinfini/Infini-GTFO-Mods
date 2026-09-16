using System;
using GameData;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>Every Harmony patch class the level-event rows install. Each one is a postfix on the game's own
/// entry for the transition the row is about, so the fact is published after the native body decided it rather
/// than from a guess about what the body was going to do. The classes are listed in `MapNativeHooks.Types`, which
/// is the list the plugin installs, so a hook is installed exactly when it is listed there and nowhere else.
///
/// Only the master publishes: every one of these transitions is a decision the host's world made — the objective
/// machine, the recall and the portal all run on the master, and a client's copy of the same reading would be a
/// second publisher of a session fact the kernel answers per host. The zone row reads a local agent's own field,
/// so it is the only one whose report a client makes; it publishes through the same host guard because the
/// kernel's replay ledger is per host world and a client-side fact for the same zone would double the row.
///
/// Evidence for every target below is in `evidence/level-event-hooks.json`, with the member signatures, the
/// call edges and the build they were read from.</summary>

[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.OnLocalPlayerStartExpedition))]
internal static class ExpeditionStartedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        if (!SNet.IsMaster) return;
        var session = Plugin.Session;
        if (session == null) return;
        // The level's own identity is already read for the `level` attachment matcher; the row publishes the same
        // string, so a plan mounted on a level and a plan reading this row agree about which level they are in.
        session.GuardLevelEvents(module => module.ExpeditionStarted(session.LevelReference()));
    }
}

[HarmonyPatch(typeof(WardenObjective), nameof(WardenObjective.OnStatusChange), new[] { typeof(bool), typeof(pWardenObjectiveState), typeof(eWardenObjectiveStatus), typeof(eWardenObjectiveStatus), typeof(eWardenSubObjectiveStatus), typeof(eWardenSubObjectiveStatus) })]
internal static class ObjectiveStatusReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(bool isRecall)
    {
        if (!SNet.IsMaster) return;
        Plugin.Session?.GuardLevelEvents(module =>
            LevelEventObservation.ReadObjectiveStatus(isRecall, module.ObjectiveStatusChanged));
    }
}

// The reactor's waves are its objective's event chain, so the chain step is read after the objective's own chain
// hook ran: the machine has already advanced its index by then and the report carries the new one.
[HarmonyPatch(typeof(WardenObjective), nameof(WardenObjective.OnStartInChain))]
internal static class ReactorWaveReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(WardenObjective __instance)
    {
        if (!SNet.IsMaster) return;
        string layer = LevelEventObservation.LayerName(__instance.Layer);
        int wave = LevelEventObservation.ChainIndex(layer);
        if (wave < 0) return;
        Plugin.Session?.GuardLevelEvents(module => module.ReactorWaveAdvanced(layer, wave, wave - 1));
    }
}

// The HSU's own transition: the objective item has been taken out of the activator. The class is the objective
// component the vanilla `ActivateHSU_Events` list belongs to, so the row fires exactly where the mount point does.
[HarmonyPatch(typeof(WO_HSUFindTakeSample), nameof(WO_HSUFindTakeSample.OnLocalPlayerSolvedObjectiveItem))]
internal static class HsuSampledReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(WO_HSUFindTakeSample __instance)
    {
        if (!SNet.IsMaster) return;
        string layer = LevelEventObservation.LayerName(__instance.Layer);
        var hsu = __instance.m_hsu;
        string? container = hsu == null
            ? null
            : LevelEventObservation.ItemReference(hsu.m_serialNumber, hsu.m_itemKey);
        Plugin.Session?.GuardLevelEvents(module => module.HsuSampled(layer, container));
    }
}

// The recall's own completion. `isRecall` is the game's own flag on the objective callback, and this is the entry
// the game calls once the reloaded world is in place, so the row fires after the state is back and not when the
// recall was requested.
[HarmonyPatch(typeof(CheckpointManager), nameof(CheckpointManager.OnRecallComplete))]
internal static class CheckpointRestoredReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        if (!SNet.IsMaster) return;
        Plugin.Session?.GuardLevelEvents(module => module.CheckpointRestored());
    }
}

// The game's own zone-entry entry, with the player and the zone it resolved. The address published is the zone's
// own three-layer coordinates, so the row is the same level-scope address every other row of this family names.
[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.OnLocalPlayerEnterZone))]
internal static class ZoneEnteredReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerAgent player, LG_Zone zone)
    {
        if (!SNet.IsMaster) return;
        var reference = PlayerIdentityModule.Current?.ReferenceOf(player);
        if (reference == null) return;
        string? address = LevelEventObservation.ZoneAddress(zone);
        if (address == null) return;
        Plugin.Session?.GuardLevelEvents(module => module.ZoneEntered(reference, address));
    }
}

// The portal's own warp entry: the player, the node they left and the node they arrived at. The portal's
// destination dimension is its own field and the "from" side is the dimension of the node the player left, so
// both numbers are reads of the game's state rather than a delta this layer remembers.
[HarmonyPatch(typeof(LG_DimensionPortal), nameof(LG_DimensionPortal.OnWarp))]
internal static class PortalWarpedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_DimensionPortal __instance, AIGraph.AIG_CourseNode fromCourseNode)
    {
        if (!SNet.IsMaster) return;
        string? id = LevelEventObservation.PortalId(__instance);
        if (id == null) return;
        int to = LevelEventObservation.Dimension(__instance);
        int from = LevelEventObservation.NodeDimension(fromCourseNode);
        Plugin.Session?.GuardLevelEvents(module => module.PortalWarped(id, to, from));
    }
}
