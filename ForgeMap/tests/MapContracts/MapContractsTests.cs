using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.MapContracts;

// Compiled SDK with a synthetic entity table; not a native Map resolver test. No native game call.
public sealed class MapContractsTests
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (RuntimeContractException) { return; }
        throw new InvalidOperationException("FAIL: accepted " + name);
    }

    [Fact]
    public void map_consumes_the_shared_sdk_assembly()
    {
        var module = ForgeMap.ModuleDefinition.Create();
        var assembly = typeof(ForgeMap.ModuleDefinition).Assembly;
        Check(module.GetType().Assembly == typeof(RuntimeKernel).Assembly, "Map consumes the actual shared SDK assembly");
        // The game-independent definition can hold no evaluator and no native resolver; the observer the
        // binding answers from is attached by the native assembly, which this project does not compile.
        Check(module.Handlers.Count == 0 && (module.EntityResolvers?.Count ?? 0) == 0 && module.Evaluators.Count == 0,
            "The game-independent Map definition declares no handlers, evaluators or resolvers");
        using (var registry = JsonDocument.Parse(module.RegistryJson))
        {
            var capabilities = registry.RootElement.GetProperty("capabilities");
            var bindings = registry.RootElement.GetProperty("bindings");
            // The player selector and the three interaction rows whose catalog shape this provider still
            // declares: every capability the definition declares has exactly the one binding that implements it,
            // and every other binding it carries names a canonical Trigger row the runtime's trigger contract
            // owns — an id this provider binds without publishing a second shape for it.
            var declared = capabilities.EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray();
            var canonical = TriggerContracts.Module().RegistryJson;
            var canonicalIds = JsonDocument.Parse(canonical).RootElement.GetProperty("capabilities")
                .EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
            var bound = bindings.EnumerateArray().Select(b => b.GetProperty("capabilityId").GetString()!).ToArray();
            Check(declared.Length != 0 && declared.Distinct(StringComparer.Ordinal).Count() == declared.Length
                && declared.All(id => bound.Count(row => row == id) == 1)
                && bound.All(id => declared.Contains(id) || canonicalIds.Contains(id)),
                "The Map definition pairs every declared capability with its one implementing binding and binds the canonical Trigger rows without a shape of its own");
        }
        Check(assembly.GetType("TestWorld") == null, "Synthetic test world is not compiled into Map");
        Check(!assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("Unity") || a.Name.StartsWith("BepInEx")),
            "Map evidence work adds no native loader dependency");
    }

    [Fact]
    public void explicit_instances_queue_and_execute_without_merging()
    {
        using var world = new TestWorld();
        var keys = new[] { "layoutA/d0/l0/z7/placementA", "layoutA/d1/l0/z7/placementA", "layoutA/d0/l1/z7/placementA",
            "layoutA/d0/l0/z7/placementB", "layoutB/d0/l0/z7/placementA" };
        var targets = keys.Select(k => world.Add(k)).ToArray();
        for (var i = 0; i < targets.Length; i++) Check(world.Publish("instance-" + i, targets[i]).Status == "queued", "Explicit synthetic instance queues: " + keys[i]);
        var result = world.Kernel.Advance(1, true);
        Check(result.CommandsExecuted == targets.Length && world.Applied.SequenceEqual(targets), "SDK preserves supplied dimension/layer/layout/placement IDs without merging");
        Check(result.Commands.All(c => c.ResourceId == "map1.test.shared-room" && c.ResourceRevision == "fixture-revision"),
            "Shared resource revision does not replace individual target identity");
    }

    [Fact]
    public void queued_old_life_cannot_affect_new_life()
    {
        using var world = new TestWorld(); var old = world.Add("reused-native-id");
        world.Publish("before-life-change", old); var fresh = world.Add("reused-native-id", 2);
        var result = world.Kernel.Advance(1, true);
        Check(result.CommandsExecuted == 0 && result.Commands.Single().Result.Code == "stale-entity" && world.Applied.Count == 0,
            "Queued old life cannot affect the same ID in a new life");
        world.Publish("fresh-life", fresh, 2);
        Check(world.Kernel.Advance(2, true).CommandsExecuted == 1 && world.Applied.Single() == fresh, "Explicit current-life reference executes once");
    }

    [Fact]
    public void destroyed_object_rejects_its_delayed_callback()
    {
        using var world = new TestWorld(); var target = world.Add("destroyed-object");
        world.Publish("destroyed-before-dispatch", target); world.Lives.Remove(target.Id);
        Check(world.Kernel.Advance(1, true).Commands.Single().Result.Code == "stale-entity" && world.Applied.Count == 0,
            "Destroyed object rejects its delayed callback");
    }

    [Fact]
    public void world_reentry_clears_and_isolates_by_epoch()
    {
        using var world = new TestWorld(); var old = world.Add("reentered-world");
        world.Publish("old-queued", old); world.Kernel.BeginWorld(2);
        Check(world.Kernel.QueuedEvents == 0, "World reentry clears old queued work");
        Check(world.Publish("late-old-world", old, world: 1).Code == "stale-world", "Late previous-world event is rejected");
        var fresh = world.Add("reentered-world"); world.Publish("new-world", fresh);
        Check(world.Kernel.Advance(1, true).CommandsExecuted == 1 && world.Applied.Single() == fresh, "Reused native ID is isolated by world epoch");
        Reject(() => world.Kernel.BeginWorld(2), "Generation cannot reuse an existing world epoch");
    }

    [Fact]
    public void duplicate_completion_identity_does_not_replay()
    {
        using var world = new TestWorld(); var target = world.Add("scan-owner");
        world.Publish("completion", target);
        Check(world.Publish("completion", target).Status == "duplicate", "Duplicate completion identity does not enqueue again");
        Check(world.Publish("completion", target, 2).Code == "event-id-conflict", "Changed payload timing cannot reuse completion identity");
        Check(world.Kernel.Advance(1, true).CommandsExecuted == 1 && world.Kernel.Advance(1, true).CommandsExecuted == 0 && world.Applied.Count == 1,
            "Repeated simulation tick cannot replay the same synthetic completion");
    }

    [Fact]
    public void unknown_explicit_target_rejects_before_queueing()
    {
        using var world = new TestWorld(); var available = world.Add("same-name-but-other-instance");
        var missing = available with { Id = TestWorld.Prefix + ":explicitly-missing-instance" };
        var admission = world.Publish("missing-target", missing);
        Check(admission.Status == "rejected" && admission.Code == "stale-entity" && world.Kernel.QueuedEvents == 0
            && world.Kernel.Advance(1, true).CommandsExecuted == 0 && world.Applied.Count == 0,
            "Unknown explicit target rejects before queueing, without name or nearest-instance fallback");
    }

    [Fact]
    public void client_does_not_call_the_authoritative_handler()
    {
        using var world = new TestWorld(); var target = world.Add("client-target"); world.Publish("client-request", target);
        var result = world.Kernel.Advance(1, false);
        Check(result.CommandsExecuted == 0 && result.Events.Single().Code == "not-host" && world.Applied.Count == 0,
            "Client does not call the synthetic authoritative handler");
    }

    [Fact]
    public void plan_cannot_grant_itself_write_permissions()
    {
        using var world = new TestWorld(false);
        // The plan declares a write permission no binding in its own closure requires; permission-lock demands an
        // exact match, so a plan cannot grant itself a permission it was never bound to earn.
        var rejected = false;
        try { world.Kernel.LoadPlan(world.Plan(new[] { TestWorld.Permission, "map1.test.unearned-write" })); }
        catch (RuntimeContractException error) { rejected = error.Code == "permission-lock"; }
        Check(rejected, "Plan cannot grant itself write permissions");
    }

    [Fact]
    public void cancelled_scope_does_not_execute_queued_work()
    {
        using var world = new TestWorld(); world.Publish("cancel-me", world.Add("scope-target"));
        Check(world.Handle.CancelScope("map1.scope") == 1 && world.Kernel.Advance(1, true).CommandsExecuted == 0 && world.Applied.Count == 0,
            "Cancelled map-source scope does not execute queued work");
    }

    [Fact]
    public void module_unload_leaves_no_queued_effects()
    {
        using var world = new TestWorld(); var target = world.Add("unloaded-source"); world.Publish("unload-me", target);
        world.Handle.Dispose();
        Check(world.Publish("late-unloaded", target).Code == "module-unregistered", "Disposed module rejects late callback");
        Check(world.Kernel.Advance(1, true).CommandsExecuted == 0 && world.Applied.Count == 0, "Module unload does not leave queued effects");
    }

    [Fact]
    public void map_handle_observes_public_current_lifecycle()
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("map1.lifecycle.test", "1.0.0", RuntimeKernel.ApiVersion, "offline-fixture"));
        // The host registers the runtime's trigger contract before the Map definition that binds its rows.
        using var triggers = kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        using var map = kernel.RegisterModule(TestWorld.MapDefinition(), RuntimeLogLevel.Off);
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
        kernel.StopRuntime();
    }
}
