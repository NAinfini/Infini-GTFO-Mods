using System.Text.Json;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeEnemy.Tests.EnemySelectorQuery;
using ForgeRuntime.Framework;

if (args.Length != 2) { Console.Error.WriteLine("Usage: EnemySelectorQuery <report.json> <website directory>"); return 2; }
// The authoring catalog lives in the website repository, so that directory is a required argument: a run that
// cannot read it would skip exactly the row comparison this suite exists for.
string websiteRoot = Path.GetFullPath(args[1]);
var checks = new List<ProbeCheck>();
void Case(string id, Func<(bool Passed, string Expected, string Observed)> body)
{
    try
    {
        var (passed, expected, observed) = body();
        checks.Add(new(id, passed, expected, observed));
    }
    catch (Exception error) { checks.Add(new(id, false, "The case runs without harness errors.", error.ToString())); }
}

// --- the declaration against the authoring catalog -----------------------------------------------------------

Case("contract.capability-row-verbatim", () =>
{
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(websiteRoot, "catalog", "capability-catalog.json")));
    var catalog = document.RootElement.GetProperty("canonicalVocabulary").EnumerateArray()
        .Single(row => row.GetProperty("id").GetString() == EnemySelectorContract.CapabilityId);
    using var declared = JsonDocument.Parse(EnemySelectorContract.CapabilityRowJson);
    var capability = declared.RootElement;
    var sameDomains = Set(capability.GetProperty("graph").GetProperty("domains"))
        .SequenceEqual(Set(catalog.GetProperty("graph").GetProperty("domains")));
    var expected = "kind=category, label=labelZh, descriptionZh, graph field-for-field, domains as a set";
    var observed = $"kind={capability.GetProperty("kind").GetString()}/{catalog.GetProperty("category").GetString()}"
        + $"; label={capability.GetProperty("label").GetString()}/{catalog.GetProperty("labelZh").GetString()}"
        + $"; description={(capability.GetProperty("parameters").GetProperty("description").GetString() == catalog.GetProperty("descriptionZh").GetString())}"
        + $"; domains={sameDomains}"
        + $"; graph={SameGraph(capability.GetProperty("graph"), catalog.GetProperty("graph"))}";
    return (capability.GetProperty("id").GetString() == EnemySelectorContract.CapabilityId
        && capability.GetProperty("owner").GetString() == ModuleDefinition.ProviderId
        && capability.GetProperty("kind").GetString() == catalog.GetProperty("category").GetString()
        && capability.GetProperty("label").GetString() == catalog.GetProperty("labelZh").GetString()
        && capability.GetProperty("parameters").GetProperty("description").GetString() == catalog.GetProperty("descriptionZh").GetString()
        && sameDomains
        && SameGraph(capability.GetProperty("graph"), catalog.GetProperty("graph")), expected, observed);
});

Case("contract.binding-row-and-shape", () =>
{
    using var declared = JsonDocument.Parse(EnemySelectorContract.BindingRowJson);
    var binding = declared.RootElement;
    var capability = JsonDocument.Parse(EnemySelectorContract.CapabilityRowJson).RootElement;
    var outputIds = EnemySelectorContract.Shape.OutputPorts;
    var parameterIds = EnemySelectorContract.Shape.ParameterIds;
    var expected = $"id={EnemySelectorContract.BindingId}, capabilityId={EnemySelectorContract.CapabilityId}, "
        + $"providerId={ModuleDefinition.ProviderId}, handler={EnemySelectorContract.HandlerName}, role=observe, status=implemented, "
        + "shape=targets{}";
    var observed = $"id={binding.GetProperty("id").GetString()}, capabilityId={binding.GetProperty("capabilityId").GetString()}, "
        + $"providerId={binding.GetProperty("providerId").GetString()}, handler={binding.GetProperty("handler").GetString()}, "
        + $"role={binding.GetProperty("role").GetString()}, status={binding.GetProperty("status").GetString()}, "
        + $"shape={string.Join(",", outputIds)}{{{string.Join(",", parameterIds)}}}";
    var declaredGraph = capability.GetProperty("graph");
    var graphOutputs = declaredGraph.GetProperty("outputs").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray();
    var graphParameters = declaredGraph.GetProperty("parameters").EnumerateArray().Select(port => port.GetProperty("id").GetString()).ToArray();
    var passes = binding.GetProperty("id").GetString() == EnemySelectorContract.BindingId
        && binding.GetProperty("capabilityId").GetString() == EnemySelectorContract.CapabilityId
        && binding.GetProperty("providerId").GetString() == ModuleDefinition.ProviderId
        && binding.GetProperty("handler").GetString() == EnemySelectorContract.HandlerName
        && binding.GetProperty("role").GetString() == "observe"
        && binding.GetProperty("status").GetString() == "implemented"
        && outputIds.SequenceEqual(new[] { "targets" })
        && parameterIds.Count == 0
        && graphOutputs.SequenceEqual(outputIds)
        && graphParameters.SequenceEqual(parameterIds);
    return (passes, expected, observed);
});

// --- the candidate source, over a real module's own entity table --------------------------------------------

Case("candidates.ordinal-order", () =>
{
    using var world = new CandidateWorld();
    var second = world.Spawn(2, 20);
    var tenth = world.Spawn(10, 30);
    var seventh = world.Spawn(7, 40);
    var observed = string.Join("|", world.Module.CurrentCandidates().Select(item => item.Id));
    var ordered = new[] { seventh, second, tenth }.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => item.Id);
    return (world.Module.CurrentCandidates().Select(item => item.Id).SequenceEqual(ordered),
        "The source answers every tracked life in id ordinal order.", observed);
});

Case("candidates.despawn-and-replaced-instance", () =>
{
    using var world = new CandidateWorld();
    var first = world.Spawn(7, 10);
    var second = world.Spawn(8, 20);
    world.Module.TrackDespawn(new Enemies.EnemyAgent { GlobalID = 8, Pointer = new IntPtr(20) });
    var afterDespawn = world.Module.CurrentCandidates().Select(item => item.Id).ToArray();
    // A wrapper for the same id and the same native instance is the life already tracked: the answer is that same
    // reference, not a second life.
    var sameInstance = world.Module.TrackSpawn(new Enemies.EnemyAgent { GlobalID = 7, Pointer = new IntPtr(10) });
    // A different native instance under the same id is a replacement: the module tracks it as a new life and the
    // reference the retired wrapper answered with stops resolving.
    var replaced = world.Module.TrackSpawn(new Enemies.EnemyAgent { GlobalID = 7, Pointer = new IntPtr(11) });
    var remaining = world.Module.CurrentCandidates();
    return (afterDespawn.SequenceEqual(new[] { first.Id }) && sameInstance == first
        && replaced.Id == first.Id && replaced != first && remaining.Count == 1 && remaining[0] == replaced,
        "A despawn retires its own life, a re-registered wrapper for the same instance is not a second life, and a replaced native instance is.",
        $"after-despawn=[{string.Join("|", afterDespawn)}]; same-instance={sameInstance.Id}/{sameInstance.LifeEpoch}"
        + $"; replaced={replaced.Id}/{replaced.LifeEpoch}; remaining=[{string.Join("|", remaining.Select(item => item.Id + "/" + item.LifeEpoch))}]"
        + $"; retired={second.Id}");
});

Case("candidates.world-change", () =>
{
    using var world = new CandidateWorld();
    world.Spawn(7, 10);
    world.Start();
    world.Kernel.BeginWorld(2);
    var afterWorld = world.Module.CurrentCandidates();
    return (afterWorld.Count == 0 && world.Module.IsRegistered,
        "A new world clears the candidate set through the module's own world-change path.",
        $"candidates={afterWorld.Count}; module-registered={world.Module.IsRegistered}");
});

Case("candidates.refuses-after-release", () =>
{
    using var world = new CandidateWorld();
    world.Start();
    var tracked = world.Spawn(7, 10);
    var second = world.Spawn(8, 20);
    // One read for the whole table: 300 > the kernel's 256 ceiling, and the source answers all of it rather than
    // being truncated to a list that would look like a smaller world.
    for (var index = 0; index < 298; index++) world.Spawn();
    var count = world.Module.CurrentCandidates().Count;
    world.Module.Dispose();
    string code = "none";
    try { world.Module.CurrentCandidates(); }
    catch (RuntimeContractException error) { code = error.Code; }
    // The released module is gone from the kernel, so no candidate source is left behind for `gtfo.enemy`: the
    // same kind a released provider must not keep answering for.
    var kind = world.Kernel.EnumerateEntityCandidates("gtfo.enemy").Code;
    return (count == 300 && code == "enemy-module-unavailable" && kind == "entity-candidates-unavailable",
        "300 tracked lives are all answered, and a released module refuses instead of answering an empty world.",
        $"count={count}; read-after-release={code}; kernel-kind={kind}; tracked={tracked.Id}/{second.Id}");
});

// --- the production handler, dispatched through a real plan --------------------------------------------------

Case("selector.registered-rows", () =>
{
    using var scene = new SelectorScene();
    using var manifest = JsonDocument.Parse(scene.Kernel.ExportManifest());
    var registry = manifest.RootElement.GetProperty("registry");
    var binding = registry.GetProperty("bindings").EnumerateArray()
        .Single(row => row.GetProperty("id").GetString() == EnemySelector.BindingId);
    var support = manifest.RootElement.GetProperty("bindingSupport").EnumerateArray()
        .Single(row => row.GetProperty("bindingId").GetString() == EnemySelector.BindingId);
    // The kernel serves the kind through the production module's own source, not through the scene's table.
    var read = scene.Kernel.EnumerateEntityCandidates(EnemySelector.EntityKind);
    var readIds = read.Items.Select(item => item.Reference.Id).ToArray();
    var observed = $"binding={binding.GetProperty("capabilityId").GetString()}/{binding.GetProperty("providerId").GetString()}"
        + $"/{binding.GetProperty("handler").GetString()}/{binding.GetProperty("role").GetString()}; support={support.GetProperty("verification").GetString()}"
        + $"; source={read.Status}/{read.Code}/{string.Join("|", readIds)}";
    return (binding.GetProperty("capabilityId").GetString() == EnemySelector.CapabilityId
        && binding.GetProperty("providerId").GetString() == ModuleDefinition.ProviderId
        && binding.GetProperty("handler").GetString() == EnemySelector.HandlerName
        && binding.GetProperty("role").GetString() == "observe"
        && binding.GetProperty("status").GetString() == "implemented"
        && read.IsComplete && readIds.SequenceEqual(scene.CandidateIds()),
        "The module registers the selector capability, binding, evaluator and its own candidate source together.",
        observed);
});

Case("selector.complete-roster", () =>
{
    using var scene = new SelectorScene();
    scene.Dispatch("select.hostile");
    var targets = scene.Targets();
    return (scene.LastStep is { Result.Status: "succeeded", Result.Code: "committed" }
        && targets.SequenceEqual(scene.CandidateIds()) && targets.Length == scene.CandidateIds().Length,
        $"the module's own candidate set, answer={string.Join("|", scene.CandidateIds())}", $"status={scene.LastStep?.Result.Status}; targets={string.Join("|", targets)}");
});

Case("selector.no-implicit-faction-options", () =>
{
    using var scene = new SelectorScene();
    var refused = new[] { 0, 1, 2, 3, 4 }.All(relation => !scene.TryLoad(new object[] { relation, 0 }).Loaded);
    return (refused, "A roster has no implicit relation or empty-policy constants; legacy constants must be rejected.", $"refused={refused}");
});
Case("selector.parameterless-reload", () =>
{
    using var scene = new SelectorScene();
    scene.Load(Array.Empty<object>());
    scene.Dispatch("select.parameterless");
    return (scene.Targets().SequenceEqual(scene.CandidateIds()), "Parameter-free roster yields exactly its current candidate set.", string.Join("|", scene.Targets()));
});

Case("selector.source-refused", () =>
{
    using var scene = new SelectorScene();
    scene.ReleaseModule(); // the production source is gone with the module that owned it
    // The released provider takes the plans that referenced it with it, so the event has no consumer left: it is
    // ignored rather than dispatched, and the failure is the kind having no source at all.
    var published = scene.Publish("select.source-refused");
    var kind = scene.Kernel.EnumerateEntityCandidates(EnemySelector.EntityKind).Code;
    return (published.Status == "ignored" && scene.Recorded.Count == 0
        && kind == "entity-candidates-unavailable",
        "A released provider leaves no candidate source behind: the event it would have carried is ignored, and the kind it served answers nothing.",
        $"status={published.Status}; code={published.Code}; kind={kind}; recorded={scene.Recorded.Count}");
});

Case("selector.budget-refused", () =>
{
    using var scene = new SelectorScene(256); // 257 candidates: one over the kernel's per-query ceiling
    scene.Dispatch("select.budget");
    var code = scene.LastStep?.Result.Code;
    scene.Dispatch("select.budget-again");
    return (code == RuntimeAbiCodes.QueryBudget && scene.Recorded.Count == 0
        && scene.CandidateIds().Length == 257,
        $"{RuntimeAbiCodes.QueryBudget} with a table of 257 candidates, never a truncated list",
        $"code={code}; again={scene.LastStep?.Result.Code}; candidates={scene.CandidateIds().Length}");
});

Case("selector.deterministic-answer", () =>
{
    using var scene = new SelectorScene(4);
    scene.Dispatch("select.first");
    var first = scene.Targets();
    scene.Dispatch("select.second");
    var second = scene.Targets();
    return (first.SequenceEqual(second) && first.SequenceEqual(scene.CandidateIds()),
        "Two reads of an unchanged world answer the same references in the same order.",
        $"first=[{string.Join("|", first)}]; second=[{string.Join("|", second)}]");
});

static string[] Set(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();

/// <summary>Field-for-field comparison of a graph row: objects compare by property set, arrays element by element
/// in their own order, so a reordered port list is a different shape and an extra field is a difference.</summary>
static bool SameGraph(JsonElement declared, JsonElement catalog) => Same(declared, catalog);

static bool Same(JsonElement left, JsonElement right)
{
    if (left.ValueKind != right.ValueKind) return false;
    if (left.ValueKind == JsonValueKind.Object)
    {
        var leftFields = left.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        var rightFields = right.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        return leftFields.Length == rightFields.Length
            && leftFields.Zip(rightFields).All(pair => pair.First.Name == pair.Second.Name && Same(pair.First.Value, pair.Second.Value));
    }
    if (left.ValueKind == JsonValueKind.Array)
    {
        var leftItems = left.EnumerateArray().ToArray();
        var rightItems = right.EnumerateArray().ToArray();
        return leftItems.Length == rightItems.Length && leftItems.Zip(rightItems).All(pair => Same(pair.First, pair.Second));
    }
    return left.GetRawText() == right.GetRawText();
}

int failed = checks.Count(check => !check.Passed), passed = checks.Count - failed;
using var sdkStream = File.OpenRead(typeof(RuntimeKernel).Assembly.Location);
using var sdkHash = System.Security.Cryptography.SHA256.Create();
var report = new
{
    scenarioRevision = "selector-query-native", schemaVersion = 1,
    verification = "production-source-and-explicit-compiled-sdk-with-test-doubles", gameExecuted = false,
    note = "Every selector row the kernel serves is the production module's own — capability, binding, handler "
        + "shape, evaluator and candidate source — and the plan's trigger and recording action are this suite's "
        + "own, because the module publishes no attack or behaviour fact without the native game.",
    frameworkAssemblySha256 = Convert.ToHexString(sdkHash.ComputeHash(sdkStream)),
    utc = DateTimeOffset.UtcNow, passed, failed, checks
};
string reportPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks.Where(check => !check.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Observed);
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {passed}/{checks.Count} enemy selector query cases; failed {failed}; no GTFO execution.");
return failed == 0 ? 0 : 1;

internal sealed record ProbeCheck(string Id, bool Passed, string Expected, string Observed);
