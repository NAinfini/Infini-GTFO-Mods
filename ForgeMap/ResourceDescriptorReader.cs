using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeMap;

// Thrown with the same `descriptor.<code>` codes and paths as
// ForgeMap/tools/verify_resource_adapter_fixtures.py; the Python checker is the fixture arbiter and this is
// the strict C# mirror of GENERATION-SPEC.md section 3.1 (G1 needs it before AssemblyPlanChecks).
public sealed class DescriptorException : Exception
{
    public string Code { get; }
    public string Path { get; }
    public DescriptorException(string code, string path) : base(code + ": " + path)
    {
        Code = code;
        Path = path;
    }
}

// Only the fields AssemblyPlanChecks needs (connector geometry, expander type, blockers); hierarchy/areas/
// sharedBytes/collider-navigation-occlusion item lists are fully validated but not retained afterward.
public sealed record DescriptorConnector(string Id, string ExpanderType, double[] Position, double[] Outward);
public sealed record DescriptorEntry(string ReferenceId, string Revision, IReadOnlyList<DescriptorConnector> Connectors,
    IReadOnlyList<string> Blockers)
{
    public string Identity => ReferenceId + "@" + Revision;
}
public sealed record DescriptorDocument(IReadOnlyList<DescriptorEntry> Descriptors);

public static class ResourceDescriptorReader
{
    private static readonly Regex Hash = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex PathIdPattern = new(@"^(0|-?[1-9][0-9]*)$", RegexOptions.Compiled);
    private static readonly Regex Pin = new(@"^([A-Za-z0-9_]+-[A-Za-z0-9_]+)-(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$",
        RegexOptions.Compiled);
    private static readonly Regex ReferenceIdForbidden = new(@"[\s/\\]", RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, int> Profiles = new Dictionary<string, int>
    {
        ["gtfo.complex-resource-geomorph"] = 1,
    };
    private static readonly HashSet<string> Evidence = new() { "fixture-synthetic", "extracted-metadata" };
    private static readonly string[] SpatialNames = { "colliders", "navigation", "occlusion" };
    private const int MaxNodes = 50000;

    private static void Fail(string code, string path) => throw new DescriptorException("descriptor." + code, path);
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

    private static bool IsText(JsonElement value, int limit = 2048)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString() ?? string.Empty;
        // Python measures len() in code points; UTF-16 units would make the bound stricter off the BMP.
        if (text.Length == 0 || text.EnumerateRunes().Count() > limit) return false;
        if (text.Trim() != text) return false;
        return !text.Any(c => c < 32);
    }

    // Python compares JSON numbers across int/float (`1.0 == 1`); booleans are not numbers here.
    private static bool NumberEquals(JsonElement value, double expected) =>
        value.ValueKind == JsonValueKind.Number && value.GetDouble() == expected;

    private static bool IsNumber(JsonElement value) => value.ValueKind == JsonValueKind.Number;

    private static bool TryVector(JsonElement value, int size, out double[] result)
    {
        result = Array.Empty<double>();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != size) return false;
        var values = new double[size];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (!IsNumber(item)) return false;
            var d = item.GetDouble();
            if (!double.IsFinite(d)) return false;
            values[index++] = d;
        }
        result = values;
        return true;
    }

    private static (string File, string PathId) SourceObject(JsonElement value, string path)
    {
        var fields = Fields(value, new[] { "file", "pathId" }, path);
        Need(IsText(fields["file"], 512) && !fields["file"].GetString()!.Contains('/'), "source", path + ".file");
        var pathId = fields["pathId"];
        var ok = pathId.ValueKind == JsonValueKind.String && PathIdPattern.IsMatch(pathId.GetString() ?? string.Empty);
        if (ok)
        {
            var text = pathId.GetString()!;
            ok = text.Length <= 20
                 && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        }
        Need(ok, "path-id", path + ".pathId must be an int64 decimal string");
        return (fields["file"].GetString()!, pathId.GetString()!);
    }

    private static string ImportedId(string pin, string bundle, string file, string pathId)
    {
        var name = Pin.Match(pin).Groups[1].Value;
        return "forge.imported:" + string.Join(":", new[] { name, bundle, file, pathId }.Select(PercentEncode));
    }

    // Mirrors Python's urllib.parse.quote(part, safe="-_.!~*'()"), i.e. JS encodeURIComponent's safe set.
    private static string PercentEncode(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') || "-_.!~*'()".IndexOf(c) >= 0)
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static void RequireTransform(JsonElement value, string path)
    {
        var fields = Fields(value, new[] { "position", "rotation", "scale" }, path);
        var positioned = TryVector(fields["position"], 3, out _);
        var rotated = TryVector(fields["rotation"], 4, out var rotation);
        var scaled = TryVector(fields["scale"], 3, out var scale) && scale.All(n => n != 0);
        Need(positioned && rotated && scaled && Math.Abs(Math.Sqrt(rotation.Sum(n => n * n)) - 1) <= 1e-4, "transform", path);
    }

    private static HashSet<string> SharedBytes(JsonElement rows, string path)
    {
        Need(rows.ValueKind == JsonValueKind.Array, "schema", path + " must be a list");
        var keys = new HashSet<string>();
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var rowPath = $"{path}[{index}]";
            var kind = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() : null;
            Need(kind is "mesh" or "texture" or "material", "shared-bytes", rowPath + ".kind");
            var allowed = kind == "mesh" ? new[] { "key", "kind", "sha256", "attributes" } : new[] { "key", "kind", "sha256" };
            var fields = Fields(row, allowed, rowPath);
            var sha256Ok = fields["sha256"].ValueKind == JsonValueKind.String && Hash.IsMatch(fields["sha256"].GetString()!);
            var keyValue = fields["key"].ValueKind == JsonValueKind.String ? fields["key"].GetString()! : string.Empty;
            var expectedKey = sha256Ok ? kind + ":" + fields["sha256"].GetString() : null;
            Need(sha256Ok && keyValue == expectedKey && !keys.Contains(keyValue), "shared-bytes", rowPath + ".key");
            keys.Add(keyValue);
            if (fields.TryGetValue("attributes", out var attributes))
            {
                Need(attributes.ValueKind == JsonValueKind.Array, "schema", rowPath + ".attributes");
                var aIndex = 0;
                foreach (var attribute in attributes.EnumerateArray())
                {
                    var aPath = $"{rowPath}.attributes[{aIndex}]";
                    var af = Fields(attribute, new[] { "semantic", "components", "fourthComponent" }, aPath);
                    var semantic = af["semantic"].ValueKind == JsonValueKind.String ? af["semantic"].GetString() : null;
                    var components = af["components"].ValueKind == JsonValueKind.Number ? af["components"].GetDouble() : double.NaN;
                    Need((semantic is "POSITION" or "NORMAL") && (components == 3 || components == 4), "schema", aPath);
                    // A fourth component is a preserved custom scalar channel: no homogeneous divide, never dropped.
                    var fourth = af["fourthComponent"];
                    var fourthOk = components == 3
                        ? fourth.ValueKind == JsonValueKind.Null
                        : fourth.ValueKind == JsonValueKind.String && fourth.GetString() == "custom-scalar";
                    Need(fourthOk, "vector-channel", aPath);
                    aIndex++;
                }
            }
            index++;
        }
        return keys;
    }

    private static DescriptorEntry ReadDescriptor(JsonElement value, string path, HashSet<string> byteKeys)
    {
        var top = Fields(value, new[] { "reference", "adapter", "source", "authorization", "runtimeLocator", "hierarchy",
            "areas", "connectors", "colliders", "navigation", "occlusion" }, path);

        var reference = Fields(top["reference"], new[] { "id", "revision" }, path + ".reference");
        var referenceId = reference["id"].ValueKind == JsonValueKind.String ? reference["id"].GetString()! : string.Empty;
        var revision = reference["revision"].ValueKind == JsonValueKind.String ? reference["revision"].GetString()! : string.Empty;
        Need(IsText(reference["id"], 512) && !ReferenceIdForbidden.IsMatch(referenceId) && Hash.IsMatch(revision),
            "reference", path + ".reference");

        var adapter = Fields(top["adapter"], new[] { "profile", "profileRevision" }, path + ".adapter");
        var profile = adapter["profile"].ValueKind == JsonValueKind.String ? adapter["profile"].GetString() : null;
        Need(profile != null && Profiles.TryGetValue(profile, out var expectedRevision)
             && NumberEquals(adapter["profileRevision"], expectedRevision), "unsupported-profile", path + ".adapter");

        var source = top["source"];
        var sourcePath = path + ".source";
        var sourceKind = source.ValueKind == JsonValueKind.Object && source.TryGetProperty("kind", out var sk) && sk.ValueKind == JsonValueKind.String
            ? sk.GetString() : null;
        Need(sourceKind is "package" or "native", "source", sourcePath + ".kind");
        var authorization = Fields(top["authorization"], new[] { "reviewRef", "runtimeUse", "redistributeBytes", "dependency" },
            path + ".authorization");

        string assetPath;
        if (sourceKind == "package")
        {
            var sourceFields = Fields(source, new[] { "kind", "packagePin", "archiveSha256", "bundle", "assetPath", "object" }, sourcePath);
            var pin = sourceFields["packagePin"].ValueKind == JsonValueKind.String ? sourceFields["packagePin"].GetString()! : string.Empty;
            var archiveSha = sourceFields["archiveSha256"].ValueKind == JsonValueKind.String ? sourceFields["archiveSha256"].GetString()! : string.Empty;
            Need(Pin.IsMatch(pin) && Hash.IsMatch(archiveSha) && IsText(sourceFields["bundle"], 512) && IsText(sourceFields["assetPath"]),
                "source", sourcePath);
            var (file, pathId) = SourceObject(sourceFields["object"], sourcePath + ".object");
            Need(revision == archiveSha, "revision-mismatch", path + ".reference.revision");
            Need(referenceId == ImportedId(pin, sourceFields["bundle"].GetString()!, file, pathId), "reference-derivation", path + ".reference.id");
            var runtimeUse = authorization["runtimeUse"].ValueKind == JsonValueKind.String ? authorization["runtimeUse"].GetString() : null;
            var dependency = authorization["dependency"].ValueKind == JsonValueKind.String ? authorization["dependency"].GetString() : null;
            Need(runtimeUse == "installed-package" && dependency == pin, "authorization", path + ".authorization.dependency");
            assetPath = sourceFields["assetPath"].GetString()!;
        }
        else
        {
            var sourceFields = Fields(source, new[] { "kind", "sourceId", "contentSha256", "evidence", "assetPath", "object" }, sourcePath);
            var evidence = Fields(sourceFields["evidence"], new[] { "path", "sha256" }, sourcePath + ".evidence");
            var contentSha = sourceFields["contentSha256"].ValueKind == JsonValueKind.String ? sourceFields["contentSha256"].GetString()! : string.Empty;
            var evidenceSha = evidence["sha256"].ValueKind == JsonValueKind.String ? evidence["sha256"].GetString()! : string.Empty;
            Need(IsText(sourceFields["sourceId"], 512) && IsText(sourceFields["assetPath"]) && IsText(evidence["path"])
                 && Hash.IsMatch(contentSha) && Hash.IsMatch(evidenceSha), "source", sourcePath);
            SourceObject(sourceFields["object"], sourcePath + ".object");
            Need(revision == contentSha, "revision-mismatch", path + ".reference.revision");
            var runtimeUse = authorization["runtimeUse"].ValueKind == JsonValueKind.String ? authorization["runtimeUse"].GetString() : null;
            Need(runtimeUse == "game-content" && authorization["dependency"].ValueKind == JsonValueKind.Null,
                "authorization", path + ".authorization.dependency");
            assetPath = sourceFields["assetPath"].GetString()!;
        }
        Need(IsText(authorization["reviewRef"], 512) && authorization["redistributeBytes"].ValueKind == JsonValueKind.False,
            "authorization", path + ".authorization");

        var locator = Fields(top["runtimeLocator"], new[] { "loadedBy", "assetPath", "requiresLoaded" }, path + ".runtimeLocator");
        var loadedBy = locator["loadedBy"].ValueKind == JsonValueKind.String ? locator["loadedBy"].GetString() : null;
        var locatorAsset = locator["assetPath"].ValueKind == JsonValueKind.String ? locator["assetPath"].GetString() : null;
        Need(loadedBy == "complex-resource-set" && locatorAsset == assetPath && locator["requiresLoaded"].ValueKind == JsonValueKind.True,
            "runtime-locator", path + ".runtimeLocator");

        var hierarchy = Fields(top["hierarchy"], new[] { "space", "nodes" }, path + ".hierarchy");
        var space = hierarchy["space"].ValueKind == JsonValueKind.String ? hierarchy["space"].GetString() : null;
        var nodesEl = hierarchy["nodes"];
        Need(space == "unity-left-handed-meters" && nodesEl.ValueKind == JsonValueKind.Array
             && nodesEl.GetArrayLength() > 0 && nodesEl.GetArrayLength() <= MaxNodes, "hierarchy", path + ".hierarchy");
        var parents = new Dictionary<string, string?>();
        var rootSources = new List<(string File, string PathId)>();
        var nIndex = 0;
        foreach (var node in nodesEl.EnumerateArray())
        {
            var nPath = $"{path}.hierarchy.nodes[{nIndex}]";
            var nf = Fields(node, new[] { "id", "parent", "name", "source", "gameObject", "local", "active", "meshes" }, nPath);
            var nodeSource = SourceObject(nf["source"], nPath + ".source");
            SourceObject(nf["gameObject"], nPath + ".gameObject");
            var id = nf["id"].ValueKind == JsonValueKind.String ? nf["id"].GetString()! : string.Empty;
            Need(id == nodeSource.File + ":" + nodeSource.PathId, "node-identity", nPath + ".id");
            Need(!parents.ContainsKey(id), "duplicate-node", nPath + ".id");
            var parentEl = nf["parent"];
            Need(parentEl.ValueKind == JsonValueKind.Null || (parentEl.ValueKind == JsonValueKind.String && IsText(parentEl, 600)),
                "hierarchy", nPath + ".parent");
            Need(IsText(nf["name"], 512) && (nf["active"].ValueKind is JsonValueKind.True or JsonValueKind.False), "schema", nPath);
            RequireTransform(nf["local"], nPath + ".local");
            var meshesEl = nf["meshes"];
            var meshList = meshesEl.ValueKind == JsonValueKind.Array
                ? meshesEl.EnumerateArray().Select(m => m.ValueKind == JsonValueKind.String ? m.GetString()! : "\0invalid").ToList()
                : null;
            Need(meshList != null && meshList.Distinct().Count() == meshList.Count, "schema", nPath + ".meshes");
            foreach (var key in meshList!)
                Need(byteKeys.Contains(key), "shared-bytes-missing", nPath + ".meshes: " + key);
            parents[id] = parentEl.ValueKind == JsonValueKind.String ? parentEl.GetString() : null;
            if (parentEl.ValueKind == JsonValueKind.Null) rootSources.Add(nodeSource);
            nIndex++;
        }
        Need(rootSources.Count == 1, "hierarchy", path + " requires exactly one root");
        foreach (var (nodeId, parent) in parents)
            Need(parent is null || parents.ContainsKey(parent), "hierarchy", path + " unknown parent of " + nodeId);
        foreach (var nodeId in parents.Keys)
        {
            var seen = new HashSet<string>();
            string? current = nodeId;
            while (current is not null)
            {
                Need(seen.Add(current), "hierarchy", path + " cycle at " + nodeId);
                current = parents[current];
            }
        }
        var (rootFile, rootPathId) = SourceObject(source.GetProperty("object"), sourcePath + ".object");
        Need(rootSources[0] == (rootFile, rootPathId), "root-mismatch", path + " root node is not the locked source object");

        var areasEl = top["areas"];
        Need(areasEl.ValueKind == JsonValueKind.Array, "schema", path + ".areas");
        var areaIds = new HashSet<string>();
        var aIdx = 0;
        foreach (var area in areasEl.EnumerateArray())
        {
            var aPath = $"{path}.areas[{aIdx}]";
            var af = Fields(area, new[] { "id", "node" }, aPath);
            var areaId = af["id"].ValueKind == JsonValueKind.String ? af["id"].GetString()! : string.Empty;
            var node = af["node"].ValueKind == JsonValueKind.String ? af["node"].GetString() : null;
            Need(IsText(af["id"], 256) && !areaIds.Contains(areaId) && node != null && parents.ContainsKey(node), "area", aPath);
            areaIds.Add(areaId);
            aIdx++;
        }

        var connectorsEl = top["connectors"];
        Need(connectorsEl.ValueKind == JsonValueKind.Array, "schema", path + ".connectors");
        var connectorIds = new HashSet<string>();
        var connectors = new List<DescriptorConnector>();
        var cIdx = 0;
        foreach (var connector in connectorsEl.EnumerateArray())
        {
            var cPath = $"{path}.connectors[{cIdx}]";
            var cf = Fields(connector, new[] { "id", "node", "area", "expanderType", "position", "outward", "doubleSided" }, cPath);
            var connectorId = cf["id"].ValueKind == JsonValueKind.String ? cf["id"].GetString()! : string.Empty;
            var node = cf["node"].ValueKind == JsonValueKind.String ? cf["node"].GetString() : null;
            var expanderType = cf["expanderType"].ValueKind == JsonValueKind.String ? cf["expanderType"].GetString() : null;
            var hasPosition = TryVector(cf["position"], 3, out var position);
            Need(IsText(cf["id"], 256) && !connectorIds.Contains(connectorId) && node != null && parents.ContainsKey(node)
                 && expanderType is "plug" or "gate" && (cf["doubleSided"].ValueKind is JsonValueKind.True or JsonValueKind.False)
                 && hasPosition, "connector", cPath);
            connectorIds.Add(connectorId);
            var area = cf["area"].ValueKind == JsonValueKind.String ? cf["area"].GetString() : null;
            Need(area != null && areaIds.Contains(area), "connector-area", cPath + ".area");
            var hasOutward = TryVector(cf["outward"], 3, out var outward);
            Need(hasOutward && Math.Abs(Math.Sqrt(outward.Sum(n => n * n)) - 1) <= 1e-4, "connector-orientation", cPath + ".outward");
            connectors.Add(new DescriptorConnector(connectorId, expanderType!, position, outward));
            cIdx++;
        }

        var blockers = new List<string>();
        foreach (var name in SpatialNames)
        {
            var statuses = name switch
            {
                "colliders" => new HashSet<string> { "source", "unknown" },
                "navigation" => new HashSet<string> { "build-time", "unknown" },
                _ => new HashSet<string> { "source", "unknown" },
            };
            var itemFields = name switch
            {
                "colliders" => new HashSet<string> { "node", "shape", "evidence" },
                "navigation" => new HashSet<string> { "node", "kind" },
                _ => new HashSet<string> { "node", "kind", "area" },
            };
            var block = top[name];
            var sPath = path + "." + name;
            Need(block.ValueKind == JsonValueKind.Object, "schema", sPath);
            var status = block.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            Need(status != null && statuses.Contains(status), name == "navigation" ? "navigation-state" : "schema", sPath + ".status");
            var bf = Fields(block, new[] { "status", "reason", "items" }, sPath);
            Need(bf["items"].ValueKind == JsonValueKind.Array, "schema", sPath + ".items");
            if (status == "unknown")
            {
                Need(IsText(bf["reason"]) && bf["items"].GetArrayLength() == 0, "unknown-reason", sPath);
                blockers.Add(name);
                continue;
            }
            Need(bf["reason"].ValueKind == JsonValueKind.Null && bf["items"].GetArrayLength() > 0, "schema", sPath);
            var iIdx = 0;
            foreach (var item in bf["items"].EnumerateArray())
            {
                var iPath = $"{sPath}.items[{iIdx}]";
                var itf = Fields(item, itemFields, iPath);
                var itemNode = itf["node"].ValueKind == JsonValueKind.String ? itf["node"].GetString() : null;
                Need(itemNode != null && parents.ContainsKey(itemNode), "schema", iPath + ".node");
                if (name == "colliders")
                {
                    var shape = itf["shape"].ValueKind == JsonValueKind.String ? itf["shape"].GetString() : null;
                    Need(shape is "box" or "sphere" or "capsule" or "mesh", "schema", iPath + ".shape");
                    // Renderer or preview bounds are never collision evidence.
                    var evidence = itf["evidence"].ValueKind == JsonValueKind.String ? itf["evidence"].GetString() : null;
                    Need(evidence == "collider-component", "collider-evidence", iPath + ".evidence");
                }
                else if (name == "navigation")
                {
                    var kind = itf["kind"].ValueKind == JsonValueKind.String ? itf["kind"].GetString() : null;
                    Need(kind is "node-volume" or "navmesh-source", "schema", iPath + ".kind");
                }
                else
                {
                    var kind = itf["kind"].ValueKind == JsonValueKind.String ? itf["kind"].GetString() : null;
                    var area = itf["area"].ValueKind == JsonValueKind.String ? itf["area"].GetString() : null;
                    Need((kind is "culling-portal" or "occluder") && area != null && areaIds.Contains(area), "schema", iPath);
                }
                iIdx++;
            }
        }

        return new DescriptorEntry(referenceId, revision, connectors, blockers);
    }

    // `root` is the path prefix of the emitted error paths: "$" for the standalone document (Python CLI),
    // "$descriptors" when the G0 plan checker validates the document the plan locks.
    public static DescriptorDocument Read(JsonElement value, string root = "$")
    {
        var top = Fields(value, new[] { "schemaVersion", "kind", "evidence", "sharedBytes", "descriptors" }, root);
        // Python compares `schemaVersion == 1`, which a JSON 1.0 also satisfies.
        Need(NumberEquals(top["schemaVersion"], 1)
             && top["kind"].ValueKind == JsonValueKind.String && top["kind"].GetString() == "forge-map-resource-descriptors",
            "schema", root + ".kind");
        var evidence = top["evidence"].ValueKind == JsonValueKind.String ? top["evidence"].GetString() : null;
        Need(evidence != null && Evidence.Contains(evidence), "evidence-escalation", root + ".evidence");
        var keys = SharedBytes(top["sharedBytes"], root + ".sharedBytes");
        var rows = top["descriptors"];
        Need(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0, "schema", root + ".descriptors");
        var results = new List<DescriptorEntry>();
        var identities = new HashSet<string>();
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var entry = ReadDescriptor(row, $"{root}.descriptors[{index}]", keys);
            Need(!identities.Contains(entry.Identity), "duplicate-resource", $"{root}.descriptors[{index}].reference");
            identities.Add(entry.Identity);
            results.Add(entry);
            index++;
        }
        return new DescriptorDocument(results);
    }
}
