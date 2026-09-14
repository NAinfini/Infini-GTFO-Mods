using System.Text.Json;
using ForgeRuntime.Framework;

var checks = new List<string>();
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException("FAIL: " + name);
    checks.Add(name);
    Console.Error.WriteLine("PASS: " + name);
}
void Reject(System.Action action, string name)
{
    try { action(); }
    catch (RuntimeContractException) { checks.Add(name); return; }
    throw new InvalidOperationException("FAIL: accepted " + name);
}
var module = ForgeMap.ModuleDefinition.Create();
var assembly = typeof(ForgeMap.ModuleDefinition).Assembly;
Check(module.GetType().Assembly == typeof(RuntimeKernel).Assembly, "Map consumes the actual shared SDK assembly");
Check(module.Handlers.Count == 0 && module.BindingSupport.Count == 0 && (module.EntityResolvers?.Count ?? 0) == 0,
    "Production Map declares no unimplemented handlers, verification or resolvers");
using (var registry = JsonDocument.Parse(module.RegistryJson))
{
    Check(registry.RootElement.GetProperty("capabilities").GetArrayLength() == 0 && registry.RootElement.GetProperty("bindings").GetArrayLength() == 0,
        "Audit does not turn planned map capabilities into executable bindings");
}
Check(assembly.GetType("TestWorld") == null, "Synthetic test world is not compiled into Map");
Check(!assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("Unity") || a.Name.StartsWith("BepInEx")),
    "Map evidence work adds no native loader dependency");
{
    var world = new TestWorld();
    var keys = new[] { "layoutA/d0/l0/z7/placementA", "layoutA/d1/l0/z7/placementA", "layoutA/d0/l1/z7/placementA",
        "layoutA/d0/l0/z7/placementB", "layoutB/d0/l0/z7/placementA" };
    var targets = keys.Select(k => world.Add(k)).ToArray();
    for (var i = 0; i < targets.Length; i++) Check(world.Publish("instance-" + i, targets[i]).Status == "queued", "Explicit synthetic instance queues: " + keys[i]);
    var result = world.Kernel.Advance(1, true);
    Check(result.CommandsExecuted == targets.Length && world.Applied.SequenceEqual(targets), "SDK preserves supplied dimension/layer/layout/placement IDs without merging");
    Check(result.Commands.All(c => c.ResourceId == "map1.test.shared-room" && c.ResourceRevision == "fixture-revision"),
        "Shared resource revision does not replace individual target identity");
}
{
    var world = new TestWorld(); var old = world.Add("reused-native-id");
    world.Publish("before-life-change", old); var fresh = world.Add("reused-native-id", 2);
    var result = world.Kernel.Advance(1, true);
    Check(result.CommandsExecuted == 0 && result.Commands.Single().Result.Code == "stale-entity" && world.Applied.Count == 0,
        "Queued old life cannot affect the same ID in a new life");
    world.Publish("fresh-life", fresh, 2);
    Check(world.Kernel.Advance(2, true).CommandsExecuted == 1 && world.Applied.Single() == fresh, "Explicit current-life reference executes once");
}
{
    var world = new TestWorld(); var target = world.Add("destroyed-object");
    world.Publish("destroyed-before-dispatch", target); world.Lives.Remove(target.Id);
    Check(world.Kernel.Advance(1, true).Commands.Single().Result.Code == "stale-entity" && world.Applied.Count == 0,
        "Destroyed object rejects its delayed callback");
}
{
    var world = new TestWorld(); var old = world.Add("reentered-world");
    world.Publish("old-queued", old); world.Kernel.BeginWorld(2);
    Check(world.Kernel.QueuedEvents == 0, "World reentry clears old queued work");
    Check(world.Publish("late-old-world", old, world: 1).Code == "stale-world", "Late previous-world event is rejected");
    var fresh = world.Add("reentered-world"); world.Publish("new-world", fresh);
    Check(world.Kernel.Advance(1, true).CommandsExecuted == 1 && world.Applied.Single() == fresh, "Reused native ID is isolated by world epoch");
    Reject(() => world.Kernel.BeginWorld(2), "Generation cannot reuse an existing world epoch");
}
{
    var world = new TestWorld(); var target = world.Add("scan-owner");
    world.Publish("completion", target);
    Check(world.Publish("completion", target).Status == "duplicate", "Duplicate completion identity does not enqueue again");
    Check(world.Publish("completion", target, 2).Code == "event-id-conflict", "Changed payload timing cannot reuse completion identity");
    Check(world.Kernel.Advance(1, true).CommandsExecuted == 1 && world.Kernel.Advance(1, true).CommandsExecuted == 0 && world.Applied.Count == 1,
        "Repeated simulation tick cannot replay the same synthetic completion");
}
{
    var world = new TestWorld(); var available = world.Add("same-name-but-other-instance");
    var missing = available with { Id = TestWorld.Prefix + ":explicitly-missing-instance" };
    var admission = world.Publish("missing-target", missing);
    Check(admission.Status == "rejected" && admission.Code == "stale-entity" && world.Kernel.QueuedEvents == 0
        && world.Kernel.Advance(1, true).CommandsExecuted == 0 && world.Applied.Count == 0,
        "Unknown explicit target rejects before queueing, without name or nearest-instance fallback");
}
{
    var world = new TestWorld(); var target = world.Add("client-target"); world.Publish("client-request", target);
    var result = world.Kernel.Advance(1, false);
    Check(result.CommandsExecuted == 0 && result.Events.Single().Code == "not-host" && world.Applied.Count == 0,
        "Client does not call the synthetic authoritative handler");
}
{
    var world = new TestWorld(false);
    // The plan declares a write permission no binding in its own closure requires; permission-lock demands an
    // exact match, so a plan cannot grant itself a permission it was never bound to earn.
    var rejected = false;
    try { world.Kernel.LoadPlan(world.Plan(new[] { TestWorld.Permission, "map1.test.unearned-write" })); }
    catch (RuntimeContractException error) { rejected = error.Code == "permission-lock"; }
    Check(rejected, "Plan cannot grant itself write permissions");
}
{
    var world = new TestWorld(); world.Publish("cancel-me", world.Add("scope-target"));
    Check(world.Handle.CancelScope("map1.scope") == 1 && world.Kernel.Advance(1, true).CommandsExecuted == 0 && world.Applied.Count == 0,
        "Cancelled map-source scope does not execute queued work");
}
{
    var world = new TestWorld(); var target = world.Add("unloaded-source"); world.Publish("unload-me", target);
    world.Handle.Dispose();
    Check(world.Publish("late-unloaded", target).Code == "module-unregistered", "Disposed module rejects late callback");
    Check(world.Kernel.Advance(1, true).CommandsExecuted == 0 && world.Applied.Count == 0, "Module unload does not leave queued effects");
}
{
    var kernel = new RuntimeKernel(new RuntimeIdentity("map1.lifecycle.test", "1.0.0", RuntimeKernel.ApiVersion, "offline-fixture"));
    using var map = kernel.RegisterModule(ForgeMap.ModuleDefinition.Create());
    var observed = new List<RuntimeLifecycleKind>();
    using var observer = map.ObserveLifecycle(value => observed.Add(value.Kind));
    Check(observed.SequenceEqual(new[] { RuntimeLifecycleKind.Snapshot }), "Map handle observes public current lifecycle snapshot");
    kernel.BeginWorld(5);
    Check(observed.Last() == RuntimeLifecycleKind.WorldChanged && kernel.WorldEpoch == 5, "Map receives public world invalidation without private bridge access");
    Check(kernel.StartRuntime(() => {}) && kernel.StartupState == RuntimeStartupState.Ready && !kernel.IsRegistrationOpen,
        "Public startup freezes registration and exposes readiness");
    kernel.Advance(1, true);
    Check(observed.Last() == RuntimeLifecycleKind.TickAdvanced, "Map observes the one public simulation tick");
    var count = observed.Count; observer.Dispose(); kernel.BeginWorld(6);
    Check(observed.Count == count && !observer.IsActive, "Disposed Map observer receives no late lifecycle callback");
}
Console.WriteLine(JsonSerializer.Serialize(new {
    verification = "compiled-sdk-with-synthetic-entity-table; not a native Map resolver test",
    nativeGameExecuted = false, productionMap = assembly.GetName().FullName,
    sharedSdk = typeof(RuntimeKernel).Assembly.GetName().FullName, passed = checks.Count, checks
}, new JsonSerializerOptions { WriteIndented = true }));
