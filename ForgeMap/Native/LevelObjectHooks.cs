using System;
using System.Collections.Generic;
using ChainedPuzzles;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>Every Harmony patch class the level-object facts install. The four readback points below are the
/// whole set; the plugin hands this one list to the class processor, so a hook is installed exactly when it is
/// listed here and nowhere else. The generators are not here: they are a map-object category, so their two
/// readbacks are declared with the door and terminal ones in `MapObjectHooks.cs`.
///
/// Each hook only chooses when to read, and every one of them reads the instance after the native body
/// returned rather than turning the callback's own arguments into fact values. A hook whose bindings no plan
/// subscribes to still reads its subject, because the value rows and the resource tables answer from the same
/// tables the facts fill; the read is one property access and one dictionary probe per native change.</summary>
internal static class LevelObjectHooks
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(ScanProgressReadback), typeof(ScanStateReadback),
        typeof(ResourceContainerStateReadback), typeof(ItemPickupReadback)
    });
}

// `ChainedPuzzleInstance.Master_OnPlayerScanChanged(float scanProgress, List<PlayerAgent> playersInScan, int
// inScanMax, bool[] reqObjsInScan)` is the master's own progress callback: only the master runs it, and it runs
// before the instance's body writes the state the progress belongs to. The postfix therefore reports the values
// the native side computed, not the state the body just wrote.
[HarmonyPatch(typeof(ChainedPuzzleInstance), "Master_OnPlayerScanChanged")]
internal static class ScanProgressReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ChainedPuzzleInstance __instance, float scanProgress, List<PlayerAgent> playersInScan)
    {
        if (!SNet.IsMaster) return;
        var players = playersInScan == null ? Array.Empty<PlayerAgent>() : playersInScan.ToArray();
        Plugin.Session?.GuardLevelObjects(module => LevelObjectObservation.ScanProgress(__instance, scanProgress, players, module));
    }
}

// The instance's own state replication callback: the one point where the game records that a scan became
// active or solved. Both the started and the completed row are read from the instance here.
[HarmonyPatch(typeof(ChainedPuzzleInstance), nameof(ChainedPuzzleInstance.OnStateChange))]
internal static class ScanStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ChainedPuzzleInstance __instance)
    {
        if (!SNet.IsMaster) return;
        Plugin.Session?.GuardLevelObjects(module => LevelObjectObservation.ScanStateChanged(__instance, module));
    }
}

// A resource container's own state callback: the one point where the game records that a locker or a resource
// box changed state — locked, opened, closed, or a player standing close to it.
[HarmonyPatch(typeof(LG_ResourceContainer_Sync), nameof(LG_ResourceContainer_Sync.OnStateChange))]
internal static class ResourceContainerStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_ResourceContainer_Sync __instance, pResourceContainerItemState newState)
    {
        if (!SNet.IsMaster) return;
        Plugin.Session?.GuardLevelObjects(module => LevelObjectObservation.ContainerStateChanged(__instance, newState, module));
    }
}

// A level item's own state callback: the one point where the game records that an item went into a player's
// hands or came back to the floor. The state carries the player the game itself replicated.
[HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.OnStateChange))]
internal static class ItemPickupReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_PickupItem_Sync __instance, pPickupItemState newState)
    {
        if (!SNet.IsMaster) return;
        Plugin.Session?.GuardLevelObjects(module => LevelObjectObservation.ItemStateChanged(__instance, newState, module));
    }
}
