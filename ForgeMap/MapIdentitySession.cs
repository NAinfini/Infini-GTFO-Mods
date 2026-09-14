using System;
using ForgeRuntime.Framework;
using static ForgeMap.MapObjectIdentityIndex;

namespace ForgeMap;

// Internal staging seam for a future audited Map adapter. Explicit construction only.
// Registers the unchanged empty provider; NO executable resolver or binding is exported.
// Probes must use the creation ticket + captured native incarnation, not pointer/ID existence.
// A pointer and Unity ID may both be reused after a missed destruction callback.
internal sealed class MapIdentitySession : IDisposable
{
    private readonly RuntimeKernel kernel;
    private readonly MapObjectIdentityIndex index;
    private readonly Func<MapCreationTicket, MapNativeIdentity, bool> nativeIsCurrent;
    private readonly RuntimeModuleHandle registration;
    private readonly RuntimeLifecycleSubscription lifecycle;
    private bool disposed, probing;

    internal MapIdentitySession(RuntimeKernel kernel, RuntimeLogLevel logLevel,
        Func<MapCreationTicket, MapNativeIdentity, bool> nativeIsCurrent, int maxEntries = 4096)
    {
        this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        this.nativeIsCurrent = nativeIsCurrent ?? throw new ArgumentNullException(nameof(nativeIsCurrent));
        index = new MapObjectIdentityIndex(maxEntries);
        registration = kernel.RegisterModule(ModuleDefinition.Create(), logLevel);
        try
        {
            lifecycle = registration.ObserveLifecycle(OnLifecycle);
            Check(lifecycle.IsActive, "lifecycle-unavailable");
        }
        catch { registration.Dispose(); throw; }
    }
    private void OnLifecycle(RuntimeLifecycleEvent change)
    {
        if (change.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged)
            index.BeginWorld(change.Current.WorldEpoch);
        if (change.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            index.Invalidate();
    }
    private RuntimeLifecycleSnapshot Ready()
    {
        var state = kernel.Lifecycle; // SDK enforces its owning simulation thread.
        Check(!disposed && registration.IsRegistered && lifecycle.IsActive, "session-detached");
        Check(!probing, "probe-reentrancy");
        Check(state.StartupState == RuntimeStartupState.Ready && state.WorldEpoch > 0, "not-ready");
        Check(state.WorldEpoch == index.WorldEpoch, "stale-world");
        // This is observation readiness, NOT an InLevel, host or action permission grant.
        return state;
    }
    internal int ObservedCount { get { _ = kernel.Lifecycle; return index.ObservedCount; } }
    internal int HistoryCount { get { _ = kernel.Lifecycle; return index.HistoryCount; } }
    internal long GapCount { get { _ = kernel.Lifecycle; return index.GapCount; } }
    internal MapObservationGap? LastGap { get { _ = kernel.Lifecycle; return index.LastGap; } }
    internal void BeginGeneration() { Ready(); index.BeginGeneration(); }
    internal void MarkGap(MapObservationGap reason) { Ready(); index.MarkGap(reason); }
    internal MapCreationTicket BeginCreation(MapObjectAddress address, MapSourceLock source)
    { Ready(); return index.BeginCreation(address, source); }

    private void Probe(MapCreationTicket ticket, MapNativeIdentity native)
    {
        probing = true;
        try { Check(nativeIsCurrent(ticket, native), "native-not-current"); }
        catch (RuntimeContractException) { throw; }
        catch (Exception error)
        { throw new RuntimeContractException("map.identity.probe-failed", error.GetType().Name); }
        finally { probing = false; }
    }
    internal MapIdentityReceipt ObserveCreated(MapCreationTicket ticket, MapCreationObservation observation)
    {
        var before = Ready(); var generation = index.Generation;
        index.ValidateTicket(ticket);
        Check(observation != null, "invalid-observation");
        ValidateNative(observation!.Native);
        if (observation.Source != null) Probe(ticket, observation.Native);
        Check(Ready().WorldEpoch == before.WorldEpoch && index.Generation == generation, "observation-changed");
        return index.ObserveCreated(ticket, observation);
    }
    internal bool CancelCreation(MapCreationTicket ticket)
    { Ready(); return index.CancelCreation(ticket); }
    internal bool Retire(MapCreationTicket ticket, MapNativeIdentity native)
    { Ready(); return index.Retire(ticket, native); }
    internal bool TryResolve(EntityReference reference, out MapIdentitySnapshot? value, out string code)
    {
        _ = kernel.Lifecycle; // Wrong-thread access is a contract violation, not a missing object.
        value = null; code = "map.identity.not-observed";
        try
        {
            var before = Ready(); var generation = index.Generation;
            if (!index.TryGetObserved(reference, out var observed, out var native, out code)) return false;
            Probe(observed!, native);
            Check(Ready().WorldEpoch == before.WorldEpoch && generation == index.Generation, "observation-changed");
            Check(index.TryGetObserved(reference, out var current, out _, out _)
                && ReferenceEquals(current, observed), "observation-changed");
            value = observed!.Value; code = "map.identity.current"; return true;
        }
        catch (RuntimeContractException error) { code = error.Code; return false; }
    }
    public void Dispose()
    {
        _ = kernel.Lifecycle; Check(!probing, "probe-reentrancy");
        if (disposed) return;
        registration.Dispose(); // Rejected disposal must leave the live session intact.
        lifecycle.Dispose(); index.Invalidate(); disposed = true;
    }
}
