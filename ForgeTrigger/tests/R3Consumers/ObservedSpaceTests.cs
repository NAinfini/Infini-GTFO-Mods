using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;
using ForgeTrigger.Targeting;

/// <summary>The eight selection rows this batch registers, exercised through the exact public entry points their
/// handlers call, against a session built the way the kernel builds one for a `query` step. What the handlers add
/// on top — port ids, enum member names, the shared `empty` policy — is checked against the catalog by the contract
/// suite and end-to-end by the framework suite; what is checked here is the selection itself: order, identity,
/// budget, refusal and the two-output split.</summary>
internal static class ObservedSpaceTests
{
    internal static void Run(Action<bool,string> check)
    {
        var self = At("self", 0, 0, 0, "blue");
        var near = At("near", 1, 0, 0, "blue");
        var far = At("far", 5, 0, 0, "red");
        var up = At("up", 0, 3, 0, "blue");
        var corner = At("corner", 3, 4, 0, "red");
        using var world = new ObservationWorld(new[] { self, near, far, up, corner });
        var session = world.Session();
        var relations = new RuntimeFactionRelations(new[]
        {
            new RuntimeFactionRelation("blue", "blue", "ally"),
            new RuntimeFactionRelation("blue", "red", "hostile")
        });
        var everything = new[] { self.Ref, near.Ref, far.Ref, up.Ref, corner.Ref };
        var origin = new double[3];
        void Reject(string name, string code, Action action)
        {
            try { action(); check(false, name + " accepted " + code); }
            catch (RuntimeContractException error) { check(error.Code == code, name + " rejected " + code + ": " + error.Code); }
        }

        // Every volume the catalog's query_shape set can mean here: the sphere reaches `up` along the axis, the
        // cylinder is inclusive at its own half-height, the capsule's axis adds its radius, and a box is the plan's
        // own extents rather than a radius. Every answer is in the identity order the collection rows share, and
        // every one of them selects from the explicit candidates the caller supplied.
        check(ObservedSpatialNodes.Overlap(session, everything, origin, ObservedVolumeShape.Sphere, 4, 8)
                .SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { self.Ref, near.Ref, up.Ref })),
            "shape_overlap: a sphere keeps every candidate inside its radius, in identity order");
        check(ObservedSpatialNodes.Overlap(session, everything, origin, ObservedVolumeShape.Cylinder, 4, 8)
                .SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { self.Ref, near.Ref, up.Ref, corner.Ref })),
            "shape_overlap: a cylinder is radial about its own axis, so the corner on it stays inside");
        check(ObservedSpatialNodes.Overlap(session, everything, origin, ObservedVolumeShape.Capsule, 4, 8)
                .SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { self.Ref, near.Ref, up.Ref })),
            "shape_overlap: a capsule reaches half its axis beyond its radius");
        check(ObservedSpatialNodes.Overlap(session, everything, origin, ObservedVolumeShape.Box, 3, 8)
                .SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { self.Ref, near.Ref, up.Ref, corner.Ref })),
            "shape_overlap: a box half size is inclusive on every world axis");
        check(ObservedSpatialNodes.Overlap(session, everything, origin, ObservedVolumeShape.Sphere, 1, 8)
                .SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { self.Ref, near.Ref })),
            "shape_overlap: a smaller radius drops the candidates it no longer covers");
        check(ObservedSpatialNodes.Overlap(session, new[] { near.Ref }, origin, ObservedVolumeShape.Sphere, 10, 8)
                .SequenceEqual(new[] { near.Ref }),
            "shape_overlap: the answer is exactly the explicit candidates the volume covers, never a wider world");
        check(ObservedSpatialNodes.Overlap(session, Array.Empty<EntityReference>(), origin, ObservedVolumeShape.Sphere, 10, 8).Count == 0,
            "shape_overlap: an empty explicit candidate set answers empty instead of the world");
        Reject("shape_overlap", "spatial-shape",
            () => ObservedSpatialNodes.Overlap(session, everything, origin, (ObservedVolumeShape)999, 4, 8));
        Reject("shape_overlap", "spatial-parameter",
            () => ObservedSpatialNodes.Overlap(session, everything, origin, ObservedVolumeShape.Sphere, 0, 8));

        // The ranking rows measure around a position the same session produced for the anchor. The origin is the
        // closest candidate there is, and `nearest` counts it: it is in the candidate set the plan supplied.
        check(ObservedSpaceNodes.Nearest(session, everything, origin, 2).SequenceEqual(new[] { self.Ref, near.Ref }),
            "nearest: the two closest candidates are answered in distance order");
        check(ObservedSpaceNodes.Farthest(session, everything, origin, 2).SequenceEqual(new[] { corner.Ref, far.Ref }),
            "farthest: equal distances fall back to the identity tie-break, never to the input order");
        check(ObservedSpaceNodes.Nearest(session, everything, origin, 8).Count == 5,
            "nearest: a request past the available candidates answers the whole set, never padding it");
        Reject("nearest", "pure-selection-count", () => ObservedSpaceNodes.Nearest(session, everything, origin, 0));
        Reject("nearest", "pure-selection-count", () => ObservedSpaceNodes.Nearest(session, everything, origin, 257));
        check(ObservedSpaceNodes.EmptyPolicy(RuntimeJson.From(new { empty = "fail" })) == EmptySelectionPolicy.Fail
            && ObservedSpaceNodes.EmptyPolicy(RuntimeJson.From(new { empty = "emit-empty" })) == EmptySelectionPolicy.EmitEmpty
            && ObservedSpaceNodes.EmptyPolicy(RuntimeJson.From(new { empty = "skip" })) == EmptySelectionPolicy.Skip,
            "empty policy: the catalog's three members reach their own policy and no others");
        Reject("empty policy", "pure-operation", () => ObservedSpaceNodes.EmptyPolicy(RuntimeJson.From(new { empty = "ignore" })));
        Reject("empty policy", "pure-empty-selection",
            () => ReferenceCollections.ApplyEmptyPolicy(Array.Empty<EntityReference>(), EmptySelectionPolicy.Fail));

        // weighted is the existing pure sampler over explicit masses: one weight per candidate, one seed, no read.
        var observations = world.Observations;
        check(ObservedSpaceNodes.Weighted(new[] { near.Ref, far.Ref }, new[] { 1d, 0d }, 1, 42).SequenceEqual(new[] { near.Ref }),
            "weighted: a zero weight is never drawn while a positive one remains");
        check(ObservedSpaceNodes.Weighted(everything, new[] { 3d, 1d, 1d, 1d, 1d }, 3, 42)
                .SequenceEqual(ObservedSpaceNodes.Weighted(everything, new[] { 3d, 1d, 1d, 1d, 1d }, 3, 42)),
            "weighted: one seed is one draw");
        check(world.Observations == observations, "weighted: the pure draw reads no world at all");
        Reject("weighted", "weighted-weight", () => ObservedSpaceNodes.Weighted(everything, new[] { 1d }, 1, 42));
        Reject("weighted", "weighted-weight", () => ObservedSpaceNodes.Weighted(new[] { near.Ref, far.Ref }, new[] { 1d, -1d }, 1, 42));
        // A pool with no positive mass answers nothing rather than a target nobody weighted.
        check(ObservedSpaceNodes.Weighted(new[] { near.Ref, far.Ref }, new[] { 0d, 0d }, 1, 42).Count == 0,
            "weighted: a pool with no positive weight answers the empty set, never an unweighted target");

        // chain hops from the origin without ever visiting it or revisiting a target: `up` is nearer to `near` than
        // `corner` or `far` is, and `corner` is nearer to `up` than `far` is to `up`. The hop order is the answer,
        // so it is compared as the visit sequence rather than re-sorted.
        var hops3 = ObservedSpaceNodes.Chain(session, everything, self.Ref, 3, 10);
        check(hops3.SequenceEqual(new[] { near.Ref, up.Ref, corner.Ref }),
            "chain: hops follow the nearest unvisited candidate and never select the origin"
            + " [got " + string.Join(",", hops3.Select(r => r.Id)) + "]");
        check(ObservedSpaceNodes.Chain(session, everything, self.Ref, 3, 1.5).SequenceEqual(new[] { near.Ref }),
            "chain: a hop past the radius ends the chain instead of jumping to the next one");
        Reject("chain", "spatial-hop-budget", () => ObservedSpaceNodes.Chain(session, everything, self.Ref, 65, 1));
        Reject("chain", "spatial-hop-budget", () => ObservedSpaceNodes.Chain(session, everything, self.Ref, 0, 1));

        // filter measures one relation against the row's own `anchor` input, not against a role looked up again:
        // the anchor is a reference the caller supplied, `self` is the one that happens to equal the executing
        // instance here, and an anchor outside the candidate set is observed without ever being selected.
        check(ObservedSpaceNodes.Filter(session, everything, self.Ref, relations, RecipientFilterRequest.Read("self"))
                .SequenceEqual(new[] { self.Ref }),
            "filter: the anchor is its own relation and is not answered as an ally");
        check(ObservedSpaceNodes.Filter(session, everything, self.Ref, relations, RecipientFilterRequest.Read("ally"))
                .SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { near.Ref, up.Ref })),
            "filter: ally keeps the candidates the world's own rules call allies of the anchor");
        check(ObservedSpaceNodes.Filter(session, everything, self.Ref, relations, RecipientFilterRequest.Read("hostile"))
                .SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { far.Ref, corner.Ref })),
            "filter: hostile keeps the candidates the world's own rules call hostile");
        check(ObservedSpaceNodes.Filter(session, everything, self.Ref, relations, RecipientFilterRequest.Read("unknown")).Count == 0,
            "filter: a relation the world does not declare for the pair is not invented");
        check(ObservedSpaceNodes.Filter(session, new[] { self.Ref, near.Ref, up.Ref }, near.Ref, relations,
                RecipientFilterRequest.Read("ally")).SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { self.Ref, up.Ref })),
            "filter: a different anchor measures the same candidates against itself");
        check(ObservedSpaceNodes.Filter(session, new[] { far.Ref }, near.Ref, relations,
                RecipientFilterRequest.Read("self")).Count == 0,
            "filter: an anchor the candidates do not contain is observed but never selected");
        check(ObservedSpaceNodes.Filter(session, Array.Empty<EntityReference>(), self.Ref, relations,
                RecipientFilterRequest.Read("ally")).Count == 0,
            "filter: an empty explicit candidate list answers empty without inventing a candidate");
        Reject("filter", "recipient-relation", () => RecipientFilterRequest.Read("friend"));
        Reject("filter", "recipient-relation", () => ObservedSpaceNodes.Filter(session, everything, self.Ref, relations,
            RecipientFilterRequest.Read("owner")));

        // partition splits on one observed field; both sides are ordered and together are exactly the input.
        var faction = ObservedSpaceNodes.PartitionOn(session, everything, "faction", "red");
        check(faction.Matched.SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { far.Ref, corner.Ref }))
            && faction.Rest.SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { self.Ref, near.Ref, up.Ref })),
            "partition: the faction field splits the candidates into two ordered, disjoint sides");
        var tag = ObservedSpaceNodes.PartitionOn(session, everything, "tag", "corner");
        check(tag.Matched.SequenceEqual(new[] { corner.Ref }) && tag.Rest.Count == 4,
            "partition: a tag key matches against the snapshot's own tags");
        var kind = ObservedSpaceNodes.PartitionOn(session, everything, "kind", "enemy");
        check(kind.Matched.Count == 5 && kind.Rest.Count == 0,
            "partition: a field every candidate has answers the whole set as matched");
        var identity = ObservedSpaceNodes.PartitionOn(session, everything, "identity", up.Ref.Id);
        check(identity.Matched.SequenceEqual(new[] { up.Ref }) && identity.Rest.Count == 4,
            "partition: the identity field matches one reference's own id and nothing else");
        var receiver = ObservedSpaceNodes.PartitionOn(session, everything, "receiver", "test.receiver.health");
        check(receiver.Matched.Count == 5 && receiver.Rest.Count == 0,
            "partition: the receiver field matches one of a target's own labels");
        var nothing = ObservedSpaceNodes.PartitionOn(session, everything, "tag", "nothing");
        check(nothing.Matched.Count == 0 && nothing.Rest.Count == 5,
            "partition: a key nothing matches leaves one side empty without dropping the other");
        // A field the catalog does not declare is refused by name: answering every target as "rest" would make a
        // misspelled field look like a world in which nothing matched.
        Reject("partition", "partition-key", () => ObservedSpaceNodes.PartitionOn(session, everything, "team", "red"));
        Reject("partition", "partition-key", () => ObservedSpaceNodes.PartitionOn(session, everything, "unknown-field", "x"));

        // The zone filter keeps the candidates the provider places in the named zone and nothing else. Zone
        // membership is read per candidate through the same session every other row reads, and a candidate the
        // provider cannot place is a refusal rather than a candidate that silently fell outside.
        var blueZone = RuntimeZones.Reference(1, 0, 0, 1);
        var redZone = RuntimeZones.Reference(1, 0, 0, 2);
        world.Zones[self.Ref.Id] = blueZone; world.Zones[near.Ref.Id] = blueZone;
        world.Zones[far.Ref.Id] = redZone; world.Zones[up.Ref.Id] = blueZone;
        world.Zones[corner.Ref.Id] = redZone;
        check(ObservedSpaceNodes.ZoneMembers(session, everything, blueZone)
                .SequenceEqual(ObservedSpaceNodes.IdentityOrder(new[] { self.Ref, near.Ref, up.Ref })),
            "zone: the filter answers exactly the candidates the provider places in the named zone, in identity order");
        check(ObservedSpaceNodes.ZoneMembers(session, everything, redZone).Count == 2
            && ObservedSpaceNodes.ZoneMembers(session, everything, RuntimeZones.Reference(1, 0, 0, 9)).Count == 0,
            "zone: another zone answers its own members, and a zone nothing stands in answers empty");
        check(ObservedSpaceNodes.ZoneMembers(session, Array.Empty<EntityReference>(), blueZone).Count == 0,
            "zone: an empty explicit candidate list answers empty instead of the world");
        var before = world.ZoneReads;
        _ = ObservedSpaceNodes.ZoneMembers(session, everything, blueZone);
        check(world.ZoneReads - before == everything.Length,
            "zone: one zone read per candidate, never a second read of the same entity");
        // A candidate the owning provider cannot place is refused: a null answer would mean the entity stands
        // outside every zone, and this fixture's provider says "cannot place" by refusing it by name instead.
        world.Zones.Remove(corner.Ref.Id);
        Reject("zone", "entity-zone-unknown", () => ObservedSpaceNodes.ZoneMembers(session, everything, blueZone));
        world.Zones[corner.Ref.Id] = redZone;
        // A kind whose owner never registered a zone responder is unavailable, not "outside every zone".
        using (var unzoned = new ObservationWorld(new[] { self, near }, zones: false))
        {
            var unzonedSession = unzoned.Session();
            Reject("zone", "entity-zone-unavailable",
                () => ObservedSpaceNodes.ZoneMembers(unzonedSession, new[] { self.Ref, near.Ref }, blueZone));
        }

        // The shared read is one budgeted call, and an incomplete answer is never a short set: one candidate the
        // kernel could not observe refuses the whole selection instead of answering with the rest.
        Reject("observe", "entity-query-incomplete", () => ObservedSpaceNodes.Observe(session,
            new[] { near.Ref, up.Ref, corner.Ref with { LifeEpoch = 9 } }));
        Reject("observe", "entity-query-incomplete", () => ObservedSpaceNodes.Nearest(session,
            new[] { near.Ref, up.Ref, corner.Ref with { LifeEpoch = 9 } }, origin, 3));
        world.OnObserve = _ => null;
        Reject("observe", "entity-query-incomplete",
            () => ObservedSpatialNodes.Overlap(session, everything, origin, ObservedVolumeShape.Sphere, 4, 8));
        world.OnObserve = null;
        world.Entities[up.Ref.Id] = At("up", 0, 3, 0, "blue", life: 2);
        Reject("observe", "entity-query-incomplete", () => ObservedSpaceNodes.Chain(session, everything, self.Ref, 1, 10));
        Reject("observe", "entity-query-budget", () => ObservedSpaceNodes.Observe(session,
            Enumerable.Repeat(near.Ref, RuntimeKernel.MaximumEntityReferencesPerQuery + 1).ToArray()));

        check(world.Kernel.QueuedEvents == 0 && world.Kernel.LoadedPlans == 0, "no selection row schedules work");
        check(ReferenceEquals(ObservedSpaceNodes.Nodes.Single(node => node.HandlerName == "trigger.selector.filter").Evaluate,
                ObservedSpaceNodes.Evaluators["trigger.selector.filter"])
            && ObservedSpaceNodes.Nodes.All(node => node.Shape == ObservedSpaceNodes.Shapes[node.HandlerName]),
            "the published handler and shape tables are the declaration table's own");
        check(ObservedSpaceNodes.Nodes.Select(node => node.CapabilityId).Distinct(StringComparer.Ordinal).Count() == 8
            && ObservedSpaceNodes.Nodes.Select(node => node.HandlerName).Distinct(StringComparer.Ordinal).Count() == 8
            && ObservedSpaceNodes.Nodes.All(node => node.CapabilityId != "forge.selector.target.priority"),
            "the batch declares eight distinct rows and never advertises priority");
    }

    private static RuntimeEntitySnapshot At(string id, double x, double y, double z, string faction, long life = 1)
        => new(new EntityReference("test.space:" + id, 1, life), "enemy", faction, "alive",
            new[] { id }, new[] { "test.receiver.health" }, new[] { x, y, z });
}
