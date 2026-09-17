using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Enemies;
using ForgeRuntime.Framework;
using Gear;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

/// <summary>Reconciles post-body native backpack readback with the single W1 identity index.
/// The handle table only maps already-recorded lives to native objects; facts live in the index.</summary>
internal sealed class EquipmentNativeAdapter : IAttackNativeReads
{
    // The definition key is observed on this reviewed build; datablock content revisions are not observable here.
    internal const string ResourceRevision = "gtfo-build-20403457";
    // The game's own record of the offline gear block a gear came from: GearManager.LoadOfflineGearDatas builds
    // GearIDRange.PlayfabItemInstanceId as this prefix plus PlayerOfflineGearDataBlock.persistentID, and the same
    // spelling is what the game's favorites file stores as LastEquipped_*. Evidence: evidence/w5-gear-block.json.
    internal const string GearBlockPrefix = "OfflineGear_ID_";
    // Player lives belong to ForgeMap. Weapon only asks the SDK which current reference an SNet_Player has,
    // so it never derives a player from slots, agents, names, pointers or the account key. Enemy lives belong to
    // ForgeEnemy the same way: a hit asks its agent's own domain for the current reference and never invents one.
    // Map objects — a door, a terminal — belong to ForgeMap as well: a hit hands the object it was resolved
    // against to that kind's own instance lookup, which is what knows what a map object is and how it is
    // addressed. Weapon never reads a door or a terminal type, so the map domain can answer this kind without
    // this package depending on it.
    internal const string PlayerKind = "gtfo.player";
    internal const string EnemyKind = "gtfo.enemy";
    internal const string MapObjectKind = "gtfo.map_object";
    internal sealed record Handle(EntityReference Entity, PlayerBackpack Backpack, IntPtr BackpackPointer,
        IntPtr ItemPointer, IntPtr InstancePointer, int SlotIndex, string ResourceId, EntityReference Owner, bool Deployed)
    {
        /// <summary>Everything that accumulates during this one equipment life. A rebuilt handle shares this
        /// object, so a `with` copy never loses the shot count, the placement count or the open world instance.</summary>
        internal Life Life { get; } = new();
    }
    /// <summary>Per-life mutable state: shots of this equipment life, world instances this life has placed, and
    /// the placement that is currently open. A new Handle is made whenever a life starts, so none of it leaks
    /// across a recall, a redeploy or a slot change; a shotgun's several hits still share the one shot.</summary>
    internal sealed class Life
    {
        internal long Shots;
        internal long Placements;
        internal PlacedObject? Placed;
    }
    /// <summary>One deployed world object: a sentry or mine instance that currently exists in the world. Its
    /// identity is separate from the equipment life that placed it, because one life can place several in turn.
    /// The owner is re-read from the native object on every check, never cached from the spawn body. The kind is
    /// the spawning family's own declaration — `SentryGunInstance` or `MineDeployerInstance` — because the two
    /// shells are the same `Item` at this layer and the game keeps no member that spells which one an instance
    /// is.</summary>
    internal sealed class PlacedObject
    {
        internal PlacedObject(EntityReference entity, Item instance, Handle handle, string kind)
        { Entity = entity; Instance = instance; InstancePointer = instance.Pointer; Handle = handle; Kind = kind; }
        internal EntityReference Entity { get; }
        internal Item Instance { get; }
        internal IntPtr InstancePointer { get; }
        internal Handle Handle { get; }
        internal string Kind { get; }
        internal bool Recalled { get; set; }
        /// <summary>Why this life ended. A closed life is not resolvable any more, so a repeated ending is refused
        /// by the missing placement rather than by a flag; the reason is kept only for one diagnostic line.</summary>
        internal string? Ended { get; set; }
    }
    private readonly Dictionary<IntPtr, Handle> _byInstance = new();
    private readonly Dictionary<string, Handle> _byEntity = new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr, PlacedObject> _placements = new();
    private readonly Dictionary<string, PlacedObject> _placementsByEntity = new(StringComparer.Ordinal);
    /// <summary>The open placement of each equipment life, so a world instance this machine never mapped can still
    /// be joined to the life that owns it. One life places one device at a time — the slot the backpack marked
    /// deployed is one slot — so this is one entry per life and never a list to choose from.</summary>
    private readonly Dictionary<string, PlacedObject> _placedByLife = new(StringComparer.Ordinal);
    private readonly HashSet<IntPtr> _unresolvedReported = new();
    private readonly HashSet<string> _unmatchedReferences = new(StringComparer.Ordinal);
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report, _info;
    private EquipmentIdentitySession? _identity;
    private long _world = -1, _next, _deploySequence;

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
    internal int PlacementCount => _placements.Count;

    /// <summary>The recorded life currently mapped to a native item, if any. Not a resolver: callers
    /// still validate the returned reference through Runtime before acting on it.</summary>
    internal EntityReference? EntityOf(Item? instance)
    {
        if (instance == null || _identity == null) return null;
        SyncWorld();
        return _byInstance.TryGetValue(instance.Pointer, out var handle) ? handle.Entity : null;
    }

    /// <summary>Counts one committed shot against the equipment life of a firing weapon and returns that life's
    /// reference together with this shot's 1-based index inside it. The counter is the dedupe point: one call per
    /// `Fire` body, never one per hit, so a shotgun's several hits still read as the same shot.</summary>
    internal (EntityReference Equipment, long Index)? RecordShot(Item? weapon)
    {
        if (weapon == null || _identity == null || !Authoritative()) return null;
        SyncWorld();
        if (!_byInstance.TryGetValue(weapon.Pointer, out var handle)) return null;
        handle.Life.Shots++;
        return (handle.Entity, handle.Life.Shots);
    }

    /// <summary>The readback the attack-instance rows need and no other caller has: the equipment life a weapon
    /// belongs to and the player reference its owner resolves to. Null for a weapon this build has not recorded,
    /// or when the owner has no current reference in its own domain: the same gate a fact needs, because a fact
    /// without its actor is not a fact.</summary>
    public (EntityReference Source, EntityReference Equipment)? AttackTarget(Item? weapon)
    {
        if (weapon == null || _identity == null || !Authoritative()) return null;
        SyncWorld();
        if (!_byInstance.TryGetValue(weapon.Pointer, out var handle)) return null;
        var owner = OwnerOf(weapon);
        return owner == null ? null : (owner, handle.Entity);
    }

    /// <summary>Read-only snapshot of one recorded equipment life. This is the `gtfo.equipment` observer: it
    /// only ever answers for a life the index already holds, and it re-checks the native object first.</summary>
    internal RuntimeEntitySnapshot? ObserveEquipment(EntityReference reference)
    {
        if (_identity == null || !Authoritative() || reference == null || reference.WorldEpoch != _world
            || !_byEntity.TryGetValue(reference.Id, out var handle) || handle.Entity != reference) return null;
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return null;
        var item = backpack.Slots![handle.SlotIndex]!;
        var instance = item.Instance!;
        var point = Position(instance);
        if (point == null) return null;
        bool deployed = backpack.IsDeployed((InventorySlot)handle.SlotIndex);
        // A placement that is still open is named in the tags, so a behaviour can carry the deployed instance
        // across steps without inventing an address; the recalled marker survives its own recall.
        var placement = handle.Life.Placed;
        var tags = placement != null && !placement.Recalled
            ? new[] { "deployed", placement.Entity.Id }
            : new[] { deployed || (placement?.Recalled ?? false) ? "deployed" : "inventory" };
        return new RuntimeEntitySnapshot(reference, "equipment", handle.Owner.Id, "alive", tags,
            Array.Empty<string>(), point);
    }

    /// <summary>Whether one deployed reference still names an open placement, read from this provider's own
    /// placement table and from nothing else. Currency and observability are two questions: an observation has to
    /// carry a world point, while a placement whose object cannot report one is still a placement this provider
    /// holds — the deployment row's optional position says exactly that — so the kernel's currency check is
    /// answered here rather than by the snapshot.</summary>
    internal bool HoldsDeployable(EntityReference reference)
    {
        if (_identity == null || !Authoritative() || reference == null || reference.WorldEpoch != _world) return false;
        return _placementsByEntity.TryGetValue(reference.Id, out var placement) && placement.Entity == reference
            && placement.Instance.Pointer == placement.InstancePointer;
    }

    /// <summary>Read-only snapshot of one deployed world instance. Null for a destroyed, reappeared or
    /// never-recorded instance, so a stale reference is refused instead of answered from a replaced object.</summary>
    internal RuntimeEntitySnapshot? ObserveDeployable(EntityReference reference)
    {
        if (_identity == null || !Authoritative() || reference == null || reference.WorldEpoch != _world
            || !_placementsByEntity.TryGetValue(reference.Id, out var placement) || placement.Entity != reference
            || placement.Instance.Pointer != placement.InstancePointer) return null;
        var owner = OwnerOf(placement.Instance);
        if (owner == null) return null;
        var point = Position(placement.Instance);
        if (point == null) return null;
        var tags = placement.Recalled ? new[] { "recalled" } : new[] { "deployed", placement.Handle.ResourceId };
        return new RuntimeEntitySnapshot(reference, "deployable", owner.Id, "alive", tags,
            Array.Empty<string>(), point);
    }

    /// <summary>A deployed world object appeared. The native instance is matched to the backpack life that holds
    /// it, so a placement always reports the equipment it came from. One identity per world instance per world
    /// epoch; the same pointer reappearing after its previous instance ended is a new identity, never the old one.
    /// The deployment fact is published here and not from the backpack marker, because the mine family never sets
    /// that marker — this spawn is the only deploy signal both families share. <paramref name="kind"/> is the
    /// spawning family's own declaration and travels with the identity, because nothing on the instance itself
    /// says which shell it is.</summary>
    internal PlacedObject? TrackDeployed(Item? instance, string? kind)
    {
        if (instance == null || kind == null || _identity == null || !Authoritative()) return null;
        SyncWorld();
        return Placed(instance, OwnerOf(instance), kind);
    }

    /// <summary>The placement of a world instance a backpack readback already located. The caller just read the
    /// native object and holds its life, so this neither re-syncs the world nor re-resolves the owner: a sync in the
    /// middle of a readback would clear the handle being read. A re-placed instance is a new life, so it gets a new
    /// identity and the recalled one stays refused.</summary>
    internal PlacedObject? TrackPlaced(Item? instance, EntityReference owner, string? kind)
    {
        if (instance == null || kind == null || _identity == null || !Authoritative()) return null;
        return Placed(instance, owner, kind);
    }

    /// <summary>One identity per world instance, numbered inside the equipment life that placed it. The owner is
    /// passed in rather than resolved here, because a spawn can be observed before this build has filled the native
    /// owner in; the marker path supplies the backpack owner, which is the same player the object belongs to.</summary>
    private PlacedObject? Placed(Item instance, EntityReference? owner, string kind)
    {
        var handle = HandleOf(instance, owner);
        if (handle == null) return null;
        if (_placements.TryGetValue(instance.Pointer, out var existing))
        { handle.Life.Placed = existing; return existing; }
        var placement = new PlacedObject(NewDeployedInstance(handle), instance, handle, kind);
        _placements[instance.Pointer] = placement; _placementsByEntity[placement.Entity.Id] = placement;
        _placedByLife[handle.Entity.Id] = placement;
        handle.Life.Placed = placement;
        _info("weapon.deployable-life-started id=" + placement.Entity.Id + " equipment=" + handle.Entity.Id
            + " kind=" + kind + " resource=" + handle.ResourceId);
        // An ownerless placement stays tracked and endable, but no fact is published for it: the catalog source is
        // an entity reference, and a fact without its actor would be a lie about who deployed it.
        if (owner == null)
        {
            _report("weapon.deploy-owner-unresolved: placement " + placement.Entity.Id
                + " is tracked without a published deployment fact.");
            return placement;
        }
        // The position is read from the world instance this spawn created, never copied from the request that
        // asked for it. An unreadable point leaves the optional port out and still publishes the placement. The
        // kind is the spawning family's own member, and a family this build cannot name publishes no placement at
        // all: the row's port is the one thing that tells a sentry from a mine, and a guessed member would be
        // worse than a missing fact.
        var kindIndex = WeaponPlacementContract.KindIndex(placement.Kind);
        if (kindIndex == null)
        {
            _report("weapon.deploy-kind-unknown: placement " + placement.Entity.Id
                + " declares kind \"" + placement.Kind + "\", which is not a member of "
                + WeaponPlacementContract.EquipmentKindSchema + "; no placement fact was published.");
            return placement;
        }
        // Nothing is listening on the deployment row: the kernel would answer `no-consumer` for the event this
        // call is about to build, so the event value is never built. The placement itself stands either way.
        if (!Unsubscribed(ModuleDefinition.DeployCompletedBinding))
            Report("deploy", PublishEvent(new RuntimeEvent(
                "gtfo.equipment.deploy:" + Number(_world) + ":" + Number(checked(++_deploySequence)),
                ModuleDefinition.DeployCompletedBinding, _world, Math.Max(0, _kernel.CurrentTick),
                "gtfo.world:" + Number(_world),
                WeaponPlacementContract.DeployOutputs(owner, placement.Entity, kindIndex, Position(instance)))));
        return placement;
    }

    /// <summary>The equipment life a world instance belongs to. The instance's own recorded life is the answer
    /// whenever this machine has one. On a machine that did not run the placement — the host, for a device a
    /// client placed — the world instance arrives through the item replication path while the backpack marker the
    /// marker path reads back is the placer's own local state, so no recorded life is keyed by that exact
    /// instance; the life is then found by the owner the replicated instance itself reports, which is the
    /// same player the placement belongs to. Only the deployed sentry slot is ever markerless this way: the mine
    /// never writes the marker at all, so every mine falls back to the same lookup on every machine.</summary>
    private Handle? HandleOf(Item instance, EntityReference? owner)
    {
        if (_byInstance.TryGetValue(instance.Pointer, out var exact)) return exact;
        if (owner == null) return null;
        Handle? found = null;
        foreach (var candidate in _byInstance.Values)
        {
            if (candidate.Owner != owner || !IsDeployedSlot(candidate)) continue;
            if (found != null && found != candidate)
                _report("weapon.placement-owner-ambiguous: player " + owner.Id
                    + " has more than one slot marked deployed, so the world instance is joined to the first.");
            found ??= candidate;
        }
        if (found == null)
            _info("weapon.placement-unmatched id=" + instance.Pointer
                + " owner=" + owner.Id + " reason=no-recorded-life");
        return found;
    }

    /// <summary>The kind a backpack item's own slot deploys. The first-person tool a placement slot holds names the
    /// family that places it, and this build's sentry tool and mine tool are two classes with one placement body
    /// each. A slot holding the deployed world object itself is the same answer — this build's `SentryGunInstance`
    /// and `MineDeployerInstance` are two more classes with one placement body each — and the two forms are read in
    /// that order because the instance classes are the object the spawn path tracks, while it is the tool that is
    /// still in the backpack at the moment the marker is written. A slot whose item is neither is not a placement
    /// this package can name, and the caller mints nothing for it. Every read is the interop cast, which refuses a
    /// destroyed object instead of throwing, so a slot cleared between the marker write and this read names none.</summary>
    private static string? PlacementKind(Item? instance)
    {
        if (instance == null) return null;
        try
        {
            if (instance.TryCast<SentryGunFirstPerson>() != null) return WeaponPlacementContract.SentryGunKind;
            if (instance.TryCast<MineDeployerFirstPerson>() != null) return WeaponPlacementContract.MineKind;
            if (instance.TryCast<SentryGunInstance>() != null) return WeaponPlacementContract.SentryGunKind;
            if (instance.TryCast<MineDeployerInstance>() != null) return WeaponPlacementContract.MineKind;
        }
        catch (Exception) { /* a destroyed native object is not a placement this build can name. */ }
        return null;
    }

    /// <summary>Whether one recorded life's own backpack slot currently reads as deployed. Read through the same
    /// backpack lookup the rest of this class uses, and false for a slot the table no longer holds, so a retired
    /// life is never joined to a new world instance.</summary>
    private static bool IsDeployedSlot(Handle handle)
    {
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return false;
        return SafeDeployed(backpack, handle.SlotIndex);
    }

    /// <summary>The deployed marker of one slot, refusing rather than throwing when the game's own table is gone.</summary>
    private static bool SafeDeployed(PlayerBackpack backpack, int slot)
    {
        try { return backpack.IsDeployed((InventorySlot)slot); }
        catch (Exception) { return false; }
    }

    /// <summary>The slot marker went back to "in the backpack", which in this build is the recall the player asked
    /// for. The world object stays resolvable (tagged recalled) until its own removal arrives and ends it, and a
    /// marker that flips with no open placement publishes nothing: the game reports a marker, not a recall.</summary>
    private void PublishRecall(Handle handle, EntityReference owner)
    {
        var placed = handle.Life.Placed;
        if (placed == null) return;
        placed.Recalled = true;
        if (Unsubscribed(ModuleDefinition.RecallCompletedBinding)) return;
        Report("recall", PublishEvent(new RuntimeEvent(
            "gtfo.equipment.recall:" + Number(_world) + ":" + Number(checked(++_deploySequence)),
            ModuleDefinition.RecallCompletedBinding, _world, Math.Max(0, _kernel.CurrentTick),
            "gtfo.world:" + Number(_world),
            WeaponPlacementContract.RecallOutputs(owner, placed.Entity,
                WeaponPlacementContract.KindIndex(placed.Kind)))));
    }

    /// <summary>One deployed world object is gone. The first ending wins; a second path (OnDespawn then OnDestroy,
    /// or OnDestroy after a recall) never publishes again, because closing removes the placement that both
    /// endings look up. The ending body is the same on both families and needs no kind: the placement it closes
    /// already carries the one its spawn declared.</summary>
    internal void EndDeployed(Item? instance, string reason, bool recalled)
    {
        if (instance == null || !_placements.TryGetValue(instance.Pointer, out var placement)
            || placement.Instance.Pointer != placement.InstancePointer) return;
        End(placement, _world, reason, recalled);
    }

    /// <summary>Ends one placement and publishes its facts. This is the only ending path, so a level change that
    /// tears the world down, a pickup and a destroy all report exactly once and all report the same way.</summary>
    private void End(PlacedObject placement, long world, string reason, bool recalled)
    {
        placement.Recalled = recalled;
        placement.Ended = reason;
        // A recall publishes its own fact first: the pickup is what the player did, the removal is what the world
        // did about it, and a plan reading both should see them in that order.
        if (recalled) PublishRecall(placement.Handle, OwnerOf(placement.Instance) ?? placement.Handle.Owner);
        // Nothing is listening on the despawn row: the event value is never built, and the ending still closes.
        if (!Unsubscribed(ModuleDefinition.DespawnedBinding))
        {
            var despawn = PublishEvent(new RuntimeEvent(
                "gtfo.equipment.despawn:" + Number(world) + ":" + Number(checked(++_deploySequence)),
                ModuleDefinition.DespawnedBinding, world, Math.Max(0, _kernel.CurrentTick),
                "gtfo.world:" + Number(world),
                RuntimeJson.From(new { entity = placement.Entity, reason })));
            Report("despawn(" + reason + ")", despawn);
        }
        ReleaseObjectScope(placement);
        Close(placement);
    }

    /// <summary>
    /// Drops the `object`-scoped variables of one retired device. A device's object scope lives as long as the
    /// object stands in the world, and the kernel cannot tell a recalled device from a live one — the half that
    /// observed the ending is the half that says so, which is what ruling 117.6 asks for. Only the world the
    /// placement belongs to is answered: a world change clears the whole table itself, and a reference from a world
    /// the kernel has already left would be refused as stale rather than released.
    /// </summary>
    private void ReleaseObjectScope(PlacedObject placement)
    {
        if (placement.Entity.WorldEpoch != _kernel.WorldEpoch) return;
        try
        {
            var released = _kernel.ReleaseVariableScope(VariableScopeKinds.Object, placement.Entity);
            if (released != 0)
                _info("weapon.object-scope-released id=" + placement.Entity.Id + " count=" + Number(released));
        }
        catch (Exception error)
        {
            _report("weapon.object-scope-release-failed id=" + placement.Entity.Id + " reason=" + error.GetType().Name);
        }
    }

    private void Report(string kind, DispatchResult result)
    {
        if (result.Status == "rejected") _report("weapon." + kind + "-fact-rejected: " + result.Code);
        else _info("weapon." + kind + "-fact status=" + result.Status + " code=" + result.Code + " id=" + result.EventId);
    }

    /// <summary>Publishes one combat fact under this provider's own registration, the same path the equipment
    /// facts use; Runtime owns ordering, dedupe and causality.</summary>
    public DispatchResult Publish(RuntimeEvent value) => PublishEvent(value);

    /// <summary>Whether no loaded plan is mounted on one of this provider's own bindings, read before an event
    /// value is built so a fact nobody subscribes to is never constructed. A session that is not attached yet has
    /// no gates of its own, and the kernel still decides every publish.</summary>
    public bool Unsubscribed(string binding) => _identity?.Unsubscribed(binding) ?? false;

    /// <summary>The one publication path, including for the facts the adapter builds itself. `Published` is the
    /// adapter's own record of what it handed to Runtime, so a caller can tell published facts from suppressed
    /// ones without reading a log line; it is not an event ledger and Runtime remains the only source of ordering.</summary>
    internal DispatchResult PublishEvent(RuntimeEvent value)
    {
        Published = value;
        Observed?.Invoke(value);
        return _identity!.Publish(value);
    }

    /// <summary>Called with every fact this adapter hands to Runtime, in publication order, before Runtime sees
    /// it. The packages register nothing here: it is the seam a focused test project uses to read the facts an
    /// observation produced without a log line, and it carries no state of the adapter's own.</summary>
    internal Action<RuntimeEvent>? Observed { get; set; }

    /// <summary>The last fact this adapter published. Replaced, never appended.</summary>
    internal RuntimeEvent? Published { get; private set; }

    private void Close(PlacedObject placement)
    {
        _placements.Remove(placement.InstancePointer);
        _placementsByEntity.Remove(placement.Entity.Id);
        if (_placedByLife.TryGetValue(placement.Handle.Entity.Id, out var open) && ReferenceEquals(open, placement))
            _placedByLife.Remove(placement.Handle.Entity.Id);
        if (placement.Handle.Life.Placed == placement) placement.Handle.Life.Placed = null;
        _info("weapon.deployable-life-ended id=" + placement.Entity.Id + " kind=" + placement.Kind
            + " reason=" + placement.Ended);
    }

    /// <summary>The recorded placement of a native world instance, if it is still open.</summary>
    internal PlacedObject? PlacementOf(Item? instance)
        => instance != null && _placements.TryGetValue(instance.Pointer, out var placement)
            && placement.Instance.Pointer == placement.InstancePointer ? placement : null;

    /// <summary>The open placement one equipment life currently holds, or null. This is the answer for a world
    /// instance that arrived without a recorded life of its own: the life is the one its owner's deployed slot
    /// names, and the placement is what that life's own marker path or spawn minted.</summary>
    internal PlacedObject? PlacementOf(EntityReference life)
        => life != null && _placedByLife.TryGetValue(life.Id, out var placement)
            && placement.Instance.Pointer == placement.InstancePointer ? placement : null;

    /// <summary>The recorded placement of a native world instance held as a bare pointer, or null. The device
    /// facts read their subject that way — a firing component and a trigger component reach the device through
    /// their own core, which is the same native object this table keyed the placement on — and a pointer whose
    /// placement already ended answers null rather than the object it used to be.</summary>
    internal PlacedObject? PlacementOf(IntPtr instancePointer)
        => _placements.TryGetValue(instancePointer, out var placement)
            && placement.Instance.Pointer == placement.InstancePointer ? placement : null;

    /// <summary>The native item of an open placement, or null. The owner tier's session resolver asks this because
    /// the reference it is handed names a world instance, and that instance's own owner chain is the game's answer
    /// to who holds it; anything that is not an open placement answers null.</summary>
    internal Item? PlacedItemOf(EntityReference reference)
        => reference != null && _placementsByEntity.TryGetValue(reference.Id, out var placement)
            && placement.Instance.Pointer == placement.InstancePointer ? placement.Instance : null;

    internal void Clear()
    {
        _byInstance.Clear(); _byEntity.Clear(); _placements.Clear(); _placementsByEntity.Clear();
        _placedByLife.Clear();
        _unresolvedReported.Clear(); _unmatchedReferences.Clear();
    }

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
            // The deployed marker belongs to the slot: a placed sentry or mine stays this slot's equipment life
            // while the game reports the slot as deployed, and a recall clears the same marker.
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
            // The rebuilt handle shares the life state, so the shot count and the open placement travel with it.
            _byInstance[instancePointer] = handle; _byEntity[handle.Entity.Id] = handle;
            // The marker and the world instance both say "placed". Tracking is idempotent, so whichever of the two
            // arrives first mints the identity and the other joins it, and a marker with no world object yet is
            // simply not a deployment. The mine family clears this marker on its own pickup path like the sentry.
            if (deployed) TrackPlaced(item.Instance, owner, PlacementKind(item.Instance));
            else if (locationChanged) PublishRecall(handle, owner);
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

    /// <summary>The `gear-block` mount: true only when the subject is one of this provider's recorded equipment
    /// lives and the text the game itself put on that gear is the reference text letter for letter. Nothing is
    /// parsed or normalized here — a second spelling of one block id is a different string and matches nothing.
    /// The native record is re-read on every call rather than cached on the handle, so a slot whose item changed
    /// no longer matches, and a gear the game did not build from an offline block reads no record at all. A
    /// deployed world instance is not in the life table, so it is never a mount target.</summary>
    internal bool MatchesGearBlock(string? reference, EntityReference subject)
    {
        // A missing reference is no text at all, so there is nothing to compare and nothing to report.
        if (reference == null || subject == null || _identity == null) return false;
        // A reference the catalog does not spell as PlainDecimalId is refused before anything native is read, and
        // the refusal is reported once per distinct text: the same plan re-asks on every event it could belong to.
        if (!IsPlainDecimalId(reference))
        {
            if (_unmatchedReferences.Add(reference))
                _report("weapon.gear-block-reference-invalid: the mount reference \"" + reference
                    + "\" is not the block id's plain decimal text, so this mount can never match.");
            return false;
        }
        if (subject.WorldEpoch != _world || _kernel.WorldEpoch != _world
            || !_byEntity.TryGetValue(subject.Id, out var handle) || handle.Entity != subject) return false;
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return false;
        return string.Equals(GearBlock(backpack.Slots![handle.SlotIndex]!), reference, StringComparison.Ordinal);
    }

    /// <summary>The offline gear block text the game itself put on a gear, read from the instance's own record:
    /// `GearManager.LoadOfflineGearDatas` spells it `OfflineGear_ID_<persistentID>`, and the block id is that
    /// suffix. Null for a gear with no such record — every gear that did not come from a
    /// `PlayerOfflineGearDataBlock` — and for a record whose suffix is not a plain decimal id, which is a
    /// spelling this build has no evidence for.</summary>
    internal static string? GearBlock(BackpackItem? item)
        => GearBlockId(item == null ? null : item.GearIDRange);

    /// <summary>The one block-id parser: the game's own offline record text, or null. The `gear-block` mount and
    /// the gear-part pose loader both ask this, so a block id can never have a second spelling in this package.</summary>
    internal static string? GearBlockId(GearIDRange? gear)
    {
        var record = gear == null ? null : gear.PlayfabItemInstanceId;
        if (record == null || !record.StartsWith(GearBlockPrefix, StringComparison.Ordinal)) return null;
        var block = record.Substring(GearBlockPrefix.Length);
        return IsPlainDecimalId(block) ? block : null;
    }

    /// <summary>Plain decimal id text: digits that parse to a `uint` and are that value's own text again, which
    /// is what the website writes with `String(blockId)`. Empty text, a sign, surrounding space, a leading zero,
    /// a radix prefix, a decimal point and anything above `UInt32.MaxValue` are other spellings and are refused.
    /// `NumberStyles.None` already refuses a sign and surrounding space; the round trip refuses a leading zero.</summary>
    private static bool IsPlainDecimalId(string? text)
        => text != null && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && text == value.ToString(CultureInfo.InvariantCulture);

    private EntityReference NewEntity()
        => new(EquipmentIdentityId.Life(_world, checked(++_next)), _world, 1);

    private EntityReference NewDeployedInstance(Handle handle)
        => new(EquipmentIdentityId.DeployedInstance(handle.Entity.Id, checked(++handle.Life.Placements)), _world, 1);

    public bool Authoritative()
    {
        if (_identity == null || !_canExecute() || !SNet.IsMaster) return false;
        var state = _kernel.Lifecycle;
        return state.StartupState == RuntimeStartupState.Ready && state.IsHost == true;
    }

    /// <summary>The player reference of a deployed object, re-derived from the native owner every time. Null when
    /// that player has no current reference in its own domain, which is what stops ownerless facts being published.</summary>
    internal EntityReference? OwnerOf(Item? instance)
    {
        var agent = instance == null ? null : instance.Owner;
        var player = agent == null ? null : agent.Owner;
        return player == null ? null : _kernel.ResolveEntityInstance(PlayerKind, player);
    }

    /// <summary>The same lookup for a hit, whose shooter arrives as the agent itself rather than as an item.</summary>
    internal EntityReference? OwnerOf(PlayerAgent? agent)
    {
        var player = agent == null ? null : agent.Owner;
        return player == null ? null : _kernel.ResolveEntityInstance(PlayerKind, player);
    }

    /// <summary>The same lookup for a backpack, whose owning player arrives as the reference itself rather than
    /// through an item or an agent.</summary>
    internal EntityReference? OwnerOf(SNet_Player? player)
        => player == null ? null : _kernel.ResolveEntityInstance(PlayerKind, player);

    /// <summary>The native player one `gtfo.player` reference names, or null when this machine cannot name one for
    /// it — the direction the owner lookups above do not answer. The candidate set is the game's own in-level agent
    /// list, and every candidate is confirmed through the owning domain's own instance lookup, so the answer is the
    /// player ForgeMap minted that reference for and never one derived here from a slot, a name or the account key.
    /// A player this machine holds no agent for stays unnamed, which is what makes a command naming it a refusal
    /// instead of a write against a guessed object.</summary>
    internal SNet_Player? PlayerOf(EntityReference reference)
    {
        if (reference == null) return null;
        var agents = PlayerManager.PlayerAgentsInLevel;
        if (agents == null) return null;
        for (int index = 0; index < agents.Count; index++)
        {
            var player = agents[index] == null ? null : agents[index].Owner;
            if (player == null) continue;
            if (_kernel.ResolveEntityInstance(PlayerKind, player) == reference) return player;
        }
        return null;
    }

    /// <summary>The live weapon one equipment life currently points at, or null. The answer is the item that life's
    /// own slot holds right now, re-checked against the handle, so a replaced slot or an ended life answers null
    /// like every other read of a stale reference instead of handing back an object the life no longer owns. The
    /// instance-override rows write through this lookup; a life whose slot holds no weapon is unanswered here and
    /// the row refuses it by name.</summary>
    internal BulletWeapon? WeaponOf(EntityReference reference)
    {
        if (reference == null || !Authoritative() || reference.WorldEpoch != _world
            || !_byEntity.TryGetValue(reference.Id, out var handle) || handle.Entity != reference) return null;
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return null;
        var item = backpack.Slots![handle.SlotIndex];
        return item?.Instance?.TryCast<BulletWeapon>();
    }

    /// <summary>The live equippable one equipment life currently points at, with the backpack and slot it sits in,
    /// or null. This is the same current-check `WeaponOf` makes — the reference's world, this machine's recorded
    /// handle for it, and the backpack's own slot table — with two differences: no authority gate, because a
    /// read-only row may answer on a machine that must not write, and no weapon cast, because a magazine read has
    /// to see the item before it can say whether that item is one. The backpack and the slot travel back with the
    /// item because the pool a magazine draws from belongs to the slot, and looking the slot up again would be a
    /// second read of the same table with a second chance to disagree.</summary>
    internal (ItemEquippable Item, PlayerBackpack Backpack, int SlotIndex)? EquippableOf(EntityReference reference)
    {
        if (reference == null || reference.WorldEpoch != _world
            || !_byEntity.TryGetValue(reference.Id, out var handle) || handle.Entity != reference) return null;
        var backpack = handle.Backpack;
        if (backpack.Pointer != handle.BackpackPointer || !Matches(handle, backpack)) return null;
        var slots = backpack.Slots;
        if (slots == null || handle.SlotIndex < 0 || handle.SlotIndex >= slots.Length) return null;
        var item = slots[handle.SlotIndex]?.Instance?.TryCast<ItemEquippable>();
        return item == null ? null : (item, backpack, handle.SlotIndex);
    }

    /// <summary>The Forge reference of the object a shot hit, chosen by that object's own native type: the damage
    /// limb the bullet was resolved against names its base agent, and the agent's own type decides whether the
    /// enemy domain or the player domain is asked. Each kind is asked with the instance its own domain records —
    /// ForgeEnemy keys its registry by the agent, ForgeMap keys its player registry by the SNet_Player the agent
    /// belongs to, and ForgeMap's map objects are asked with the hit object itself, because a door or a terminal
    /// is reached through the component the bullet was resolved against and only that domain can climb from one
    /// to the other — so no reference is ever derived from the shooter or from allegiance. A hit on world
    /// geometry, a deployable or anything else this build cannot name answers null, which is what leaves the port
    /// out of the fact instead of publishing a null reference.</summary>
    internal EntityReference? HitTarget(Weapon.WeaponHitData? data)
    {
        var collider = data == null ? null : data.rayHit.collider;
        if (collider == null) return null;
        if (collider.GetComponentInParent<Dam_EnemyDamageLimb>()?.GetBaseAgent()?.TryCast<EnemyAgent>() is { } enemy)
            return _kernel.ResolveEntityInstance(EnemyKind, enemy);
        if (collider.GetComponentInParent<Dam_PlayerDamageLimb>()?.GetBaseAgent()?.TryCast<PlayerAgent>() is { } player)
            return OwnerOf(player);
        return _kernel.ResolveEntityInstance(MapObjectKind, collider);
    }

    // A destroyed Unity object reads as null through its own overloaded equality, and its transform then throws;
    // a non-finite coordinate is refused rather than published as a position. The one reader answers both the
    // observer snapshots and the deployment fact, so a position can never have a second spelling in this package.
    private static double[]? Position(Item instance)
    {
        var transform = instance == null ? null : instance.transform;
        if (transform == null) return null;
        var position = transform.position;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return null;
        return new[] { (double)position.x, (double)position.y, (double)position.z };
    }

    internal void SyncWorld()
    {
        long world = _kernel.WorldEpoch;
        // The index clears itself on world, authority and stop transitions; that invalidates every handle.
        // Instance IDs are a per-world sequence; within one world the sequence never rewinds, because retired
        // lives of that world still block reuse of their IDs after an authority-loss clear.
        // The index's own epoch is read as well as this adapter's, because the index clears on authority and stop
        // transitions too, and a placement from the world the index no longer holds must not outlive it either.
        if (world != _world || _identity!.WorldEpoch != world)
        { ClearObserved("world-changed"); _world = world; _next = 0; }
        else if (_byInstance.Count != 0 && _identity.Count == 0) ClearObserved("identity-cleared");
    }

    private void ClearObserved(string reason)
    {
        int count = _byInstance.Count;
        // Placements are dropped with the world they were placed in, without a despawn fact: the world transition
        // is a hard boundary, so their old-world epoch can no longer be published and re-stamping them as
        // new-world facts would attribute an ending to a world they never existed in. This is the one ending a
        // level change cannot report, and it is logged instead.
        int placed = _placements.Count;
        _placements.Clear(); _placementsByEntity.Clear();
        Clear();
        if (count != 0) _info("weapon.equipment-lives-cleared world=" + Number(_world) + " count=" + Number(count) + " reason=" + reason);
        if (placed != 0) _info("weapon.deployments-closed world=" + Number(_world) + " count=" + Number(placed) + " reason=" + reason);
    }

    private void Retire(Handle handle, string? reason)
    {
        // The world instances this life placed go with it: a placement outliving its equipment life would be an
        // address nothing could ever end, and its despawn fact would never be published.
        foreach (var placed in _placements.Values.Where(p => p.Handle == handle).ToArray())
            End(placed, _world, "equipment-ended", recalled: false);
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

    /// <summary>The agent behind a player reference on this machine, or null when that player holds none here.
    /// Every read that starts from a player passes through this one cast, so a reference this machine cannot
    /// resolve answers null rather than a guessed agent, inventory or owner.</summary>
    internal static PlayerAgent? Agent(SNet_Player? player)
        => player != null && player.HasPlayerAgent ? player.PlayerAgent?.TryCast<PlayerAgent>() : null;

    private static PlayerInventoryBase? Inventory(SNet_Player? player) => Agent(player)?.Inventory;

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
