using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeMap;

// G0 assembly-plan checker driver.
//
//   --fixtures <dir>  run the website fixture corpus (MANIFEST.json sha256, then cases.json by group)
//   --self-check      prove every rejection rule of FORGE-FRAMEWORK.md section 3.2 can be detected, using
//                     single-point mutations of a synthetic plan authored here (no website fixture is copied)
//   --emit <dir>      optional with --self-check: write the exact mutated documents so an external checker
//                     (ForgeMap/tools/verify_assembly_plan_fixtures.py) can be run over the same corpus
//
// A valid plan is not a generation success: G0-G6 passing returns blockers, never a built map.

// ---------------------------------------------------------------- synthetic corpus

const string RevisionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string RevisionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
const string RevisionC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
const string EvidenceA = "1111111111111111111111111111111111111111111111111111111111111111";
const string EvidenceB = "2222222222222222222222222222222222222222222222222222222222222222";
const string EvidenceC = "3333333333333333333333333333333333333333333333333333333333333333";

static string DescriptorJson(string room, string revision, string evidenceSha, string connectors) => $$"""
{
  "reference": { "id": "forge.native.room:{{room}}", "revision": "{{revision}}" },
  "adapter": { "profile": "gtfo.complex-resource-geomorph", "profileRevision": 1 },
  "source": {
    "kind": "native",
    "sourceId": "{{room}}",
    "contentSha256": "{{revision}}",
    "evidence": { "path": "site/preview/map-rooms/{{room}}-0123456789ab.json", "sha256": "{{evidenceSha}}" },
    "assetPath": "Assets/Rooms/{{room}}.prefab",
    "object": { "file": "sharedassets1.assets", "pathId": "100" }
  },
  "authorization": { "reviewRef": "map-room-review:{{room}}", "runtimeUse": "game-content", "redistributeBytes": false, "dependency": null },
  "runtimeLocator": { "loadedBy": "complex-resource-set", "assetPath": "Assets/Rooms/{{room}}.prefab", "requiresLoaded": true },
  "hierarchy": {
    "space": "unity-left-handed-meters",
    "nodes": [
      { "id": "sharedassets1.assets:100", "parent": null, "name": "Root",
        "source": { "file": "sharedassets1.assets", "pathId": "100" },
        "gameObject": { "file": "sharedassets1.assets", "pathId": "200" },
        "local": { "position": [0, 0, 0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] },
        "active": true, "meshes": [] }
    ]
  },
  "areas": [ { "id": "Area_{{room}}", "node": "sharedassets1.assets:100" } ],
  "connectors": [{{connectors}}],
  "colliders": { "status": "unknown", "reason": "static extraction does not reconstruct colliders", "items": [] },
  "navigation": { "status": "unknown", "reason": "static extraction does not reconstruct navigation", "items": [] },
  "occlusion": { "status": "unknown", "reason": "static extraction does not reconstruct occlusion portals", "items": [] }
}
""";

static string ConnectorJson(string id, string area, string expanderType, int x, int y, int z, int ox, int oy, int oz) => $$"""
{ "id": "{{id}}", "node": "sharedassets1.assets:100", "area": "Area_{{area}}", "expanderType": "{{expanderType}}",
  "position": [{{x}}, {{y}}, {{z}}], "outward": [{{ox}}, {{oy}}, {{oz}}], "doubleSided": false }
""";

static JsonObject BaselineDescriptors()
{
    var roomA = DescriptorJson("room-a", RevisionA, EvidenceA, string.Join(",\n    ", new[]
    {
        ConnectorJson("plug-a-out", "room-a", "plug", 0, 0, 10, 0, 0, 1),
        ConnectorJson("plug-a-side", "room-a", "plug", 10, 0, 0, 1, 0, 0),
    }));
    var roomB = DescriptorJson("room-b", RevisionB, EvidenceB, string.Join(",\n    ", new[]
    {
        ConnectorJson("plug-b-in", "room-b", "plug", 0, 0, -10, 0, 0, -1),
        ConnectorJson("plug-b-top", "room-b", "plug", 0, 10, 0, 0, 1, 0),
    }));
    var roomC = DescriptorJson("room-c", RevisionC, EvidenceC,
        ConnectorJson("plug-c-bottom", "room-c", "plug", 0, -10, 0, 0, -1, 0));
    return (JsonObject)JsonNode.Parse($$"""
{
  "schemaVersion": 1,
  "kind": "forge-map-resource-descriptors",
  "evidence": "fixture-synthetic",
  "sharedBytes": [],
  "descriptors": [{{roomA}}, {{roomB}}, {{roomC}}]
}
""")!;
}

// Two zones, three placements and two pairs: entry (room-a) meets peer (room-b) through an intra-zone pair
// at [0,0,10], and peer's top plug meets the child (room-c) through the cross-zone pair at [0,10,20]. Every
// rotation is identity so the mutation cases stay single-point.
const string BaselinePlanJson = """
{
  "schemaVersion": 1,
  "kind": "forge-map-assembly-plan",
  "planId": "expedition-alpha",
  "levelLayoutId": 3788602088,
  "seed": 12345,
  "descriptors": { "path": "forge/maps/rooms.descriptors.json", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" },
  "zones": [
    { "dimension": 0, "layer": 0, "localIndex": 0, "parentLocalIndex": null },
    { "dimension": 0, "layer": 0, "localIndex": 1, "parentLocalIndex": 0 }
  ],
  "entry": { "placementId": "room:entry" },
  "placements": [
    { "placementId": "room:entry", "room": { "id": "forge.native.room:room-a", "revision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
      "locator": { "dimension": 0, "layer": 0, "localZoneIndex": 0 },
      "transform": { "position": [0, 0, 0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] } },
    { "placementId": "room:peer", "room": { "id": "forge.native.room:room-b", "revision": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
      "locator": { "dimension": 0, "layer": 0, "localZoneIndex": 0 },
      "transform": { "position": [0, 0, 20], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] } },
    { "placementId": "room:child", "room": { "id": "forge.native.room:room-c", "revision": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" },
      "locator": { "dimension": 0, "layer": 0, "localZoneIndex": 1 },
      "transform": { "position": [0, 20, 20], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] } }
  ],
  "pairs": [
    { "pairId": "connection:cross", "a": { "placementId": "room:peer", "connectorId": "plug-b-top" },
      "b": { "placementId": "room:child", "connectorId": "plug-c-bottom" }, "door": { "doorId": "door:cross" } },
    { "pairId": "connection:inner", "a": { "placementId": "room:entry", "connectorId": "plug-a-out" },
      "b": { "placementId": "room:peer", "connectorId": "plug-b-in" }, "door": null }
  ]
}
""";

static JsonObject BaselinePlan() => (JsonObject)JsonNode.Parse(BaselinePlanJson)!;
static JsonObject Obj(JsonNode? node) => (JsonObject)node!;
static JsonArray Arr(JsonNode? node) => (JsonArray)node!;
static JsonObject At(JsonNode? node, int index) => (JsonObject)Arr(node)[index]!;
static void RemoveAt(JsonNode? node, int index) => Arr(node).RemoveAt(index);
static void SetNull(JsonNode? parent, string name) => Obj(parent)[name] = JsonNode.Parse("null");
static JsonNode Clone(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;
static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static JsonObject ExtraPlacement(string placementId, string revision, int localZoneIndex) => (JsonObject)JsonNode.Parse($$"""
{ "placementId": "{{placementId}}", "room": { "id": "forge.native.room:{{(localZoneIndex == 0 ? "room-a" : "room-c")}}", "revision": "{{revision}}" },
  "locator": { "dimension": 0, "layer": 0, "localZoneIndex": {{localZoneIndex}} },
  "transform": { "position": [0, 40, 20], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] } }
""")!;

static string LockedDescriptorsJson() => BaselineDescriptors().ToJsonString();

static string LockedPlanJson()
{
    var descriptors = Encoding.UTF8.GetBytes(LockedDescriptorsJson());
    var plan = BaselinePlan();
    ((JsonObject)plan["descriptors"]!)["sha256"] = Sha256Hex(descriptors);
    return plan.ToJsonString();
}

// Single-point mutations of the baseline above. Each one must produce exactly the listed code and path; no
// mutation touches an earlier rule, so the first error stays the one under test.
var PlanCases = new PlanCase[]
{
    new("baseline-accepted", null, null, true, (_, _) => { },
        new[] { "colliders", "dimension-bounds-unknown", "navigation", "occlusion" }),
    new("descriptor-delegated-revision", "descriptor.revision-mismatch",
        "$descriptors.descriptors[0].reference.revision", true,
        (_, descriptors) => Obj(At(descriptors["descriptors"], 0)["reference"])["revision"] = new string('f', 64), null),
    new("descriptor-document-evidence", "descriptor.evidence-escalation", "$descriptors.evidence", true,
        (_, descriptors) => descriptors["evidence"] = "game-verified", null),
    new("descriptor-document-unparsable", "descriptor.schema", "$descriptors", true, (_, _) => { }, null,
        "{ not json"),
    new("schema-seed-type", "assembly.schema", "$.seed", true, (plan, _) => plan["seed"] = "12345", null),
    // The path of an unknown-field error carries the offending names, like the Python checker's
    // "$: budget" detail; the website fixture corpus must spell expectedError.path the same way.
    new("unknown-field", "assembly.unknown-field", "$: budget", true, (plan, _) => plan["budget"] = 10, null),
    new("plan-id", "assembly.plan-id", "$.planId", true, (plan, _) => plan["planId"] = "1bad id", null),
    new("level-layout", "assembly.level-layout", "$.levelLayoutId", true, (plan, _) => plan["levelLayoutId"] = 0, null),
    new("seed", "assembly.seed", "$.seed", true, (plan, _) => plan["seed"] = 2147483648L, null),
    new("descriptor-lock-path", "assembly.descriptor-lock", "$.descriptors.path", true,
        (plan, _) => Obj(plan["descriptors"])["path"] = "forge/maps/other.descriptors.json", null),
    new("descriptor-lock-sha256", "assembly.descriptor-lock", "$.descriptors.sha256", false,
        (plan, _) => Obj(plan["descriptors"])["sha256"] = new string('0', 64), null),
    new("limit-zones", "assembly.limit", "$.zones", true, (plan, _) =>
    {
        for (var index = 0; index < 64; index++) Arr(plan["zones"]).Add(Clone(At(plan["zones"], 1)));
    }, null),
    new("limit-zone-placements", "assembly.limit", "$.placements", true, (plan, _) =>
    {
        for (var index = 0; index < 31; index++) Arr(plan["placements"]).Add(ExtraPlacement("room:extra" + index, RevisionA, 0));
    }, null),
    new("limit-descriptors", "assembly.limit", "$descriptors.descriptors", true, (_, descriptors) =>
    {
        for (var index = 0; index < 255; index++)
        {
            var clone = Obj(Clone(At(descriptors["descriptors"], 0)));
            Obj(clone["reference"])["id"] = "forge.native.room:extra-" + index;
            Arr(descriptors["descriptors"]).Add(clone);
        }
    }, null),
    new("order-zones", "assembly.order", "$.zones", true, (plan, _) =>
    {
        var first = Clone(At(plan["zones"], 0));
        Arr(plan["zones"])[0] = Clone(At(plan["zones"], 1));
        Arr(plan["zones"])[1] = first;
    }, null),
    new("order-placements", "assembly.order", "$.placements", true, (plan, _) =>
    {
        var first = Clone(At(plan["placements"], 0));
        Arr(plan["placements"])[0] = Clone(At(plan["placements"], 1));
        Arr(plan["placements"])[1] = first;
    }, null),
    new("order-pairs", "assembly.order", "$.pairs", true, (plan, _) =>
    {
        var first = Clone(At(plan["pairs"], 0));
        Arr(plan["pairs"])[0] = Clone(At(plan["pairs"], 1));
        Arr(plan["pairs"])[1] = first;
    }, null),
    new("order-pair-endpoints", "assembly.order", "$.pairs[1]", true, (plan, _) =>
    {
        var pair = At(plan["pairs"], 1);
        var first = Clone(pair["a"]!);
        pair["a"] = Clone(pair["b"]!);
        pair["b"] = first;
    }, null),
    new("zone-parent-local-index", "assembly.zone", "$.zones[1].parentLocalIndex", true,
        (plan, _) => At(plan["zones"], 1)["parentLocalIndex"] = 5, null),
    new("zone-dimension", "assembly.zone", "$.zones[1]", true, (plan, _) => At(plan["zones"], 1)["dimension"] = 1, null),
    new("placement-id", "assembly.placement-id", "$.placements[0].placementId", true,
        (plan, _) => At(plan["placements"], 0)["placementId"] = "room:entry!", null),
    new("duplicate-placement", "assembly.duplicate-placement", "$.placements[1].placementId", true,
        (plan, _) => At(plan["placements"], 1)["placementId"] = "room:entry", null),
    new("room-reference", "assembly.room-reference", "$.placements[0].room.id", true,
        (plan, _) => Obj(At(plan["placements"], 0)["room"])["id"] = "room-a", null),
    new("room-descriptor", "assembly.room-descriptor", "$.placements[0].room", true,
        (plan, _) => Obj(At(plan["placements"], 0)["room"])["revision"] = new string('f', 64), null),
    new("locator", "assembly.locator", "$.placements[2].locator", true,
        (plan, _) => Obj(At(plan["placements"], 2)["locator"])["localZoneIndex"] = 2, null),
    // assembly.locator-unsupported (#16) cannot be reached: check 10 already rejects any declared zone with
    // dimension != 0 or layer != 0, and check 15 already rejects a locator no zone declares, while a declared
    // locator must match its zone. This case pins that order; the report flags the unreachable code.
    new("locator-unsupported-is-preceded-by-zone", "assembly.zone", "$.zones[1]", true, (plan, _) =>
    {
        At(plan["zones"], 1)["layer"] = 1;
        Obj(At(plan["placements"], 2)["locator"])["layer"] = 1;
    }, null),
    new("float32", "assembly.float32", "$.placements[0].transform.position", true,
        (plan, _) => Arr(Obj(At(plan["placements"], 0)["transform"])["position"])[0] = 0.1, null),
    new("transform-rotation", "assembly.transform-rotation", "$.placements[1].transform.rotation", true,
        (plan, _) => Obj(At(plan["placements"], 1)["transform"])["rotation"] = new JsonArray(1, 0, 0, 0), null),
    new("transform-scale", "assembly.transform-scale", "$.placements[1].transform.scale", true,
        (plan, _) => Obj(At(plan["placements"], 1)["transform"])["scale"] = new JsonArray(2, 1, 1), null),
    new("entry", "assembly.entry", "$.entry.placementId", true,
        (plan, _) => Obj(plan["entry"])["placementId"] = "room:child", null),
    new("entry-transform", "assembly.entry-transform", "$.entry.placementId", true,
        (plan, _) => Obj(At(plan["placements"], 0)["transform"])["position"] = new JsonArray(0, 0, 1), null),
    new("zone-empty", "assembly.zone-empty", "$.zones[1]", true, (plan, _) => RemoveAt(plan["placements"], 2), null),
    new("pair-id", "assembly.pair-id", "$.pairs[0].pairId", true,
        (plan, _) => At(plan["pairs"], 0)["pairId"] = "bad id", null),
    new("duplicate-pair", "assembly.duplicate-pair", "$.pairs[1].pairId", true,
        (plan, _) => At(plan["pairs"], 1)["pairId"] = "connection:cross", null),
    new("connector-reference", "assembly.connector-reference", "$.pairs[0].a.connectorId", true,
        (plan, _) => Obj(At(plan["pairs"], 0)["a"])["connectorId"] = "plug-missing", null),
    new("connector-type", "assembly.connector-type", "$.pairs[1].a.connectorId", true, (plan, descriptors) =>
    {
        Arr(At(descriptors["descriptors"], 0)["connectors"]).Add(JsonNode.Parse(
            ConnectorJson("gate-a", "room-a", "gate", 0, 0, 10, 0, 0, 1))!);
        Obj(At(plan["pairs"], 1)["a"])["connectorId"] = "gate-a";
    }, null),
    new("connector-self", "assembly.connector-self", "$.pairs[1]", true, (plan, _) =>
    {
        Obj(At(plan["pairs"], 1)["b"])["placementId"] = "room:entry";
        Obj(At(plan["pairs"], 1)["b"])["connectorId"] = "plug-a-side";
    }, null),
    new("connector-reused", "assembly.connector-reused", "$.pairs[1].b", true, (plan, _) =>
    {
        Obj(At(plan["pairs"], 1)["b"])["placementId"] = "room:peer";
        Obj(At(plan["pairs"], 1)["b"])["connectorId"] = "plug-b-top";
    }, null),
    new("pair-direction", "assembly.pair-direction", "$.pairs[0]", true, (plan, _) =>
    {
        Obj(At(plan["pairs"], 0)["a"])["placementId"] = "room:child";
        Obj(At(plan["pairs"], 0)["a"])["connectorId"] = "plug-c-bottom";
        Obj(At(plan["pairs"], 0)["b"])["placementId"] = "room:peer";
        Obj(At(plan["pairs"], 0)["b"])["connectorId"] = "plug-b-top";
    }, null),
    new("door-cross-zone-missing", "assembly.door", "$.pairs[0].door", true,
        (plan, _) => SetNull(At(plan["pairs"], 0), "door"), null),
    new("door-intra-zone-present", "assembly.door", "$.pairs[1].door", true,
        (plan, _) => At(plan["pairs"], 1)["door"] = JsonNode.Parse("""{ "doorId": "door:inner" }"""), null),
    new("connector-orientation", "assembly.connector-orientation", "$.pairs[1]", true,
        (plan, _) => Obj(At(plan["placements"], 1)["transform"])["rotation"] = new JsonArray(0, 1, 0, 0), null),
    new("connector-position", "assembly.connector-position", "$.pairs[0]", true,
        (plan, _) => Obj(At(plan["placements"], 2)["transform"])["position"] = new JsonArray(0, 30, 20), null),
    new("zone-link", "assembly.zone-link", "$.zones[1]", true, (plan, _) => RemoveAt(plan["pairs"], 0), null),
    new("zone-disconnected", "assembly.zone-disconnected", "$.zones[1]", true,
        (plan, _) => Arr(plan["placements"]).Add(ExtraPlacement("room:child2", RevisionC, 1)), null),
};

var PackageCases = new PackageCase[]
{
    new("package-plan-file", "assembly.plan-file", "renamed.assembly.json", caseRoot =>
    {
        var maps = Path.Combine(caseRoot, "one", "forge", "maps");
        Directory.CreateDirectory(maps);
        File.WriteAllText(Path.Combine(maps, "renamed.assembly.json"), LockedPlanJson());
        File.WriteAllText(Path.Combine(maps, "rooms.descriptors.json"), LockedDescriptorsJson());
    }),
    new("package-duplicate-level-layout", "assembly.duplicate-level-layout", null, caseRoot =>
    {
        var maps = Path.Combine(caseRoot, "one", "forge", "maps");
        Directory.CreateDirectory(maps);
        var second = (JsonObject)JsonNode.Parse(LockedPlanJson())!;
        second["planId"] = "expedition-beta";
        File.WriteAllText(Path.Combine(maps, "expedition-alpha.assembly.json"), LockedPlanJson());
        File.WriteAllText(Path.Combine(maps, "expedition-beta.assembly.json"), second.ToJsonString());
        File.WriteAllText(Path.Combine(maps, "rooms.descriptors.json"), LockedDescriptorsJson());
    }),
    new("package-layout", "assembly.package-layout", null, caseRoot =>
    {
        foreach (var name in new[] { "one", "two" })
        {
            var maps = Path.Combine(caseRoot, name, "forge", "maps");
            Directory.CreateDirectory(maps);
            File.WriteAllText(Path.Combine(maps, "rooms.descriptors.json"), LockedDescriptorsJson());
        }
    }),
};

// ---------------------------------------------------------------- driver

string? fixtures = null;
string? emit = null;
var selfCheck = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--fixtures": fixtures = index + 1 < args.Length ? args[++index] : null; break;
        case "--self-check": selfCheck = true; break;
        case "--emit": emit = index + 1 < args.Length ? args[++index] : null; break;
        default: Console.Error.WriteLine("unknown argument: " + args[index]); return 2;
    }
}
if (!selfCheck && fixtures is null)
{
    Console.Error.WriteLine("usage: --fixtures <dir> | --self-check [--emit <dir>]");
    return 2;
}

var failures = new List<string>();
var checks = new List<string>();

void Report(bool ok, string name, string detail)
{
    if (ok) { checks.Add(name); Console.Error.WriteLine("PASS: " + name + " [" + detail + "]"); }
    else { failures.Add(name + ": " + detail); Console.Error.WriteLine("FAIL: " + name + " [" + detail + "]"); }
}

static string Describe(Exception error) => error is AssemblyPlanException plan ? plan.Code + " at " + plan.Path : error.Message;

static IEnumerable<JsonElement> Rows(JsonElement root, string name) =>
    root.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array
        ? rows.EnumerateArray() : Enumerable.Empty<JsonElement>();

static string Text(JsonElement row, string name) =>
    row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

static string[] Strings(JsonElement row, string name) =>
    Rows(row, name).Select(value => value.GetString() ?? string.Empty).ToArray();

static bool VerifyManifest(string fixtureRoot, List<string> failures)
{
    var manifestPath = Path.Combine(fixtureRoot, "MANIFEST.json");
    if (!File.Exists(manifestPath)) { failures.Add("MANIFEST.json is missing"); return false; }
    using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
    var listed = Rows(manifest.RootElement, "files").Select(row => (Path: Text(row, "path"), Sha: Text(row, "sha256"))).ToList();
    var onDisk = Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories)
        .Select(file => System.IO.Path.GetRelativePath(fixtureRoot, file).Replace('\\', '/'))
        .Where(path => path != "MANIFEST.json").OrderBy(path => path, StringComparer.Ordinal).ToArray();
    if (manifest.RootElement.TryGetProperty("schemaVersion", out var version) && version.GetInt32() != 1)
        failures.Add("MANIFEST.json schemaVersion is not 1");
    if (listed.Count != listed.Select(row => row.Path).Distinct().Count()
        || !listed.Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(onDisk))
    {
        failures.Add("MANIFEST.json must list every fixture file exactly once");
        return false;
    }
    var mismatched = listed.Where(row => Sha256Hex(File.ReadAllBytes(System.IO.Path.Combine(fixtureRoot, row.Path))) != row.Sha)
        .Select(row => row.Path).ToArray();
    if (mismatched.Length > 0) { failures.Add("MANIFEST.json sha256 mismatch: " + string.Join(", ", mismatched)); return false; }
    return true;
}

// Package-level checks (assembly.plan-file / duplicate-level-layout / package-layout). The case root stands
// in for a `BepInEx/plugins/` directory: exactly one child may contain `forge/maps/`. Discovery itself
// belongs to ForgeMap/Native and is not implemented here.
static void CheckPackage(string caseRoot, SortedSet<string> blockers)
{
    var mapDirs = Directory.GetDirectories(caseRoot).OrderBy(dir => dir, StringComparer.Ordinal)
        .Where(dir => Directory.Exists(System.IO.Path.Combine(dir, "forge", "maps"))).ToArray();
    if (mapDirs.Length != 1) throw new AssemblyPlanException("assembly.package-layout", caseRoot);
    var mapsDir = System.IO.Path.Combine(mapDirs[0], "forge", "maps");
    var descriptorsPath = System.IO.Path.Combine(mapsDir, "rooms.descriptors.json");
    if (!File.Exists(descriptorsPath)) throw new AssemblyPlanException("assembly.package-layout", mapsDir);
    var descriptorsBytes = File.ReadAllBytes(descriptorsPath);
    var seenLayouts = new Dictionary<long, string>();
    var missingLevelSeen = false;
    foreach (var planFile in Directory.GetFiles(mapsDir, "*.assembly.json").OrderBy(file => file, StringComparer.Ordinal))
    {
        using var plan = JsonDocument.Parse(File.ReadAllBytes(planFile));
        var planId = plan.RootElement.ValueKind == JsonValueKind.Object
            && plan.RootElement.TryGetProperty("planId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        if (planId is null || System.IO.Path.GetFileName(planFile) != planId + ".assembly.json")
            throw new AssemblyPlanException("assembly.plan-file", planFile);
        long? level = plan.RootElement.TryGetProperty("levelLayoutId", out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed) ? parsed : null;
        if (level is null)
        {
            if (missingLevelSeen) throw new AssemblyPlanException("assembly.duplicate-level-layout", planFile);
            missingLevelSeen = true;
        }
        else
        {
            if (seenLayouts.ContainsKey(level.Value)) throw new AssemblyPlanException("assembly.duplicate-level-layout", planFile);
            seenLayouts[level.Value] = System.IO.Path.GetFileName(planFile);
        }
        foreach (var blocker in AssemblyPlanChecks.Validate(plan.RootElement, descriptorsBytes).Blockers) blockers.Add(blocker);
    }
}

if (fixtures is not null)
{
    var fixtureRoot = Path.GetFullPath(fixtures);
    var manifestVerified = VerifyManifest(fixtureRoot, failures);
    var casesPath = Path.Combine(fixtureRoot, "cases.json");
    if (!File.Exists(casesPath)) failures.Add("cases.json is missing");
    using var cases = JsonDocument.Parse(File.ReadAllBytes(casesPath));
    var counts = new Dictionary<string, (int Passed, int Total)>
    {
        ["valid"] = (0, 0), ["invalid"] = (0, 0), ["package"] = (0, 0),
    };
    foreach (var row in Rows(cases.RootElement, "valid"))
    {
        counts["valid"] = (counts["valid"].Passed, counts["valid"].Total + 1);
        var id = Text(row, "id");
        try
        {
            var planBytes = File.ReadAllBytes(Path.Combine(fixtureRoot, Text(row, "file")));
            var descriptorBytes = File.ReadAllBytes(Path.Combine(fixtureRoot, Text(row, "descriptors")));
            using var plan = JsonDocument.Parse(planBytes);
            var blockers = AssemblyPlanChecks.Validate(plan.RootElement, descriptorBytes).Blockers;
            var expected = Strings(row, "expectedBlockers");
            if (!blockers.SequenceEqual(expected))
                failures.Add($"{id}: blockers [{string.Join(", ", blockers)}] != [{string.Join(", ", expected)}]");
            else counts["valid"] = (counts["valid"].Passed + 1, counts["valid"].Total);
        }
        catch (Exception error) when (error is AssemblyPlanException or JsonException or IOException)
        {
            failures.Add($"{id}: valid fixture rejected: {Describe(error)}");
        }
    }
    foreach (var row in Rows(cases.RootElement, "invalid"))
    {
        counts["invalid"] = (counts["invalid"].Passed, counts["invalid"].Total + 1);
        var id = Text(row, "id");
        var expected = row.GetProperty("expectedError");
        var expectedCode = Text(expected, "code");
        var expectedPath = expected.TryGetProperty("path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String
            ? pathValue.GetString() : null;
        try
        {
            var planBytes = File.ReadAllBytes(Path.Combine(fixtureRoot, Text(row, "file")));
            var descriptorBytes = File.ReadAllBytes(Path.Combine(fixtureRoot, Text(row, "descriptors")));
            using var plan = JsonDocument.Parse(planBytes);
            AssemblyPlanChecks.Validate(plan.RootElement, descriptorBytes);
            failures.Add($"{id}: invalid fixture accepted");
        }
        catch (AssemblyPlanException error)
        {
            if (error.Code != expectedCode || (expectedPath is not null && error.Path != expectedPath))
                failures.Add($"{id}: expected {expectedCode} at {expectedPath ?? "<any path>"}, got {error.Code} at {error.Path}");
            else counts["invalid"] = (counts["invalid"].Passed + 1, counts["invalid"].Total);
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            failures.Add($"{id}: invalid fixture unreadable: {Describe(error)}");
        }
    }
    foreach (var row in Rows(cases.RootElement, "package"))
    {
        counts["package"] = (counts["package"].Passed, counts["package"].Total + 1);
        var id = Text(row, "id");
        var caseRoot = Path.Combine(fixtureRoot, Text(row, "root"));
        if (row.TryGetProperty("expectedError", out var expectedError))
        {
            var expectedCode = Text(expectedError, "code");
            var expectedPath = expectedError.TryGetProperty("path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String
                ? pathValue.GetString() : null;
            try
            {
                CheckPackage(caseRoot, new SortedSet<string>(StringComparer.Ordinal));
                failures.Add($"{id}: package fixture accepted");
            }
            catch (AssemblyPlanException error)
            {
                if (error.Code != expectedCode || (expectedPath is not null && error.Path != expectedPath))
                    failures.Add($"{id}: expected {expectedCode} at {expectedPath ?? "<any path>"}, got {error.Code} at {error.Path}");
                else counts["package"] = (counts["package"].Passed + 1, counts["package"].Total);
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                failures.Add($"{id}: package fixture unreadable: {Describe(error)}");
            }
        }
        else
        {
            var blockers = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                CheckPackage(caseRoot, blockers);
                var expected = Strings(row, "expectedBlockers");
                if (!blockers.SequenceEqual(expected))
                    failures.Add($"{id}: blockers [{string.Join(", ", blockers)}] != [{string.Join(", ", expected)}]");
                else counts["package"] = (counts["package"].Passed + 1, counts["package"].Total);
            }
            catch (AssemblyPlanException error)
            {
                failures.Add($"{id}: package fixture rejected: {Describe(error)}");
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                failures.Add($"{id}: package fixture unreadable: {Describe(error)}");
            }
        }
    }
    var passed = failures.Count == 0 && manifestVerified && counts.Values.All(group => group.Passed == group.Total);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        verification = "assembly-plan-fixture-only; static checks are not a generation success",
        nativeGameExecuted = false, manifestVerified, passed,
        counts = counts.ToDictionary(pair => pair.Key, pair => new { passed = pair.Value.Passed, total = pair.Value.Total }),
        failures,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return passed ? 0 : 1;
}

var scratch = emit is null
    ? Path.Combine(Path.GetTempPath(), "map-assembly-self-check-" + Guid.NewGuid().ToString("N"))
    : Path.GetFullPath(emit);
Directory.CreateDirectory(scratch);
var validCases = new List<object>();
var invalidCases = new List<object>();
var packageCases = new List<object>();
try
{
    foreach (var testCase in PlanCases)
    {
        var plan = BaselinePlan();
        var descriptors = BaselineDescriptors();
        testCase.Mutate(plan, descriptors);
        var descriptorBytes = testCase.DescriptorsOverride is null
            ? Encoding.UTF8.GetBytes(descriptors.ToJsonString())
            : Encoding.UTF8.GetBytes(testCase.DescriptorsOverride);
        if (testCase.Relock) ((JsonObject)plan["descriptors"]!)["sha256"] = Sha256Hex(descriptorBytes);
        var planBytes = Encoding.UTF8.GetBytes(plan.ToJsonString());
        string? code = null;
        string? path = null;
        string[]? blockers = null;
        try
        {
            using var document = JsonDocument.Parse(planBytes);
            blockers = AssemblyPlanChecks.Validate(document.RootElement, descriptorBytes).Blockers.ToArray();
        }
        catch (AssemblyPlanException error)
        {
            code = error.Code;
            path = error.Path;
        }
        var ok = testCase.ExpectedCode is null
            ? code is null && blockers!.SequenceEqual(testCase.ExpectedBlockers!)
            : code == testCase.ExpectedCode && path == testCase.ExpectedPath;
        Report(ok, testCase.Name, ok
            ? testCase.ExpectedCode is null ? "blockers " + string.Join(", ", blockers!) : code + " at " + path
            : $"expected {testCase.ExpectedCode ?? "no error"} at {testCase.ExpectedPath ?? "<blockers>"}, got "
              + (code is null ? "no error (blockers " + string.Join(", ", blockers ?? Array.Empty<string>()) + ")" : code + " at " + path));
        if (emit is not null)
        {
            Directory.CreateDirectory(Path.Combine(scratch, "plan"));
            var planPath = "plan/" + testCase.Name + ".assembly.json";
            var descriptorPath = "plan/" + testCase.Name + ".descriptors.json";
            File.WriteAllBytes(Path.Combine(scratch, planPath), planBytes);
            File.WriteAllBytes(Path.Combine(scratch, descriptorPath), descriptorBytes);
            // --emit writes a fixture set in the arbitrating checker's own cases.json/MANIFEST.json shape, so
            // tools/verify_assembly_plan_fixtures.py can be run over this corpus verbatim.
            if (testCase.ExpectedCode is null)
                validCases.Add(new
                {
                    id = testCase.Name, file = planPath, descriptors = descriptorPath,
                    expectedBlockers = testCase.ExpectedBlockers!,
                });
            else
                invalidCases.Add(new
                {
                    id = testCase.Name, file = planPath, descriptors = descriptorPath,
                    expectedError = new { code = testCase.ExpectedCode, path = testCase.ExpectedPath! },
                });
        }
    }

    foreach (var testCase in PackageCases)
    {
        var caseRoot = Path.Combine(scratch, "package", testCase.Name);
        testCase.Build(caseRoot);
        string? code = null;
        string? path = null;
        try
        {
            CheckPackage(caseRoot, new SortedSet<string>(StringComparer.Ordinal));
        }
        catch (AssemblyPlanException error)
        {
            code = error.Code;
            path = error.Path;
        }
        var pathOk = testCase.ExpectedPathSuffix is null
            || (path is not null && path.EndsWith(testCase.ExpectedPathSuffix, StringComparison.Ordinal));
        var ok = code == testCase.ExpectedCode && pathOk;
        Report(ok, testCase.Name, ok ? code + " at " + path
            : $"expected {testCase.ExpectedCode} at *{testCase.ExpectedPathSuffix ?? "*"}, got {code ?? "no error"} at {path ?? "<none>"}");
        if (emit is not null)
            packageCases.Add(new
            {
                id = testCase.Name, root = "package/" + testCase.Name,
                expectedError = new { code = testCase.ExpectedCode },
            });
    }
}
finally
{
    if (emit is null && Directory.Exists(scratch)) Directory.Delete(scratch, true);
}
if (emit is not null)
{
    File.WriteAllText(Path.Combine(scratch, "cases.json"), JsonSerializer.Serialize(
        new { valid = validCases, invalid = invalidCases, package = packageCases },
        new JsonSerializerOptions { WriteIndented = true }));
    var files = Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(scratch, file).Replace(Path.DirectorySeparatorChar, '/'))
        .Where(relative => relative != "MANIFEST.json")
        .OrderBy(relative => relative, StringComparer.Ordinal)
        .Select(relative => new { path = relative, sha256 = Sha256Hex(File.ReadAllBytes(Path.Combine(scratch, relative))) })
        .ToArray();
    File.WriteAllText(Path.Combine(scratch, "MANIFEST.json"),
        JsonSerializer.Serialize(new { schemaVersion = 1, files }, new JsonSerializerOptions { WriteIndented = true }));
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    verification = "self-check by single-point mutation; synthetic plan data, no website fixture copied",
    nativeGameExecuted = false, passed = checks.Count, failed = failures.Count, checks, failures,
}, new JsonSerializerOptions { WriteIndented = true }));
return failures.Count == 0 ? 0 : 1;

sealed record PlanCase(string Name, string? ExpectedCode, string? ExpectedPath, bool Relock,
    Action<JsonObject, JsonObject> Mutate, string[]? ExpectedBlockers, string? DescriptorsOverride = null);

sealed record PackageCase(string Name, string ExpectedCode, string? ExpectedPathSuffix, Action<string> Build);
