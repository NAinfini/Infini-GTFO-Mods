using System;
using System.Linq;
using ForgeRuntime.Framework;
using ForgeTrigger.Targeting;

/// <summary>The two entity-state reads the batch registers, exercised through the exact public entry points their
/// handlers call, against a session built the way the kernel builds one for a `query` step. The kind and the zone
/// are both the world's own answers — the provider that owns the entity publishes the kind and answers the
/// placement — and a placement no provider can answer stays the kernel's refusal instead of a zone this row would
/// have to invent. Port ids, output shape and the catalog kind of both rows are checked by the contract suite.</summary>
internal static class ObservedEntityStateTests
{
    internal static void Run(Action<bool, string> check)
    {
        var enemy = At("enemy", "enemy");
        var player = At("player", "player");
        var unplaced = At("unplaced", "enemy");
        using var world = new ObservationWorld(new[] { enemy, player, unplaced });
        var session = world.Session();
        void Reject(string name, string code, Action action)
        {
            try { action(); check(false, name + " accepted " + code); }
            catch (RuntimeContractException error) { check(error.Code == code, name + " rejected " + code + ": " + error.Code); }
        }

        // The kind is the observation the owning provider published, never a kind spelled from the reference's id
        // or from the entity's faction: two entities of one fixture answer the two kinds they were observed as.
        check(ObservedEntityStateDeclarations.Kind(session, enemy.Ref) == "enemy"
                && ObservedEntityStateDeclarations.Kind(session, player.Ref) == "player",
            "kind: the row answers the kind the provider observed");
        Reject("kind", "entity-query-incomplete",
            () => ObservedEntityStateDeclarations.Kind(session, enemy.Ref with { LifeEpoch = 9 }));

        // The zone is the placement the provider that owns the entity's kind answers, read per entity through the
        // one session, and a second call costs a second read rather than an answer this row cached.
        var blue = RuntimeZones.Reference(1, 0, 0, 1);
        var red = RuntimeZones.Reference(1, 0, 0, 2);
        world.Zones[enemy.Ref.Id] = blue;
        world.Zones[player.Ref.Id] = red;
        check(ObservedEntityStateDeclarations.ZoneOf(session, enemy.Ref) == blue
                && ObservedEntityStateDeclarations.ZoneOf(session, player.Ref) == red,
            "zone: the row answers the zone the provider placed the entity in");
        var before = world.ZoneReads;
        _ = ObservedEntityStateDeclarations.ZoneOf(session, enemy.Ref);
        check(world.ZoneReads - before == 1, "zone: one zone read per answer, never a second read of the same entity");

        // An entity the owning provider cannot place is refused by the provider's own code, and a kind whose owner
        // registered no zone responder at all is a different refusal — neither is answered as "no zone", because an
        // entity standing nowhere and an unreadable placement must not look the same to a consumer.
        world.Zones.Remove(unplaced.Ref.Id);
        Reject("zone", "entity-zone-unknown", () => ObservedEntityStateDeclarations.ZoneOf(session, unplaced.Ref));
        using (var unzoned = new ObservationWorld(new[] { enemy }, zones: false))
        {
            var unzonedSession = unzoned.Session();
            Reject("zone", "entity-zone-unavailable",
                () => ObservedEntityStateDeclarations.ZoneOf(unzonedSession, unzoned.Entities[enemy.Ref.Id].Ref));
        }

        check(world.Kernel.QueuedEvents == 0 && world.Kernel.LoadedPlans == 0, "no state row schedules work");

        // The family's own table is what the module registers: two `state` reads published as world-reading
        // observations, so a rename, a role change or a kind read off the id cannot pass as "some row is published".
        var rows = ObservedEntityStateDeclarations.Family.Nodes;
        check(rows.Count == 2
                && rows.Select(row => row.CapabilityId).OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "forge.query.entity.kind", "forge.query.entity.zone" })
                && rows.All(row => row.Kind == "state" && row.Role == "observe" && row.Execution == "query"
                    && row.Graph.GetProperty("reads")[0].GetString() == "world"),
            "the family publishes the two entity-state reads as world-reading state rows");
    }

    /// <summary>One entity of the synthetic world: the reference is the one the row is handed, and the kind is
    /// what the owning provider observes for it.</summary>
    private static RuntimeEntitySnapshot At(string id, string kind)
        => new(new EntityReference("test.state:" + id, 1, 1), kind, null, "alive",
            new[] { id }, new[] { "test.receiver.health" }, new double[] { 0, 0, 0 });
}
