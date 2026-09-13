using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

static class ContractTests
{
    internal static EntityReference Ref(string id = "test.entity:1", long life = 1, long world = 1)
        => new(id, world, life);
    internal static RuntimeEntitySnapshot Entity(EntityReference? reference = null,
        string? faction = "blue", string kind = "enemy")
        => new(reference ?? Ref(), kind, faction, "alive", new[] { "tag" }, new[] { "combat.health" }, new double[] { 1, 2, 3 });
    public static void Run()
    {
        var reference = Ref("test.entity:18446744073709551615", RuntimeJson.MaxSafeInteger);
        Check.That(RuntimeEntityReferences.Validate(reference) == reference, "opaque large game ID stays text");
        foreach (var invalid in new[] { "", " x", "x ", "x\ny", "x\ty", "x\0y", "x\u007fy", new string('x', 257) })
            Check.Reject(() => RuntimeEntityReferences.Validate(Ref(invalid)), "invalid reference text", "invalid-string");
        Check.That(RuntimeEntityReferences.Validate(Ref(new string('x', 256))).Id.Length == 256, "text limit accepted exactly");
        Check.That(RuntimeEntityReferences.Validate(Ref("entity:敌人😀")).Id == "entity:敌人😀", "Unicode identity preserved");
        foreach (var epoch in new[] { -1L, RuntimeJson.MaxSafeInteger + 1 })
        {
            Check.Reject(() => RuntimeEntityReferences.Validate(Ref(life: epoch)), "bad life", "invalid-integer");
            Check.Reject(() => RuntimeEntityReferences.Validate(Ref(world: epoch)), "bad world", "invalid-integer");
        }
        Check.That(RuntimeEntityReferences.Validate(Ref(life: 0, world: 0)).WorldEpoch == 0, "zero epoch is structurally valid");
        Check.Reject(() => RuntimeEntityReferences.Validate(null!), "null reference");
        SnapshotTests(); ActorTests(); RelationTests();
    }
    private static void SnapshotTests()
    {
        var tags = new[] { "a" }; var receivers = new[] { "health" }; var position = new double[] { 1, 2, 3 };
        var snapshot = new RuntimeEntitySnapshot(Ref(), "custom-kind", null, "downed", tags, receivers, position);
        tags[0] = "changed"; receivers[0] = "changed"; position[0] = 999;
        Check.That(snapshot.Tags[0] == "a" && snapshot.Receives[0] == "health" && snapshot.Position[0] == 1,
            "snapshot does not alias source arrays");
        Check.That(((IList<string>)snapshot.Tags).IsReadOnly && ((IList<double>)snapshot.Position).IsReadOnly,
            "snapshot collections cannot be mutated");
        var roundtrip = RuntimeEntitySnapshot.FromJson(RuntimeJson.From(snapshot));
        Check.That(roundtrip.Ref == snapshot.Ref && roundtrip.Faction == null && roundtrip.LifeState == "downed",
            "wire shape roundtrip preserves nullable faction and life");
        Check.That(roundtrip.Kind == "custom-kind" && roundtrip.Receives.SequenceEqual(snapshot.Receives), "custom kind is not faction");
        foreach (var life in new[] { "alive", "downed", "dead" })
            Check.That(new RuntimeEntitySnapshot(Ref(), "enemy", null, life, Array.Empty<string>(), Array.Empty<string>(), position).LifeState == life,
                "explicit life state " + life);
        Check.Reject(() => new RuntimeEntitySnapshot(Ref(), "enemy", null, "unknown", tags, receivers, position), "unknown life", "entity-life-state");
        Check.Reject(() => new RuntimeEntitySnapshot(Ref(), "enemy", null, "alive", new[] { "a", "a" }, receivers, position), "duplicate tags", "duplicate-value");
        Check.Reject(() => new RuntimeEntitySnapshot(Ref(), "enemy", null, "alive", tags, new[] { "x", "x" }, position), "duplicate receivers", "duplicate-value");
        var labels = Enumerable.Range(0, 128).Select(i => "tag" + i).ToArray();
        Check.That(new RuntimeEntitySnapshot(Ref(), "enemy", null, "alive", labels, labels, position).Tags.Count == 128, "128 labels accepted");
        Check.Reject(() => new RuntimeEntitySnapshot(Ref(), "enemy", null, "alive", labels.Append("extra").ToArray(), receivers, position),
            "129 labels rejected", "entity-label-budget");
        foreach (var n in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check.Reject(() => new RuntimeEntitySnapshot(Ref(), "enemy", null, "alive", tags, receivers, new[] { n, 0, 0 }), "nonfinite position", "entity-position");
        Check.Reject(() => new RuntimeEntitySnapshot(Ref(), "enemy", null, "alive", tags, receivers, new double[2]), "bad position shape", "entity-position");
        var json = RuntimeJson.From(Entity()).GetRawText();
        var extra = JsonNode.Parse(json)!.AsObject(); extra["nativePointer"] = "123";
        Check.Reject(() => RuntimeEntitySnapshot.FromJson(RuntimeJson.Parse(extra.ToJsonString())), "unknown snapshot field", "unknown-field");
        foreach (var field in new[] { "ref", "kind", "faction", "lifeState", "tags", "receives", "position" })
        {
            var missing = JsonNode.Parse(json)!.AsObject(); missing.Remove(field);
            Check.Reject(() => RuntimeEntitySnapshot.FromJson(RuntimeJson.Parse(missing.ToJsonString())), "missing snapshot " + field, "missing-field");
        }
        using var duplicate = JsonDocument.Parse(json[..^1] + ",\"kind\":\"player\"}");
        Check.Reject(() => RuntimeEntitySnapshot.FromJson(duplicate.RootElement), "duplicate JSON key", "duplicate-key");
    }
    private static void ActorTests()
    {
        string[] roles = { "self", "source", "owner", "instigator", "event-target" };
        var values = roles.Select((role, i) => (role, value: Ref("test.entity:" + i))).ToDictionary(x => x.role, x => x.value);
        var actors = new RuntimeActorContext(values);
        foreach (var role in roles) Check.That(actors.Get(role) == values[role], "explicit actor " + role);
        values["owner"] = Ref("changed"); values.Clear();
        Check.That(actors.Get("owner")!.Id == "test.entity:2", "actors copied from caller dictionary");
        Check.That(((IDictionary<string, EntityReference>)actors.Actors).IsReadOnly, "actors exposed read-only");
        var sourceOnly = new RuntimeActorContext(new Dictionary<string, EntityReference> { ["source"] = Ref() });
        foreach (var role in roles.Where(r => r != "source")) Check.That(sourceOnly.Get(role) == null, "no fallback for " + role);
        Check.Reject(() => sourceOnly.Get("caster"), "unknown queried actor", "actor-role");
        Check.Reject(() => new RuntimeActorContext(new Dictionary<string, EntityReference> { ["caster"] = Ref() }), "unknown declared actor", "actor-role");
        var parsed = RuntimeActorContext.FromJson(RuntimeJson.From(actors.Actors));
        Check.That(parsed.Actors.Count == 5 && parsed.Get("event-target") == actors.Get("event-target"), "actor JSON roundtrip");
        Check.Reject(() => RuntimeActorContext.FromJson(RuntimeJson.Parse("{\"owner\":null}")), "null is not an absent actor", "object-required");
        Check.That(RuntimeActorContext.FromJson(RuntimeJson.Parse("{}")).Actors.Count == 0, "no actors explicitly present");
    }
    private static void RelationTests()
    {
        var rules = new[] { new RuntimeFactionRelation("blue", "red", "ally"), new RuntimeFactionRelation("red", "blue", "hostile") };
        var relations = new RuntimeFactionRelations(rules);
        var a = Entity(); var b = Entity(Ref("test.entity:2"), "red", "player");
        Check.That(relations.Resolve(a, b) == "ally", "enemy kind can have player ally");
        Check.That(relations.Resolve(b, a) == "hostile", "relationships are directed");
        Check.That(relations.Resolve(a, a) == "self", "full reference equality is self");
        Check.That(relations.Resolve(a, Entity(Ref("test.entity:3"))) == "unknown", "same faction is not implicitly ally");
        Check.That(relations.Resolve(a, Entity(Ref(life: 2))) != "self", "old and new life are not self");
        Check.That(relations.Resolve(a, Entity(Ref("test.entity:3"), null)) == "unknown", "unknown faction stays unknown");
        Check.Reject(() => relations.Resolve(a, Entity(Ref(world: 2))), "cross world relation", "stale-world");
        rules[0] = new("blue", "red", "hostile");
        Check.That(relations.Resolve(a, b) == "ally", "relation input mutation cannot alter snapshot");
        Check.Reject(() => new RuntimeFactionRelations(new[] { rules[0], rules[0] }), "duplicate directed rule", "relation-conflict");
        foreach (var relation in new[] { "self", "unknown", "enemy", "" })
            Check.Reject(() => new RuntimeFactionRelations(new[] { new RuntimeFactionRelation("a", "b", relation) }), "bad relation value", "relation-value");
        var many = Enumerable.Range(0, 4096).Select(i => new RuntimeFactionRelation("f" + i, "to", "neutral")).ToArray();
        Check.That(new RuntimeFactionRelations(many).Resolve(Entity(faction: "f4095"), Entity(Ref("test.entity:2"), "to")) == "neutral", "4096 rules accepted");
        Check.Reject(() => new RuntimeFactionRelations(many.Append(new("extra", "to", "ally")).ToArray()), "relation capacity enforced", "relation-budget");
        var wire = RuntimeFactionRelations.FromJson(RuntimeJson.From(new[] { new RuntimeFactionRelation("blue", "red", "neutral") }));
        Check.That(wire.Resolve(a, b) == "neutral", "relation wire roundtrip");
        Check.Reject(() => RuntimeFactionRelations.FromJson(RuntimeJson.Parse("[{\"from\":\"a\",\"to\":\"b\",\"relation\":\"ally\",\"symmetric\":true}]")),
            "no implicit symmetric field", "unknown-field");
    }
}
