using System;
using System.Collections.Generic;
using Gear;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;

namespace ForgeWeapon.Native;

internal static class WeaponNativeHooks
{
    /// <summary>The hook classes this file declares: the equipment, combat, attack-instance, deployed-device and
    /// melee families. The deployed-device five and the melee two are declared by the observers they feed
    /// (<c>WeaponDeployableFacts</c> and <c>WeaponMeleeHitFacts</c>); they are listed here because this file is
    /// the package's own hook list and the one place a patch is declared.</summary>
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(BackpackItemStored), typeof(BackpackSlotCleared), typeof(BackpackInstancesDestroyed), typeof(BackpackItemDeployed),
        typeof(LocalItemWielded), typeof(LocalItemUnwielded), typeof(SyncedItemEquipped), typeof(SyncedItemUnwielded),
        typeof(WeaponFired), typeof(ShotgunFired), typeof(SyncedWeaponFired), typeof(SyncedShotgunFired), typeof(BulletHit),
        typeof(GearPartsSpawned), typeof(GearOffersForSlot), typeof(GearPoolLoading),
        // The attack-instance family: the game's two empty-clip paths and the two ends of a burst sequence on the
        // two burst-capable archetypes.
        typeof(AttackAutoFiredEmptyClip), typeof(AttackBurstFiredEmptyClip),
        typeof(AttackBurstStarted), typeof(AttackBurstEnded), typeof(AttackAutoStarted), typeof(AttackAutoEnded),
        // The deployed-device and tool facts: one firing update pair on the sentry, the glue gun's two launch
        // bodies, and the mine's own trigger.
        typeof(SentryFireStarted), typeof(SentryFired), typeof(GlueGunBurst), typeof(GlueGunSingle), typeof(MineDetonated),
        // The melee hit: the first-person body the local player's own swing runs per target, the third-person
        // body this machine runs for somebody else, and the master-side receiver a remote player's swing becomes
        // on the host.
        typeof(MeleeSwingDamage), typeof(MeleeThirdPersonDamage), typeof(MeleeRemoteDamage),
        // The one chain in the shipped code that rebuilds an archetype, which is where an accepted instance
        // override is replayed from the ledger after the game has built the new block.
        typeof(GearSpawnCompleted),
        // This batch's own bodies: the two ends of a firing window, the two charge families, and the holder's
        // own update the sight state is read from.
        typeof(WeaponFireStarted), typeof(WeaponFireResolved), typeof(ShotgunFireStarted), typeof(ShotgunFireResolved),
        typeof(MeleeChargeStarted), typeof(MeleeChargeRan), typeof(MeleeChargeEnded), typeof(MeleeChargeReleased),
        typeof(RangedChargeRan), typeof(AimStateSampled)
    });

    /// <summary>Every hook class the package installs, in one list: this file's own set plus the two families that
    /// declare theirs beside their observer (<c>ReloadInventoryHooks</c> and <c>WeaponPlacementHooks</c>).
    /// <see cref="Plugin"/> installs this and nothing else, so a hook class that is not carried here is never
    /// patched — the failure mode a family's own list would otherwise reintroduce.</summary>
    internal static IReadOnlyList<Type> Installed { get; } = Sum();

    private static IReadOnlyList<Type> Sum()
    {
        var types = new List<Type>(Types.Count + ReloadInventoryHooks.Types.Count + WeaponPlacementHooks.Types.Count);
        types.AddRange(Types);
        types.AddRange(ReloadInventoryHooks.Types);
        types.AddRange(WeaponPlacementHooks.Types);
        return types.AsReadOnly();
    }
}

// Every hook is a postfix that only chooses when to read. Observed values always come from the
// backpack and inventory state after the native body returned; parameters are never trusted as facts.
// Targets and their non-shared native bodies are frozen in evidence/w1-native-hooks.json.

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.CreateAndStoreBackpackItem))]
internal static class BackpackItemStored
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.Reconcile(__instance));
}

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.TryClearSlot))]
internal static class BackpackSlotCleared
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.Reconcile(__instance));
}

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.DestroyAllInstance))]
internal static class BackpackInstancesDestroyed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.Reconcile(__instance));
}

// Deployables stay in their backpack slot: the sentry or mine marks the slot deployed instead of leaving the
// backpack, and a recall clears the same marker. This hook only chooses when to read that marker back.
[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.SetDeployed))]
internal static class BackpackItemDeployed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.Reconcile(__instance));
}

[HarmonyPatch(typeof(PlayerInventoryLocal), nameof(PlayerInventoryLocal.DoWieldItem))]
internal static class LocalItemWielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryLocal __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.ReconcileInventory(__instance));
}

[HarmonyPatch(typeof(PlayerInventoryLocal), nameof(PlayerInventoryLocal.UnWield))]
internal static class LocalItemUnwielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryLocal __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.ReconcileInventory(__instance));
}

[HarmonyPatch(typeof(PlayerInventorySynced), nameof(PlayerInventorySynced.DoEquipItem))]
internal static class SyncedItemEquipped
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventorySynced __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.ReconcileInventory(__instance));
}

[HarmonyPatch(typeof(PlayerInventorySynced), nameof(PlayerInventorySynced.UnWield))]
internal static class SyncedItemUnwielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventorySynced __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Adapter.ReconcileInventory(__instance));
}

// The Fire postfixes are the per-shot dedupe point: one body runs per trigger pull, while the bullet routine it
// calls runs once per hit, so a shotgun's several hits still belong to the one shot opened here.
// Slot 151 has four bodies in this build and each one is reached on its own. `BulletWeapon.Fire` and
// `Shotgun.Fire` are the firing player's own weapon — the unsynced body, and the one that registers the shot
// with `PlayerSync` — while `BulletWeaponSynced.Fire` and `ShotgunSynced.Fire` are the copy every other machine
// holds of that player's weapon, replayed from the replicated shot count. `RifleWeapon` and `RifleWeaponSynced`
// declare no Fire of their own and use their family's body. No one of the four reaches another, so one patch per
// body publishes exactly one shot fact and a shot counted once stays counted once; the call structure is frozen
// in evidence/w4-player-fire.json.

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.Fire))]
internal static class WeaponFired
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BulletWeapon __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Combat.Open(__instance));
}

[HarmonyPatch(typeof(Shotgun), nameof(Shotgun.Fire))]
internal static class ShotgunFired
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Shotgun __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Combat.Open(__instance));
}

// The synced pair is not a duplicate of the pair above: a remote player's weapon never runs the unsynced body
// on this machine, so without these two the firing player of every other machine — a joining client on the host,
// or the host and every other client seen from one machine — produces no shot at all.
[HarmonyPatch(typeof(BulletWeaponSynced), nameof(BulletWeaponSynced.Fire))]
internal static class SyncedWeaponFired
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BulletWeaponSynced __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Combat.Open(__instance));
}

[HarmonyPatch(typeof(ShotgunSynced), nameof(ShotgunSynced.Fire))]
internal static class SyncedShotgunFired
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ShotgunSynced __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Combat.Open(__instance));
}

// One static body serves every weapon family, and a candidate is published into the shot the last `Fire` opened;
// a hit with no open shot is not billed to a player, and the sentry's own firing component
// (`SentryGunInstance_Firing_Bullets.FireBullet` / `UpdateFireShotgunSemi`) is the caller that reaches this
// routine without any weapon `Fire`.
// The firing weapon is the patch's own instance and is not read: the hit data names the shooter and the point.
// `weaponRayData` is the game's own parameter name for the by-value hit data.
[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.BulletHit))]
internal static class BulletHit
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BulletWeapon __instance, Weapon.WeaponHitData weaponRayData)
        => WeaponNativeSession.Current?.Guard(session => session.Combat.Hit(weaponRayData));
}

// The two ends of one firing body, around the shot the four `Fire` patches above already count. The window is
// what tells a shot that reached something from one that reached nothing: the native hit routine only runs when
// a ray did reach something, so a shot with no impact in its own window is the miss the `shot_resolved` row
// publishes. The bodies are the same two the shot fact is counted from, so a shot that is not an equipment life
// this machine answers for produces no resolution either.
[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.Fire))]
internal static class WeaponFireStarted
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(BulletWeapon __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Combat.BeginFire(__instance));
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.Fire))]
internal static class WeaponFireResolved
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BulletWeapon __instance)
        => WeaponNativeSession.Current?.Guard(session =>
        {
            if (session.Combat.ResolveFire(__instance) is { } shot) session.CombatFacts.ShotResolved(shot);
        });
}

[HarmonyPatch(typeof(Shotgun), nameof(Shotgun.Fire))]
internal static class ShotgunFireStarted
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Shotgun __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Combat.BeginFire(__instance));
}

[HarmonyPatch(typeof(Shotgun), nameof(Shotgun.Fire))]
internal static class ShotgunFireResolved
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Shotgun __instance)
        => WeaponNativeSession.Current?.Guard(session =>
        {
            if (session.Combat.ResolveFire(__instance) is { } shot) session.CombatFacts.ShotResolved(shot);
        });
}

// The two charge families. A melee charge is the `MWS_ChargeUp` state object's own life: `Enter` starts it,
// `Update` runs it, `Exit` ends it, and `OnChargeupRelease` is the charged attack really being swung. A ranged
// charge is the archetype's own per-frame update, read on both sides of the body so a charge that starts and
// ends inside one update is still seen as both.
[HarmonyPatch(typeof(MWS_ChargeUp), nameof(MWS_ChargeUp.Enter))]
internal static class MeleeChargeStarted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MWS_ChargeUp __instance)
        => WeaponNativeSession.Current?.Guard(_ => CombatFactsObserver.Current?.MeleeEntered(__instance));
}

[HarmonyPatch(typeof(MWS_ChargeUp), nameof(MWS_ChargeUp.Update))]
internal static class MeleeChargeRan
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MWS_ChargeUp __instance)
        => WeaponNativeSession.Current?.Guard(_ => CombatFactsObserver.Current?.MeleeRan(__instance));
}

[HarmonyPatch(typeof(MWS_ChargeUp), nameof(MWS_ChargeUp.Exit))]
internal static class MeleeChargeEnded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MWS_ChargeUp __instance)
        => WeaponNativeSession.Current?.Guard(_ => CombatFactsObserver.Current?.MeleeEnded(__instance));
}

[HarmonyPatch(typeof(MWS_ChargeUp), "OnChargeupRelease")]
internal static class MeleeChargeReleased
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MWS_ChargeUp __instance)
        => WeaponNativeSession.Current?.Guard(_ => CombatFactsObserver.Current?.MeleeReleased(__instance));
}

// A Harmony patch body is named for the patch point it is, and the patch point here is the body the game already
// runs — this package drives no loop of its own. The priority keeps the reading after every other patch of the
// same body, so what is read is the state the game finished deciding.
[HarmonyPatch(typeof(BulletWeaponArchetype), nameof(BulletWeaponArchetype.Update))]
internal static class RangedChargeRan
{
    private static readonly Dictionary<IntPtr, bool> Before = new();

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(BulletWeaponArchetype __instance)
    {
        if (__instance == null) return;
        Before[__instance.Pointer] = CombatFactsObserver.RangedCharging(__instance);
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BulletWeaponArchetype __instance)
    {
        if (__instance == null) return;
        Before.Remove(__instance.Pointer, out var before);
        WeaponNativeSession.Current?.Guard(_ => CombatFactsObserver.Current?.RangedRan(__instance, before));
    }
}

// The holder's own per-frame body, which is where the sights' state is finished being decided. The state is the
// holder's own `ItemAimTrigger`, not the key that asked for it, so an aim that takes time to come up or that an
// EMP folded away is still seen entering and leaving.
[HarmonyPatch(typeof(FirstPersonItemHolder), "Update")]
internal static class AimStateSampled
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(FirstPersonItemHolder __instance)
        => WeaponNativeSession.Current?.Guard(_ => CombatFactsObserver.Current?.AimSampled(__instance));
}

// A deployed object is a world instance separate from the backpack life that placed it, and its placement and
// ending hooks live in `WeaponPlacementHooks` beside the kind each family declares.

// The one presentation hook: a holder has finished assembling its parts, which is the only point at which every
// part of this holder exists. It reads the gear's own offline block record and writes the authored absolute local
// pose onto the parts it finds; it publishes no fact and changes no game state. It fires once per holder, so the
// first person gear, another player's third person copy, a menu preview, an icon render and a deployed sentry each
// reach it on their own.
[HarmonyPatch(typeof(GearPartHolder), nameof(GearPartHolder.OnAllPartsSpawned))]
internal static class GearPartsSpawned
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(GearPartHolder __instance)
        => WeaponNativeSession.Current?.Guard(session => session.GearParts.Apply(__instance));
}

// The loadout policy's two ends. Neither is an observation: one narrows the pool the game is about to read and one
// projects the list it hands to a picker, and both answer the game's own list in full whenever no policy is in
// force for the loaded rundown. `GetAllGearForSlot` is the only source of a slot's selectable gear — the lobby
// window and the in-level inventory flow both build from it — so the projection covers every consumer at once,
// while the expedition gate keeps a gear already carried from being hidden from the HUD.

// Last, so the projection sees the array every other patch left behind; the body is read-only and replaces the
// returned array rather than any state the game owns.
[HarmonyPatch(typeof(GearManager), nameof(GearManager.GetAllGearForSlot))]
internal static class GearOffersForSlot
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(InventorySlot slot, ref Il2CppReferenceArray<GearIDRange> __result)
    {
        // The session is read from its own property rather than captured in a delegate: a `ref` parameter cannot
        // be captured, and the array handed back here is the one the game's own caller is about to read.
        var session = WeaponNativeSession.Current;
        if (session == null) return;
        var offers = __result;
        if (offers == null) return;
        var projected = session.Offer(slot, Items(offers));
        if (projected == null) return;
        // The game's own array is replaced only by a projection: the array built here is the type the game's own
        // getter returns, holds the policy's gears in the offer's own order, and is never handed to the game when
        // no policy is in force.
        var items = new GearIDRange[projected.Count];
        for (int index = 0; index < items.Length; index++) items[index] = projected[index];
        __result = items;
    }

    /// <summary>The game's own returned array read item by item, so the projection works in managed gears. The
    /// array was built by the game's own getter for this one call, so nothing outlives it; it is read through the
    /// list interface the interop array already implements rather than through its own type, which keeps this hook
    /// compiled against the one interop member the layout suite pins.</summary>
    internal static GearIDRange[] Items(IList<GearIDRange> live)
    {
        var items = new GearIDRange[live.Count];
        for (int index = 0; index < items.Length; index++) items[index] = live[index];
        return items;
    }
}

// First, so the pool is already the policy's when `RescanFavorites` reads it: a `LastEquipped_*` name that is not
// in the narrowed pool matches nothing and the game falls back to that pool's own first entry. The return value is
// never false — a policy that cannot be applied leaves the game's own contents in place.
[HarmonyPatch(typeof(GearManager), nameof(GearManager.OnGearLoadingDone))]
internal static class GearPoolLoading
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => WeaponNativeSession.Current?.GuardNarrow();
}

// The one chain that rebuilds a weapon's archetype. The postfix runs after the game finished, which is the order
// the replay needs: the rebuilt instance carries the game's own block again, and the ledger's own absolute field
// set is written onto the new clone. Nothing is replayed for a weapon no override was ever accepted for.
[HarmonyPatch(typeof(BulletWeaponSynced), nameof(BulletWeaponSynced.OnGearSpawnComplete))]
internal static class GearSpawnCompleted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(BulletWeaponSynced __instance)
        => WeaponNativeSession.Current?.Guard(session => session.Overrides.OnGearSpawnComplete(__instance));
}
