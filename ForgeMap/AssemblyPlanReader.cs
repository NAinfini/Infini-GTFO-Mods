using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

// Strict G0 plan parsing: every field is required, unknown fields are rejected, and an integer field must be
// a JSON integer (a literal `1.0` is rejected here, exactly like the Python and TS checkers). This mirrors the
// shape phase (codes `assembly.schema` / `assembly.unknown-field`) of
// ForgeMap/tools/verify_assembly_plan_fixtures.py: top-down, left-to-right, arrays by index, no value check
// before the whole shape is known good.
public static class AssemblyPlanReader
{
    private static void Fail(string code, string path) => throw new AssemblyPlanException("assembly." + code, path);
    private static void Need(bool ok, string code, string path) { if (!ok) Fail(code, path); }

    private static Dictionary<string, JsonElement> Fields(JsonElement value, IReadOnlyCollection<string> allowed, string path)
    {
        Need(value.ValueKind == JsonValueKind.Object, "schema", path + " must be an object");
        var seen = new Dictionary<string, JsonElement>();
        var unknown = new List<string>();
        foreach (var property in value.EnumerateObject())
        {
            if (allowed.Contains(property.Name)) seen[property.Name] = property.Value;
            else unknown.Add(property.Name);
        }
        Need(unknown.Count == 0, "unknown-field", path + ": " + string.Join(", ", unknown.OrderBy(s => s, StringComparer.Ordinal)));
        var missing = allowed.Where(a => !seen.ContainsKey(a)).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Need(missing.Count == 0, "schema", path + " missing " + string.Join(", ", missing));
        return seen;
    }

    // JSON integers only: `TryGetInt64` is false for 1.0, 1e2 and out-of-range literals. The Python checker
    // holds arbitrary-precision ints, so a literal beyond int64 fails shape here where Python fails a range
    // check later; both reject it, and no canonical website document can contain one.
    private static long Integer(JsonElement value, string path)
    {
        Need(value.ValueKind == JsonValueKind.Number, "schema", path);
        Need(value.TryGetInt64(out var result), "schema", path);
        return result;
    }

    private static string Text(JsonElement value, string path)
    {
        Need(value.ValueKind == JsonValueKind.String, "schema", path);
        return value.GetString()!;
    }

    private static double[] NumberVector(JsonElement value, int size, string path)
    {
        var ok = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == size;
        var numbers = new double[size];
        if (ok)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number) { ok = false; break; }
                numbers[index++] = item.GetDouble();
            }
        }
        Need(ok, "schema", path);
        return numbers;
    }

    public static AssemblyPlan Read(JsonElement value)
    {
        var plan = Fields(value, new[] { "schemaVersion", "kind", "planId", "levelLayoutId", "seed", "descriptors",
            "zones", "entry", "placements", "pairs" }, "$");
        Need(plan["kind"].ValueKind == JsonValueKind.String && plan["kind"].GetString() == AssemblyPlanLimits.Kind
             && plan["schemaVersion"].ValueKind == JsonValueKind.Number
             && plan["schemaVersion"].GetDouble() == AssemblyPlanLimits.SchemaVersion, "schema", "$.kind");
        var planId = Text(plan["planId"], "$.planId");
        var levelLayoutId = Integer(plan["levelLayoutId"], "$.levelLayoutId");
        var seed = Integer(plan["seed"], "$.seed");

        var descriptors = Fields(plan["descriptors"], new[] { "path", "sha256" }, "$.descriptors");
        var descriptorsPath = Text(descriptors["path"], "$.descriptors.path");
        var descriptorsSha = Text(descriptors["sha256"], "$.descriptors.sha256");

        var zonesElement = plan["zones"];
        Need(zonesElement.ValueKind == JsonValueKind.Array, "schema", "$.zones");
        var zones = new List<AssemblyZone>();
        var zoneIndex = 0;
        foreach (var zone in zonesElement.EnumerateArray())
        {
            var path = $"$.zones[{zoneIndex}]";
            var fields = Fields(zone, new[] { "dimension", "layer", "localIndex", "parentLocalIndex" }, path);
            var dimension = Integer(fields["dimension"], path + ".dimension");
            var layer = Integer(fields["layer"], path + ".layer");
            var localIndex = Integer(fields["localIndex"], path + ".localIndex");
            var parentElement = fields["parentLocalIndex"];
            Need(parentElement.ValueKind == JsonValueKind.Null
                 || (parentElement.ValueKind == JsonValueKind.Number && parentElement.TryGetInt64(out _)),
                "schema", path + ".parentLocalIndex");
            var parent = parentElement.ValueKind == JsonValueKind.Number ? parentElement.GetInt64() : (long?)null;
            zones.Add(new AssemblyZone(dimension, layer, localIndex, parent));
            zoneIndex++;
        }

        var entryFields = Fields(plan["entry"], new[] { "placementId" }, "$.entry");
        var entry = new AssemblyEntry(Text(entryFields["placementId"], "$.entry.placementId"));

        var placementsElement = plan["placements"];
        Need(placementsElement.ValueKind == JsonValueKind.Array, "schema", "$.placements");
        var placements = new List<AssemblyPlacement>();
        var placementIndex = 0;
        foreach (var placement in placementsElement.EnumerateArray())
        {
            var path = $"$.placements[{placementIndex}]";
            var fields = Fields(placement, new[] { "placementId", "room", "locator", "transform" }, path);
            var placementId = Text(fields["placementId"], path + ".placementId");
            var room = Fields(fields["room"], new[] { "id", "revision" }, path + ".room");
            var roomId = Text(room["id"], path + ".room.id");
            var revision = Text(room["revision"], path + ".room.revision");
            var locator = Fields(fields["locator"], new[] { "dimension", "layer", "localZoneIndex" }, path + ".locator");
            var dimension = Integer(locator["dimension"], path + ".locator.dimension");
            var layer = Integer(locator["layer"], path + ".locator.layer");
            var localZoneIndex = Integer(locator["localZoneIndex"], path + ".locator.localZoneIndex");
            var transform = Fields(fields["transform"], new[] { "position", "rotation", "scale" }, path + ".transform");
            var position = NumberVector(transform["position"], 3, path + ".transform.position");
            var rotation = NumberVector(transform["rotation"], 4, path + ".transform.rotation");
            var scale = NumberVector(transform["scale"], 3, path + ".transform.scale");
            placements.Add(new AssemblyPlacement(placementId, new AssemblyRoomReference(roomId, revision),
                new AssemblyLocator(dimension, layer, localZoneIndex), new AssemblyTransform(position, rotation, scale)));
            placementIndex++;
        }

        var pairsElement = plan["pairs"];
        Need(pairsElement.ValueKind == JsonValueKind.Array, "schema", "$.pairs");
        var pairs = new List<AssemblyPair>();
        var pairIndex = 0;
        foreach (var pair in pairsElement.EnumerateArray())
        {
            var path = $"$.pairs[{pairIndex}]";
            var fields = Fields(pair, new[] { "pairId", "a", "b", "door" }, path);
            var pairId = Text(fields["pairId"], path + ".pairId");
            var endpoints = new AssemblyEndpoint[2];
            for (var side = 0; side < 2; side++)
            {
                var sideName = side == 0 ? "a" : "b";
                var sideFields = Fields(fields[sideName], new[] { "placementId", "connectorId" }, $"{path}.{sideName}");
                endpoints[side] = new AssemblyEndpoint(
                    Text(sideFields["placementId"], $"{path}.{sideName}.placementId"),
                    Text(sideFields["connectorId"], $"{path}.{sideName}.connectorId"));
            }
            AssemblyDoorReference? door = null;
            var doorValue = fields["door"];
            Need(doorValue.ValueKind == JsonValueKind.Null || doorValue.ValueKind == JsonValueKind.Object, "schema", path + ".door");
            if (doorValue.ValueKind == JsonValueKind.Object)
            {
                var doorFields = Fields(doorValue, new[] { "doorId" }, path + ".door");
                var doorId = Text(doorFields["doorId"], path + ".door.doorId");
                Need(doorId.Length > 0, "schema", path + ".door.doorId");
                door = new AssemblyDoorReference(doorId);
            }
            pairs.Add(new AssemblyPair(pairId, endpoints[0], endpoints[1], door));
            pairIndex++;
        }

        return new AssemblyPlan(planId, levelLayoutId, seed, new AssemblyDescriptorsLock(descriptorsPath, descriptorsSha),
            zones, entry, placements, pairs);
    }
}
