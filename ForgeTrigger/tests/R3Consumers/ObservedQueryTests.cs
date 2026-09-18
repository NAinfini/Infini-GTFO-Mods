using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;
using ForgeTrigger.Targeting;

/// <summary>
/// The public consumers the observation conditions and the collection selectors are built on: the boundary
/// <see cref="ObservedEntityNodes.Exists"/> draws between a reference the kernel proved gone and one it could not
/// observe, the kind, tag and life-state reads, the collection algebra the seven selectors are made of, and the
/// actor delivery the five role selectors read. The declaration table is asserted here too, so a row cannot be
/// registered without the shape and the binding role the kernel resolves a `query` step through.
/// </summary>
internal static class ObservedQueryTests
{
    /// <summary>The rows this package's query module declares, in registration order. The list is the batch's own
    /// vocabulary rather than a count of the module: a family another batch adds to the same module declares its own
    /// rows, and only the rows named here are asserted one by one.</summary>
    private static readonly string[] QueryRows =
    {
        "forge.selector.target.distinct", "forge.selector.target.limit", "forge.selector.target.shuffle",
        "forge.selector.target.random", "forge.selector.target.union", "forge.selector.target.intersection",
        "forge.selector.target.difference", "forge.selector.target.self", "forge.selector.target.owner",
        "forge.selector.target.source", "forge.selector.target.instigator", "forge.selector.target.event_target"
    };

    internal static void Run(Action<bool, string> check)
    {
        void Reject(string name, string code, Action action)
        {
            try { action(); check(false, name + " accepted"); }
            catch (RuntimeContractException error) { check(error.Code == code, name + ": " + error.Code + " expected " + code); }
        }

        Declaration(check);
        Existence(check, Reject);
        KindAndTag(check);
        Collections(check, Reject);
        SeededSelection(check, Reject);
        Roles(check, Reject);
    }

    /// <summary>Every observation row is declared once, carries a capability graph, a shape and an `observe`
    /// binding, and is registered by the production module together with the handler it names.</summary>
    private static void Declaration(Action<bool, string> check)
    {
        var module = ForgeTrigger.ModuleDefinition.Create();
        var bindings = RuntimeJson.Parse(module.RegistryJson).GetProperty("bindings").EnumerateArray().ToArray();
        check(ObservedQueryModule.Nodes.Select(row => row.CapabilityId).Distinct(StringComparer.Ordinal).Count() == ObservedQueryModule.Nodes.Count
            && ObservedQueryModule.Nodes.Select(row => row.HandlerName).Distinct(StringComparer.Ordinal).Count() == ObservedQueryModule.Nodes.Count,
            "every declared observation row is one capability under one handler");
        foreach (var id in QueryRows)
        {
            var node = ObservedQueryModule.Nodes.SingleOrDefault(row => row.CapabilityId == id);
            var binding = node == null ? default : bindings.SingleOrDefault(row => row.GetProperty("id").GetString() == node.BindingId);
            check(node != null && binding.ValueKind == JsonValueKind.Object
                && binding.GetProperty("role").GetString() == "observe"
                && binding.GetProperty("status").GetString() == "implemented"
                && node.Graph.GetProperty("execution").GetString() == "query"
                && module.Evaluators.ContainsKey(node.HandlerName) && module.Shapes.ContainsKey(node.HandlerName)
                && module.BindingSupport.Any(support => support.BindingId == node.BindingId),
                "a declared observation row answers as a query step and is registered: " + id);
        }
    }

    /// <summary>Three answers, two of which are the same `false` for the author and one of which is a refusal: a
    /// live reference, a world or life the kernel proved stale, and an observation it could not make.</summary>
    private static void Existence(Action<bool, string> check, Action<string, string, Action> reject)
    {
        var entity = ObservationWorld.At("test.observer:one"); var reference = entity.Ref;
        using var world = new ObservationWorld(new[] { entity });
        var kernel = world.Kernel;
        check(ObservedEntityNodes.Exists(kernel, reference), "exists: a current reference is true");
        check(!ObservedEntityNodes.Exists(kernel, reference with { LifeEpoch = 2 }), "exists: a life the resolver dropped is false");
        check(!ObservedEntityNodes.Exists(kernel, reference with { WorldEpoch = 2 }), "exists: a reference from an ended world is false");
        world.OnObserve = _ => null;
        reject("exists: an unreadable observation", "entity-query-incomplete", () => ObservedEntityNodes.Exists(kernel, reference));
        world.OnObserve = null;
        reject("exists: a reference of an unregistered kind", "entity-query-incomplete",
            () => ObservedEntityNodes.Exists(kernel, reference with { Id = "test.absent:one" }));
        check(ObservedEntityNodes.Exists(kernel, reference), "exists: an unknown read does not poison the next query");
    }

    private static void KindAndTag(Action<bool, string> check)
    {
        var reference = new EntityReference("test.observer:tagged", 1, 1);
        var tagged = new RuntimeEntitySnapshot(reference, "enemy", "red", "alive", new[] { "burning" },
            new[] { "test.receiver.health" }, new[] { 0d, 0d, 0d });
        using var world = new ObservationWorld(new[] { tagged });
        var kernel = world.Kernel;
        check(ObservedEntityNodes.IsKind(kernel, reference, "enemy") && !ObservedEntityNodes.IsKind(kernel, reference, "player"),
            "entity_type: the observed kind decides, not the reference's own spelling");
        check(ObservedEntityNodes.HasTag(kernel, reference, "burning") && !ObservedEntityNodes.HasTag(kernel, reference, "frozen"),
            "has_tag: the observed tags decide, ordinal");
    }
    /// <summary>The set algebra the seven collection selectors answer with: full identity in, one reference per
    /// identity out, and one order — the ordinal key of world epoch, id and life epoch — except where the catalog
    /// says the authored order is the answer.</summary>
    private static void Collections(Action<bool, string> check, Action<string, string, Action> reject)
    {
        var a = new EntityReference("test.entity:a", 1, 1);
        var b = new EntityReference("test.entity:b", 1, 1);
        var c = new EntityReference("test.entity:c", 1, 1);
        var secondLife = a with { LifeEpoch = 2 };
        var input = new[] { b, a, b, secondLife };
        var before = input.ToArray();
        var distinct = ReferenceCollections.Distinct(input);
        check(input.SequenceEqual(before), "collections: the input array is never mutated");
        check(distinct.Count == 3 && distinct.Contains(a) && distinct.Contains(secondLife),
            "collections: distinct keeps one reference per full identity, life epochs included");
        check(ReferenceCollections.Distinct(new[] { a, a with { LifeEpoch = 10 }, a with { LifeEpoch = 100 }, secondLife })
                .Select(row => row.LifeEpoch).SequenceEqual(new long[] { 100, 10, 1, 2 }),
            "collections: distinct orders by world epoch, id and life epoch, ordinal");
        var reversed = input.AsEnumerable().Reverse().ToArray();
        check(ReferenceCollections.Distinct(reversed).SequenceEqual(distinct)
            && ReferenceCollections.Union(reversed, new[] { c }).SequenceEqual(ReferenceCollections.Union(input, new[] { c })),
            "collections: the set operations are input order independent");
        check(ReferenceCollections.Union(new[] { b, a }, new[] { b, c }).SequenceEqual(new[] { a, b, c }),
            "collections: union merges and re-sorts");
        check(ReferenceCollections.Intersection(new[] { b, a, secondLife }, new[] { a, secondLife, c }).SequenceEqual(new[] { a, secondLife }),
            "collections: intersection keeps only the shared identities");
        check(ReferenceCollections.Difference(new[] { a, secondLife, b }, new[] { a }).SequenceEqual(new[] { secondLife, b }),
            "collections: difference removes exactly the excluded identity, life epochs included");
        check(ReferenceCollections.Limit(new[] { c, b, a, c }, 2).Selected.SequenceEqual(new[] { c, b }),
            "collections: limit preserves the authored order and deduplicates first");
        check(ReferenceCollections.Limit(new[] { c, b, a }, 5) is { SelectedCount: 3, RequestedCount: 5, UnfilledCount: 2 },
            "collections: a limit larger than the collection reports its shortfall");
        check(ReferenceCollections.ApplyEmptyPolicy(Array.Empty<EntityReference>(), EmptySelectionPolicy.EmitEmpty).Count == 0
            && ReferenceCollections.ApplyEmptyPolicy(Array.Empty<EntityReference>(), EmptySelectionPolicy.Skip).Count == 0,
            "collections: emit-empty and skip both answer the empty set they were given");
        reject("collections: the fail empty policy", "pure-empty-selection",
            () => ReferenceCollections.ApplyEmptyPolicy(Array.Empty<EntityReference>(), EmptySelectionPolicy.Fail));
        reject("collections: a null candidate list", "pure-collection-null", () => ReferenceCollections.Distinct(null!));
        reject("collections: a null candidate", "pure-reference-null", () => ReferenceCollections.Distinct(new EntityReference[] { null! }));
        reject("collections: an invalid trailing reference", "invalid-integer",
            () => ReferenceCollections.Union(new[] { a }, new[] { b with { LifeEpoch = -1 } }));
        reject("collections: one reference past the candidate budget", "pure-collection-budget",
            () => ReferenceCollections.Distinct(Enumerable.Repeat(a, 4097).ToArray()));
        reject("collections: a selection size outside the catalog bound", "pure-selection-count",
            () => ReferenceCollections.Limit(new[] { a }, 257));
        var large = Enumerable.Range(0, 4096).Select(i => a with { Id = "test.entity:" + i }).ToArray();
        reject("collections: one reference past the output budget", "pure-collection-output-budget",
            () => ReferenceCollections.Union(large, new[] { a }));
    }

    private static void SeededSelection(Action<bool, string> check, Action<string, string, Action> reject)
    {
        var a = new EntityReference("test.entity:a", 1, 1);
        var b = new EntityReference("test.entity:b", 1, 1);
        var c = new EntityReference("test.entity:c", 1, 1);
        var refs = new[] { a, b, c, a with { LifeEpoch = 2 } };
        for (var seed = 0; seed < 16; seed++)
        {
            var permutation = ReferenceCollections.Shuffle(refs, seed);
            check(permutation.SequenceEqual(ReferenceCollections.Shuffle(refs, seed)), "shuffle repeats a seed " + seed);
            check(permutation.SequenceEqual(ReferenceCollections.Shuffle(refs.AsEnumerable().Reverse().Concat(refs).ToArray(), seed)),
                "shuffle ignores the input order " + seed);
            check(permutation.Count == refs.Length && permutation.Distinct().Count() == refs.Length,
                "shuffle is a permutation without replacement " + seed);
            check(ReferenceCollections.Random(refs, seed, 2).Selected.SequenceEqual(permutation.Take(2)),
                "random is the permutation's own prefix " + seed);
        }
        check(!ReferenceCollections.Shuffle(refs, 0).SequenceEqual(ReferenceCollections.Shuffle(refs, 1)),
            "shuffle: two seeds never share one order");
        check(ReferenceCollections.Random(new[] { a, a }, 42, 5) is { AvailableCount: 1, SelectedCount: 1, UnfilledCount: 4 },
            "random: a shortfall is explicit rather than a full success");
        check(ReferenceCollections.Shuffle(Array.Empty<EntityReference>(), 42).Count == 0,
            "shuffle: an empty collection has one permutation");
        reject("shuffle: a negative seed", "pure-seed", () => ReferenceCollections.Shuffle(refs, -1));
        reject("shuffle: a seed past the unsigned 32-bit range", "pure-seed", () => ReferenceCollections.Shuffle(refs, 4294967296));
        reject("random: the seed is validated before the count", "pure-seed", () => ReferenceCollections.Random(refs, -1, 1));
        reject("random: a zero selection", "pure-selection-count", () => ReferenceCollections.Random(refs, 1, 0));
    }

    /// <summary>The five roles the catalog's own selectors read: the payload port each one is read from, `self` from
    /// the entities the plan's mount accepted for the dispatch, and the one role whose absence is a refusal and the
    /// one whose several answers are.</summary>
    private static void Roles(Action<bool, string> check, Action<string, string, Action> reject)
    {
        var actor = new EntityReference("test.entity:actor", 1, 1);
        var victim = new EntityReference("test.entity:victim", 1, 1);
        var source = new EntityReference("test.entity:source", 1, 1);
        RuntimeEvent Event(object payload) => new("event-1", "test.trigger.binding.event", 1, 1, "scope", RuntimeJson.From(payload));
        var carried = RuntimeActorRoles.FromTriggerEvent(Event(new
        {
            target = victim, source, owner = actor, instigator = actor, self = actor, event_target = actor
        }), new[] { victim });
        check(carried.Actors.Count == 5 && carried.Get("self") == victim && carried.Get("owner") == actor
            && carried.Get("instigator") == actor && carried.Get("event-target") == victim && carried.Get("source") == source,
            "roles: each role arrives from its own slot, and `self` is what the mount accepted rather than a port");
        // An event whose payload names no role port and a mount that accepted nothing: the four roles the payload
        // does not carry stay absent, and `self` — the role the contract never lets be absent — is missing.
        var absent = RuntimeActorRoles.FromTriggerEvent(Event(new { amount = 1 }), Array.Empty<EntityReference>());
        check(absent.Get("owner") == null && absent.Get("instigator") == null && absent.Get("self") == null
            && absent.Get("source") == null,
            "roles: a role the event does not carry is absent rather than borrowed from a neighbour");
        check(absent.Get("event-target") == null, "roles: a payload with no `target` port answers no event-target either");
        reject("roles: reading an absent role by requirement", "actor-missing", () => absent.Require("self"));
        var targetOnly = RuntimeActorRoles.FromTriggerEvent(Event(new { target = victim }), Array.Empty<EntityReference>());
        check(targetOnly.Get("event-target") == victim, "roles: event-target reads the payload's own `target` port");
        var decoyOnly = RuntimeActorRoles.FromTriggerEvent(Event(new { event_target = victim }), Array.Empty<EntityReference>());
        check(decoyOnly.Get("event-target") == null && decoyOnly.Actors.Count == 0,
            "roles: the payload's `event_target` spelling is not the event-target slot");
        var sourceOnly = RuntimeActorRoles.FromTriggerEvent(Event(new { source }), Array.Empty<EntityReference>());
        check(sourceOnly.Get("source") == source && sourceOnly.Get("self") == null,
            "roles: only `self` comes from the dispatch; the payload's own ports never answer it");
        var explicitNull = RuntimeActorRoles.FromTriggerEvent(Event(new { owner = (EntityReference?)null, source }), Array.Empty<EntityReference>());
        check(explicitNull.Get("owner") == null && explicitNull.Get("source") == source,
            "roles: a port the event answers null is that role's absence, and says nothing about the others");
        var ambiguous = RuntimeActorRoles.FromTriggerEvent(Event(new { target = victim }), new[] { actor, victim });
        reject("roles: reading self of a mount that accepted two entities", "actor-ambiguous", () => ambiguous.Get("self"));
        reject("roles: requiring self of a mount that accepted two entities", "actor-ambiguous", () => ambiguous.Require("self"));
        check(ambiguous.Get("event-target") == victim, "roles: an unreadable self leaves the payload's own roles readable");
        reject("roles: a name outside the table", "actor-role", () => absent.Get("caster"));
        check(RuntimeActorRoles.Roles.SequenceEqual(new[] { "self", "source", "owner", "instigator", "event-target" }),
            "roles: the table spells the five roles once");

        // The five role rows answer with the role itself: the catalog marks the four that may be absent nullable
        // and `self`, whose absence the handler refuses by name, has no nullable flag to answer with null.
        foreach (var (capability, role) in new[]
        {
            ("forge.selector.target.self", "self"), ("forge.selector.target.owner", "owner"),
            ("forge.selector.target.source", "source"), ("forge.selector.target.instigator", "instigator"),
            ("forge.selector.target.event_target", "event-target")
        })
        {
            var node = ObservedQueryModule.Nodes.Single(row => row.CapabilityId == capability);
            var output = node.Graph.GetProperty("outputs").EnumerateArray().Single();
            var nullable = output.TryGetProperty("nullable", out var flag) && flag.GetBoolean();
            check(RuntimeActorRoles.IsRole(role) && nullable == (role != "self") && output.GetProperty("type").GetString() == "entity",
                "the declared role row answers with the role, nullable exactly where the role may be absent: " + role);
        }
    }
}
