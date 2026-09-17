using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;
using ForgeTrigger.Targeting;

// Test-only modules exercise the compiled SDK. They never call native game APIs. The authoring catalog is the
// contract every declared capability publishes, and it lives in the website repository: the website directory is
// therefore a required argument, because a run that cannot read it would skip exactly the comparison this suite
// exists for.
if (args.Length != 4 || args[0] is not ("export" or "check"))
    throw new ArgumentException("Usage: <export|check> <ForgeTrigger directory> <evidence directory> <website directory>");
var root = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
var catalogPath = Path.Combine(Path.GetFullPath(args[3]), "catalog", "capability-catalog.json");
if (!File.Exists(catalogPath))
    throw new FileNotFoundException("The authoring catalog this suite compares against is required: " + catalogPath);
Directory.CreateDirectory(output);
var seed = File.ReadAllText(Path.Combine(root, "tests", "fixtures", "t1", "seed.json"));
var assertions = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    assertions++;
    Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string code, string name)
{
    try { action(); }
    catch (RuntimeContractException error) { Check(error.Code == code, name + " [" + error.Code + "]"); return; }
    throw new Exception("FAIL: accepted " + name);
}
// The production declaration tables are the Trigger vocabulary, and the catalog is that vocabulary's contract:
// every declared row has a capability, a binding, an evaluator and a shape in the module, and the capability it
// publishes is the catalog row port for port. Nothing outside the tables may be advertised. A value-only row is
// bound as `evaluate` and a row that observes the world as `observe`, which is the role the kernel resolves a
// `query` step's binding through.
var production = ForgeTrigger.ModuleDefinition.Create();
var declared = PureModule.Nodes
    .Select(node => new Declared(node.CapabilityId, node.BindingId, node.HandlerName, "evaluate", node.Evaluate, node.Shape))
    .Concat(ObservedQueryModule.Nodes
        .Select(node => new Declared(node.CapabilityId, node.BindingId, node.HandlerName, "observe", node.Evaluate, node.Shape)))
    .Concat(ObservedSpaceNodes.Nodes
        .Select(node => new Declared(node.CapabilityId, node.BindingId, node.HandlerName, "observe", node.Evaluate, node.Shape)))
    .ToArray();
var declaredRegistry = RuntimeJson.Parse(production.RegistryJson);
var declaredCapabilities = declaredRegistry.GetProperty("capabilities").EnumerateArray().ToArray();
var declaredBindings = declaredRegistry.GetProperty("bindings").EnumerateArray().ToArray();
var catalogVocabulary = JsonDocument.Parse(File.ReadAllText(catalogPath)).RootElement
    .GetProperty("canonicalVocabulary").EnumerateArray().ToArray();
Check(declaredCapabilities.Length == declared.Length && declaredBindings.Length == declared.Length
    && production.Evaluators.Count == declared.Length && production.Shapes.Count == declared.Length
    && production.BindingSupport.Count == declared.Length && production.Handlers.Count == 0
    && !production.RegistryJson.Contains("test.trigger", StringComparison.Ordinal),
    "production Trigger advertises exactly its declaration tables, no test handler and no command handler");
foreach (var node in declared)
{
    var capability = declaredCapabilities.SingleOrDefault(row => row.GetProperty("id").GetString() == node.CapabilityId);
    var binding = declaredBindings.SingleOrDefault(row => row.GetProperty("id").GetString() == node.BindingId);
    var catalogRow = catalogVocabulary.SingleOrDefault(row => row.GetProperty("id").GetString() == node.CapabilityId);
    Check(capability.ValueKind == JsonValueKind.Object && binding.ValueKind == JsonValueKind.Object
        && binding.GetProperty("capabilityId").GetString() == node.CapabilityId
        && binding.GetProperty("handler").GetString() == node.HandlerName
        && binding.GetProperty("role").GetString() == node.Role
        && binding.GetProperty("status").GetString() == "implemented"
        && production.Evaluators.TryGetValue(node.HandlerName, out var evaluator) && evaluator == node.Evaluate
        && production.Shapes.TryGetValue(node.HandlerName, out var shape) && ReferenceEquals(shape, node.Shape)
        && production.BindingSupport.Any(support => support.BindingId == node.BindingId),
        "declared row has its capability, evaluate binding, evaluator, shape and support: " + node.CapabilityId);
    var declaredRow = capability.ValueKind == JsonValueKind.Object && catalogRow.ValueKind == JsonValueKind.Object;
    Check(declaredRow
        && catalogRow.GetProperty("category").GetString() == capability.GetProperty("kind").GetString()
        && catalogRow.GetProperty("labelZh").GetString() == capability.GetProperty("label").GetString()
        && catalogRow.GetProperty("descriptionZh").GetString() == capability.GetProperty("parameters").GetProperty("description").GetString(),
        "declared capability kind, label and description equal the catalog row: " + node.CapabilityId);
    Check(declaredRow && SameGraph(catalogRow.GetProperty("graph"), capability.GetProperty("graph")),
        "declared capability graph equals the catalog row port for port: " + node.CapabilityId);
}
// Domains compare as a set, the way the two repositories compare every shared graph; every other field, including
// a variadic port template, is compared field for field.
bool SameGraph(JsonElement expected, JsonElement actual)
{
    var expectedDomains = expected.GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()!).ToArray();
    var actualDomains = actual.GetProperty("domains").EnumerateArray().Select(domain => domain.GetString()!).ToArray();
    if (expectedDomains.Length != actualDomains.Length || !expectedDomains.All(actualDomains.Contains)) return false;
    var left = expected.EnumerateObject().Where(field => field.Name != "domains").ToDictionary(field => field.Name, field => field.Value);
    var right = actual.EnumerateObject().Where(field => field.Name != "domains").ToDictionary(field => field.Name, field => field.Value);
    return left.Count == right.Count
        && left.All(field => right.TryGetValue(field.Key, out var value) && AuthoringContractTests.Same(field.Value, value));
}
var registration = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
using (var registered = registration.RegisterModule(production, RuntimeLogLevel.Off))
{
    var snapshot = RuntimeJson.Parse(registration.ExportManifest()).GetProperty("registry");
    Check(snapshot.GetProperty("capabilities").EnumerateArray().Select(row => row.GetProperty("id").GetString()!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(declared.Select(node => node.CapabilityId).OrderBy(id => id, StringComparer.Ordinal))
        && snapshot.GetProperty("bindings").EnumerateArray().Select(row => row.GetProperty("id").GetString()!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(declared.Select(node => node.BindingId).OrderBy(id => id, StringComparer.Ordinal)),
        "the registered manifest carries exactly the declared rows");
    var before = registration.ExportManifest();
    Reject(() => registration.RegisterModule(production, RuntimeLogLevel.Off), "provider-conflict", "duplicate provider rejected");
    Check(before == registration.ExportManifest(), "duplicate rejection is atomic");
    registration.BeginWorld(1);
    Check(registration.Advance(0, true).CommandsExecuted == 0 && registration.QueuedEvents == 0,
        "production registration starts no work");
}
Check(typeof(ForgeTrigger.ModuleDefinition).Assembly.GetType(typeof(RuntimeKernel).FullName!) == null,
    "Trigger contains no copied kernel");
Check(typeof(ForgeTrigger.ModuleDefinition).Assembly.GetReferencedAssemblies()
    .Where(a => a.Name!.StartsWith("Forge", StringComparison.Ordinal)).All(a => a.FullName == typeof(RuntimeKernel).Assembly.FullName),
    "Trigger uses the single compiled SDK");
// One canonical trigger row has exactly one owner. A registration that redeclares a capability the Trigger
// contract already owns is refused rather than merged or overridden, so a domain package can bind a canonical id
// but can never publish a second shape for it; the refused registration leaves the registry exactly as it was.
var rivalKernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "canonical-contract-audit-no-game"));
using (rivalKernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off))
{
    var beforeRival = rivalKernel.ExportManifest();
    Reject(() => rivalKernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off), "provider-conflict",
        "a second trigger contract provider is rejected");
    var rival = new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = "forge.module.rival.weapon", kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = new[]
        {
            new
            {
                id = "forge.trigger.input.equipped", owner = "forge.module.rival.weapon", kind = "trigger",
                label = "装备切入", version = "1.0.0", parameters = new { description = "玩家切到了某件装备。" },
                graph = new
                {
                    domains = new[] { "weapon" }, execution = "host", inputs = Array.Empty<object>(),
                    outputs = new object[] { new { id = "next", type = "execution" } }, parameters = Array.Empty<object>()
                }
            }
        },
        bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
    Reject(() => rivalKernel.RegisterModule(rival, RuntimeLogLevel.Off), "capability-conflict",
        "a domain module redeclaring an owned trigger capability is rejected");
    Check(beforeRival == rivalKernel.ExportManifest(), "the refused duplicate left the registry unchanged");
}
if (args[0] == "export")
{
    using var harness = new Harness(seed);
    File.WriteAllText(Path.Combine(output, "sdk-manifest.json"), harness.Kernel.ExportManifest());
    var canonicalKernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "canonical-contract-audit-no-game"));
    using (canonicalKernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off))
    using (canonicalKernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off))
        File.WriteAllText(Path.Combine(output, "sdk-canonical-manifest.json"), canonicalKernel.ExportManifest());
    Console.WriteLine($"PASS {assertions} export/architecture assertions; synthetic manifest only.");
    return;
}
AuthoringContractTests.Run(Path.Combine(output, "authoring-registration-cases.json"), Check);
var wire = RuntimeJson.Parse(File.ReadAllText(Path.Combine(output, "wire-cases.json")));
foreach (var row in wire.GetProperty("cases").EnumerateArray())
{
    using var harness = new Harness(seed);
    var id = row.GetProperty("id").GetString()!;
    var plan = row.GetProperty("plan").GetRawText();
    if (row.GetProperty("accepted").GetBoolean())
    {
        harness.Kernel.LoadPlan(plan);
        Check(harness.Kernel.LoadedPlans == 1, "shared plan accepted: " + id);
    }
    else
    {
        Reject(() => harness.Kernel.LoadPlan(plan), row.GetProperty("code").GetString()!, "shared plan rejected: " + id);
        Check(harness.Kernel.LoadedPlans == 0 && harness.Kernel.QueuedEvents == 0, "rejected plan has no work: " + id);
    }
}
var valid = wire.GetProperty("cases").EnumerateArray().First(r => r.GetProperty("id").GetString() == "linear");
var validPlan = valid.GetProperty("plan").GetRawText();
var chainPlan = wire.GetProperty("cases").EnumerateArray().First(r => r.GetProperty("id").GetString() == "linear-chain").GetProperty("plan").GetRawText();
void Exercise(string name, Action<Harness> test, string? plan = null)
{
    using var h = new Harness(seed);
    h.Kernel.LoadPlan(plan ?? validPlan);
    h.Kernel.BeginWorld(1); h.Kernel.Advance(0, true);
    test(h); Console.WriteLine("SCENARIO: " + name);
}
Exercise("queue/commit separation, identity and duplicate delivery", h =>
{
    Check(h.Handle.Publish(h.Event()).Status == "queued" && h.Calls.Count == 0, "queued is not committed");
    Check(h.Handle.Publish(h.Event()).Status == "duplicate", "duplicate event before dispatch");
    var result = h.Kernel.Advance(1, true);
    Check(result.CommandsExecuted == 1 && result.Commands.Single().Result.Status == CommandStatuses.Succeeded, "one synthetic action executes");
    var call = h.Calls.Single();
    Check(call.GetEntityInput("target").Id == "test.entity:target", "the action receives the entity the event's own payload names");
    Check(call.Inputs.GetProperty("value").GetDouble() == 5 && call.NodeId == "Record", "typed input and author node preserved");
    Check(h.Handle.Publish(h.Event()).Status == "duplicate" && h.Kernel.Advance(1, true).CommandsExecuted == 0, "same tick does not replay");
});
// Life is revalidated where the entity is read: the payload port the plan wires is the only entity channel left.
Exercise("stale life: test.entity:target", h =>
{
    h.Handle.Publish(h.Event()); h.Lives["test.entity:target"] = 2;
    var receipt = h.Kernel.Advance(1, true).Commands.Single();
    Check(h.Calls.Count == 0 && receipt.Result.Code == "stale-entity", "stale life rejected before invocation: test.entity:target");
});
Exercise("missing recipient never falls back to source", h =>
{
    var e = h.Event() with { Outputs = RuntimeJson.From(new { amount = 5, enabled = true, position = new[] { 0, 0, 0 }, distance = 10, maybe_target = (EntityReference?)null }) };
    Check(h.Handle.Publish(e).Code == "missing-field" && h.Calls.Count == 0, "missing recipient rejected");
});
Exercise("missing resolver", h =>
{
    var e = h.Event() with { Outputs = RuntimeJson.From(new { target = new EntityReference("unknown.entity:target", 1, 1), amount = 5, enabled = true, position = new[] { 0, 0, 0 }, distance = 10, maybe_target = (EntityReference?)null }) };
    Check(h.Handle.Publish(e).Code == "entity-resolver", "missing resolver rejected without guessing");
});
Exercise("world transition", h =>
{
    h.Handle.Publish(h.Event()); h.Kernel.BeginWorld(2);
    Check(h.Kernel.Advance(1, true).CommandsExecuted == 0 && h.Calls.Count == 0, "world transition clears old work");
});
Exercise("scope cancellation", h =>
{
    h.Handle.Publish(h.Event()); h.Handle.CancelScope("scope");
    Check(h.Kernel.Advance(1, true).CommandsExecuted == 0, "cancelled scope cannot execute");
});
Exercise("client authority", h =>
{
    h.Handle.Publish(h.Event());
    Check(h.Kernel.Advance(1, false).CommandsExecuted == 0 && h.Calls.Count == 0, "client cannot invoke action");
});
Exercise("plan unload", h =>
{
    h.Handle.Publish(h.Event()); h.Kernel.UnloadPlan("t1-linear");
    Check(h.Kernel.Advance(1, true).CommandsExecuted == 0, "unloaded plan cannot execute");
});
Exercise("unknown commit stops chain", h =>
{
    h.Outcome = "unknown"; h.Handle.Publish(h.Event());
    var result = h.Kernel.Advance(1, true);
    Check(h.Calls.Count == 1 && result.Commands.Single().Result.CommitState == CommitStates.Unknown, "unknown is not success and second action is not invoked");
}, chainPlan);
File.WriteAllText(Path.Combine(output, "csharp-result.json"), JsonSerializer.Serialize(new { assertions, status = "passed", gameVerified = false }));
Console.WriteLine($"PASS {assertions} C# assertions. No native API, installation, network or game validation.");

/// <summary>One declared row of the Trigger vocabulary, whichever table it came from: what the contract suite
/// compares against the catalog and against the registered module.</summary>
sealed record Declared(string CapabilityId, string BindingId, string HandlerName, string Role,
    EvaluatorHandler Evaluate, HandlerShape Shape);

sealed class Harness : IDisposable
{
    public readonly RuntimeKernel Kernel = new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
    public readonly Dictionary<string, long> Lives = new() { ["test.entity:target"] = 1 };
    public readonly List<CommandContext> Calls = new();
    public readonly RuntimeModuleHandle Handle;
    public string Outcome = "success";
    public Harness(string seed)
    {
        var bindings = RuntimeJson.Parse(seed).GetProperty("bindings").EnumerateArray().ToArray();
        var support = bindings.Select(b => new BindingSupport(b.GetProperty("id").GetString()!, "implementation-only",
            b.GetProperty("handler").GetString() == "record" ? new[] { "test.permission.record" } : Array.Empty<string>())).ToArray();
        Handle = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, seed,
            new Dictionary<string, CommandHandler> { ["record"] = context =>
            {
                Calls.Add(context);
                return Outcome == "unknown" ? CommandResult.FailedUnknown("synthetic-unknown") : CommandResult.Succeeded(RuntimeJson.From(new { value = context.Inputs.GetProperty("value").GetDouble() }));
            } }, support, new Dictionary<string, Func<EntityReference, bool>>
            { ["test.entity"] = e => e.WorldEpoch == Kernel.WorldEpoch && Lives.TryGetValue(e.Id, out var life) && e.LifeEpoch == life })
        {
            // Every declared port of both doubles, so the shape table describes exactly what each one reads.
            Evaluators = new Dictionary<string, EvaluatorHandler> { ["add"] = context =>
                RuntimeJson.From(new { value = context.Inputs.GetProperty("a").GetDouble() + context.Inputs.GetProperty("b").GetDouble() }) },
            Shapes = new Dictionary<string, HandlerShape> {
                ["record"] = new HandlerShape().Inputs("value").Outputs("result"),
                ["add"] = new HandlerShape().Inputs("a", "b").Outputs("value") }
        }, RuntimeLogLevel.Off);
    }
    public RuntimeEvent Event() => new("event-1", "test.trigger.binding.event", 1, 1, "scope",
        RuntimeJson.From(new { target = new EntityReference("test.entity:target", 1, 1), amount = 5, enabled = true, position = new[] { 0, 0, 0 }, distance = 10, maybe_target = (EntityReference?)null }));
    public void Dispose() => Handle.Dispose();
}
