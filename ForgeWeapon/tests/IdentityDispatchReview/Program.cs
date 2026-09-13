using ForgeRuntime.Framework;
using ForgeWeapon;
using System.Text.Json;

// Compiled public SDK dispatcher and actual EquipmentIdentitySession; NO native game call.
// The action below is deliberately a fixture binding, not a Forge gameplay capability.
var results = new List<object>(); int failures = 0;
void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
void Test(string name, Action test)
{
    try { test(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; results.Add(new { name, status = "failed", error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Rejected(string code, Action action)
{
    try { action(); }
    catch (RuntimeContractException error) { Check(error.Code == code, "Expected " + code + "; got " + error.Code); return; }
    throw new Exception("Expected rejection: " + code);
}
void NoInvocation(Fixture f, string code)
{
    var tick = f.Kernel.Advance(1, true);
    Check(tick.Commands.Count == 1, "Expected a terminal command receipt");
    Check(tick.Commands[0].Result.Code == code, "Unexpected rejection: " + tick.Commands[0].Result.Code);
    Check(tick.Commands[0].Result.CommitState == CommitStates.None, "Precondition failure claimed commitment");
    Check(f.HandlerEntries == 0 && f.FixtureEffects == 0, "Stale command reached the fixture action");
}

Test("live-public-dispatch-executes-once", () =>
{
    using var f = new Fixture(); Check(f.Queue().Status == "queued", "not admitted");
    var tick = f.Kernel.Advance(1, true);
    Check(tick.Commands.Count == 1 && tick.Commands[0].Result.Status == CommandStatuses.Succeeded, "current command failed");
    Check(f.HandlerEntries == 1 && f.FixtureEffects == 1, "wrong fixture count");
    f.Kernel.Advance(1, true); Check(f.FixtureEffects == 1, "same tick replayed work");
});
Test("duplicate-publish-does-not-double-invoke", () =>
{
    using var f = new Fixture(); Check(f.Queue().Status == "queued", "not admitted");
    Check(f.Publish().Status == "duplicate", "same event not deduplicated");
    f.Kernel.Advance(1, true); Check(f.FixtureEffects == 1, "duplicate event produced another action");
});
Test("unknown-equipment-is-rejected-before-queue", () =>
{
    using var f = new Fixture(); var result = f.Publish(target: f.Item.Entity with { Id = "gtfo.equipment:missing" });
    Check(result.Status == "rejected" && result.Code == "stale-entity", "unknown recipient accepted");
    Check(f.Kernel.QueuedEvents == 0 && f.FixtureEffects == 0, "unknown entity started work");
});
Test("native-disappearance-after-queue-prevents-invocation", () =>
{
    using var f = new Fixture(); f.Queue(); f.NativeLive = false; NoInvocation(f, "stale-entity");
});
Test("owner-disappearance-after-queue-prevents-invocation", () =>
{
    using var f = new Fixture(); f.Queue(); f.OwnerLive = false; NoInvocation(f, "stale-entity");
});
Test("removed-equipment-after-queue-prevents-invocation", () =>
{
    using var f = new Fixture(); f.Queue(); f.Session.Remove(f.Item.Entity); NoInvocation(f, "stale-entity");
});
Test("same-native-key-new-life-does-not-receive-old-command", () =>
{
    using var f = new Fixture(); f.Queue(); f.Session.Remove(f.Item.Entity);
    f.Session.Record(f.Item with { Entity = f.Item.Entity with { LifeEpoch = 2 } }); NoInvocation(f, "stale-entity");
});
Test("world-reset-drops-old-queue", () =>
{
    using var f = new Fixture(); f.Queue(); f.Kernel.BeginWorld(8); var tick = f.Kernel.Advance(0, true);
    Check(tick.Commands.Count == 0 && f.Kernel.QueuedEvents == 0 && f.FixtureEffects == 0, "old world work survived");
});
Test("unregistered-resolver-prevents-queued-invocation", () =>
{
    using var f = new Fixture(); f.Queue(); f.Session.Dispose(); NoInvocation(f, "entity-resolver");
});
Test("queued-ownership-ABA-rejected-by-process-local-ticket", () =>
{
    using var f = new Fixture(); f.Queue();
    f.Session.Record(f.Item with { Owner = f.Owner with { Id = "fixture.player:b" } }); f.Session.Record(f.Item);
    var tick = f.Kernel.Advance(1, true); var result = tick.Commands.Single().Result;
    Check(f.HandlerEntries == 1, "Physical instance unexpectedly ceased to exist");
    Check(result.Status == CommandStatuses.Rejected && result.Code == "equipment.observation-changed", "Old ownership ticket accepted");
    Check(result.CommitState == CommitStates.None && f.FixtureEffects == 0, "Rejected ticket committed a fixture effect");
});
Test("queued-unwield-rejects-old-use-but-preserves-physical-instance", () =>
{
    using var f = new Fixture(); f.Queue(); f.Session.Record(f.Item with { IsWielded = false });
    var tick = f.Kernel.Advance(1, true);
    Check(f.Session.IsCurrent(f.Item.Entity), "Unwield destroyed physical identity");
    Check(tick.Commands.Single().Result.Code == "equipment.observation-changed" && f.FixtureEffects == 0, "Old wielded use survived");
});
Test("owner-life-change-rejects-queued-use", () =>
{
    using var f = new Fixture(); f.Queue(); f.Session.Record(f.Item with { Owner = f.Owner with { LifeEpoch = 2 } });
    var tick = f.Kernel.Advance(1, true);
    Check(tick.Commands.Single().Result.Code == "equipment.observation-changed" && f.FixtureEffects == 0, "Old owner life used equipment");
});
Test("client-tick-rejects-queued-host-work", () =>
{
    using var f = new Fixture(); f.Queue(); var tick = f.Kernel.Advance(1, false);
    Check(tick.CommandsExecuted == 0 && f.FixtureEffects == 0 && f.Session.Count == 0, "client executed host work");
    Check(tick.Events.Any(e => e.Code == "not-host"), "authority failure lost its reason");
});
Test("scope-cancel-rejects-queued-work", () =>
{
    using var f = new Fixture(); f.Queue(); f.Dispatch.CancelScope("fixture.scope");
    var tick = f.Kernel.Advance(1, true);
    Check(f.FixtureEffects == 0 && tick.Events.Any(e => e.Status == "cancelled"), "cancelled scope executed");
});
Test("native-reader-exception-is-precondition-rejection", () =>
{
    using var f = new Fixture(); f.Queue(); f.ThrowNativeRead = true; NoInvocation(f, "stale-entity");
});
Test("source-reference-is-revalidated-independently-of-recipient", () =>
{
    using var f = new Fixture(); var source = f.Item with { Entity = f.Item.Entity with { Id = "gtfo.equipment:source" }, Slot = "GearSpecial" };
    f.Session.Record(source); f.Queue(source.Entity); f.Session.Remove(source.Entity); NoInvocation(f, "stale-entity");
});
Test("post-commit-source-cleanup-does-not-undo-fixture-effect", () =>
{
    using var f = new Fixture(); f.Queue(); f.Kernel.Advance(1, true); f.Session.Remove(f.Item.Entity); f.Kernel.Advance(2, true);
    Check(f.FixtureEffects == 1, "lifetime cleanup reversed a committed effect");
});
Test("missing-host-permission-rejects-plan-before-dispatch", () =>
{
    using var f = new Fixture(loadPlan: false);
    Rejected("permission-denied", () => f.Kernel.LoadPlan(f.Plan(), Array.Empty<string>()));
    Check(f.Kernel.LoadedPlans == 0 && f.Kernel.QueuedEvents == 0, "denied plan was installed");
});
Test("duplicate-equipment-registration-does-not-damage-live-session", () =>
{
    var kernel = new RuntimeKernel(new("fixture.dispatch.registration", "1.0.0", RuntimeKernel.ApiVersion, "synthetic"));
    using var session = new EquipmentIdentitySession(kernel, _ => true, _ => true); string before = kernel.ExportManifest();
    Rejected("provider-conflict", () => new EquipmentIdentitySession(kernel, _ => true, _ => true));
    Check(before == kernel.ExportManifest(), "failed registration altered registry"); kernel.StopRuntime();
});
Test("startup-failure-disposal-does-not-throw", () =>
{
    var kernel = new RuntimeKernel(new("fixture.dispatch.startup", "1.0.0", RuntimeKernel.ApiVersion, "synthetic"));
    var session = new EquipmentIdentitySession(kernel, _ => true, _ => true); kernel.BeginWorld(7);
    try { kernel.StartRuntime(() => throw new InvalidOperationException("fixture-startup-failure")); }
    catch (InvalidOperationException) { }
    Check(kernel.StartupState == RuntimeStartupState.Failed, "startup failure did not latch");
    session.Dispose(); session.Dispose(); Check(session.Count == 0, "failed startup retained entities");
});

Console.WriteLine($"DISPATCH REVIEW: {results.Count - failures}/{results.Count} passed; fixture-only; gameExecuted=false");
if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Expected --report NEW.json");
using (var stream = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write))
    JsonSerializer.Serialize(stream, new { scope = "compiled-equipment-public-dispatch", gameExecuted = false,
        installed = false, failures, tests = results }, new JsonSerializerOptions { WriteIndented = true });
return failures == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    internal const string Provider = "fixture.dispatch";
    internal const string Trigger = Provider + ".binding.trigger", Action = Provider + ".binding.action";
    internal RuntimeKernel Kernel { get; } = new(new("fixture.dispatch.runtime", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
    internal EquipmentIdentitySession Session { get; }
    internal RuntimeModuleHandle Dispatch { get; }
    internal EntityReference Owner { get; } = new("fixture.player:a", 7, 1);
    internal EquipmentObservation Item { get; }
    internal bool NativeLive = true, OwnerLive = true, ThrowNativeRead;
    internal int HandlerEntries, FixtureEffects;
    private EquipmentUseTicket? ticket;
    internal Fixture(bool loadPlan = true)
    {
        Kernel.BeginWorld(7);
        Session = new(Kernel, _ => ThrowNativeRead ? throw new InvalidOperationException("fixture-reader") : NativeLive, _ => OwnerLive);
        Item = new(new("gtfo.equipment:a", 7, 1), "fixture.rifle", "r1", Owner, "GearStandard", EquipmentLocation.Inventory, true, true);
        Dispatch = Kernel.RegisterModule(Module());
        Kernel.StartRuntime(() => { if (loadPlan) Kernel.LoadPlan(Plan(), new[] { "fixture.dispatch.use" }); });
        Kernel.Advance(0, true); Session.Record(Item);
    }
    internal DispatchResult Queue(EntityReference? source = null)
    { ticket = Session.CaptureOwnedUse(Item.Entity, Owner, true); return Publish(source: source); }
    internal DispatchResult Publish(EntityReference? target = null, EntityReference? source = null)
        => Dispatch.Publish(new("fixture.event.1", Trigger, Kernel.WorldEpoch, 1, "fixture.scope",
            RuntimeJson.From(new { target = target ?? Item.Entity }), source));
    private CommandResult Apply(CommandContext context)
    {
        HandlerEntries++;
        try
        {
            if (ticket == null) return CommandResult.Rejected("fixture.missing-ticket");
            var observed = Session.RequireCurrent(ticket);
            if (observed.Entity != context.GetEntityInput("target")) return CommandResult.Rejected("fixture.target-mismatch");
        }
        catch (RuntimeContractException error) { return CommandResult.Rejected(error.Code); }
        FixtureEffects++; // Test counter only: NOT native damage, inventory, money or a resource transaction.
        return CommandResult.Succeeded(RuntimeJson.From(new { fixtureOnly = true }));
    }
    private object Capability(string kind)
    {
        object graph = kind == "trigger" ? new
        {
            domains = new[] { "weapon" }, execution = "host", inputs = Array.Empty<object>(),
            outputs = new[] { new { id = "next", type = "execution" }, new { id = "target", type = "entity" } }, parameters = Array.Empty<object>()
        } : new
        {
            domains = new[] { "weapon" }, execution = "host",
            inputs = new[] { new { id = "enter", type = "execution" }, new { id = "target", type = "entity" } },
            outputs = new[] { new { id = "result", type = "result", schema = "fixture.dispatch.result" } }, parameters = Array.Empty<object>(),
            recipients = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" }
        };
        return new { id = Provider + "." + kind, owner = Provider, kind, label = "Synthetic dispatch review", version = "1.0.0", parameters = new { }, graph };
    }
    private RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = Provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = new[] { Capability("trigger"), Capability("action") },
        bindings = new[] { "action", "trigger" }.Select(kind => new
        {
            id = Provider + ".binding." + kind, capabilityId = Provider + "." + kind, providerId = Provider,
            handler = Provider + "." + kind, role = kind == "action" ? "execute" : "observe", status = "implemented",
            dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
        }).ToArray()
    }).GetRawText(), new Dictionary<string, CommandHandler> { [Provider + ".action"] = Apply },
        new[] { new BindingSupport(Trigger, "implementation-only", Array.Empty<string>()),
            new BindingSupport(Action, "implementation-only", new[] { "fixture.dispatch.use" }) });
    private static readonly string[] PortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };
    private JsonElement Graph(string kind) => RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry").GetProperty("capabilities")
        .EnumerateArray().Single(c => c.GetProperty("id").GetString() == Provider + "." + kind).GetProperty("graph");
    // schemaVersion 2 slot frame written independently of the SDK; these ports carry no value set or lifetime.
    private static object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new
    {
        index, type = Array.IndexOf(PortTypes, p.GetProperty("type").GetString()!),
        cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0, valueSet = -1, lifetime = -1,
        optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
    }).ToArray();
    private static object Layout(JsonElement graph) => new { inputs = Slots(graph.GetProperty("inputs")), outputs = Slots(graph.GetProperty("outputs")), constants = Array.Empty<object>(), promoted = Array.Empty<int>() };
    private static int Slot(JsonElement ports, string name) => ports.EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == name).i;
    internal string Plan()
    {
        var kinds = new[] { "action", "trigger" }; var trigger = Graph("trigger"); var action = Graph("action");
        return RuntimeJson.From(new
        {
            schemaVersion = 2, kind = "forge-runtime-plan", planId = "fixture.plan", resource = new { id = "fixture.resource", revision = "r1" },
            runtime = Kernel.Identity, domain = "weapon", authority = "host", failurePolicy = "stop-entrypoint",
            permissions = new[] { "fixture.dispatch.use" }, dependencies = Array.Empty<string>(),
            limits = new { maxEventsPerTick = 8, maxCommandsPerTick = 8, maxQueuedEvents = 8, maxCausalDepth = 4 },
            bindings = kinds.Select(kind => new
            {
                bindingId = Provider + ".binding." + kind, capabilityId = Provider + "." + kind,
                capabilityVersion = "1.0.0", providerId = Provider, providerVersion = "1.0.0", handler = Provider + "." + kind
            }).ToArray(),
            entrypoints = new[] { new { nodeId = "entry", binding = Array.IndexOf(kinds, "trigger"), layout = Layout(trigger),
                steps = new[] { new { nodeId = "apply", binding = Array.IndexOf(kinds, "action"), layout = Layout(action),
                    inputs = new[] { new { slot = Slot(action.GetProperty("inputs"), "target"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "target") } } } } } }
        }).GetRawText();
    }
    public void Dispose() { Dispatch.Dispose(); Session.Dispose(); Kernel.StopRuntime(); }
}
