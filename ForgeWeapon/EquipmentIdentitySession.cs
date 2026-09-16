using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;
using static ForgeWeapon.EquipmentIdentityIndex;

namespace ForgeWeapon;

/// <summary>Explicit managed W1 registration, not an auto-loaded GTFO plugin.
/// Trusted adapters must provide current native/owner probes and observed references.
/// No attack, inventory write, timer, network protocol or resource reservation is installed.</summary>
public sealed class EquipmentIdentitySession : IDisposable
{
    private readonly RuntimeKernel kernel;
    private readonly EquipmentIdentityIndex index;
    private readonly Func<EquipmentObservation, bool> nativeIsCurrent;
    private readonly Func<EntityReference, bool> ownerIsCurrent;
    private readonly Func<EntityReference, RuntimeEntitySnapshot?>? deployedObserver;
    /// <summary>Whether one deployed reference is still a placement the owning adapter holds. It is a separate
    /// answer from <see cref="deployedObserver"/> because a snapshot has to carry a world point and a placement
    /// does not.</summary>
    private readonly Func<EntityReference, bool>? deployedCurrent;
    private readonly Action<RuntimeLifecycleEvent>? observe;
    private readonly RuntimeModuleHandle registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> gates;
    private readonly RuntimeLifecycleSubscription lifecycle;
    private readonly object ticketOwner = new();
    private bool disposed, probing;
    public EquipmentIdentitySession(RuntimeKernel kernel, RuntimeLogLevel logLevel, Func<EquipmentObservation, bool> nativeIsCurrent,
        Func<EntityReference, bool> ownerIsCurrent, int maxActive = 1024, int maxHistory = 8192,
        Func<EntityReference, RuntimeEntitySnapshot?>? equipmentObserver = null,
        Func<EntityReference, RuntimeEntitySnapshot?>? deployedObserver = null,
        Func<EntityReference, bool>? deployedCurrent = null,
        Func<string, EntityReference, bool>? gearBlockMatcher = null,
        RuntimeModule? module = null,
        Action<RuntimeLifecycleEvent>? observe = null)
    {
        this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        this.nativeIsCurrent = nativeIsCurrent ?? throw new ArgumentNullException(nameof(nativeIsCurrent));
        this.ownerIsCurrent = ownerIsCurrent ?? throw new ArgumentNullException(nameof(ownerIsCurrent));
        this.deployedObserver = deployedObserver;
        this.deployedCurrent = deployedCurrent;
        this.observe = observe;
        index = new EquipmentIdentityIndex(maxActive, maxHistory);
        // `module` is the weapon provider declaration the native half built, already carrying the bodies this
        // machine supplies; a session that was handed none registers the observation surface alone. This session
        // adds the equipment identity tables to that one declaration and owns the registration.
        registration = kernel.RegisterModule((module ?? ModuleDefinition.Create()) with
        {
            // Both shapes belong to this provider and share the one registry entry ruling 31 gives it: no second
            // world registry, and no second namespace for the world instances one equipment life places.
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
            {
                [EquipmentIdentityId.Namespace] = IsCurrent
            },
            EntityObservers = Observers(equipmentObserver, deployedObserver),
            AttachmentMatchers = Matchers(gearBlockMatcher)
        }, logLevel);
        try
        {
            lifecycle = registration.ObserveLifecycle(ObserveLifecycle);
            Check(lifecycle.IsActive, "equipment.lifecycle-unavailable");
        }
        catch { registration.Dispose(); throw; }
        // The subscription gates of this provider's own bindings, taken here because this is the one moment the
        // registration exists and its bindings are already facts of the registry.
        gates = registration.SubscriptionGates();
    }
    /// <summary>Whether no loaded plan is mounted on one of this provider's own bindings. The kernel precomputes
    /// the answer and refreshes it where the subscription table changes, so a native callback with nothing to say
    /// to anyone skips the event value instead of building one the kernel would refuse as `no-consumer`. A binding
    /// this registration does not declare is not this provider's to skip, and the kernel still decides it.</summary>
    public bool Unsubscribed(string binding) => gates.TryGetValue(binding, out var gate) && !gate.HasSubscribers;
    /// <summary>One subscription carries the whole weapon session's level lifecycle: the identity index is what
    /// this class watches for itself, and the session's other halves that hold something a world boundary
    /// invalidates are handed the same event through <c>observe</c>, so one world change reaches every table that
    /// has to give something up rather than a second subscription per table.</summary>
    private void ObserveLifecycle(RuntimeLifecycleEvent value)
    {
        if (value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged)
            index.BeginWorld(value.Current.WorldEpoch);
        if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped
            || value.Current.IsHost == false) index.Clear();
        observe?.Invoke(value);
    }
    /// <summary>One namespace, one observer: which table answers a reference is decided by the id shape the
    /// resolver uses too, so a query can never read an equipment life out of the placement table or the reverse.</summary>
    private static IReadOnlyDictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>? Observers(
        Func<EntityReference, RuntimeEntitySnapshot?>? equipment, Func<EntityReference, RuntimeEntitySnapshot?>? deployed)
    {
        if (equipment == null && deployed == null) return null;
        return new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>(StringComparer.Ordinal)
        {
            [EquipmentIdentityId.Namespace] = reference
                => EquipmentIdentityId.ShapeOf(reference?.Id) == EquipmentEntityShape.DeployedInstance
                    ? deployed?.Invoke(reference!) : equipment?.Invoke(reference!)
        };
    }
    /// <summary>The mount kinds this provider owns. A gear-block reference is read against the native instance
    /// the recorded life currently points at, so the matcher is the adapter's own readback rather than a table
    /// kept here: the session owns the reference, the adapter owns what the game says about it.</summary>
    private static IReadOnlyDictionary<string, AttachmentMatcherRegistration>? Matchers(Func<string, EntityReference, bool>? gearBlock)
        => gearBlock == null ? null : new Dictionary<string, AttachmentMatcherRegistration>(StringComparer.Ordinal)
        {
            // A gear block names a whole block, so there is no category to carry; the framework refuses one. The
            // kind is matched against the event's own subject: the equipment instance the gear block was read from.
            [ModuleDefinition.GearBlockAttachmentKind] =
                AttachmentMatcherRegistration.BySubject((_, reference, subject) => gearBlock(reference, subject))
        };
    /// <summary>Deployed instances are not in the W1 index, so this answers from the owning adapter's own placement
    /// table through the currency predicate it supplies; an already-ended placement is not current, which is what
    /// refuses a stale identity. The snapshot observer is not asked here: a placement whose object cannot report a
    /// world point is unobservable, not ended.</summary>
    private bool IsDeployedInstanceCurrent(EntityReference reference)
        => deployedCurrent != null && reference != null && deployedCurrent(reference);

    public int Count { get { _ = kernel.Lifecycle; return index.Count; } }
    /// <summary>The world the recorded lives belong to. It only moves when the index observes a world change, so a
    /// caller comparing it with the kernel's epoch can tell that everything it held belongs to a gone world.</summary>
    public long WorldEpoch => index.WorldEpoch;
    public int IdentityHistoryCount { get { _ = kernel.Lifecycle; return index.HistoryCount; } }
    private RuntimeLifecycleSnapshot Ready()
    {
        var state = kernel.Lifecycle; // Also enforces the owning simulation thread.
        Check(!disposed && registration.IsRegistered && lifecycle.IsActive, "equipment.session-detached");
        Check(!probing, "equipment.probe-reentrancy");
        Check(state.StartupState == RuntimeStartupState.Ready && state.IsHost == true,
            "equipment.not-authoritative-ready");
        Check(state.WorldEpoch == index.WorldEpoch, "equipment.stale-world");
        return state;
    }
    private void Probe(EquipmentObservation value)
    {
        probing = true;
        try
        {
            Check(nativeIsCurrent(value), "equipment.native-not-current");
            if (value.Owner != null) Check(ownerIsCurrent(value.Owner), "equipment.owner-not-current");
        }
        catch (RuntimeContractException) { throw; }
        catch (Exception error)
        { throw new RuntimeContractException("equipment.probe-failed", error.GetType().Name); }
        finally { probing = false; }
    }
    /// <summary>Records one verified observation. Returns the wield fact dispatch only when this observation
    /// is a directly observed wield flip of the same life, owner and inventory slot; otherwise null.
    /// First sightings, owner or slot changes and location changes never synthesize wield history.</summary>
    public DispatchResult? Record(EquipmentObservation value)
    {
        var before = Ready(); index.Validate(value); Probe(value);
        Check(Ready().WorldEpoch == before.WorldEpoch, "equipment.stale-world");
        var previous = index.Get(value.Entity)?.Value;
        var entry = index.Record(value);
        if (previous == null || previous.IsWielded == value.IsWielded || previous.Owner != value.Owner
            || previous.Slot != value.Slot || previous.Location != EquipmentLocation.Inventory
            || value.Location != EquipmentLocation.Inventory) return null;
        var world = before.WorldEpoch;
        var kind = value.IsWielded ? "equipped" : "unequipped";
        string binding = value.IsWielded ? ModuleDefinition.EquippedBinding : ModuleDefinition.UnequippedBinding;
        // Nothing is listening on the wield row: the kernel would answer `no-consumer` for the event this call is
        // about to build, so the event value is never built. The flip above is still recorded, because a wield
        // observed while nobody listened is one this session has already recorded.
        if (Unsubscribed(binding)) return null;
        return registration.Publish(new RuntimeEvent(
            "gtfo.equipment." + kind + ":" + world + ":" + value.Entity.Id + ":" + entry.Revision,
            binding, world, Math.Max(0, kernel.CurrentTick), "gtfo.world:" + world,
            RuntimeJson.From(new { actor = value.Owner, equipment = value.Entity })));
    }
    public bool Remove(EntityReference reference)
    {
        _ = kernel.Lifecycle; Check(!probing, "equipment.probe-reentrancy"); Reference(reference);
        return !disposed && registration.IsRegistered && index.Remove(reference);
    }
    /// <summary>Publishes one equipment, deployable or combat fact under this provider's own registration. Facts
    /// travel through the kernel like the wield facts do; this provider never queues or duplicates an event.</summary>
    public DispatchResult Publish(RuntimeEvent value)
    {
        Ready(); ArgumentNullException.ThrowIfNull(value);
        Check(value.WorldEpoch == index.WorldEpoch, "equipment.stale-world");
        return registration.Publish(value);
    }
    private Entry Verified(EntityReference reference)
    {
        var before = Ready(); Reference(reference);
        var entry = index.Get(reference);
        Check(entry != null, "equipment.stale-instance");
        Probe(entry!.Value);
        Check(Ready().WorldEpoch == before.WorldEpoch && ReferenceEquals(entry, index.Get(reference)),
            "equipment.observation-changed");
        return entry;
    }
    public bool TryResolve(EntityReference reference, out EquipmentObservation? observation, out string code)
    {
        _ = kernel.Lifecycle; observation = null; code = "equipment.current";
        if (reference == null) { code = "equipment.invalid-reference"; return false; }
        try { observation = Verified(reference).Value; return true; }
        catch (RuntimeContractException error) { code = error.Code; return false; }
    }
    /// <summary>Every reference this provider answers for, routed by the id shape it issued: a deployed world
    /// instance lives in the adapter's placement table, a backpack life in the W1 index, and neither table ever
    /// answers for the other shape. Both routes end in this one resolver of the one `gtfo.equipment` namespace.</summary>
    public bool IsCurrent(EntityReference reference)
    {
        if (reference == null) return false;
        return EquipmentIdentityId.ShapeOf(reference.Id) switch
        {
            EquipmentEntityShape.DeployedInstance => IsDeployedInstanceCurrent(reference),
            EquipmentEntityShape.Life => TryResolve(reference, out _, out _),
            _ => false
        };
    }
    public EquipmentUseTicket CaptureOwnedUse(EntityReference reference, EntityReference expectedOwner,
        bool requireWielded)
    {
        Reference(expectedOwner); var entry = Verified(reference);
        Check(entry.Value.Owner == expectedOwner, "equipment.owner-mismatch");
        RequireUsable(entry.Value, requireWielded);
        return new EquipmentUseTicket(ticketOwner, entry.Revision, entry.Value, requireWielded);
    }
    public EquipmentObservation RequireCurrent(EquipmentUseTicket ticket)
    {
        _ = kernel.Lifecycle; ArgumentNullException.ThrowIfNull(ticket);
        Check(ReferenceEquals(ticket.Session, ticketOwner), "equipment.foreign-ticket");
        var entry = Verified(ticket.Observation.Entity);
        Check(entry.Revision == ticket.Revision, "equipment.observation-changed");
        RequireUsable(entry.Value, ticket.RequireWielded); return entry.Value;
    }
    private static void RequireUsable(EquipmentObservation value, bool requireWielded)
    {
        Check(value.Owner != null, "equipment.owner-required");
        Check(value.IsReady, "equipment.not-loaded");
        Check(!requireWielded || value.IsWielded, "equipment.not-wielded");
    }
    public void Dispose()
    {
        _ = kernel.Lifecycle; Check(!probing, "equipment.probe-reentrancy");
        if (disposed) return;
        registration.Dispose(); // If dispatch rejects disposal, keep the live session intact.
        lifecycle.Dispose(); index.Clear(); disposed = true;
    }
}
