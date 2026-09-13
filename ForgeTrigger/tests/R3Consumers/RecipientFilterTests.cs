using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Targeting;

internal static class RecipientFilterTests
{
    internal static void Run(string referencePath, Action<bool, string> check)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(referencePath));
        var data = document.RootElement;
        check(data.GetProperty("kind").GetString() == "test-only-recipient-filter-vectors", "recipient fixture provenance");
        check(!data.GetProperty("gameVerified").GetBoolean(), "recipient fixtures are not game evidence");
        check(data.GetProperty("canonicalId").GetString() == "forge.selector.target.filter", "existing canonical filter");
        foreach (var row in data.GetProperty("cases").EnumerateArray())
        {
            using var world = ObservationWorld.FromJson(data.GetProperty("world"));
            var actors = RuntimeActorContext.FromJson(data.GetProperty("world").GetProperty("actors"));
            var relations = RuntimeFactionRelations.FromJson(data.GetProperty("world").GetProperty("relations"));
            var input = row.GetProperty("candidates").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
            var original = input.ToArray();
            var receivers = row.GetProperty("receivers").EnumerateArray().Select(v => v.GetString()!).ToArray();
            var name = row.GetProperty("id").GetString()!;
            RecipientFilterSelection Evaluate() => ObservedRecipientFilter.Select(world.Kernel, input, actors,
                relations, row.GetProperty("policy"), receivers);
            if (row.GetProperty("outcome").GetString() == "rejected")
            {
                Reject(name, row.GetProperty("expectedCode").GetString()!, () => Evaluate(), check);
                if (row.GetProperty("expectedCode").GetString() == "recipient-policy")
                    check(world.Observations == 0, "invalid policy does not query " + name);
                continue;
            }
            try
            {
                var actual = Evaluate(); var expected = row.GetProperty("expected");
                var selected = expected.GetProperty("selected").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
                check(actual.Selected.SequenceEqual(selected), "recipient cross-language targets " + name);
                var exclusions = expected.GetProperty("rejected").EnumerateArray().Select(x => new RecipientFilterExclusion(
                    RuntimeJson.Entity(x.GetProperty("target")), x.GetProperty("code").GetString()!,
                    x.TryGetProperty("detail", out var detail) ? detail.GetString() : null)).ToArray();
                check(actual.Excluded.SequenceEqual(exclusions), "recipient cross-language exclusion reasons " + name);
                var counts = expected.GetProperty("counts");
                check(actual.Requested == counts.GetProperty("requested").GetInt32()
                    && actual.Distinct == counts.GetProperty("distinct").GetInt32()
                    && actual.Matched == counts.GetProperty("matched").GetInt32(), "recipient cardinality " + name);
                check(Evaluate().Selected.SequenceEqual(actual.Selected), "recipient repeated evaluation " + name);
                check(input.SequenceEqual(original), "recipient inputs unchanged " + name);
                check(actual.Context.WorldEpoch == 1 && actual.Context.SimulationTick == 0, "recipient context " + name);
                check(world.Kernel.QueuedEvents == 0 && world.Kernel.LoadedPlans == 0, "recipient selection has no side effects " + name);
            }
            catch (Exception error) { check(false, "recipient " + name + ": " + error.Message); }
        }
        Boundaries(check);
    }
    private static void Reject(string name, string code, Action action, Action<bool, string> check)
    {
        try { action(); check(false, name + " accepted"); }
        catch (RuntimeContractException error) { check(error.Code == code, name + ": " + error.Code); }
        catch (Exception error) { check(false, name + ": wrong exception " + error.GetType().Name); }
    }
    private static RuntimeEntitySnapshot Row(string id, string faction, long life = 1,
        string receiver = "test.receiver.health") => new(new EntityReference("test.filter:" + id, 1, life),
        id == "target" ? "player" : "enemy", faction, "alive", new[] { "marked" },
        new[] { receiver }, new[] { 0d, 0d, 0d });
    private static JsonElement Policy(string[]? allowed = null, int maximum = 256) => RuntimeJson.From(new
    {
        schemaVersion = 1, anchor = "owner", kinds = "any", relations = allowed ?? new[] { "hostile" },
        lifeStates = new[] { "alive" }, requireTags = Array.Empty<string>(), excludeTags = Array.Empty<string>(),
        sort = "stable-id", maxTargets = maximum
    });
    private static void Boundaries(Action<bool, string> check)
    {
        var owner = Row("owner", "blue"); var target = Row("target", "red"); var ally = Row("ally", "blue");
        using var world = new ObservationWorld(new[] { owner, target, ally }); var k = world.Kernel;
        var actors = new RuntimeActorContext(new Dictionary<string, EntityReference> { ["owner"] = owner.Ref, ["source"] = ally.Ref });
        var relations = new RuntimeFactionRelations(new[] { new RuntimeFactionRelation("blue", "red", "hostile"),
            new RuntimeFactionRelation("blue", "blue", "ally"), new RuntimeFactionRelation("red", "blue", "ally") });
        var health = new[] { "test.receiver.health" }; var hostile = Policy();
        RecipientFilterSelection Select(IReadOnlyList<EntityReference> refs, JsonElement policy)
            => ObservedRecipientFilter.Select(k, refs, actors, relations, policy, health);
        var first = Select(new[] { target.Ref, target.Ref }, hostile);
        check(first.Selected.Count == 1 && first.Requested == 2 && first.Distinct == 1, "filter duplicate identity accounting");
        check(first.Excluded.Count == 0, "filter duplicate is not an exclusion failure");
        check(Select(Array.Empty<EntityReference>(), hostile).Selected.Count == 0, "filter empty explicit list");
        try { ((IList<EntityReference>)first.Selected)[0] = owner.Ref; check(false, "filter result mutable"); }
        catch (NotSupportedException) { check(true, "filter result read-only"); }
        var sourceOnly = new RuntimeActorContext(new Dictionary<string, EntityReference> { ["source"] = ally.Ref });
        Reject("filter missing owner", "actor-missing", () => ObservedRecipientFilter.Select(k,
            new[] { target.Ref }, sourceOnly, relations, hostile, health), check);
        Reject("filter all-target limit", "recipient-target-limit", () => Select(new[] { target.Ref, ally.Ref },
            Policy(new[] { "ally", "hostile" }, 1)), check);
        var observedBefore = world.Observations;
        Reject("filter raw query budget", "entity-query-budget", () => Select(Enumerable.Repeat(target.Ref, 257).ToArray(), hostile), check);
        Reject("filter anchor shares query budget", "entity-query-budget", () => Select(Enumerable.Repeat(target.Ref, 256).ToArray(), hostile), check);
        Reject("filter null receiver list", "recipient-policy", () => ObservedRecipientFilter.Select(k,
            new[] { target.Ref }, actors, relations, hostile, null!), check);
        Reject("filter duplicate receivers", "recipient-policy", () => ObservedRecipientFilter.Select(k,
            new[] { target.Ref }, actors, relations, hostile, new[] { health[0], health[0] }), check);
        check(world.Observations == observedBefore, "invalid filter input does not query observers");
        world.Entities[target.Ref.Id] = Row("target", "blue");
        var changed = Select(new[] { target.Ref }, hostile);
        check(changed.Selected.Count == 0 && changed.Excluded.Single().Code == "relation-filter", "filter reobserves changed faction");
        check(first.Selected.Single() == target.Ref, "filter previous result does not change with world");
        world.Entities[target.Ref.Id] = Row("target", "red", receiver: "test.receiver.energy");
        changed = Select(new[] { target.Ref }, hostile);
        check(changed.Selected.Count == 0 && changed.Excluded.Single().Code == "receiver-unsupported", "filter missing receiver is reported");
        check(ObservedRecipientFilter.Select(k, new[] { target.Ref }, actors, relations, hostile,
            Array.Empty<string>()).Selected.Count == 1, "filter receiver requirements are explicit");
        var replacement = "test.receiver." + '\uFFFD';
        world.Entities[target.Ref.Id] = Row("target", "red", receiver: replacement);
        var observationCount = world.Observations;
        Reject("filter invalid UTF-16 receiver", "recipient-policy", () => ObservedRecipientFilter.Select(k,
            new[] { target.Ref }, actors, relations, hostile, new[] { "test.receiver." + '\uD800' }), check);
        check(world.Observations == observationCount, "invalid receiver text is rejected before observation");
        check(ObservedRecipientFilter.Select(k, new[] { target.Ref }, actors, relations, hostile,
            new[] { replacement }).Selected.Count == 1, "valid replacement character is not silently rewritten");
        world.OnObserve = _ => null;
        Reject("filter unknown observation", "entity-query-incomplete", () => Select(new[] { target.Ref }, hostile), check);
        world.OnObserve = null;
        world.Entities[target.Ref.Id] = Row("target", "red", life: 2);
        Reject("filter stale life", "entity-query-incomplete", () => Select(new[] { target.Ref }, hostile), check);
        Reject("filter invalid tail cannot be filtered away", "entity-query-incomplete", () => Select(
            new[] { ally.Ref, target.Ref }, Policy(new[] { "ally" })), check);
        var fresh = world.Entities[target.Ref.Id].Ref;
        check(Select(new[] { fresh }, hostile).Selected.Single() == fresh, "filter freshly supplied life succeeds");
        k.Advance(1, true);
        for (var i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick; i++) Select(new[] { fresh }, hostile);
        Reject("filter shared tick budget", "entity-query-tick-budget", () => Select(new[] { fresh }, hostile), check);
        k.Advance(1, true);
        Reject("filter same tick does not reset budget", "entity-query-tick-budget", () => Select(new[] { fresh }, hostile), check);
        k.Advance(2, true);
        check(Select(new[] { fresh }, hostile).Selected.Count == 1, "filter next tick query succeeds");
        k.BeginWorld(2); k.Advance(0, true);
        Reject("filter stale world", "entity-query-incomplete", () => Select(new[] { fresh }, hostile), check);
        check(k.QueuedEvents == 0 && k.LoadedPlans == 0, "filter tests produce no scheduled effects");
    }
}
