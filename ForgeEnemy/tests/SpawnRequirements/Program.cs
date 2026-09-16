using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeEnemy.Spawn;
using ForgeRuntime.Framework;

if (args.Length != 3 || args[0] is not ("check" or "generate"))
{
    Console.Error.WriteLine("Usage: SpawnRequirements check <ForgeEnemy root> <report.json> | generate <ForgeEnemy root> <contract.json>");
    return 2;
}
string root = Path.GetFullPath(args[1]);
string evidenceDirectory = Path.Combine(root, "evidence", "e2-spawn-space-20403457");
byte[] evidence = File.ReadAllBytes(Path.Combine(evidenceDirectory, "spawn-space-evidence.json"));
// The evidence file is a fixture: it is translated into the same typed rows the runtime source reads.
EnemySpawnRequirementCatalog Catalog(byte[] evidenceUtf8, out EnemySpawnRequirementProvenance provenance) =>
    EnemySpawnRequirementCatalog.Build(EvidenceInputs.Read(evidenceUtf8, out provenance));
var catalog = Catalog(evidence, out var provenance);
if (args[0] == "generate")
{
    File.WriteAllText(Path.GetFullPath(args[2]), catalog.ToJson(provenance), new UTF8Encoding(false));
    Console.WriteLine("Wrote " + catalog.Requirements.Count + " requirements from evidence " + provenance.EvidenceSha256);
    return 0;
}

var checks = new List<Row>();
void Case(string id, Func<(bool Passed, string Detail)> body)
{
    try { var (passed, detail) = body(); checks.Add(new(id, passed, detail)); }
    catch (Exception error) { checks.Add(new(id, false, error.GetType().Name + ": " + error.Message)); }
}
void Rejects(string id, Action body)
{
    try { body(); checks.Add(new(id, false, "accepted")); }
    catch (InvalidDataException error) { checks.Add(new(id, true, error.Message)); }
    catch (Exception error) { checks.Add(new(id, false, "wrong failure " + error.GetType().Name + ": " + error.Message)); }
}
string contract = catalog.ToJson(provenance);
string contractPath = Path.Combine(evidenceDirectory, "spawn-requirements.json");
string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
EnemySpawnRequirement R(uint id) => catalog.Get(id);
HashSet<uint> Ids(EnemyMovementKind kind) => catalog.Requirements.Where(r => r.Movement == kind).Select(r => r.EnemyDataBlockId).ToHashSet();

// Data interpretation. Every asserted value traces to a DataBlock, base prefab or project setting in the evidence.
Case("evidence.catalog", () => (catalog.Requirements.Count == 39 && provenance.GameBuild == "20403457" && provenance.EvidenceSha256 == Sha(evidence)
    && catalog.AgentTypes.Select(a => a.AgentTypeId).SequenceEqual(new[] { 0, -1372625422, -334000983 })
    && catalog.Areas.Select(a => a.Name).SequenceEqual(new[] { "Walkable", "Not Walkable", "Jump" }),
    $"requirements={catalog.Requirements.Count}; evidence={provenance.EvidenceSha256}"));
Case("contract.checked-in-matches-evidence", () => (File.ReadAllText(contractPath) == contract, contractPath));
Case("contract.round-trip", () =>
{
    var parsed = EnemySpawnRequirementCatalog.Parse(contract, out var roundTrip);
    return (parsed.ToJson(roundTrip) == contract, "Parse(ToJson()) is byte-identical.");
});
Case("movement.partition", () => (Ids(EnemyMovementKind.Unresolved).SetEquals(new uint[] { 22, 44, 61 })
    && Ids(EnemyMovementKind.Flying).SetEquals(new uint[] { 42, 43, 45, 58 }) && Ids(EnemyMovementKind.Ground).Count == 32,
    $"ground={Ids(EnemyMovementKind.Ground).Count}; flying={string.Join(',', Ids(EnemyMovementKind.Flying))}; unresolved={string.Join(',', Ids(EnemyMovementKind.Unresolved))}"));
Case("ground.striker-wave", () =>
{
    var r = R(13); var g = r.GroundNavigation!;
    return (r.Movement == EnemyMovementKind.Ground && g.AgentTypeId == 0 && g.AgentRadius == 0.20000000298023224 && g.AgentHeight == 2
        && g.WalkableAreaMask == uint.MaxValue && g.AutoTraverseOffMeshLink && r.LadderDescent == true && r.CollisionRadius == 0.45
        && r.ModelSizeRanges.Single() == new EnemySizeRange(0.9, 1.1)
        && r.Unverified.SequenceEqual(new[] { "base-prefab-resolution", "collision-radius-semantics", "datablock-overrides",
            "runtime-navmesh-agent-profile", "size-multiplier-effect", "spawn-clearance" }), JsonSerializer.Serialize(r));
});
Case("ground.megamother-boss-navigation", () =>
{
    var r = R(55); var g = r.GroundNavigation!;
    return (g.WalkableAreaMask == 3 && !g.AutoTraverseOffMeshLink && g.AgentTypeId == 0 && r.LadderDescent == false
        && r.ModelSizeRanges.Single() == new EnemySizeRange(6, 6), JsonSerializer.Serialize(r));
});
Case("flying.flyer-air-graph", () =>
{
    var r = R(42);
    return (r.Movement == EnemyMovementKind.Flying && r.GroundNavigation == null && r.LadderDescent == false
        && r.Unverified.Contains("air-graph-clearance") && !r.Unverified.Contains("runtime-navmesh-agent-profile"), JsonSerializer.Serialize(r));
});
Case("unresolved.cocoon-without-movement-block", () =>
{
    var r = R(22);
    return (r.UnresolvedReason == "movement-datablock-absent" && r.LadderDescent == null && r.CollisionRadius == null
        && r.CanBePushed == null && !r.Unverified.Contains("collision-radius-semantics"), JsonSerializer.Serialize(r));
});
Case("unresolved.squid-locomotion-conflict", () => (new uint[] { 44, 61 }.All(id => R(id).UnresolvedReason == "locomotion-navigation-conflict"
    && R(id).GroundNavigation == null), "PathMove movement block on an air-graph base is not silently treated as flying or ground."));
Case("arena.pouncer", () => (R(46).ArenaDimensions.SequenceEqual(new uint[] { 14 }) && R(46).Unverified.Contains("arena-dimension-requirement"),
    JsonSerializer.Serialize(R(46))));
Case("lookup.no-substitution", () =>
{
    try { catalog.Get(9999); return (false, "missing id returned a requirement"); }
    catch (KeyNotFoundException) { return (R(55).EnemyDataBlockId == 55 && R(55).Name == "MegaMother", "Exact id only."); }
});

// Evidence interpretation fails closed or stays unresolved; it never invents a navigation profile.
byte[] MutateEvidence(Action<JsonObject> change)
{
    var node = JsonNode.Parse(evidence)!.AsObject(); change(node); return Encoding.UTF8.GetBytes(node.ToJsonString());
}
JsonObject Row(JsonObject document, string array, uint id) =>
    document[array]!.AsArray().Select(n => n!.AsObject()).Single(r => (uint)r["id"]! == id);
JsonObject Base(JsonObject document, string name) =>
    document["basePrefabs"]!.AsArray().Select(n => n!.AsObject()).Single(r => ((string)r["path"]!).EndsWith("/" + name + ".prefab", StringComparison.Ordinal));
Rejects("evidence.reject-game-executed", () => Catalog(MutateEvidence(e => e["gameExecuted"] = true), out _));
Rejects("evidence.reject-missing-base-prefab", () => Catalog(MutateEvidence(e => Row(e, "enemies", 13)["basePrefabs"]!.AsArray().Add("Assets/AssetPrefabs/Characters/Enemies/Bases/Unknown.prefab")), out _));
Rejects("evidence.reject-missing-movement-block", () => Catalog(MutateEvidence(e => Row(e, "enemies", 13)["movementDataId"] = 999), out _));
Rejects("evidence.reject-disabled-enemy", () => Catalog(MutateEvidence(e => Row(e, "enemies", 13)["internalEnabled"] = false), out _));
Case("evidence.unmapped-locomotion-state", () =>
{
    var mutated = Catalog(MutateEvidence(e => Row(e, "movementBlocks", 13)["locomotionPathMove"] = 5), out _);
    return (mutated.Get(13).UnresolvedReason == "locomotion-state-unmapped", "ES_StateEnum 5 is not a known path-move state.");
});
Case("evidence.ground-base-with-air-graph-conflicts", () =>
{
    var mutated = Catalog(MutateEvidence(e => Base(e, "StrikerBase")["airGraphAgent"] = true), out _);
    return (mutated.Get(13).UnresolvedReason == "locomotion-navigation-conflict", "Both navigation components means unresolved, not a guess.");
});

// Map-facing contract is closed: unknown fields and contradictory shapes are rejected.
string MutateContract(Action<JsonObject> change)
{
    var node = JsonNode.Parse(contract)!.AsObject(); change(node); return node.ToJsonString();
}
JsonObject Requirement(JsonObject document, uint id) =>
    document["requirements"]!.AsArray().Select(n => n!.AsObject()).Single(r => (uint)r["enemyDataBlockId"]! == id);
void RejectContract(string id, Action<JsonObject> change) => Rejects(id, () => EnemySpawnRequirementCatalog.Parse(MutateContract(change), out _));
RejectContract("contract.reject-unknown-field", c => Requirement(c, 13)["nativeAgent"] = "EnemyAgent");
RejectContract("contract.reject-version", c => c["version"] = 2);
RejectContract("contract.reject-ground-without-navigation", c => Requirement(c, 13)["groundNavigation"] = null);
RejectContract("contract.reject-flying-with-navigation", c => Requirement(c, 42)["groundNavigation"] = JsonNode.Parse(Requirement(c, 13)["groundNavigation"]!.ToJsonString()));
RejectContract("contract.reject-unresolved-without-reason", c => Requirement(c, 22)["unresolvedReason"] = null);
RejectContract("contract.reject-unknown-agent-type", c => Requirement(c, 13)["groundNavigation"]!["agentTypeId"] = 12345);
RejectContract("contract.reject-closed-clearance", c => Requirement(c, 13)["unverified"]!.AsArray().RemoveAt(
    Requirement(c, 13)["unverified"]!.AsArray().Select(n => (string)n!).ToList().IndexOf("spawn-clearance")));
RejectContract("contract.reject-unknown-unverified-code", c => Requirement(c, 13)["unverified"]!.AsArray().Add("zz-guessed"));
RejectContract("contract.reject-duplicate-id", c => c["requirements"]!.AsArray().Add(JsonNode.Parse(Requirement(c, 13).ToJsonString())));
RejectContract("contract.reject-partial-balancing", c => Requirement(c, 13)["canBePushed"] = null);

// Enemy-side candidate fixtures against the Map stand-in.
using (var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "SpawnRequirements", "fixtures", "map-candidates.json"))))
{
    var candidates = MapCandidate.Load(fixture.RootElement);
    foreach (var expectation in fixture.RootElement.GetProperty("expectations").EnumerateArray())
    {
        uint enemy = expectation.GetProperty("enemy").GetUInt32(); string candidate = expectation.GetProperty("candidate").GetString()!;
        string expected = expectation.GetProperty("result").GetString()!;
        Case($"map-stand-in.{enemy}.{candidate}", () =>
        {
            string actual = candidates[candidate].Evaluate(R(enemy));
            return (actual == expected, $"expected={expected}; actual={actual}");
        });
    }
}

// Dependencies follow actual references; Enemy bindings are read from the real provider registry.
var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"), new RuntimeLimits());
kernel.BeginWorld(1); kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
using var module = new EnemyModule(kernel, RuntimeLogLevel.Off, () => true, _ => { });
using var manifest = JsonDocument.Parse(kernel.ExportManifest());
var enemyBindingRows = manifest.RootElement.GetProperty("registry").GetProperty("bindings").EnumerateArray()
    .Where(b => b.GetProperty("providerId").GetString() == ModuleDefinition.ProviderId).ToArray();
var registered = enemyBindingRows.Select(b => b.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
// Content dependencies read only a plan's binding pins, so the pins come straight from the registered Enemy rows.
string PinPlan(IEnumerable<JsonElement> rows) => JsonSerializer.Serialize(new { domain = "enemy", bindings = rows
    .Select(b => new { bindingId = b.GetProperty("id").GetString(), providerId = b.GetProperty("providerId").GetString() }).ToArray() });
// Two plans over the same registry: one pinning every registered Enemy binding, one a subset. Both expectations
// are the registered rows themselves, so neither has to be edited when the Enemy registry gains a binding.
string allEnemyPlan = PinPlan(enemyBindingRows);
string enemyPlan = PinPlan(enemyBindingRows.Where(b => b.GetProperty("id").GetString() is EnemyModule.HealBinding or EnemyModule.LimbBrokenBinding));
Case("dependencies.appearance-only", () =>
{
    var set = EnemyContentDependencies.Compute(new[] { "model:striker@9f1c", "material:glow@2", "model:striker@9f1c" }, Array.Empty<string>());
    return (!set.RequiresEnemyPack && set.EnemyBindings.Count == 0 && set.OtherProviders.Count == 0
        && set.Resources.SequenceEqual(new[] { "material:glow@2", "model:striker@9f1c" }), JsonSerializer.Serialize(set));
});
Case("dependencies.enemy-plan-bindings", () =>
{
    // A plan pinning enemy bindings requires the pack and contributes exactly the pins the registry declares.
    // Both sides are sorted, so the equality holds however the registry happens to order its rows.
    var set = EnemyContentDependencies.Compute(new[] { "model:striker@9f1c" }, new[] { allEnemyPlan });
    return (set.RequiresEnemyPack && set.EnemyBindings.OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(registered.OrderBy(id => id, StringComparer.Ordinal))
        && set.OtherProviders.Count == 0, JsonSerializer.Serialize(set) + "; registered=" + string.Join(',', registered));
});
Case("dependencies.enemy-bindings-follow-the-pins", () =>
{
    // A narrower plan reports only its own pins, never the whole registry, and every pin must be registered.
    var set = EnemyContentDependencies.Compute(new[] { "model:striker@9f1c" }, new[] { enemyPlan });
    return (set.RequiresEnemyPack && set.EnemyBindings.SequenceEqual(new[] { EnemyModule.HealBinding, EnemyModule.LimbBrokenBinding })
        && set.EnemyBindings.All(registered.Contains), JsonSerializer.Serialize(set) + "; registered=" + string.Join(',', registered));
});
Case("dependencies.domain-label-is-not-dependency", () =>
{
    var set = EnemyContentDependencies.Compute(Array.Empty<string>(),
        new[] { """{"domain":"enemy","bindings":[{"bindingId":"forge.module.gtfo.map.binding.door_open","providerId":"forge.module.gtfo.map"}]}""" });
    return (!set.RequiresEnemyPack && set.OtherProviders.SequenceEqual(new[] { "forge.module.gtfo.map" }), JsonSerializer.Serialize(set));
});
Rejects("dependencies.reject-plan-without-bindings", () => EnemyContentDependencies.Compute(Array.Empty<string>(), new[] { """{"domain":"enemy"}""" }));
Rejects("dependencies.reject-untrimmed-resource", () => EnemyContentDependencies.Compute(new[] { " model:striker" }, Array.Empty<string>()));

int failed = checks.Count(c => !c.Passed);
var report = new
{
    schemaVersion = 1, verification = "offline-data-contract-with-map-stand-in", gameExecuted = false, utc = DateTimeOffset.UtcNow,
    evidenceSha256 = provenance.EvidenceSha256, contractSha256 = Sha(Encoding.UTF8.GetBytes(contract)),
    frameworkAssemblySha256 = Sha(File.ReadAllBytes(typeof(RuntimeKernel).Assembly.Location)),
    passed = checks.Count - failed, failed, checks,
};
string reportPath = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Detail);
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {checks.Count - failed}/{checks.Count} spawn requirement checks; Map solver is a test stand-in; game NOT executed.");
return failed == 0 ? 0 : 1;

internal sealed record Row(string Id, bool Passed, string Detail);
