using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Native;

/// <summary>One player's slot table and ammunition pool, read back after a native body returned. The observer holds
/// this interface and no game type, so the rules below are exercised against a double and the native half is only
/// these reads. Every member answers about one machine's own state: a read that answers null leaves the port out of
/// the fact instead of publishing a placeholder.</summary>
internal interface IInventoryNativeReads
{
    /// <summary>The `gtfo.player` reference behind a native backpack, resolved through that domain's own lookup and
    /// never derived here, or null when the owning domain has no current reference.</summary>
    EntityReference? OwnerOfBackpack(object backpack);
    /// <summary>The `gtfo.player` reference of the player a native item belongs to, resolved the same way.</summary>
    EntityReference? OwnerOfItem(object item);
    /// <summary>The recorded equipment life a native item is right now, or null when this machine holds no live
    /// identity for it. The identity table's own current-check is part of this read, so a reference the machine no
    /// longer answers for never leaves here.</summary>
    EntityReference? EquipmentOfItem(object item);
    /// <summary>The slot table as it reads right now, or null when the backpack cannot answer. An empty slot has a
    /// null item; the order is the game's own slot order.</summary>
    IReadOnlyList<NativeSlot>? Slots(object backpack);
    /// <summary>Every ammunition pool this player holds, keyed by slot name, as it reads right now. A slot with no
    /// pool is absent rather than zero, so a pool that was never seen is not a pool that emptied.</summary>
    IReadOnlyDictionary<string, long>? Pools(object backpack);
    /// <summary>The magazine the native item reports, or null when the item has none. Declared apart from the
    /// reload reader's own clip read even though one native member answers both, so that a type implementing both
    /// readers has no member two interfaces could bind differently.</summary>
    int? ItemCharge(object item);
    /// <summary>The item's own reload flag, read to keep a reload's own sequence from being published here as a
    /// use. The reload rows own that fact.</summary>
    bool ItemIsReloading(object item);
    /// <summary>A finite world position of a native item, or null when the instance is destroyed or its transform
    /// does not read as a finite point.</summary>
    double[]? Position(object item);
}

/// <summary>What one slot held at one moment. The item and instance identities are what a slot change is measured
/// against; the equipment reference and the world position are resolved while the native object is still there,
/// because a clear destroys the instance and nothing about the item is readable afterwards.</summary>
internal sealed record SlotState(IntPtr Item, IntPtr Instance, EntityReference? Equipment, double[]? Position);

/// <summary>The six equipment rows, decided from one readback of a player's slot table and pool after a native
/// body returned:
///
/// `picked_up` and `dropped` are one slot's occupancy becoming and ceasing to be, so a single slot change produces
/// at most one of them. The drop reports the item the slot no longer holds, from the identity and the position this
/// machine resolved while that item was still readable; evidence `evidence/inventory-facts.json` records why the
/// row is defined as "left the backpack" and why its position port is optional.
///
/// `stack_changed` is published with either. This build's slot holds one item instance and exposes no count, so the
/// count is the slot's occupancy — 1 while an item is there, 0 once it is gone — which is the slot's own count
/// rather than a second reading of something else.
///
/// `refilled` is a slot's pool gaining rounds, published only for a pool-backed slot with a live equipment
/// identity, because the catalog row names the equipment the refill happened to. A pool that grew while a reload
/// life was open is that reload's transfer and is never published here as well.
///
/// `use_started` is the item's own use sequence starting on a non-reload use. The sequence entry point is the one
/// every use animation goes through, including the reload sequence, so the reload flag is read first and a reload
/// publishes no use fact: the reload rows already carry that start.
///
/// `use_failed` is a use the game refused, reported with the catalog's outcome and the gate's own reason. The one
/// gate this build exposes for a refused use is the reload gate; a shot refused for an empty magazine is
/// `forge.trigger.combat.dry_fire` and is never published here, and evidence records which refusals stay uncovered
/// rather than inventing them from a return value the metadata does not explain.</summary>
internal sealed class InventoryObserver
{
    /// <summary>The reason a refused reload carries: the game's own reload gate answered false.</summary>
    internal const string NotReloadableReason = "not-reloadable";
    /// <summary>The outcome a refused use is published with: the catalog's own `execution_outcome` member for a
    /// request the game declined before doing anything.</summary>
    internal const string RejectedOutcome = "rejected";

    /// <summary>One player's last readback, keyed by the owner reference so a slot the game renumbers shifts
    /// nothing else in the table.</summary>
    private sealed class Player
    {
        internal Player(EntityReference owner) => Owner = owner;
        internal EntityReference Owner { get; }
        internal Dictionary<string, SlotState> Slots { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> Pools { get; } = new(StringComparer.Ordinal);
    }

    private readonly IInventoryNativeReads _native;
    private readonly Func<EntityReference?, bool> _reloadOpen;
    private readonly Func<long> _world, _tick;
    private readonly Func<bool> _authoritative;
    private readonly Action<RuntimeEvent> _publish;
    private readonly Func<string, bool> _unsubscribed;
    private readonly Action<string> _report, _info;
    private readonly Dictionary<string, Player> _players = new(StringComparer.Ordinal);
    private readonly HashSet<string> _uses = new(StringComparer.Ordinal);
    private long _epoch = -1, _sequence;

    /// <summary>`reloadOpen` is the reload family's own table, asked rather than copied: the two families read the
    /// same pool, and a movement is published by exactly one of them.</summary>
    internal InventoryObserver(IInventoryNativeReads native, Func<EntityReference?, bool> reloadOpen,
        Func<long> worldEpoch, Func<long> tick, Func<bool> authoritative,
        Action<RuntimeEvent> publish, Action<string> report, Action<string> info, Func<string, bool> unsubscribed)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _reloadOpen = reloadOpen ?? throw new ArgumentNullException(nameof(reloadOpen));
        _world = worldEpoch ?? throw new ArgumentNullException(nameof(worldEpoch));
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _authoritative = authoritative ?? throw new ArgumentNullException(nameof(authoritative));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _unsubscribed = unsubscribed ?? throw new ArgumentNullException(nameof(unsubscribed));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    internal int TrackedPlayers => _players.Count;
    internal int OpenUses => _uses.Count;

    /// <summary>One player's slots and pools read back after a native body returned. The first sighting of a player
    /// records the table without publishing anything: a backpack that was never seen did not change, and a fact for
    /// it would report a pickup nobody made.</summary>
    internal void Reconcile(object backpack)
    {
        if (!Enter() || backpack == null) return;
        var owner = _native.OwnerOfBackpack(backpack);
        if (owner == null) return;
        var slots = _native.Slots(backpack);
        if (slots == null) return;
        if (!_players.TryGetValue(owner.Id, out var state))
        {
            _players[owner.Id] = state = new Player(owner);
            Record(slots, state, first: true);
        }
        else
        {
            Record(slots, state, first: false);
        }
        Pools(backpack, state);
    }

    private void Record(IReadOnlyList<NativeSlot> slots, Player state, bool first)
    {
        foreach (var slot in slots)
        {
            var before = state.Slots.TryGetValue(slot.Name, out var previous) ? previous : (SlotState?)null;
            var now = slot.Item == null
                ? (SlotState?)null
                : new SlotState(slot.ItemIdentity, slot.InstanceIdentity, slot.Equipment,
                    _native.Position(slot.Item));
            if (now == null) state.Slots.Remove(slot.Name); else state.Slots[slot.Name] = now;
            if (!first) Slot(slot.Name, before, now, state);
        }
    }

    /// <summary>One slot's change. An occupancy that appeared is a pickup, one that disappeared is a drop, and
    /// either also moves the slot's count; a slot whose instance was replaced under the same occupancy is the count
    /// row's alone.</summary>
    private void Slot(string name, SlotState? before, SlotState? now, Player player)
    {
        if (before == null && now != null)
        {
            if (now.Equipment == null)
            {
                _report("weapon.pickup-unresolved: slot " + name + " holds an item with no live equipment"
                    + " identity; no pickup fact published.");
                return;
            }
            Publish("picked_up", now.Equipment.Id, ReloadInventoryContract.PickedUpBinding,
                () => new RuntimeEvent(Id(), ReloadInventoryContract.PickedUpBinding, _epoch, Tick(),
                    "gtfo.equipment:" + now.Equipment.Id,
                    RuntimeJson.From(new { actor = (EntityReference?)player.Owner, item = now.Equipment })));
            Count(player, now.Equipment, 1, 1);
            return;
        }
        if (before != null && now == null)
        {
            if (before.Equipment == null)
            {
                _report("weapon.drop-unresolved: slot " + name + " lost an item with no live equipment identity;"
                    + " no drop fact published.");
                return;
            }
            Publish("dropped", before.Equipment.Id, ReloadInventoryContract.DroppedBinding,
                () => new RuntimeEvent(Id(), ReloadInventoryContract.DroppedBinding, _epoch, Tick(),
                    "gtfo.equipment:" + before.Equipment.Id,
                    before.Position == null
                        ? RuntimeJson.From(new { actor = (EntityReference?)player.Owner, item = before.Equipment })
                        : RuntimeJson.From(new { actor = (EntityReference?)player.Owner, item = before.Equipment,
                            position = before.Position })));
            Count(player, before.Equipment, 0, -1);
            return;
        }
        if (before != null && now != null && before.Instance != now.Instance)
            Count(player, now.Equipment, 1, 0);
    }

    /// <summary>The stack row for a slot's count. This build's slot holds one instance and no count member, so the
    /// count is the occupancy and the delta is what this readback moved it by. The row's `actor` and `item` ports
    /// are both required entities, so a change this machine cannot name is reported and not published: a fact the
    /// runtime would refuse for a null port is not a fact worth publishing.</summary>
    private void Count(Player player, EntityReference? equipment, int count, int delta)
    {
        if (equipment == null) return;
        Publish("stack_changed", equipment.Id, ReloadInventoryContract.StackChangedBinding,
            () => new RuntimeEvent(Id(), ReloadInventoryContract.StackChangedBinding, _epoch, Tick(),
                "gtfo.equipment:" + equipment.Id,
                RuntimeJson.From(new { actor = (EntityReference?)player.Owner, item = equipment, count, delta })));
    }

    /// <summary>Every pool the player holds, read back after a body that could have written one. A gain is a refill
    /// unless a reload life is open for that player, in which case the movement is the reload family's transfer and
    /// this row stays silent: both rows read the same pool, and one movement is published once.</summary>
    private void Pools(object backpack, Player player)
    {
        var pools = _native.Pools(backpack);
        if (pools == null) return;
        foreach (var pool in pools)
        {
            var known = player.Pools.TryGetValue(pool.Key, out var before);
            player.Pools[pool.Key] = pool.Value;
            if (!known || pool.Value <= before) continue;
            if (_reloadOpen(player.Owner)) continue;
            var gain = pool.Value - before;
            var equipment = Equipment(player, pool.Key);
            if (equipment == null)
            {
                _report("weapon.refill-unresolved: slot " + pool.Key + " gained " + Number(gain)
                    + " rounds with no live equipment identity; no refill fact published.");
                continue;
            }
            Publish("refilled", equipment.Id, ReloadInventoryContract.RefilledBinding,
                () => new RuntimeEvent(Id(), ReloadInventoryContract.RefilledBinding, _epoch, Tick(),
                    "gtfo.equipment:" + equipment.Id,
                    RuntimeJson.From(new { actor = (EntityReference?)player.Owner, equipment, amount = gain })));
        }
    }

    /// <summary>The equipment the slot held at the last readback, which is what a pool that grew belongs to.</summary>
    private static EntityReference? Equipment(Player player, string slot)
        => player.Slots.TryGetValue(slot, out var state) ? state.Equipment : null;

    /// <summary>An item's use sequence started. A reload sequence reaches the same entry point, so the reload flag
    /// is read first: a reload's start is the reload rows' and is never republished as a use.</summary>
    internal void UseStarted(object item)
    {
        if (!Enter() || _native.ItemIsReloading(item)) return;
        var equipment = _native.EquipmentOfItem(item);
        if (equipment == null || !_uses.Add(equipment.Id)) return;
        Publish("use_started", equipment.Id, ReloadInventoryContract.UseStartedBinding,
            () => new RuntimeEvent(Id(), ReloadInventoryContract.UseStartedBinding, _epoch, Tick(),
                "gtfo.equipment:" + equipment.Id,
                RuntimeJson.From(new { actor = _native.OwnerOfItem(item), equipment })));
    }

    /// <summary>A use sequence that had started is over. This row reports refusals, not outcomes, so an ended use
    /// publishes nothing: nothing read here says whether it succeeded, and `cancelled` would be a claim this machine
    /// cannot support. The open use is only forgotten, so the next sequence can start a new one.</summary>
    internal void UseEnded(object item)
    {
        var equipment = _native.EquipmentOfItem(item);
        if (equipment != null) _uses.Remove(equipment.Id);
    }

    /// <summary>A use the game refused before doing anything. The one gate this build exposes is the reload gate,
    /// so a reload the player asked for that the gate answered false for is the refusal this row carries.</summary>
    internal void Refused(object item)
    {
        if (!Enter()) return;
        var equipment = _native.EquipmentOfItem(item);
        if (equipment == null) return;
        Publish("use_failed", equipment.Id, ReloadInventoryContract.UseFailedBinding,
            () => new RuntimeEvent(Id(), ReloadInventoryContract.UseFailedBinding, _epoch, Tick(),
                "gtfo.equipment:" + equipment.Id,
                RuntimeJson.From(new { actor = _native.OwnerOfItem(item), equipment,
                    outcome = RejectedOutcome, reason = NotReloadableReason })));
    }

    private bool Enter()
    {
        SyncWorld();
        return _epoch != -1;
    }

    /// <summary>The world epoch and the authority gate, read at the front of every observation. A change drops
    /// every recorded table without a fact: a world transition is a hard boundary, so a slot that differs across it
    /// was never a change this machine watched.</summary>
    internal void SyncWorld()
    {
        var world = _authoritative() ? _world() : -1;
        if (world == _epoch) return;
        if (_epoch != -1 && (_players.Count != 0 || _uses.Count != 0))
            _info("weapon.inventory-tables-cleared world=" + Number(_epoch) + " players=" + Number(_players.Count)
                + " uses=" + Number(_uses.Count)
                + " reason=" + (world == -1 ? "not-authoritative" : "world-changed"));
        _players.Clear();
        _uses.Clear();
        _epoch = world;
    }

    /// <summary>Drops every recorded table for a disposal. Nothing is published: the machine is going away.</summary>
    internal void Clear()
    {
        if (_players.Count != 0 || _uses.Count != 0)
            _info("weapon.inventory-tables-cleared world=" + Number(_epoch) + " players=" + Number(_players.Count)
                + " uses=" + Number(_uses.Count) + " reason=dispose");
        _players.Clear();
        _uses.Clear();
        _epoch = -1;
    }

    /// <summary>Publishes one row's fact, and builds it only when somebody subscribes to the row. Nothing is
    /// listening on a closed binding: the kernel would answer `no-consumer` for the event, so it is never reached
    /// and the fact is not reported — and the fact itself, its id, its ports and its payload, is work no consumer
    /// would ever read. Reading the gate here rather than at the call site is what keeps every publish point from
    /// having to remember the order.</summary>
    private void Publish(string kind, string subject, string binding, Func<RuntimeEvent> fact)
    {
        if (_unsubscribed(binding)) return;
        var built = fact();
        try { _publish(built); }
        catch (RuntimeContractException error)
        { _report("weapon." + kind + "-fact-rejected: " + error.Code); return; }
        _info("weapon." + kind + "-fact subject=" + subject + " id=" + built.EventId);
    }

    private string Id() => "gtfo.weapon.inventory:" + Number(_epoch) + ":" + Number(checked(++_sequence));
    private long Tick() => Math.Max(0, _tick());
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
