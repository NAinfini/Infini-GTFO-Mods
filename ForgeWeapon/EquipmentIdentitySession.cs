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
    public const string EntityNamespace = "gtfo.equipment";
    private readonly RuntimeKernel kernel;
    private readonly EquipmentIdentityIndex index;
    private readonly Func<EquipmentObservation, bool> nativeIsCurrent;
    private readonly Func<EntityReference, bool> ownerIsCurrent;
    private readonly RuntimeModuleHandle registration;
    private readonly RuntimeLifecycleSubscription lifecycle;
    private readonly object ticketOwner = new();
    private bool disposed, probing;
    public EquipmentIdentitySession(RuntimeKernel kernel, Func<EquipmentObservation, bool> nativeIsCurrent,
        Func<EntityReference, bool> ownerIsCurrent, int maxActive = 1024, int maxHistory = 8192)
    {
        this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        this.nativeIsCurrent = nativeIsCurrent ?? throw new ArgumentNullException(nameof(nativeIsCurrent));
        this.ownerIsCurrent = ownerIsCurrent ?? throw new ArgumentNullException(nameof(ownerIsCurrent));
        index = new EquipmentIdentityIndex(maxActive, maxHistory);
        registration = kernel.RegisterModule(ModuleDefinition.Create() with
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
            { [EntityNamespace] = IsCurrent }
        });
        try
        {
            lifecycle = registration.ObserveLifecycle(ObserveLifecycle);
            Check(lifecycle.IsActive, "equipment.lifecycle-unavailable");
        }
        catch { registration.Dispose(); throw; }
    }
    private void ObserveLifecycle(RuntimeLifecycleEvent value)
    {
        if (value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged)
            index.BeginWorld(value.Current.WorldEpoch);
        if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped
            || value.Current.IsHost == false) index.Clear();
    }
    public int Count { get { _ = kernel.Lifecycle; return index.Count; } }
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
        return registration.Publish(new RuntimeEvent(
            "gtfo.equipment." + kind + ":" + world + ":" + value.Entity.Id + ":" + entry.Revision,
            value.IsWielded ? ModuleDefinition.EquippedBinding : ModuleDefinition.UnequippedBinding,
            world, Math.Max(0, kernel.CurrentTick), "gtfo.world:" + world,
            RuntimeJson.From(new { actor = value.Owner, equipment = value.Entity })));
    }
    public bool Remove(EntityReference reference)
    {
        _ = kernel.Lifecycle; Check(!probing, "equipment.probe-reentrancy"); Reference(reference);
        return !disposed && registration.IsRegistered && index.Remove(reference);
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
    public bool IsCurrent(EntityReference reference) => TryResolve(reference, out _, out _);
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
