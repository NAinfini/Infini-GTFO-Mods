using System;
using System.Collections.Generic;
using HarmonyLib;
using Player;

namespace ForgeWeapon.Native;

internal static class WeaponNativeHooks
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(BackpackItemStored), typeof(BackpackSlotCleared), typeof(BackpackInstancesDestroyed), typeof(BackpackItemDeployed),
        typeof(LocalItemWielded), typeof(LocalItemUnwielded), typeof(SyncedItemEquipped), typeof(SyncedItemUnwielded)
    });
}

// Every hook is a postfix that only chooses when to read. Observed values always come from the
// backpack and inventory state after the native body returned; parameters are never trusted as facts.
// Targets and their non-shared native bodies are frozen in evidence/w1-native-hooks.json.

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.CreateAndStoreBackpackItem))]
internal static class BackpackItemStored
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Current?.Guard(adapter => adapter.Reconcile(__instance));
}

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.TryClearSlot))]
internal static class BackpackSlotCleared
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Current?.Guard(adapter => adapter.Reconcile(__instance));
}

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.DestroyAllInstance))]
internal static class BackpackInstancesDestroyed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Current?.Guard(adapter => adapter.Reconcile(__instance));
}

// Deployables stay in their backpack slot: the sentry or barrier marks the slot deployed instead of leaving the
// backpack, and the pickup clears the same marker. This hook only chooses when to read that marker back.
[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.SetDeployed))]
internal static class BackpackItemDeployed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Current?.Guard(adapter => adapter.Reconcile(__instance));
}

[HarmonyPatch(typeof(PlayerInventoryLocal), nameof(PlayerInventoryLocal.DoWieldItem))]
internal static class LocalItemWielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryLocal __instance)
        => WeaponNativeSession.Current?.Guard(adapter => adapter.ReconcileInventory(__instance));
}

[HarmonyPatch(typeof(PlayerInventoryLocal), nameof(PlayerInventoryLocal.UnWield))]
internal static class LocalItemUnwielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryLocal __instance)
        => WeaponNativeSession.Current?.Guard(adapter => adapter.ReconcileInventory(__instance));
}

[HarmonyPatch(typeof(PlayerInventorySynced), nameof(PlayerInventorySynced.DoEquipItem))]
internal static class SyncedItemEquipped
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventorySynced __instance)
        => WeaponNativeSession.Current?.Guard(adapter => adapter.ReconcileInventory(__instance));
}

[HarmonyPatch(typeof(PlayerInventorySynced), nameof(PlayerInventorySynced.UnWield))]
internal static class SyncedItemUnwielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventorySynced __instance)
        => WeaponNativeSession.Current?.Guard(adapter => adapter.ReconcileInventory(__instance));
}
