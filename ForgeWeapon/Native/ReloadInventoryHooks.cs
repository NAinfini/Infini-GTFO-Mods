using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeWeapon.Native;

// The native half of the reload and inventory families. The observers themselves are game-independent: they are
// written in `IReloadNativeReads` / `IInventoryNativeReads`, an item key and a slot name, and they are what
// `ForgeWeapon/tests/ReloadInventoryFacts` exercises without a game. This file turns the real game members into
// those reads and installs the hooks that call them, so it is only ever compiled into `ForgeWeapon.Native`.
//
// Every hook only chooses when to read: the observed values always come from the native objects after the body
// returned, and a parameter is never trusted as a fact. The targets and their non-shared native bodies are frozen
// in `evidence/reload-facts.json` and `evidence/inventory-facts.json`.

/// <summary>The one reload and inventory session this process runs, and the hooks' way to reach it. A hook body has
/// no session to close over, so it asks here; both observers are null until <see cref="Install"/> has wired them,
/// which is what keeps a hook that fires before the session is built from doing anything at all.</summary>
internal static class ReloadInventoryHooks
{
    private static ReloadObserver? _reload;
    private static InventoryObserver? _inventory;

    /// <summary>The hook classes this family installs. The package's own hook list carries these beside the ones it
    /// already has, so its single `CreateClassProcessor` loop keeps installing everything.</summary>
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(ReloadGateAnswered), typeof(ReloadRequested), typeof(ReloadCommitted), typeof(ReloadFlagChanged),
        typeof(MagazineRefilled), typeof(PoolDataArrived), typeof(UseSequenceStarted), typeof(BackpackReconciled)
    });

    /// <summary>Builds the two observers over the game's own reads and wires them to this package's identity
    /// session. `identity` is what publishes: these facts belong to the one equipment registration the package
    /// already owns, so there is no second place a fact can leave from, and both families ask the one adapter
    /// whether this machine may observe at all.</summary>
    internal static void Install(RuntimeKernel kernel, EquipmentIdentitySession identity,
        EquipmentNativeAdapter adapter, Action<string> report, Action<string> info)
    {
        var reads = new ReloadNativeReads(adapter);
        // The registration's publication path as the `Action` the observers take: these facts belong to the one
        // equipment registration, so there is no second place a fact can leave from.
        Action<RuntimeEvent> publish = value => identity.Publish(value);
        var reload = new ReloadObserver(reads, identity.IsCurrent, () => kernel.WorldEpoch,
            () => kernel.CurrentTick, adapter.Authoritative, publish, report, info, identity.Unsubscribed);
        _reload = reload;
        // The inventory family asks the reload family's own table whether a movement is already inside a reload
        // window, so the one observer is built here and reached through the local rather than the field.
        _inventory = new InventoryObserver(reads, owner => reload.OpenFor(owner), () => kernel.WorldEpoch,
            () => kernel.CurrentTick, adapter.Authoritative, publish, report, info, identity.Unsubscribed);
    }

    /// <summary>Drops both sessions for a disposal. Nothing is published: the machine is going away.</summary>
    internal static void Clear()
    {
        _reload?.Clear();
        _inventory?.Clear();
        _reload = null;
        _inventory = null;
    }

    internal static ReloadObserver? Reload => _reload;
    internal static InventoryObserver? Inventory => _inventory;

    /// <summary>Every hook body's front door: the package's own session is asked once and its guard decides —
    /// owning thread, disposal, fault and start state — so a hook that fires with no session, on the wrong thread,
    /// or after the session faulted does nothing at all.</summary>
    internal static void Guard(Action<ReloadObserver> body)
    {
        var session = WeaponNativeSession.Current;
        var reload = _reload;
        if (session == null || reload == null) return;
        session.Guard(_ => body(reload));
    }

    internal static void GuardInventory(Action<InventoryObserver> body)
    {
        var session = WeaponNativeSession.Current;
        var inventory = _inventory;
        if (session == null || inventory == null) return;
        session.Guard(_ => body(inventory));
    }

    /// <summary>The wielded item of a player inventory whose backpack is this one. The chain is the game's own —
    /// backpack to owning player, player to agent, agent to inventory — so no table is kept here and a machine
    /// that holds no agent for that player gets null rather than a stale item.</summary>
    internal static ItemEquippable? Wielded(PlayerBackpack? backpack)
        => EquipmentNativeAdapter.Agent(backpack?.Owner)?.Inventory?.m_wieldedItem;
}

/// <summary>The game reads, answered from the native objects the hooks were handed. Every member re-reads the live
/// object on the call and caches nothing, so a value answered here is the game's value at that moment. A read that
/// throws is answered as the fallback and leaves its fact out, and an item this machine holds no recorded
/// equipment life for answers null rather than a guessed reference.</summary>
internal sealed class ReloadNativeReads : IReloadNativeReads, IInventoryNativeReads
{
    private readonly EquipmentNativeAdapter _adapter;

    internal ReloadNativeReads(EquipmentNativeAdapter adapter)
        => _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    /// <summary>The magazine of a native item. `GetCurrentClip` is the base member every weapon family answers, so
    /// one read covers the magazine families and the pressure-and-recharge tool family alike.</summary>
    public int? Clip(object item)
        => item is ItemEquippable equippable ? Safe<int?>(() => equippable.GetCurrentClip(), null) : null;

    public bool IsReloading(object item)
        => item is ItemEquippable equippable && Safe(() => equippable.IsReloading, false);

    public EntityReference? EquipmentOf(object item)
        => item is Item value ? _adapter.EntityOf(value) : null;

    public EntityReference? OwnerOf(object item)
        => item is Item value ? _adapter.OwnerOf(value) : null;

    public EntityReference? EquipmentOfItem(object item) => EquipmentOf(item);
    public EntityReference? OwnerOfItem(object item) => OwnerOf(item);
    public int? ItemCharge(object item) => Clip(item);
    public bool ItemIsReloading(object item) => IsReloading(item);

    /// <summary>The player reference behind a native backpack. The backpack itself names its owning player, so the
    /// read goes through the same domain lookup the equipment adapter already uses for an item's owner — the chain
    /// is backpack to player and player to reference, and nothing is derived from a slot or a name.</summary>
    public EntityReference? OwnerOfBackpack(object backpack)
        => backpack is PlayerBackpack value ? _adapter.OwnerOf(value.Owner) : null;

    public double[]? Position(object item)
    {
        if (item is not Item value) return null;
        var transform = Safe<Transform?>(() => value.transform, null);
        if (transform == null) return null;
        var position = Safe(() => transform.position, default(Vector3));
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return null;
        return new[] { (double)position.x, (double)position.y, (double)position.z };
    }

    /// <summary>The slot table as the game reports it right now: one entry per slot, with the item the slot holds
    /// and the equipment life this machine records for that item's instance.</summary>
    public IReadOnlyList<NativeSlot>? Slots(object backpack)
    {
        if (backpack is not PlayerBackpack value) return null;
        // The table is read through the list interface its own array type already implements: the game's slots are
        // an interop array, and nothing here needs that interop shape — only its entries, in slot order.
        IList<BackpackItem?>? slots;
        try { slots = value.Slots; }
        catch (Exception) { return null; }
        if (slots == null) return null;
        var table = new List<NativeSlot>(slots.Count);
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = (InventorySlot)index;
            var item = slots[index];
            var instance = item == null ? null : item.Instance;
            table.Add(new NativeSlot(slot.ToString(), instance,
                item?.Pointer ?? IntPtr.Zero, instance?.Pointer ?? IntPtr.Zero, _adapter.EntityOf(instance)));
        }
        return table;
    }

    /// <summary>Every slot's pool as it reads right now. A slot the game reports no pool for is left out, so a pool
    /// that was never seen is not a pool that emptied.</summary>
    public IReadOnlyDictionary<string, long>? Pools(object backpack)
    {
        if (backpack is not PlayerBackpack value) return null;
        var storage = Safe<PlayerAmmoStorage?>(() => value.AmmoStorage, null);
        if (storage == null) return null;
        var pools = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (InventorySlot slot in Enum.GetValues(typeof(InventorySlot)))
        {
            var ammo = Safe<InventorySlotAmmo?>(() => storage.GetInventorySlotAmmo(slot), null);
            if (ammo == null) continue;
            pools[slot.ToString()] = Safe(() => (long)ammo.BulletsInPack, 0L);
        }
        return pools;
    }

    /// <summary>One native read, with a failed call answered as the fallback. Every read here is a game member
    /// reached through interop, and a read that throws is exactly the case where a fact must not be published.</summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }
}

// The reload gate. `TriggerReload` is the player's own ask and the game answers it with `CanReloadCurrent`; the
// false answer is the one refusal this build exposes, and it is also reached from paths that never asked. Only a
// held item whose gate answered false is therefore a refused use.
[HarmonyPatch(typeof(PlayerInventoryBase), nameof(PlayerInventoryBase.CanReloadCurrent))]
internal static class ReloadGateAnswered
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryBase __instance, bool __result)
    {
        if (__result) return;
        ReloadInventoryHooks.GuardInventory(inventory => inventory.Refused(__instance.m_wieldedItem));
    }
}

// The reload entry point. It opens a life only when the item's own flag reads true right after, so the observer,
// not this hook, decides whether a reload is under way.
[HarmonyPatch(typeof(PlayerInventoryBase), nameof(PlayerInventoryBase.TriggerReload))]
internal static class ReloadRequested
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryBase __instance)
        => ReloadInventoryHooks.Guard(reload => reload.Requested(__instance.m_wieldedItem));
}

// The reload body. The magazine is read back after it ran, which is where the rounds that entered the magazine are
// visible; the life recorded its baseline when it opened, so a gain over that baseline is the transfer.
[HarmonyPatch(typeof(PlayerInventoryBase), nameof(PlayerInventoryBase.DoReload))]
internal static class ReloadCommitted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerInventoryBase __instance)
        => ReloadInventoryHooks.Guard(reload => reload.MagazineRead(__instance.m_wieldedItem));
}

// The item's own flag. The rising edge opens the life and the falling edge closes it as completed or cancelled;
// the setter is the one place both edges pass through, so no other hook has to guess which one it saw.
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.IsReloading), MethodType.Setter)]
internal static class ReloadFlagChanged
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemEquippable __instance)
        => ReloadInventoryHooks.Guard(reload => reload.FlagChanged(__instance, __instance.IsReloading));
}

// A slot's clip was written, which is where a reload actually moves rounds from the pack into the magazine. The
// magazine of the inventory that owns this pool is read, and the same readback reconciles the backpack's slots and
// pools: one readback, and the two families decide between them which of them owns each movement.
// The one-argument overload is named explicitly: this build declares a second `SetClipAmmoInSlot` that takes the
// slot, the ammo type and the slot's own pool entry, and a patch declared by name alone would not say which of the
// two bodies it brackets.
[HarmonyPatch(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.SetClipAmmoInSlot), new[] { typeof(InventorySlot) })]
internal static class MagazineRefilled
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerAmmoStorage __instance)
    {
        var backpack = __instance.m_playerBackpack;
        if (backpack == null) return;
        var wielded = ReloadInventoryHooks.Wielded(backpack);
        if (wielded != null) ReloadInventoryHooks.Guard(reload => reload.MagazineRead(wielded));
        ReloadInventoryHooks.GuardInventory(inventory => inventory.Reconcile(backpack));
    }
}

// Another machine's pool arrived as storage data. It is the same readback as any other pool write, so the pool that
// grew is published as the fact it is rather than as a second, network-specific one.
[HarmonyPatch(typeof(PlayerAmmoStorage), nameof(PlayerAmmoStorage.SetStorageData))]
internal static class PoolDataArrived
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerAmmoStorage __instance)
    {
        var backpack = __instance.m_playerBackpack;
        if (backpack != null) ReloadInventoryHooks.GuardInventory(inventory => inventory.Reconcile(backpack));
    }
}

// The use sequence entry point, which is private on this build and patched by name. A reload sequence reaches the
// same entry, so the observer reads the reload flag first and a reload never publishes a use fact.
[HarmonyPatch(typeof(ItemEquippable), "TryStartAnimationSequenceCoroutine")]
internal static class UseSequenceStarted
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemEquippable __instance)
        => ReloadInventoryHooks.GuardInventory(inventory => inventory.UseStarted(__instance));
}

// The backpack readback: every path that stores an item ends here, and one readback of the slot table and the pool
// is what the pickup, drop, stack and refill rows are decided from.
[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.CreateAndStoreBackpackItem))]
internal static class BackpackReconciled
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerBackpack __instance)
        => ReloadInventoryHooks.GuardInventory(inventory => inventory.Reconcile(__instance));
}
