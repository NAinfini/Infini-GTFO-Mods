using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeMap;

// G0-G6 static checks in the contract's rejection order (FORGE-FRAMEWORK.md section 3.2, codes #1..#34):
// descriptor.* first, then assembly.schema/unknown-field, then G0-G6 one rule at a time, first error wins.
// A legal plan returns deduplicated, ordinal-sorted blockers instead; G5 never fails without collider
// evidence, and v1 cannot read LG_Dimension bounds, so every legal plan carries `dimension-bounds-unknown`.
public static class AssemblyPlanChecks
{
    private static void Fail(string code, string path) => throw new AssemblyPlanException("assembly." + code, path);
    private static void Need(bool ok, string code, string path) { if (!ok) Fail(code, path); }

    // The descriptor document is validated before the plan document, exactly like the Python checker, because
    // it is check #1 of the rejection order.
    public static AssemblyPlanValidation Validate(JsonElement planJson, byte[] descriptorsBytes)
    {
        DescriptorDocument descriptors;
        try
        {
            using var document = JsonDocument.Parse(descriptorsBytes);
            descriptors = ResourceDescriptorReader.Read(document.RootElement, "$descriptors");
        }
        catch (DescriptorException error)
        {
            throw new AssemblyPlanException(error.Code, error.Path);
        }
        catch (JsonException)
        {
            throw new AssemblyPlanException("descriptor.schema", "$descriptors");
        }
        var plan = AssemblyPlanReader.Read(planJson);
        return new AssemblyPlanValidation(plan, Check(plan, descriptors, descriptorsBytes));
    }

    private static (long, long, long) ZoneKey(AssemblyZone zone) => (zone.Dimension, zone.Layer, zone.LocalIndex);
    private static (long, long, long) LocatorKey(AssemblyLocator locator) =>
        (locator.Dimension, locator.Layer, locator.LocalZoneIndex);

    private static int CompareKeys((long, long, long) left, (long, long, long) right) => left.CompareTo(right);

    private static int CompareOrdinal(string left, string right) => string.CompareOrdinal(left, right);

    private static double[] Rotate(double[] rotation, IReadOnlyList<double> vector)
    {
        double x = rotation[0], y = rotation[1], z = rotation[2], w = rotation[3];
        double vx = vector[0], vy = vector[1], vz = vector[2];
        var tx = 2.0 * (y * vz - z * vy);
        var ty = 2.0 * (z * vx - x * vz);
        var tz = 2.0 * (x * vy - y * vx);
        return new[]
        {
            vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx),
        };
    }

    private static double[] WorldPoint(AssemblyPlacement placement, double[] local)
    {
        var rotated = Rotate(placement.Transform.Rotation, local);
        return new[]
        {
            rotated[0] + placement.Transform.Position[0],
            rotated[1] + placement.Transform.Position[1],
            rotated[2] + placement.Transform.Position[2],
        };
    }

    private static double[] WorldDirection(AssemblyPlacement placement, IReadOnlyList<double> local) =>
        Rotate(placement.Transform.Rotation, local);

    private static double Norm(IReadOnlyList<double> vector) => Math.Sqrt(vector.Sum(c => c * c));

    public static IReadOnlyList<string> Check(AssemblyPlan plan, DescriptorDocument descriptors, byte[] descriptorsBytes)
    {
        // 4-6. planId / levelLayoutId / seed.
        Need(AssemblyPlanLimits.PlanId.IsMatch(plan.PlanId), "plan-id", "$.planId");
        Need(plan.LevelLayoutId >= 1 && plan.LevelLayoutId <= 4294967295L, "level-layout", "$.levelLayoutId");
        Need(plan.Seed >= 0 && plan.Seed <= 2147483647L, "seed", "$.seed");

        // 7. descriptor-lock: the plan pins the descriptor document by path and by file-byte sha256.
        Need(plan.Descriptors.Path == AssemblyPlanLimits.DescriptorsPath, "descriptor-lock", "$.descriptors.path");
        Need(plan.Descriptors.Sha256 == Convert.ToHexString(SHA256.HashData(descriptorsBytes)).ToLowerInvariant(),
            "descriptor-lock", "$.descriptors.sha256");

        // 8. limit: static schemaVersion 1 ceilings, never a per-plan budget.
        Need(plan.Zones.Count <= AssemblyPlanLimits.Zones, "limit", "$.zones");
        Need(plan.Placements.Count <= AssemblyPlanLimits.Placements, "limit", "$.placements");
        Need(plan.Pairs.Count <= AssemblyPlanLimits.Pairs, "limit", "$.pairs");
        Need(descriptors.Descriptors.Count <= AssemblyPlanLimits.Descriptors, "limit", "$descriptors.descriptors");
        var zoneCounts = new Dictionary<(long, long, long), int>();
        foreach (var placement in plan.Placements)
        {
            var key = LocatorKey(placement.Locator);
            zoneCounts[key] = zoneCounts.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        Need(zoneCounts.Values.All(count => count <= AssemblyPlanLimits.PerZonePlacements), "limit", "$.placements");

        // 9. order: canonical ordering makes the exported bytes reproducible.
        for (var index = 1; index < plan.Zones.Count; index++)
            Need(CompareKeys(ZoneKey(plan.Zones[index - 1]), ZoneKey(plan.Zones[index])) <= 0, "order", "$.zones");
        for (var index = 1; index < plan.Placements.Count; index++)
        {
            var previous = plan.Placements[index - 1];
            var current = plan.Placements[index];
            var left = LocatorKey(previous.Locator);
            var right = LocatorKey(current.Locator);
            Need(CompareKeys(left, right) < 0 || (left == right && CompareOrdinal(previous.PlacementId, current.PlacementId) <= 0),
                "order", "$.placements");
        }
        for (var index = 1; index < plan.Pairs.Count; index++)
            Need(CompareOrdinal(plan.Pairs[index - 1].PairId, plan.Pairs[index].PairId) <= 0, "order", "$.pairs");
        var locatorByPlacementId = new Dictionary<string, AssemblyLocator>();
        foreach (var placement in plan.Placements) locatorByPlacementId[placement.PlacementId] = placement.Locator;
        for (var index = 0; index < plan.Pairs.Count; index++)
        {
            var pair = plan.Pairs[index];
            if (!locatorByPlacementId.TryGetValue(pair.A.PlacementId, out var locatorA)
                || !locatorByPlacementId.TryGetValue(pair.B.PlacementId, out var locatorB)
                || LocatorKey(locatorA) != LocatorKey(locatorB)) continue; // cross-zone or dangling
            Need(CompareOrdinal(pair.A.PlacementId, pair.B.PlacementId) < 0
                 || (pair.A.PlacementId == pair.B.PlacementId && CompareOrdinal(pair.A.ConnectorId, pair.B.ConnectorId) <= 0),
                "order", $"$.pairs[{index}]");
        }

        // 10. zone: v1 accepts dimension 0 / layer 0 only, local indices are contiguous from 0, and each
        // non-zero zone names a strictly smaller parent.
        for (var index = 0; index < plan.Zones.Count; index++)
        {
            var zone = plan.Zones[index];
            Need(zone.Dimension == 0 && zone.Layer == 0, "zone", $"$.zones[{index}]");
        }
        Need(plan.Zones.Select(zone => zone.LocalIndex).OrderBy(value => value)
                .SequenceEqual(Enumerable.Range(0, plan.Zones.Count).Select(index => (long)index)), "zone", "$.zones");
        for (var index = 0; index < plan.Zones.Count; index++)
        {
            var zone = plan.Zones[index];
            var path = $"$.zones[{index}].parentLocalIndex";
            if (zone.LocalIndex == 0) Need(zone.ParentLocalIndex is null, "zone", path);
            else Need(zone.ParentLocalIndex is not null && zone.ParentLocalIndex >= 0 && zone.ParentLocalIndex < zone.LocalIndex,
                "zone", path);
        }

        // 11-12. placement-id / duplicate-placement.
        for (var index = 0; index < plan.Placements.Count; index++)
            Need(AssemblyPlanLimits.PlacementId.IsMatch(plan.Placements[index].PlacementId), "placement-id",
                $"$.placements[{index}].placementId");
        var placementIds = new HashSet<string>();
        for (var index = 0; index < plan.Placements.Count; index++)
        {
            Need(placementIds.Add(plan.Placements[index].PlacementId), "duplicate-placement", $"$.placements[{index}].placementId");
        }

        // 13-14. room-reference / room-descriptor: every placement names exactly one descriptor in the
        // document the plan locked. Revision is the website's content hash; G0 never recomputes it.
        for (var index = 0; index < plan.Placements.Count; index++)
        {
            var room = plan.Placements[index].Room;
            Need(AssemblyPlanLimits.RoomId.IsMatch(room.Id), "room-reference", $"$.placements[{index}].room.id");
            Need(AssemblyPlanLimits.RoomRevision.IsMatch(room.Revision), "room-reference", $"$.placements[{index}].room.revision");
        }
        var descriptorsByReference = new Dictionary<(string Id, string Revision), DescriptorEntry>();
        foreach (var descriptor in descriptors.Descriptors)
            descriptorsByReference[(descriptor.ReferenceId, descriptor.Revision)] = descriptor;
        for (var index = 0; index < plan.Placements.Count; index++)
        {
            var room = plan.Placements[index].Room;
            Need(descriptorsByReference.ContainsKey((room.Id, room.Revision)), "room-descriptor", $"$.placements[{index}].room");
        }

        // 15-16. locator / locator-unsupported.
        var declaredZones = plan.Zones.Select(ZoneKey).ToHashSet();
        for (var index = 0; index < plan.Placements.Count; index++)
        {
            var locator = plan.Placements[index].Locator;
            Need(locator.Dimension >= 0 && locator.Layer >= 0 && locator.LocalZoneIndex >= 0, "locator", $"$.placements[{index}].locator");
            Need(declaredZones.Contains(LocatorKey(locator)), "locator", $"$.placements[{index}].locator");
        }
        // Unreachable while check 10 already restricts every declared zone to dimension 0 / layer 0, which a
        // declared locator must match; kept because the contract pins the code and the order.
        for (var index = 0; index < plan.Placements.Count; index++)
        {
            var locator = plan.Placements[index].Locator;
            Need(locator.Dimension == 0 && locator.Layer == 0, "locator-unsupported", $"$.placements[{index}].locator");
        }

        // 17. float32: every TRS component is finite and exactly representable in float32.
        for (var index = 0; index < plan.Placements.Count; index++)
        {
            var transform = plan.Placements[index].Transform;
            var components = new (string Name, double[] Values)[]
            {
                ("position", transform.Position), ("rotation", transform.Rotation), ("scale", transform.Scale),
            };
            foreach (var (name, values) in components)
                Need(values.All(value => double.IsFinite(value) && (float)value == value), "float32",
                    $"$.placements[{index}].transform.{name}");
        }

        // 18-19. transform-rotation / transform-scale.
        for (var index = 0; index < plan.Placements.Count; index++)
        {
            var rotation = plan.Placements[index].Transform.Rotation.Select(NormalizeZero).ToArray();
            Need(AssemblyPlanLimits.CanonicalRotations.Any(canonical =>
                canonical.X == rotation[0] && canonical.Y == rotation[1] && canonical.Z == rotation[2] && canonical.W == rotation[3]),
                "transform-rotation", $"$.placements[{index}].transform.rotation");
        }
        for (var index = 0; index < plan.Placements.Count; index++)
        {
            var scale = plan.Placements[index].Transform.Scale.Select(NormalizeZero).ToArray();
            Need(scale[0] == 1.0 && scale[1] == 1.0 && scale[2] == 1.0, "transform-scale",
                $"$.placements[{index}].transform.scale");
        }

        // 20-21. entry / entry-transform: the entry placement exists, sits in zone 0 and is the identity
        // transform, because every other pose is written relative to the entry prefab root.
        var placementsById = new Dictionary<string, AssemblyPlacement>();
        foreach (var placement in plan.Placements) placementsById[placement.PlacementId] = placement;
        Need(placementsById.ContainsKey(plan.Entry.PlacementId), "entry", "$.entry.placementId");
        var entry = placementsById[plan.Entry.PlacementId];
        Need(entry.Locator.LocalZoneIndex == 0, "entry", "$.entry.placementId");
        var entryTransform = entry.Transform;
        Need(entryTransform.Position.Select(NormalizeZero).All(value => value == 0.0)
             && entryTransform.Rotation.Select(NormalizeZero).SequenceEqual(new[] { 0.0, 0.0, 0.0, 1.0 })
             && entryTransform.Scale.Select(NormalizeZero).All(value => value == 1.0),
            "entry-transform", "$.entry.placementId");

        // 22. zone-empty: a declared zone without a placement cannot be built.
        for (var index = 0; index < plan.Zones.Count; index++)
        {
            var key = ZoneKey(plan.Zones[index]);
            Need(plan.Placements.Any(placement => LocatorKey(placement.Locator) == key), "zone-empty", $"$.zones[{index}]");
        }

        // 23-24. pair-id / duplicate-pair.
        for (var index = 0; index < plan.Pairs.Count; index++)
            Need(AssemblyPlanLimits.PlacementId.IsMatch(plan.Pairs[index].PairId), "pair-id", $"$.pairs[{index}].pairId");
        var pairIds = new HashSet<string>();
        for (var index = 0; index < plan.Pairs.Count; index++)
            Need(pairIds.Add(plan.Pairs[index].PairId), "duplicate-pair", $"$.pairs[{index}].pairId");

        DescriptorConnector? ConnectorOf(AssemblyPlacement placement, string connectorId)
        {
            var descriptor = descriptorsByReference[(placement.Room.Id, placement.Room.Revision)];
            return descriptor.Connectors.FirstOrDefault(connector => connector.Id == connectorId);
        }

        // 25. connector-reference: both endpoints must resolve through the placement's room descriptor.
        for (var index = 0; index < plan.Pairs.Count; index++)
        {
            var pair = plan.Pairs[index];
            for (var side = 0; side < 2; side++)
            {
                var endpoint = side == 0 ? pair.A : pair.B;
                var path = $"$.pairs[{index}].{(side == 0 ? "a" : "b")}";
                Need(placementsById.ContainsKey(endpoint.PlacementId), "connector-reference", path + ".placementId");
                Need(ConnectorOf(placementsById[endpoint.PlacementId], endpoint.ConnectorId) is not null,
                    "connector-reference", path + ".connectorId");
            }
        }

        // 26. connector-type: only `plug` pairs across placements; `gate` is internal to a geomorph.
        for (var index = 0; index < plan.Pairs.Count; index++)
        {
            var pair = plan.Pairs[index];
            for (var side = 0; side < 2; side++)
            {
                var endpoint = side == 0 ? pair.A : pair.B;
                var connector = ConnectorOf(placementsById[endpoint.PlacementId], endpoint.ConnectorId)!;
                Need(connector.ExpanderType == "plug", "connector-type", $"$.pairs[{index}].{(side == 0 ? "a" : "b")}.connectorId");
            }
        }

        // 27. connector-self: a pair joins two different placements.
        for (var index = 0; index < plan.Pairs.Count; index++)
            Need(plan.Pairs[index].A.PlacementId != plan.Pairs[index].B.PlacementId, "connector-self", $"$.pairs[{index}]");

        // 28. connector-reused: one endpoint appears in at most one pair (unpaired plugs are capped natively).
        var usedEndpoints = new HashSet<(string PlacementId, string ConnectorId)>();
        for (var index = 0; index < plan.Pairs.Count; index++)
        {
            var pair = plan.Pairs[index];
            for (var side = 0; side < 2; side++)
            {
                var endpoint = side == 0 ? pair.A : pair.B;
                Need(usedEndpoints.Add((endpoint.PlacementId, endpoint.ConnectorId)), "connector-reused",
                    $"$.pairs[{index}].{(side == 0 ? "a" : "b")}");
            }
        }

        var zoneByKey = plan.Zones.ToDictionary(ZoneKey);
        AssemblyLocator ZoneOf(AssemblyPlacement placement) => placement.Locator;

        // 29. pair-direction: a cross-zone pair builds the child zone from its parent zone, so `a` is the
        // parent endpoint.
        for (var index = 0; index < plan.Pairs.Count; index++)
        {
            var pair = plan.Pairs[index];
            var zoneA = ZoneOf(placementsById[pair.A.PlacementId]);
            var zoneB = ZoneOf(placementsById[pair.B.PlacementId]);
            if (LocatorKey(zoneA) == LocatorKey(zoneB)) continue;
            Need(zoneByKey[LocatorKey(zoneB)].ParentLocalIndex == zoneA.LocalZoneIndex, "pair-direction", $"$.pairs[{index}]");
        }

        // 30. door: a cross-zone pair carries the door identity, an intra-zone pair carries none.
        for (var index = 0; index < plan.Pairs.Count; index++)
        {
            var pair = plan.Pairs[index];
            var sameZone = LocatorKey(ZoneOf(placementsById[pair.A.PlacementId])) == LocatorKey(ZoneOf(placementsById[pair.B.PlacementId]));
            var path = $"$.pairs[{index}].door";
            if (sameZone) Need(pair.Door is null, "door", path);
            else Need(pair.Door is not null, "door", path);
        }

        // 31-32. connector-orientation / connector-position: G4 geometry in the plan frame, double math over
        // float32 inputs.
        for (var index = 0; index < plan.Pairs.Count; index++)
        {
            var pair = plan.Pairs[index];
            var placementA = placementsById[pair.A.PlacementId];
            var placementB = placementsById[pair.B.PlacementId];
            var outwardA = WorldDirection(placementA, ConnectorOf(placementA, pair.A.ConnectorId)!.Outward);
            var outwardB = WorldDirection(placementB, ConnectorOf(placementB, pair.B.ConnectorId)!.Outward);
            var normA = Norm(outwardA);
            var normB = Norm(outwardB);
            if (normA == 0) normA = 1.0;
            if (normB == 0) normB = 1.0;
            var dot = outwardA.Zip(outwardB, (left, right) => (left / normA) * (right / normB)).Sum();
            Need(dot <= AssemblyPlanLimits.ConnectorOutwardDot, "connector-orientation", $"$.pairs[{index}]");
        }
        for (var index = 0; index < plan.Pairs.Count; index++)
        {
            var pair = plan.Pairs[index];
            var placementA = placementsById[pair.A.PlacementId];
            var placementB = placementsById[pair.B.PlacementId];
            var pointA = WorldPoint(placementA, ConnectorOf(placementA, pair.A.ConnectorId)!.Position);
            var pointB = WorldPoint(placementB, ConnectorOf(placementB, pair.B.ConnectorId)!.Position);
            var distance = Math.Sqrt(pointA.Zip(pointB, (left, right) => (left - right) * (left - right)).Sum());
            Need(distance <= AssemblyPlanLimits.ConnectorPositionTolerance, "connector-position", $"$.pairs[{index}]");
        }

        // 33. zone-link: every non-zero zone is built through exactly one pair with its parent zone.
        for (var index = 0; index < plan.Zones.Count; index++)
        {
            var zone = plan.Zones[index];
            if (zone.LocalIndex == 0) continue;
            var key = ZoneKey(zone);
            var links = plan.Pairs.Count(pair =>
                LocatorKey(ZoneOf(placementsById[pair.A.PlacementId])) != LocatorKey(ZoneOf(placementsById[pair.B.PlacementId]))
                && LocatorKey(ZoneOf(placementsById[pair.B.PlacementId])) == key);
            Need(links == 1, "zone-link", $"$.zones[{index}]");
        }

        // 34. zone-disconnected: placements of one zone are connected through intra-zone pairs.
        for (var index = 0; index < plan.Zones.Count; index++)
        {
            var key = ZoneKey(plan.Zones[index]);
            var members = plan.Placements.Where(placement => LocatorKey(ZoneOf(placement)) == key)
                .Select(placement => placement.PlacementId).ToList();
            if (members.Count <= 1) continue;
            var adjacency = members.ToDictionary(member => member, _ => new HashSet<string>());
            foreach (var pair in plan.Pairs)
            {
                if (LocatorKey(ZoneOf(placementsById[pair.A.PlacementId])) != key
                    || LocatorKey(ZoneOf(placementsById[pair.B.PlacementId])) != key) continue;
                adjacency[pair.A.PlacementId].Add(pair.B.PlacementId);
                adjacency[pair.B.PlacementId].Add(pair.A.PlacementId);
            }
            var visited = new HashSet<string> { members[0] };
            var frontier = new Stack<string>();
            frontier.Push(members[0]);
            while (frontier.Count > 0)
            {
                foreach (var neighbour in adjacency[frontier.Pop()])
                    if (visited.Add(neighbour)) frontier.Push(neighbour);
            }
            Need(visited.Count == members.Count, "zone-disconnected", $"$.zones[{index}]");
        }

        var blockers = new SortedSet<string>(StringComparer.Ordinal) { "dimension-bounds-unknown" };
        foreach (var placement in plan.Placements)
        {
            var descriptor = descriptorsByReference[(placement.Room.Id, placement.Room.Revision)];
            foreach (var blocker in descriptor.Blockers) blockers.Add(blocker);
        }
        return blockers.ToList();
    }

    private static double NormalizeZero(double value) => value == 0 ? 0.0 : value;
}
