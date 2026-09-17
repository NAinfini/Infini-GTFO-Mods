using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Targeting;

/// <summary>The `forge.selector.target.filter` binding's own semantics over the shared website fixture: one
/// relation against the row's explicit `anchor` input, the shared `empty` policy, and nothing else — the row
/// declares no other field, so a policy field the old reader accepted is now a name no handler can see. The fixture
/// is produced by the website's own targeting code with the same anchor entity in the same port, so a C# that
/// measured the relation against another reference, borrowed the event's `self`, or treated an undeclared relation
/// as a match would answer a different set here.</summary>
internal static class ObservedFilterTests
{
    internal static void Run(string referencePath, Action<bool, string> check)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(referencePath));
        var data = document.RootElement;
        check(data.GetProperty("kind").GetString() == "test-only-recipient-filter-vectors", "filter fixture provenance");
        check(!data.GetProperty("gameVerified").GetBoolean(), "filter fixtures are not game evidence");
        check(data.GetProperty("canonicalId").GetString() == "forge.selector.target.filter", "existing canonical filter");
        var relations = data.GetProperty("relations").EnumerateArray().Select(value => value.GetString()!).ToArray();
        check(relations.SequenceEqual(RecipientFilterRequest.Relations), "every catalog relation has a fixture case");
        var cases = data.GetProperty("cases").EnumerateArray().ToArray();
        check(cases.Length > 0, "the fixture carries at least one selection case");
        var anchorRoles = data.GetProperty("anchorRoles").EnumerateArray().Select(value => value.GetString()!).ToArray();
        check(anchorRoles.Length > 1, "the fixture anchors relation cases at more than one context actor");
        foreach (var row in cases)
        {
            using var world = ObservationWorld.FromJson(data.GetProperty("world"));
            var rules = RuntimeFactionRelations.FromJson(data.GetProperty("world").GetProperty("relations"));
            var candidates = row.GetProperty("candidates").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
            var expected = row.GetProperty("expected").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
            var anchor = RuntimeJson.Entity(row.GetProperty("anchor"));
            var name = row.GetProperty("id").GetString()!;
            var request = RecipientFilterRequest.Read(row.GetProperty("relation").GetString()!);
            check(data.GetProperty("world").GetProperty("actors").TryGetProperty(row.GetProperty("anchorRole").GetString()!, out var actor)
                && RuntimeJson.Entity(actor).Equals(anchor),
                "filter case anchor is the context actor it names " + name);
            try
            {
                var actual = ObservedRecipientFilter.Select(world.Session(), candidates, anchor, rules, request);
                check(actual.Selected.SequenceEqual(expected), "filter cross-language selection " + name);
                check(actual.Requested == candidates.Length && actual.Distinct == candidates.Distinct().Count(),
                    "filter cardinality accounting " + name);
                check(ObservedRecipientFilter.Select(world.Session(), candidates, anchor, rules, request)
                    .Selected.SequenceEqual(expected), "filter repeated evaluation " + name);
                check(world.Kernel.QueuedEvents == 0 && world.Kernel.LoadedPlans == 0,
                    "filter selection is not a side effect " + name);
            }
            catch (Exception error) { check(false, "filter " + name + ": " + error.Message); }
        }
        Boundaries(check);
        Members(check);
    }

    /// <summary>The row's three optional members over one world: a live enemy, a sleeping one, one whose state no
    /// observer publishes, and a map object. Every member defaults to the one that filters nothing, which is what
    /// the cross-language cases — written before these members existed — prove by selecting on the relation alone.
    /// </summary>
    private static void Members(Action<bool, string> check)
    {
        var anchor = Row("anchor", "blue", aiState: "patrolling");
        var awake = Row("awake", "blue", aiState: "patrolling");
        var asleep = Row("asleep", "blue", aiState: "hibernating");
        var stateless = Row("stateless", "blue");
        var door = Row("door", "blue", tags: new[] { "map-object.category=door" });
        using var world = new ObservationWorld(new[] { anchor, awake, asleep, stateless, door });
        var rules = new RuntimeFactionRelations(new[] { new RuntimeFactionRelation("blue", "blue", "ally") });
        var enemies = new[] { awake.Ref, asleep.Ref };
        var all = new[] { awake.Ref, asleep.Ref, stateless.Ref, door.Ref };
        RecipientFilterSelection Select(IReadOnlyList<EntityReference> input, string state = "any",
            bool doors = true, bool self = true)
            => ObservedRecipientFilter.Select(world.Session(), input, anchor.Ref, rules,
                RecipientFilterRequest.Read("ally", state, null, doors, self));
        check(Select(all).Selected.Count == 4, "an unwritten state and both flags filter nothing");
        check(Select(enemies, state: "awake").Selected.SequenceEqual(new[] { awake.Ref }),
            "awake keeps the candidate whose own state is not the sleeping one");
        check(Select(enemies, state: "sleeping").Selected.SequenceEqual(new[] { asleep.Ref }),
            "sleeping keeps the candidate whose own state is the sleeping one");
        check(Select(all, doors: false).Selected.SequenceEqual(
                ObservedSpaceNodes.IdentityOrder(new[] { awake.Ref, asleep.Ref, stateless.Ref })),
            "a walk that does not treat doors as damageable drops the door and keeps every entity");
        check(Select(all, doors: true).Selected.SequenceEqual(
                ObservedSpaceNodes.IdentityOrder(new[] { awake.Ref, asleep.Ref, stateless.Ref, door.Ref })),
            "a walk that treats doors as damageable keeps the door");
        // `include_self` decides whether the anchor itself may come back, which is the only candidate it could add:
        // the anchor is measured against itself as `self`, never as the relation the row asked for.
        check(Select(new[] { anchor.Ref, asleep.Ref }).Selected.SequenceEqual(new[] { asleep.Ref }),
            "an anchor that is also a candidate answers the row's relation, not itself");
        check(Select(new[] { anchor.Ref }, self: false).Selected.Count == 0,
            "a walk that excludes its own source leaves the source out of its own result");
        // A candidate whose own state no observer published is refused, never answered as awake: the row reads a
        // value, and a substituted one would filter on something nobody read.
        Reject(RecipientFilterRequest.StateUnknownCode, () => Select(all, state: "sleeping"), check);
        // The row declares no `origin`: how an entity entered the level is a reading no provider publishes, so the
        // parameter is gone rather than declared-and-always-refused, and a hand-built member is as unknown as any
        // other name outside the two vocabularies the row keeps.
        Reject("recipient-state", () => Select(all, state: "dormant"), check);
        Reject("recipient-state", () => RecipientFilterRequest.Read("ally", "spawned"), check);
        Reject("recipient-relation", () => RecipientFilterRequest.Read("friend", "any"), check);
        check(world.Kernel.QueuedEvents == 0 && world.Kernel.LoadedPlans == 0,
            "no member of the filter row schedules work or loads a plan");
        check(world.Observations > 0, "the members are read from the one observation the row already made");
    }

    private static void Boundaries(Action<bool, string> check)
    {
        var self = Row("self", "blue"); var ally = Row("ally", "blue");
        var hostile = Row("hostile", "red"); var stranger = Row("stranger", "green");
        using var world = new ObservationWorld(new[] { self, ally, hostile, stranger });
        var rules = new RuntimeFactionRelations(new[]
        {
            new RuntimeFactionRelation("blue", "blue", "ally"),
            new RuntimeFactionRelation("blue", "red", "hostile")
        });
        var candidates = new[] { self.Ref, ally.Ref, hostile.Ref, stranger.Ref };
        RecipientFilterSelection Select(string relation, IReadOnlyList<EntityReference>? input = null,
            EntityReference? anchor = null)
            => ObservedRecipientFilter.Select(world.Session(), input ?? candidates, anchor ?? self.Ref, rules,
                RecipientFilterRequest.Read(relation));
        // Everything a step can decide from its own inputs is decided before the world is read, so a request the row
        // could never answer leaves no observation behind.
        Reject("recipient-relation", () => RecipientFilterRequest.Read("friend"), check);
        Reject("recipient-relation", () => RecipientFilterRequest.Read(null!), check);
        Reject("recipient-relation", () => Select("friend"), check);
        check(world.Observations == 0, "an undeclared relation never reads the world");
        Reject("entity-query-budget", () => Select("ally",
            Enumerable.Repeat(ally.Ref, RuntimeKernel.MaximumEntityReferencesPerQuery + 1).ToArray()), check);
        check(world.Observations == 0, "a candidate list past the read budget is refused before it is read");
        Reject("entity-query-incomplete", () => Select("ally", candidates, self.Ref with { LifeEpoch = 9 }), check);
        check(world.Observations > 0, "an anchor the world cannot observe is a refusal, not a silent empty set");
        check(Select("self").Selected.SequenceEqual(new[] { self.Ref }),
            "the anchor is its own relation, and only that relation");
        check(Select("ally").Selected.SequenceEqual(new[] { ally.Ref }),
            "an ally is the ally relation and never the self relation");
        check(Select("hostile").Selected.SequenceEqual(new[] { hostile.Ref }), "a hostile faction pair is hostile");
        check(Select("neutral").Selected.Count == 0, "a faction pair the world says nothing about is not neutral");
        check(Select("unknown").Selected.SequenceEqual(new[] { stranger.Ref }),
            "an undeclared faction pair is unknown, and unknown is a relation like any other");
        check(Select("ally", Array.Empty<EntityReference>()).Selected.Count == 0, "an empty explicit list answers empty");
        check(Select("ally", new[] { ally.Ref, ally.Ref }).Selected.SequenceEqual(new[] { ally.Ref }),
            "a duplicated candidate is answered once, in identity order");
        // The same candidates against another explicit anchor: the relation follows the port, and the anchor is
        // observed without ever being selected. The red anchor has no declared rule against blue or green, so those
        // candidates answer `unknown` where the blue anchor answered `ally`/`unknown`.
        check(Select("unknown", candidates, hostile.Ref).Selected.SequenceEqual(
                ObservedSpaceNodes.IdentityOrder(new[] { ally.Ref, self.Ref, stranger.Ref })),
            "a different anchor re-measures every candidate instead of reusing the event's self");
        check(Select("self", candidates, hostile.Ref).Selected.SequenceEqual(new[] { hostile.Ref }),
            "the named anchor is the only candidate that answers the self relation");
        check(Select("ally", new[] { ally.Ref, self.Ref }, hostile.Ref).Selected.Count == 0,
            "an anchor outside the candidate set is never added to the answer");
        check(Select("self", new[] { ally.Ref }, self.Ref).Selected.Count == 0,
            "an anchor the candidate set does not name answers no candidate, itself included");
        world.Entities[hostile.Ref.Id] = Row("hostile", "blue");
        var changedHostile = Select("hostile"); var changedAlly = Select("ally");
        check(changedHostile.Selected.Count == 0
            && changedAlly.Selected.SequenceEqual(new[] { ally.Ref, hostile.Ref }),
            "a changed faction is reobserved, and an earlier selection does not silently follow it"
            + " [hostile=" + string.Join(",", changedHostile.Selected.Select(r => r.Id))
            + " ally=" + string.Join(",", changedAlly.Selected.Select(r => r.Id)) + "]");
        world.OnObserve = _ => null;
        Reject("entity-query-incomplete", () => Select("ally"), check);
        world.OnObserve = null;
        world.Entities[ally.Ref.Id] = Row("ally", "blue", life: 2);
        Reject("entity-query-incomplete", () => Select("ally"), check);
        check(Select("ally", new[] { hostile.Ref, world.Entities[ally.Ref.Id].Ref }).Selected.Count == 2,
            "a freshly supplied life is observed as the new one");
        world.Kernel.BeginWorld(2); world.Kernel.Advance(0, true);
        Reject("entity-query-incomplete", () => Select("ally"), check);
        check(world.Kernel.QueuedEvents == 0 && world.Kernel.LoadedPlans == 0,
            "no filter case schedules work or loads a plan");
    }

    private static void Reject(string code, Action action, Action<bool, string> check)
    {
        try { action(); check(false, "filter accepted " + code); }
        catch (RuntimeContractException error) { check(error.Code == code, "filter rejected " + code + ": " + error.Code); }
        catch (Exception error) { check(false, "filter " + code + ": wrong exception " + error.GetType().Name); }
    }

    private static RuntimeEntitySnapshot Row(string id, string? faction, long life = 1, string? aiState = null,
        string[]? tags = null)
        => new(new EntityReference("test.filter:" + id, 1, life), "enemy", faction, "alive",
            tags ?? new[] { "marked" }, new[] { "test.receiver.health" }, new[] { 0d, 0d, 0d }, aiState: aiState);
}
