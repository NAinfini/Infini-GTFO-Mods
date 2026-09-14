using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeRuntime.Framework;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

/// <summary>Reconciles post-body native backpack readback with the single W1 identity index.
/// The handle table only maps already-recorded lives to native objects; facts live in the index.</summary>
internal sealed class EquipmentNativeAdapter
{
    // The definition key is observed on this reviewed build; datablock content revisions are not observable here.
    internal const string ResourceRevision = "gtfo-build-20403457";
    // Player lives belong to ForgeMap. Weapon only asks the SDK which current reference an SNet_Player has,
    // so it never derives a player from slots, agents, names, pointers or the account key.
    internal const string PlayerKind = "gtfo.player";
    private sealed record Handle(EntityReference Entity, PlayerBackpack Backpack, IntPtr BackpackPointer,
        IntPtr ItemPointer, IntPtr InstancePointer, int SlotIndex, string ResourceId, EntityReference Owner, bool Deployed);
    private readonly Dictionary<IntPtr, Handle> _byInstance = new();
    private readonly Dictionary<string, Handle> _byEntity = new(StringComparer.Ordinal);
    private readonly HashSet<IntPtr> _unresolvedReported = new();
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report, _info;
    private EquipmentIdentitySession? _identity;
    private long _world = -1, _next;

    // Info lines are one per life start/end, observed wield fact or table clear, so in-game checks can follow
    // each step without a consuming plan. They carry only Forge references, slot and resource keys.
    internal EquipmentNativeAdapter(RuntimeKernel kernel, Func<bool> canExecute, Action<string> report, Action<string> info)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    internal void Attach(EquipmentIdentitySession identity)
    {
        if (_identity != null) throw new InvalidOperationException("The adapter already feeds an identity session.");
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    internal int TrackedCount => _byInstance.Count;

    /// <summary>The recorded life currently mapped to a native item, if any. Not a resolver: callers
    /// still validate the returned reference through Runtime before acting on it.</summary>
    internal EntityReference? EntityOf(Item? instance)
    {
        if (instance == null || _identity == null) return null;
        SyncWorld();
        return _byInstance.TryGetValue(instance.Pointer, out var handle) ? handle.Entity : null;
    }

    internal void Clear() { _byInstance.Clear(); _byEntity.Clear(); _unresolvedReported.Clear(); }

    internal void ReconcileInventory(PlayerInventoryBase? inventory)
    {
        if (inventory == null) return;
        var agent = inventory.Owner; // Unity equality: a destroyed agent reads as null.
        if (agent == null) return;
        var player = agent.Owner;
        if (player != null && PlayerBackpackManager.TryGetBackpack(player, out var backpack)) Reconcile(backpack);
    }

    internal void Reconcile(PlayerBackpack? backpack)
    {
        if (backpack == null || !Authoritative()) return;
        SyncWorld();
        var pointer = backpack.Pointer;
        var player = backpack.Owner;
        var owner = player == null ? null : _kernel.ResolveEntityInstance(PlayerKind, player);
        foreach (var stale in _byInstance.Values.Where(h => h.BackpackPointer == pointer).ToArray())
        {
            if (owner == null) Retire(stale, "owner-unresolved");
            else if (owner != stale.Owner) Retire(stale, "owner-changed");
            else if (!Matches(stale, backpack)) Retire(stale, "slot-changed");
        }
        var slots = backpack.Slots;
        if (slots == null) return;
        var inventory = Inventory(player);
        for (int index = 0; index < slots.Length; index++)
        {
            var item = slots[index];
            if (item == null || item.Instance == null) continue;
            if (owner == null)
            {
                if (_unresolvedReported.Add(pointer))
                    _report("weapon.owner-unresolved: backpack items are not recorded without a player reference from its owning domain.");
                return;
            }
            var instancePointer = item.Instance.Pointer;
            var resource = Resource(item);
            // The deployed marker belongs to the slot, not to a second object: a sentry or barrier stays this slot's
            // equipment life while the game reports the slot as deployed. The world instance itself is not tracked.
            bool deployed = backpack.IsDeployed((InventorySlot)index);
            if (_byInstance.TryGetValue(instancePointer, out var existing)
                && (existing.BackpackPointer != pointer || existing.SlotIndex != index
                    || existing.ItemPointer != item.Pointer || existing.ResourceId != resource))
            {
                // Moved or replaced: the old life ends; no transfer or re-wield history is reconstructed.
                Retire(existing, "moved-or-replaced");
                existing = null;
            }
            bool recorded = existing != null, locationChanged = recorded && existing!.Deployed != deployed;
            var handle = existing == null
                ? new Handle(NewEntity(), backpack, pointer, item.Pointer, instancePointer, index, resource, owner, deployed)
                : existing with { Deployed = deployed };
            _byInstance[instancePointer] = handle; _byEntity[handle.Entity.Id] = handle;
            var slot = ((InventorySlot)index).ToString();
            var observation = new EquipmentObservation(handle.Entity, resource, ResourceRevision, owner,
                deployed ? null : slot, deployed ? EquipmentLocation.Deployed : EquipmentLocation.Inventory,
                item.IsLoaded, !deployed && Wielded(inventory, item));
            try
            {
                var published = _identity!.Record(observation);
                if (!recorded)
                    _info("weapon.equipment-life-started id=" + handle.Entity.Id + " world=" + Number(handle.Entity.WorldEpoch)
                        + " owner=" + owner.Id + " ownerLife=" + Number(owner.LifeEpoch) + " slot=" + slot + " resource=" + resource
                        + " location=" + (deployed ? "Deployed" : "Inventory"));
                if (locationChanged)
                    _info("weapon.equipment-location id=" + handle.Entity.Id + " location=" + (deployed ? "Deployed" : "Inventory"));
                if (published == null) continue;
                if (published.Status == "rejected") _report("weapon.wield-fact-rejected: " + published.Code);
                else _info("weapon.wield-fact kind=" + (observation.IsWielded ? "equipped" : "unequipped") + " id=" + handle.Entity.Id
                    + " owner=" + owner.Id + " status=" + published.Status + " code=" + published.Code);
            }
            catch (RuntimeContractException error)
            {
                _report("weapon.observation-rejected: " + error.Code);
                // A life that never started is not reported as ended.
                Retire(handle, recorded ? "observation-rejected" : null);
            }
        }
    }

    internal bool IsNativeCurrent(EquipmentObservation value)
    {
        if (value == null || value.Entity.WorldEpoch != _world || _kernel.WorldEpoch != _world
            || !_byEntity.TryGetValue(value.Entity.Id, out var handle) || handle.Entity != value.Entity) return false;
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return false;
        var item = backpack.Slots![handle.SlotIndex]!;
        bool deployed = backpack.IsDeployed((InventorySlot)handle.SlotIndex);
        var player = backpack.Owner;
        return deployed == handle.Deployed && player != null && _kernel.ResolveEntityInstance(PlayerKind, player) == value.Owner
            && value.Location == (deployed ? EquipmentLocation.Deployed : EquipmentLocation.Inventory)
            && (deployed ? value.Slot == null : ((InventorySlot)handle.SlotIndex).ToString() == value.Slot)
            && Resource(item) == value.ResourceId && item.IsLoaded == value.IsReady
            && value.IsWielded == (!deployed && Wielded(Inventory(player), item));
    }

    private EntityReference NewEntity()
        => new("gtfo.equipment:" + _world.ToString(CultureInfo.InvariantCulture) + "."
            + checked(++_next).ToString(CultureInfo.InvariantCulture), _world, 1);

    private bool Authoritative()
    {
        if (_identity == null || !_canExecute() || !SNet.IsMaster) return false;
        var state = _kernel.Lifecycle;
        return state.StartupState == RuntimeStartupState.Ready && state.IsHost == true;
    }

    private void SyncWorld()
    {
        long world = _kernel.WorldEpoch;
        // The index clears itself on world, authority and stop transitions; that invalidates every handle.
        // Instance IDs are a per-world sequence; within one world the sequence never rewinds, because retired
        // lives of that world still block reuse of their IDs after an authority-loss clear.
        if (world != _world) { ClearObserved("world-changed"); _world = world; _next = 0; }
        else if (_byInstance.Count != 0 && _identity!.Count == 0) ClearObserved("identity-cleared");
    }

    private void ClearObserved(string reason)
    {
        int count = _byInstance.Count;
        Clear();
        if (count != 0) _info("weapon.equipment-lives-cleared world=" + Number(_world) + " count=" + Number(count) + " reason=" + reason);
    }

    private void Retire(Handle handle, string? reason)
    {
        _byInstance.Remove(handle.InstancePointer);
        _byEntity.Remove(handle.Entity.Id);
        _identity!.Remove(handle.Entity);
        if (reason != null) _info("weapon.equipment-life-ended id=" + handle.Entity.Id + " reason=" + reason);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool Matches(Handle handle, PlayerBackpack backpack)
    {
        var slots = backpack.Slots;
        if (slots == null || handle.SlotIndex >= slots.Length) return false;
        var item = slots[handle.SlotIndex];
        return item != null && item.Pointer == handle.ItemPointer && item.Instance != null
            && item.Instance.Pointer == handle.InstancePointer && Resource(item) == handle.ResourceId;
    }

    private static PlayerInventoryBase? Inventory(SNet_Player? player)
        => player != null && player.HasPlayerAgent ? player.PlayerAgent?.TryCast<PlayerAgent>()?.Inventory : null;

    private static bool Wielded(PlayerInventoryBase? inventory, BackpackItem item)
    {
        var wielded = inventory?.WieldedItem;
        return wielded != null && item.Instance != null && wielded.Pointer == item.Instance.Pointer;
    }

    private static string Resource(BackpackItem item)
    {
        var gear = item.GearIDRange;
        return gear != null
            ? "gtfo.gear:" + gear.GetChecksum().ToString(CultureInfo.InvariantCulture)
            : "gtfo.item:" + item.ItemID.ToString(CultureInfo.InvariantCulture);
    }
}
