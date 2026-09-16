using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>
/// The runtime resources a provider owns, and the one budgeted read a `query` step reaches them through. A
/// resource is not discovered here: the kernel holds no world and scans nothing, so a kind is answerable only
/// because the package that knows it registered its own provider — the same ownership rule entity candidates
/// follow. A kind nobody registered is a refusal with a code, never an empty answer, because an empty list would
/// be indistinguishable from a world that really holds none.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>Resources one read may answer with, for the same reason an entity enumeration is bounded: a longer
    /// answer is refused rather than truncated, since a short list reads as a smaller world.</summary>
    public const int MaximumResourcesPerRead = 256;
    /// <summary>Resource reads one tick may make, shared with the entity table's own per-tick ceiling: both are
    /// the same metered world read, so they spend one budget and one refusal code names either of them.</summary>
    private int resourcesThisTick;

    private RuntimeResources RejectedResources(string code)
        => new("rejected", code, Lifecycle, Array.Empty<ResourceRef>());

    /// <summary>Every resource of one kind the provider that owns that kind currently holds. This is the resource
    /// half of <see cref="EnumerateEntityCandidates"/>: the provider's own table, read under the query budget,
    /// with a table that throws or answers with another kind's reference refused by name. A kind is a member of the
    /// shared resource-kind vocabulary and not a dotted id, so a name no provider owns is answered the same way an
    /// absent optional package is: `resource-unavailable`, never a refusal that reads like a contract violation.</summary>
    public RuntimeResources EnumerateResources(string kind)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(kind);
        if (StartupState != RuntimeStartupState.Ready || !worldStarted) return RejectedResources("runtime-not-ready");
        if (!registry.ResourceProviders.TryGetValue(kind, out var provider))
            return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
        ResetEntityBudget();
        if (!ResourceBudgetAvailable(0)) return RejectedResources("entity-query-tick-budget");
        ResourceRef[] captured;
        observingEntities = true;
        try
        {
            IReadOnlyList<ResourceRef> answered;
            try { answered = provider.Provider.Enumerate(); }
            catch (Exception) { return RejectedResources("resource-provider-failed"); }
            if (answered == null) return RejectedResources("resource-provider-failed");
            captured = answered.ToArray();
            foreach (var reference in captured)
            {
                try { ValidateResourceAnswer(kind, reference); }
                catch (RuntimeContractException error) { return RejectedResources(error.Code); }
            }
        }
        finally { observingEntities = false; }
        if (captured.Length > MaximumResourcesPerRead || !ResourceBudgetAvailable(captured.Length))
            return RejectedResources("entity-query-budget");
        if (!ChargeEntityQuery(captured.Length)) return RejectedResources("entity-query-tick-budget");
        return new("complete", "resources-enumerated", Lifecycle, captured);
    }

    /// <summary>Resolves a set of ids through the owner of their kind, in one budgeted read. A provider that
    /// answers nothing for an id it does not hold is the one refusal this read reports as `resource-unavailable`:
    /// the reference is not there, which is a different fact from a spent budget or a malformed table.</summary>
    public RuntimeResources ResolveResources(string kind, IReadOnlyList<string> resourceIds)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(kind); ArgumentNullException.ThrowIfNull(resourceIds);
        if (resourceIds.Count > MaximumResourcesPerRead) return RejectedResources("entity-query-budget");
        if (StartupState != RuntimeStartupState.Ready || !worldStarted) return RejectedResources("runtime-not-ready");
        if (!registry.ResourceProviders.TryGetValue(kind, out var provider))
            return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
        ResetEntityBudget();
        if (!ResourceBudgetAvailable(resourceIds.Count)) return RejectedResources("entity-query-tick-budget");
        var captured = new ResourceRef[resourceIds.Count];
        observingEntities = true;
        try
        {
            for (var index = 0; index < resourceIds.Count; index++)
            {
                var id = RuntimeJson.Text(resourceIds[index]);
                ResourceRef? reference;
                try { reference = provider.Provider.Resolve(id); }
                catch (Exception) { return RejectedResources("resource-provider-failed"); }
                if (reference == null) return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
                try { ValidateResourceAnswer(kind, reference); }
                catch (RuntimeContractException error) { return RejectedResources(error.Code); }
                captured[index] = reference;
            }
        }
        finally { observingEntities = false; }
        if (!ChargeEntityQuery(resourceIds.Count)) return RejectedResources("entity-query-tick-budget");
        return new("complete", "resources-resolved", Lifecycle, captured);
    }

    /// <summary>The same read for references the caller already holds: every one of them must still be there, and
    /// each is checked against the kind it names rather than against a kind the caller supplies.</summary>
    public RuntimeResources ResolveResourceRefs(IReadOnlyList<ResourceRef> references)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(references);
        if (references.Count > MaximumResourcesPerRead) return RejectedResources("entity-query-budget");
        if (StartupState != RuntimeStartupState.Ready || !worldStarted) return RejectedResources("runtime-not-ready");
        ResetEntityBudget();
        if (!ResourceBudgetAvailable(references.Count)) return RejectedResources("entity-query-tick-budget");
        var captured = new ResourceRef[references.Count];
        observingEntities = true;
        try
        {
            for (var index = 0; index < references.Count; index++)
            {
                var reference = references[index];
                try { ValidateResourceAnswer(reference?.ResourceKind ?? "", reference); }
                catch (RuntimeContractException error) { return RejectedResources(error.Code); }
                if (!registry.ResourceProviders.TryGetValue(reference!.ResourceKind, out var provider))
                    return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
                ResourceRef? current;
                try { current = provider.Provider.Resolve(reference.ResourceId); }
                catch (Exception) { return RejectedResources("resource-provider-failed"); }
                if (current == null) return RejectedResources(RuntimeAbiCodes.ResourceUnavailable);
                captured[index] = current;
            }
        }
        finally { observingEntities = false; }
        if (!ChargeEntityQuery(references.Count)) return RejectedResources("entity-query-tick-budget");
        return new("complete", "resources-resolved", Lifecycle, captured);
    }

    /// <summary>One provider's answer is only trusted after the same routed check a published reference passes:
    /// the kind must be the one that was asked for, and the reference must carry a usable id.</summary>
    private static void ValidateResourceAnswer(string kind, ResourceRef? reference)
    {
        RuntimeJson.Require(reference != null, RuntimeAbiCodes.ResourceUnavailable, kind);
        RuntimeJson.Require(reference!.ResourceKind == kind, RuntimeAbiCodes.ResourceKind, reference.ResourceId);
        RuntimeJson.Text(reference.ResourceId);
    }

    /// <summary>
    /// Resolves one resource reference at the kernel boundary, for a value an event payload or a plan's own
    /// compile-time reference carries. A resource the owner no longer answers for was compiled for a world that has
    /// ended: that is `stale-resource`, and it is a rejection here rather than a value that travels on and is
    /// refused later at whichever step happens to read it.
    /// </summary>
    private void ValidateResourceValue(JsonElement value, JsonElement port)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (RuntimeJson.Text(port, "type") != "resource") return;
        // The shape is read first, so a malformed reference is refused as one wherever it arrived from; whether
        // the resource is still there is asked second and only about kind and id.
        var reference = RuntimeJson.ResourceRefOf(value);
        RuntimeJson.Require(reference.ResourceKind == RuntimeJson.Text(port, "resourceKind"),
            RuntimeAbiCodes.ResourceKind, reference.ResourceId);
        if (StartupState != RuntimeStartupState.Ready || !worldStarted) return;
        RuntimeJson.Require(ResolveCompiledResource(reference.ResourceKind, reference.ResourceId) != null,
            RuntimeAbiCodes.StaleResource, reference.ResourceKind + " " + reference.ResourceId);
    }

    /// <summary>
    /// One compiled resource reference, resolved through the provider that owns its kind. The answer is the
    /// provider's own, so a provider that does not have the resource — including one that answers nothing yet,
    /// because its world has not been built — leaves the reference unresolved rather than invented. Load-time
    /// resolution reads this as the registry's static declaration; dispatch-time validation reads the same answer
    /// and turns a null into `stale-resource`.
    /// </summary>
    internal ResourceRef? ResolveCompiledResource(string kind, string resourceId)
    {
        if (kind == null || resourceId == null) return null;
        if (!RuntimeGraphContracts.ResourceKinds.Contains(kind)) return null;
        if (!registry.ResourceProviders.TryGetValue(kind, out var provider)) return null;
        try { return provider.Provider.Resolve(resourceId); }
        catch (Exception) { return null; }
    }
}
