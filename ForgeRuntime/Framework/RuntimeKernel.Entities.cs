using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeRuntime.Framework;

public sealed partial class RuntimeKernel
{
    public const int MaximumEntityObservers = 256;
    public const int MaximumEntityReferencesPerQuery = 256;
    public const int MaximumEntityQueriesPerTick = 64;
    public const int MaximumEntityReferencesPerTick = 1024;
    private bool observingEntities;
    private long entityBudgetWorld = -1, entityBudgetTick = -2;
    private int entityQueriesThisTick, entityReferencesThisTick;
    private void NoEntityObservationMutation()
        => RuntimeJson.Require(!observingEntities, "entity-observer-mutation",
            "Entity observers and their resolvers cannot mutate or recursively query the kernel.");
    private RuntimeEntityQueryResult RejectedEntityQuery(string code, int requested = 0, int distinct = 0)
        => new("rejected", code, Lifecycle, requested, distinct, Array.Empty<RuntimeEntityInspection>());

    public RuntimeEntityQueryResult InspectActor(RuntimeActorContext actors, string role)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(actors);
        var reference = actors.Get(role);
        return reference == null ? RejectedEntityQuery("actor-missing") : InspectEntities(new[] { reference });
    }

    /// <summary>Current observations for explicit refs only; no discovery, truncation or permission grant.</summary>
    public RuntimeEntityQueryResult InspectEntities(IReadOnlyList<EntityReference> references)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(references);
        int requested = references.Count;
        if (requested > MaximumEntityReferencesPerQuery) return RejectedEntityQuery("entity-query-budget", requested);
        var captured = new EntityReference[requested];
        for (int i = 0; i < requested; i++) captured[i] = RuntimeEntityReferences.Validate(references[i]);
        var distinct = captured.Distinct().ToArray();
        if (StartupState != RuntimeStartupState.Ready || !worldStarted)
            return RejectedEntityQuery("runtime-not-ready", requested, distinct.Length);
        if (entityBudgetWorld != WorldEpoch || entityBudgetTick != CurrentTick)
        {
            entityBudgetWorld = WorldEpoch; entityBudgetTick = CurrentTick;
            entityQueriesThisTick = entityReferencesThisTick = 0;
        }
        if (entityQueriesThisTick >= MaximumEntityQueriesPerTick
            || entityReferencesThisTick + requested > MaximumEntityReferencesPerTick)
            return RejectedEntityQuery("entity-query-tick-budget", requested, distinct.Length);
        entityQueriesThisTick++; entityReferencesThisTick += requested;
        var context = Lifecycle;
        var items = new List<RuntimeEntityInspection>(distinct.Length);
        observingEntities = true;
        try { foreach (var reference in distinct) items.Add(InspectEntity(reference)); }
        finally { observingEntities = false; }
        bool complete = items.All(item => item.Snapshot != null);
        return new(complete ? "complete" : "partial", complete ? "entities-observed" : "entity-query-incomplete",
            context, requested, distinct.Length, items);
    }
    private RuntimeEntityInspection InspectEntity(EntityReference reference)
    {
        RuntimeEntityInspection Failure(string code) => new(reference, null, code);
        try { CheckEntity(reference); }
        catch (RuntimeContractException error) { return Failure(error.Code); }
        var prefix = reference.Id[..reference.Id.IndexOf(':')];
        if (!registry.EntityObservers.TryGetValue(prefix, out var observer))
            return Failure("entity-observer-unavailable");
        RuntimeEntitySnapshot? snapshot;
        try { snapshot = observer.Observe(reference); }
        catch (Exception) { return Failure("entity-observer-failed"); }
        if (snapshot == null) return Failure("entity-observation-unavailable");
        if (snapshot.Ref != reference) return Failure("entity-observation-mismatch");
        try { CheckEntity(reference); }
        catch (RuntimeContractException error) { return Failure(error.Code); }
        return new(reference, snapshot, "entity-observed");
    }
}
