using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeRuntime.Framework;

public sealed partial class RuntimeKernel
{
    public const int MaximumEntityObservers = 256;
    public const int MaximumEntityReferencesPerQuery = 256;
    /// <summary>Entities one enumeration of a kind may answer with. A provider that tracks more than this is not
    /// truncated to fit: the read is refused with `entity-query-budget`, because a short list would be
    /// indistinguishable from a world that really holds that many fewer.</summary>
    public const int MaximumEntityCandidatesPerQuery = 256;
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

    /// <summary>Every entity of one kind the provider that owns that kind currently tracks. The kernel itself
    /// discovers nothing: a kind is answerable only because its own provider registered the candidate source next
    /// to the resolver it already owns for that kind, and every reference that comes back is checked to be of that
    /// kind. A kind nobody exposes is `entity-candidates-unavailable` — an explicit refusal, never an empty world.</summary>
    public RuntimeEntityQueryResult EnumerateEntityCandidates(string kind)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(kind);
        if (!RuntimeJson.IsId(kind)) throw new RuntimeContractException("entity-kind", kind);
        if (StartupState != RuntimeStartupState.Ready || !worldStarted) return RejectedEntityQuery("runtime-not-ready");
        if (!registry.EntityCandidates.TryGetValue(kind, out var registered))
            return RejectedEntityQuery("entity-candidates-unavailable");
        var source = registered.Candidates;
        ResetEntityBudget();
        if (!EntityBudgetAvailable(0)) return RejectedEntityQuery("entity-query-tick-budget");
        EntityReference[] captured;
        observingEntities = true;
        try
        {
            // A provider's own table is untyped input like any other: it is copied into a fresh array, and a source
            // that throws, returns nothing or answers with a reference of another kind is refused by name with a
            // result rather than reaching the query step as a short answer or unwinding as an internal failure.
            IReadOnlyList<EntityReference> answered;
            try { answered = source(); }
            catch (Exception) { return RejectedEntityQuery("entity-candidates-failed"); }
            if (answered == null) return RejectedEntityQuery("entity-candidates-failed");
            captured = answered.ToArray();
            foreach (var reference in captured)
            {
                try
                {
                    RuntimeEntityReferences.Validate(reference);
                    RuntimeJson.Require(RuntimeJson.KindOf(reference.Id) == kind, "entity-candidate-kind", reference.Id);
                }
                catch (RuntimeContractException error) { return RejectedEntityQuery(error.Code); }
            }
        }
        finally { observingEntities = false; }
        if (captured.Length > MaximumEntityCandidatesPerQuery
            || !EntityBudgetAvailable(captured.Length)) return RejectedEntityQuery("entity-query-budget", captured.Length);
        var distinct = captured.Distinct().ToArray();
        if (!ChargeEntityQuery(captured.Length)) return RejectedEntityQuery("entity-query-tick-budget", captured.Length, distinct.Length);
        var context = Lifecycle;
        return new("complete", "entities-enumerated", context, captured.Length, distinct.Length,
            distinct.Select(reference => new RuntimeEntityInspection(reference, null, "entity-enumerated")));
    }

    /// <summary>The query tick's counters, reset at a new world as well as a new tick. The per-query and per-tick
    /// ceilings are both read before anything is charged, so the same call has one answer wherever it is checked.
    /// The resource table's own reads are reset here too: an entity read and a resource read are the same metered
    /// world read and spend the same per-tick budget.</summary>
    private void ResetEntityBudget()
    {
        if (entityBudgetWorld == WorldEpoch && entityBudgetTick == CurrentTick) return;
        entityBudgetWorld = WorldEpoch; entityBudgetTick = CurrentTick;
        entityQueriesThisTick = entityReferencesThisTick = resourcesThisTick = 0;
    }

    private bool EntityBudgetAvailable(int requested)
        => entityQueriesThisTick < MaximumEntityQueriesPerTick
           && entityReferencesThisTick + requested <= MaximumEntityReferencesPerTick;

    /// <summary>Whether one more resource read of <paramref name="requested"/> references fits in this tick's
    /// remaining budget. The count is shared with the entity table's references, so the two can never sum past the
    /// ceiling even when both are read in the same tick.</summary>
    private bool ResourceBudgetAvailable(int requested)
        => entityReferencesThisTick + resourcesThisTick + requested <= MaximumEntityReferencesPerTick;

    private bool ChargeEntityQuery(int requested)
    {
        if (!EntityBudgetAvailable(requested)) return false;
        entityQueriesThisTick++; entityReferencesThisTick += requested;
        return true;
    }

    /// <summary>The explicit actor roles of the trigger event a step is being evaluated for. Delivery, not a second
    /// lookup: four roles come from the event's own payload through the one <see cref="RuntimeActorRoles"/> table,
    /// and `self` is the entity this plan's own mount accepted for the dispatch, carried on the work item.</summary>
    private RuntimeActorContext ActorContext(RuntimeEvent value, Work item) => RuntimeActorRoles.FromTriggerEvent(value, item.Subjects);

    /// <summary>The roles a capability cannot be evaluated without, from the one table that decides that: the roles
    /// it answers with and declares non-nullable. A capability that merely takes a role-named value input declares
    /// none, so the refusal only ever names a role the capability itself said the event must carry.</summary>
    internal IReadOnlyList<string> RequiredActors(string bindingId)
    {
        if (!registry.Bindings.TryGetValue(bindingId, out var binding)) return Array.Empty<string>();
        var capabilityId = RuntimeJson.Text(binding, "capabilityId");
        return registry.Capabilities.TryGetValue(capabilityId, out var capability)
            ? RuntimeActorRoles.Required(capability) : Array.Empty<string>();
    }

    /// <summary>The bounded directed faction relations the world exposes. The kernel holds one table per world; a
    /// world whose providers published none answers `unknown` for every pair, which is a refusal to claim a
    /// relationship, not a neutral one.</summary>
    private RuntimeFactionRelations relations = new(Array.Empty<RuntimeFactionRelation>());

    /// <summary>Publishes the faction relations of the current world, the way an entity resolver is published: the
    /// package that knows how its own factions relate (player teams, enemy packs) hands the kernel one frozen
    /// bounded table instead of every selector asking it separately. A world change and an unregistering owner both
    /// drop it back to empty, because rules about a world that has ended are not rules about this one.</summary>
    public void SetFactionRelations(RuntimeFactionRelations value)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(value); relations = value;
    }

    public RuntimeEntityQueryResult InspectActor(RuntimeActorContext actors, string role)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(actors);
        RuntimeActorContext.ValidateRole(role);
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
        ResetEntityBudget();
        if (!ChargeEntityQuery(requested))
            return RejectedEntityQuery("entity-query-tick-budget", requested, distinct.Length);
        var context = Lifecycle;
        var items = new List<RuntimeEntityInspection>(distinct.Length);
        observingEntities = true;
        try { foreach (var reference in distinct) items.Add(InspectEntity(reference)); }
        finally { observingEntities = false; }
        bool complete = items.All(item => item.Snapshot != null);
        return new(complete ? "complete" : "partial", complete ? "entities-observed" : "entity-query-incomplete",
            context, requested, distinct.Length, items);
    }
    /// <summary>
    /// The zone one entity stands in, answered by the provider that owns the entity's kind. The kernel itself
    /// tests no volume and reads no level: a kind whose owner registered no zone responder is
    /// <see cref="EntityZoneResolution.UnavailableCode"/>, and a responder that answered null has answered that the
    /// entity stands in no zone of this world — that is <see cref="EntityZoneResolution.OutsideCode"/> and not a
    /// refusal, because an entity the level placed outside every zone is a fact a selector has to be able to
    /// exclude by name. A read that could not be made is refused instead: a responder that throws a
    /// <see cref="RuntimeContractException"/> is reported with its own code, any other failure as
    /// <see cref="EntityZoneResolution.FailedCode"/>. The answer is checked to be of the zone kind before it is
    /// handed back, because a responder is a provider's own table like any other and a reference of another kind
    /// would route a later read to the wrong owner.
    /// </summary>
    public EntityZoneResolution ZoneOfEntity(EntityReference entity)
    {
        ReadThread(); AcceptRuntimeWork();
        EntityZoneResolution Refused(string code) => EntityZoneResolution.Refused(code);
        try { CheckEntity(entity); }
        catch (RuntimeContractException error) { return Refused(error.Code); }
        // A zone read is one more step of a query rather than a world read of its own — the responder reads the
        // provider's own table — but it is still a query: it is counted, so a plan that asks about a candidate set
        // one entity at a time cannot spend more reads per tick than the same set read in one call.
        ResetEntityBudget();
        if (!EntityBudgetAvailable(0)) return Refused("entity-query-tick-budget");
        var kind = RuntimeJson.KindOf(entity.Id);
        if (!registry.EntityZones.TryGetValue(kind, out var responder)) return Refused(EntityZoneResolution.UnavailableCode);
        EntityReference? zone;
        try { zone = responder.Zone(entity); }
        // A provider that cannot place an entity it owns says so in its own words: the code travels as the
        // provider spelled it, so "not current", "no course node" and "collected mid-read" stay tellable apart
        // from the one answer that is not a failure.
        catch (RuntimeContractException error) { return Refused(error.Code); }
        catch (Exception) { return Refused(EntityZoneResolution.FailedCode); }
        if (zone == null) return EntityZoneResolution.Outside();
        if (RuntimeJson.KindOf(zone.Id) != RuntimeZones.EntityKind) return Refused(EntityZoneResolution.KindCode);
        if (!ChargeEntityQuery(0)) return Refused("entity-query-tick-budget");
        return EntityZoneResolution.InZone(zone);
    }

    /// <summary>Asks only the provider owning <paramref name="kind"/> for the current reference of a native instance.
    /// No enumeration and no other provider is consulted; an instance that is unknown or no longer current is null.
    /// A kind no provider registered is unresolved for the same reason: every domain package is optional, so the
    /// caller drops the port instead of treating a missing package as a contract violation. Only a malformed kind
    /// name is refused, by the same rule registration applies to every registered kind.</summary>
    public EntityReference? ResolveEntityInstance(string kind, object instance)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(kind); ArgumentNullException.ThrowIfNull(instance);
        RuntimeJson.Require(RuntimeJson.IsId(kind), "entity-resolver", kind);
        if (StartupState != RuntimeStartupState.Ready || !worldStarted) return null;
        observingEntities = true;
        try
        {
            var reference = EntityInstanceOf(kind, instance);
            if (reference == null) return null;
            // The owner's answer is only trusted after the same routed check every published reference passes.
            try { CheckEntity(reference); }
            catch (RuntimeContractException) { return null; }
            return reference;
        }
        finally { observingEntities = false; }
    }

    /// <summary>
    /// The reference one provider's own native lookup answers for <paramref name="kind"/>, with no currency check:
    /// the caller decides whether the answer is about the current life. Only the owning provider is asked, and a
    /// provider that throws — native tables can carry account-level identity — is reported as a resolver failure
    /// without its message. An answer that names another namespace is not that provider's to give and is dropped.
    /// </summary>
    private EntityReference? EntityInstanceOf(string kind, object instance)
    {
        if (!registry.EntityInstanceResolvers.TryGetValue(kind, out var resolver)) return null;
        EntityReference? reference;
        try { reference = resolver.Resolve(instance); }
        catch (Exception) { throw new RuntimeContractException("entity-resolver-failed", kind); }
        if (reference == null) return null;
        return reference.Id == null || !reference.Id.StartsWith(kind + ":", StringComparison.Ordinal) ? null : reference;
    }

    /// <summary>
    /// The one entry point for "which entity is this native object": each registered object resolver is asked in
    /// registration order and the first non-null answer wins. It exists because the caller often holds an object
    /// whose kind it cannot know — a raycast hit is a collider, not an enemy or a door — and the packages that do
    /// know their own native types each answer for their own and return null for everything else, so no package
    /// needs to know about another. A kind no resolver recognizes is null for the same reason an absent optional
    /// package is: the port that would have carried it is left missing rather than filled with a placeholder. The
    /// answer is the same routed, currency-checked reference every other lookup returns, so a resolver that names a
    /// stale life is unanswered rather than handed back.
    /// </summary>
    public EntityReference? ResolveEntity(object instance)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(instance);
        if (StartupState != RuntimeStartupState.Ready || !worldStarted) return null;
        observingEntities = true;
        try
        {
            foreach (var (_, resolver) in registry.ObjectEntityResolvers)
            {
                EntityReference? reference;
                // A resolver that throws is a failure of that package's own table, not a reason to stop asking:
                // the remaining packages still answer for the objects they know, and the object stays unanswered
                // exactly as if the failing one had returned null.
                try { reference = resolver.Resolve(instance); }
                catch (Exception) { continue; }
                if (reference?.Id == null) continue;
                var kind = RuntimeJson.KindOf(reference.Id);
                if (!registry.Resolvers.TryGetValue(kind, out var owner)) continue;
                try { if (!owner.Resolve(reference)) continue; }
                catch (Exception) { continue; }
                if (reference.WorldEpoch != WorldEpoch) continue;
                return reference;
            }
            return null;
        }
        finally { observingEntities = false; }
    }

    /// <summary>The kind one native object belongs to, answered by the same object resolvers
    /// <see cref="ResolveEntity"/> asks, in the same order. A handle that holds a native object is released when
    /// the life of the object it holds has ended, which needs the object's kind before it can ask the owner
    /// whether that life is still current.</summary>
    private string ResolveEntityObject(object instance)
    {
        foreach (var (_, resolver) in registry.ObjectEntityResolvers)
        {
            EntityReference? reference;
            try { reference = resolver.Resolve(instance); }
            catch (Exception) { continue; }
            if (reference?.Id == null) continue;
            var kind = RuntimeJson.KindOf(reference.Id);
            if (kind != "" && registry.Resolvers.ContainsKey(kind)) return kind;
        }
        return "";
    }

    public bool IsEntityCurrent(EntityReference reference)
    {
        ReadThread(); AcceptRuntimeWork(); ArgumentNullException.ThrowIfNull(reference);
        try { CheckEntity(reference); return true; }
        catch (RuntimeContractException) { return false; }
    }

    private RuntimeEntityInspection InspectEntity(EntityReference reference)
    {
        RuntimeEntityInspection Failure(string code) => new(reference, null, code);
        try { CheckEntity(reference); }
        catch (RuntimeContractException error) { return Failure(error.Code); }
        var prefix = RuntimeJson.KindOf(reference.Id);
        if (!registry.EntityObservers.TryGetValue(prefix, out var observer))
            return Failure("entity-observer-unavailable");
        RuntimeEntitySnapshot? snapshot;
        try { snapshot = observer.Observe(reference); }
        catch (Exception) { return Failure("entity-observer-failed"); }
        if (snapshot == null) return Failure("entity-observation-unavailable");
        if (snapshot.Ref != reference) return Failure("entity-observation-mismatch");
        try { CheckEntity(reference); }
        catch (RuntimeContractException error) { return Failure(error.Code); }
        // A successful observation is what tells the kernel who hangs from whom: the observer publishes its own
        // entity's parent, and the reverse index is derived here so nothing else has to keep a children list.
        RecordParent(snapshot);
        return new(reference, snapshot, "entity-observed");
    }
}
