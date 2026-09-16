using System;
using System.Collections.Generic;
using Gear;
using HarmonyLib;

namespace ForgeWeapon.Native;

/// <summary>
/// The deployed-device placement hooks: the four world-instance bodies that say a sentry or a mine now exists and
/// the three that say it is gone, each declaring the family it belongs to.
///
/// The two ends are the instance's own spawn and its own pickup, and the world object is a separate replicated
/// object from the backpack life that placed it. Its spawn is the moment a device really is on the ground on every
/// machine: in this build `SentryGunInstance.OnSpawn` is one of exactly three bodies that write the owning
/// backpack's deployed marker, and it is reached through the item replication path rather than by the placement
/// code, so the host sees a client's placement here even though the client's own marker write never leaves that
/// machine. The mine family never writes that marker at all — neither its spawn nor its pickup does — so this
/// spawn is the only deploy signal the two families share, and its pickup is the mine's only recall signal.
///
/// The recall is the pickup body on both families, which is an `ISyncedItem` implementation reached through
/// interface dispatch and takes the acting player as its own parameter. Neither the sentry's nor the mine's
/// `OnDestroy` is a recall: those are the world taking the object away, and the life-cycle observer reports them
/// as the ending they are.
///
/// Nothing here reads a value out of a parameter: the kind is the hook class's own declaration of the body it
/// patches — the sentry hook patches `SentryGunInstance` and nothing else — and the position, owner and identity
/// are read from the instance after its body returned. Bodies and their decoded edges are frozen in
/// `evidence/w4-deployable-hooks.json` and `evidence/deployable-life-facts.json`.
/// </summary>
internal static class WeaponPlacementHooks
{
    /// <summary>The hook classes this family installs. The package's own hook list carries these beside the ones
    /// it already has, so its single `CreateClassProcessor` loop keeps installing everything.</summary>
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(SentryPlaced), typeof(SentryRecalled), typeof(SentryWorldDespawned), typeof(SentryWorldDestroyed),
        typeof(MinePlaced), typeof(MineRecalled), typeof(MineWorldDestroyed)
    });
}

// A sentry world instance appeared. Slot 150 of the replication path, so it is the game's own spawn entry and not
// a caller of the placement code.
[HarmonyPatch(typeof(SentryGunInstance), nameof(SentryGunInstance.OnSpawn))]
internal static class SentryPlaced
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(SentryGunInstance __instance)
        => WeaponNativeSession.Current?.Guard(session =>
            session.Adapter.TrackDeployed(__instance, WeaponPlacementContract.SentryGunKind));
}

// A mine world instance appeared. Same Slot 150 shape and the same kind of dynamic replicator supplier, so the
// mine's world object also exists through the replication path.
[HarmonyPatch(typeof(MineDeployerInstance), nameof(MineDeployerInstance.OnSpawn))]
internal static class MinePlaced
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MineDeployerInstance __instance)
        => WeaponNativeSession.Current?.Guard(session =>
            session.Adapter.TrackDeployed(__instance, WeaponPlacementContract.MineKind));
}

// The recall, on both families: Slot 45 of ISyncedItem, so it is the game's own recall entry and it carries the
// acting player. The sentry's body also clears the backpack marker; the mine's does not, which is why the world
// object rather than the marker is what both recalls are read from.
[HarmonyPatch(typeof(SentryGunInstance), nameof(SentryGunInstance.SyncedPickup))]
internal static class SentryRecalled
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(SentryGunInstance __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.EndDeployed(__instance, "recalled", recalled: true));
}

[HarmonyPatch(typeof(MineDeployerInstance), nameof(MineDeployerInstance.SyncedPickup))]
internal static class MineRecalled
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MineDeployerInstance __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.EndDeployed(__instance, "recalled", recalled: true));
}

// The world taking a device away, which is not a recall: `OnDespawn` (Slot 35, an override of the item's own end)
// and the private `OnDestroy` are separate paths that both reach the same teardown. Which of them arrives first
// is what the placement's ending is dated to, and the first one to arrive is the one that publishes.
[HarmonyPatch(typeof(SentryGunInstance), nameof(SentryGunInstance.OnDespawn))]
internal static class SentryWorldDespawned
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(SentryGunInstance __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.EndDeployed(__instance, "despawned", recalled: false));
}

// The mine family declares no `OnDespawn`, so its only ending paths are the pickup above and this one.
[HarmonyPatch(typeof(MineDeployerInstance), nameof(MineDeployerInstance.OnDestroy))]
internal static class MineWorldDestroyed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MineDeployerInstance __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.EndDeployed(__instance, "destroyed", recalled: false));
}

[HarmonyPatch(typeof(SentryGunInstance), nameof(SentryGunInstance.OnDestroy))]
internal static class SentryWorldDestroyed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(SentryGunInstance __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.EndDeployed(__instance, "destroyed", recalled: false));
}
