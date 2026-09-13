using System.Text.Json;
using ForgeRuntime.Framework;

// Test-only modules exercise the compiled SDK. They never call native game APIs.
if (args.Length != 3 || args[0] is not ("export" or "check"))
    throw new ArgumentException("Usage: <export|check> <ForgeTrigger directory> <evidence directory>");
var root = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
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
var production = ForgeTrigger.ModuleDefinition.Create();
Check(production.Handlers.Count == 0 && production.BindingSupport.Count == 0,
    "production Trigger does not advertise test handlers or support");
var registration = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
using (var registered = registration.RegisterModule(production))
{
    var snapshot = RuntimeJson.Parse(registration.ExportManifest()).GetProperty("registry");
    Check(snapshot.GetProperty("capabilities").GetArrayLength() == 0 && snapshot.GetProperty("bindings").GetArrayLength() == 0,
        "production Trigger manifest remains empty");
    var before = registration.ExportManifest();
    Reject(() => registration.RegisterModule(production), "provider-conflict", "duplicate provider rejected");
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
if (args[0] == "export")
{
    using var harness = new Harness(seed);
    File.WriteAllText(Path.Combine(output, "sdk-manifest.json"), harness.Kernel.ExportManifest());
    var canonicalKernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "canonical-contract-audit-no-game"));
    using (canonicalKernel.RegisterModule(CombatContracts.Module()))
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
    var grants = row.GetProperty("grants").EnumerateArray().Select(x => x.GetString()!).ToArray();
    if (row.GetProperty("accepted").GetBoolean())
    {
        harness.Kernel.LoadPlan(plan, grants);
        Check(harness.Kernel.LoadedPlans == 1, "shared plan accepted: " + id);
    }
    else
    {
        Reject(() => harness.Kernel.LoadPlan(plan, grants), row.GetProperty("code").GetString()!, "shared plan rejected: " + id);
        Check(harness.Kernel.LoadedPlans == 0 && harness.Kernel.QueuedEvents == 0, "rejected plan has no work: " + id);
    }
}
var valid = wire.GetProperty("cases").EnumerateArray().First(r => r.GetProperty("id").GetString() == "linear");
var validPlan = valid.GetProperty("plan").GetRawText();
var chainPlan = wire.GetProperty("cases").EnumerateArray().First(r => r.GetProperty("id").GetString() == "linear-chain").GetProperty("plan").GetRawText();
void Exercise(string name, Action<Harness> test, string? plan = null)
{
    using var h = new Harness(seed);
    h.Kernel.LoadPlan(plan ?? validPlan, new[] { "test.permission.record" });
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
    Check(call.GetEntityInput("target").Id == "test.entity:target" && call.Source!.Id == "test.entity:source", "source and explicit recipient remain distinct");
    Check(call.Inputs.GetProperty("value").GetDouble() == 5 && call.NodeId == "Record", "typed input and author node preserved");
    Check(h.Handle.Publish(h.Event()).Status == "duplicate" && h.Kernel.Advance(1, true).CommandsExecuted == 0, "same tick does not replay");
});
foreach (var key in new[] { "test.entity:target", "test.entity:source" })
    Exercise("stale life: " + key, h =>
    {
        h.Handle.Publish(h.Event()); h.Lives[key] = 2;
        var receipt = h.Kernel.Advance(1, true).Commands.Single();
        Check(h.Calls.Count == 0 && receipt.Result.Code == "stale-entity", "stale life rejected before invocation: " + key);
    });
Exercise("missing recipient never falls back to source", h =>
{
    var e = h.Event() with { Outputs = RuntimeJson.From(new { amount = 5, enabled = true, position = new[] { 0, 0, 0 }, distance = 10, maybe_target = (EntityReference?)null }) };
    Check(h.Handle.Publish(e).Code == "missing-field" && h.Calls.Count == 0, "missing recipient rejected");
});
Exercise("missing resolver", h =>
{
    var e = h.Event() with { Source = new EntityReference("unknown.entity:source", 1, 1) };
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

sealed class Harness : IDisposable
{
    public readonly RuntimeKernel Kernel = new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
    public readonly Dictionary<string, long> Lives = new() { ["test.entity:target"] = 1, ["test.entity:source"] = 1 };
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
            { ["test.entity"] = e => e.WorldEpoch == Kernel.WorldEpoch && Lives.TryGetValue(e.Id, out var life) && e.LifeEpoch == life }));
    }
    public RuntimeEvent Event() => new("event-1", "test.trigger.binding.event", 1, 1, "scope",
        RuntimeJson.From(new { target = new EntityReference("test.entity:target", 1, 1), amount = 5, enabled = true, position = new[] { 0, 0, 0 }, distance = 10, maybe_target = (EntityReference?)null }),
        new EntityReference("test.entity:source", 1, 1));
    public void Dispose() => Handle.Dispose();
}
