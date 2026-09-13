using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

var checks = 0;
void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
void Reject(Action action, string name)
{
    try { action(); } catch (RuntimeContractException) { checks++; return; }
    throw new Exception("FAIL: " + name + " did not reject");
}

{
    var s = new Scenario();
    var a = s.Register("example.alpha"); var b = s.Register("example.beta");
    s.Plan("alpha", "example.alpha"); s.Plan("beta", "example.beta");
    Check(s.Kernel.HasSubscribers(Fixture.Trigger("example.alpha")), "loaded subscription is discoverable");
    Check(a.Publish(s.Event("a1", "example.alpha")).Status == "queued", "module A queues explicit recipient");
    Check(b.Publish(s.Event("b1", "example.beta")).Status == "queued", "module B queues through same SDK");
    var tick = s.Kernel.Advance(1, true);
    Check(tick.CommandsExecuted == 2 && s.Applied.SequenceEqual(new[] { "example.alpha:5", "example.beta:5" }), "two independent modules execute in queue order");
    Check(tick.Commands.All(c => c.Result.Status == "succeeded" && c.WorldEpoch == 1 && c.ResourceRevision == "revision-1"), "commands carry source revision and epochs");
    Check(a.Publish(s.Event("a1", "example.alpha")).Status == "duplicate", "replayed event cannot execute twice");
    Check(s.Kernel.Advance(1, true).CommandsExecuted == 0, "duplicate simulation tick is inert");
    Check(a.Publish(s.Event("a1", "example.alpha", tick: 2)).Code == "event-id-conflict", "reusing identity with changed event rejects");
    Check(a.Publish(s.Event("cross", "example.beta")).Code == "binding-owner", "module cannot publish another module binding");
    Reject(() => s.Register("example.alpha"), "provider conflict rejects atomically");
    Reject(() => s.Kernel.RegisterModule(Fixture.Module("example.third") with { ApiVersion = "2.0.0" }), "API version mismatch");
    Check(b.IsRegistered, "rejected registration does not damage other module");
}
{
    var s = new Scenario(); var a = s.Register("example.alpha"); s.Plan("alpha", "example.alpha");
    a.Publish(s.Event("life", "example.alpha")); s.Life = 2;
    var stale = s.Kernel.Advance(1, true);
    Check(stale.CommandsExecuted == 0 && stale.Commands[0].Result.Code == "stale-entity", "entity life is revalidated at execution");
    s.Kernel.BeginWorld(2);
    Check(a.Publish(s.Event("world", "example.alpha")).Code == "stale-world", "old world event rejected");
    Reject(() => s.Kernel.BeginWorld(2), "world epoch cannot be reused");
    Reject(() => s.Kernel.BeginWorld(RuntimeJson.MaxSafeInteger + 1), "C# does not accept unsafe JS epoch");
}
{
    var s = new Scenario(); var a = s.Register("example.alpha"); var b = s.Register("example.beta");
    s.Plan("alpha", "example.alpha"); s.Plan("beta", "example.beta");
    a.Publish(s.Event("cancel-a", "example.alpha")); b.Publish(s.Event("keep-b", "example.beta"));
    Check(a.CancelScope("shared-scope") == 1, "scope cancellation is owned by its module");
    var tick = s.Kernel.Advance(1, true);
    Check(tick.CommandsExecuted == 1 && s.Applied.Single() == "example.beta:5", "cancelling A does not cancel B same-named scope");
    Check(a.Publish(s.Event("later-a", "example.alpha")).Code == "scope-cancelled", "cancelled source scope cannot restart");
    s.Kernel.BeginWorld(2); s.World = 2;
    a.Publish(s.Event("unregister-a", "example.alpha")); b.Publish(s.Event("keep-again-b", "example.beta"));
    a.Dispose();
    Check(a.Publish(s.Event("dead-module", "example.alpha")).Code == "module-unregistered", "old module handle fails closed");
    Check(s.Kernel.Advance(1, true).CommandsExecuted == 1, "module unregister preserves independent module tasks");
    var replacement = s.Register("example.alpha");
    Check(replacement.IsRegistered && !s.Kernel.HasSubscribers(Fixture.Trigger("example.alpha")), "re-register does not resurrect old plans/tasks");
}
{
    var s = new Scenario(); var a = s.Register("example.alpha"); s.Plan("alpha", "example.alpha");
    a.Publish(s.Event("client", "example.alpha"));
    var client = s.Kernel.Advance(1, false);
    Check(client.CommandsExecuted == 0 && client.Events.Single().Code == "not-host" && s.Applied.Count == 0, "client never calls authoritative handler");
    Reject(() => s.Kernel.Advance(2, true), "host migration requires supported new world lifecycle");
    s.Kernel.BeginWorld(2); s.World = 2;
    a.Publish(s.Event("host", "example.alpha")); Check(s.Kernel.Advance(1, true).CommandsExecuted == 1, "new authoritative world executes");
    Reject(() => s.Kernel.Advance(0, true), "simulation time reversal rejects");
}
{
    var s = new Scenario(); var a = s.Register("example.alpha", _ => CommandResult.Rejected("deliberate")); var b = s.Register("example.beta");
    s.Plan("alpha", "example.alpha", 2); s.Plan("beta", "example.beta");
    a.Publish(s.Event("reject-a", "example.alpha")); b.Publish(s.Event("ok-b", "example.beta"));
    var tick = s.Kernel.Advance(1, true);
    Check(tick.CommandsExecuted == 2 && tick.Commands.Count == 2 && tick.Commands[0].Result.Code == "deliberate", "failure stops only its entrypoint chain");
    Check(s.Applied.Single() == "example.beta:5", "module rejection does not interrupt independent module");
}
{
    var s = new Scenario(); var a = s.Register("example.alpha", _ => throw new InvalidOperationException("test handler")); s.Plan("alpha", "example.alpha");
    a.Publish(s.Event("throws", "example.alpha"));
    var tick = s.Kernel.Advance(1, true);
    Check(tick.Commands.Single().Result.Status == "failed" && tick.Commands.Single().Result.Code == "handler-exception", "handler exception is an explicit failed result");
    Check(a.Publish(s.Event("throws", "example.alpha")).Status == "duplicate", "failed command is not automatically retried after possibly committed effects");
}
{
    var limits = new RuntimeLimits { MaxEventsPerTick = 1, MaxCommandsPerTick = 2, MaxQueuedEvents = 2 };
    var s = new Scenario(limits); var a = s.Register("example.alpha"); s.Plan("alpha", "example.alpha");
    Check(a.Publish(s.Event("q1", "example.alpha")).Status == "queued", "first event queues");
    Check(a.Publish(s.Event("q2", "example.alpha")).Status == "queued", "second event queues");
    Check(a.Publish(s.Event("q2", "example.alpha")).Status == "duplicate", "duplicate still recognized when queue is full");
    Check(a.Publish(s.Event("overflow", "example.alpha")).Status == "rejected", "queue overflow explicitly rejects");
    Check(s.Kernel.Advance(1, true).CommandsExecuted == 1 && s.Kernel.QueuedEvents == 1, "tick event budget defers remaining event");
    Check(s.Kernel.Advance(1, true).CommandsExecuted == 0 && s.Kernel.QueuedEvents == 1, "same tick cannot reset budget");
    Check(s.Kernel.Advance(2, true).CommandsExecuted == 1 && s.Applied.Count == 2, "deferred event executes once next tick");
}
{
    var limits = new RuntimeLimits { MaxCausalDepth = 2 };
    var s = new Scenario(limits); var a = s.Register("example.alpha", ctx => CommandResult.Succeeded(RuntimeJson.EmptyObject,
        new RuntimeFact(Fixture.Trigger("example.alpha"), RuntimeJson.From(new { target = ctx.GetEntityInput("target") }))));
    s.Plan("alpha", "example.alpha"); a.Publish(s.Event("root", "example.alpha"));
    var tick = s.Kernel.Advance(1, true);
    Check(tick.CommandsExecuted == 3 && tick.Events.Any(e => e.Code == "causal-depth"), "causal loop bounded across generated events");
    Check(tick.Commands[1].CauseId == tick.Commands[0].CommandId && tick.Commands.All(c => c.RootEventId == "root"), "derived facts inherit actual command causality");
}
{
    var s = new Scenario();
    var handlers = new Dictionary<string, CommandHandler> { [Fixture.Handler("example.alpha")] = _ => { s.Applied.Add("original"); using var doc = JsonDocument.Parse("{\"actual\":0}"); return CommandResult.Succeeded(doc.RootElement); } };
    var permissions = new List<string> { "example.health.write" };
    var module = Fixture.Module("example.alpha") with { Handlers = handlers, BindingSupport = Fixture.Support("example.alpha", permissions), EntityResolvers = s.Resolvers("example.alpha") };
    var a = s.Kernel.RegisterModule(module);
    handlers[Fixture.Handler("example.alpha")] = _ => throw new Exception("mutated"); permissions.Clear();
    s.Plan("alpha", "example.alpha");
    using (var doc = JsonDocument.Parse("{\"target\":{\"id\":\"example.alpha:1\",\"worldEpoch\":1,\"lifeEpoch\":1}}"))
        Check(a.Publish(s.Event("snapshot", "example.alpha") with { Outputs = doc.RootElement }).Status == "queued", "borrowed event payload accepted as snapshot");
    var tick = s.Kernel.Advance(1, true);
    Check(s.Applied.Single() == "original" && tick.Commands.Single().Result.Outputs.GetProperty("actual").GetInt32() == 0, "registration, event and zero-actual result survive external mutation/disposal");
}
{
    var s = new Scenario(); var a = s.Register("example.alpha");
    Check(!s.Kernel.HasSubscribers(Fixture.Trigger("example.alpha")) && a.Publish(s.Event("idle", "example.alpha")).Code == "no-consumer" && s.Kernel.QueuedEvents == 0, "no installed plans avoid queue work");
    var plan = Fixture.Plan(s.Kernel, "alpha", "example.alpha");
    Reject(() => s.Kernel.LoadPlan(plan, Array.Empty<string>()), "uploaded plan does not self-grant permission");
    var bad = JsonNode.Parse(plan)!; bad["runtime"]!["gameBuild"] = "wrong-build";
    Reject(() => s.Kernel.LoadPlan(bad.ToJsonString(), Fixture.Permissions), "game build is exactly pinned");
    bad = JsonNode.Parse(plan)!; bad["bindings"]![0]!["providerVersion"] = "9.0.0";
    Reject(() => s.Kernel.LoadPlan(bad.ToJsonString(), Fixture.Permissions), "provider version is exactly pinned");
    bad = JsonNode.Parse(plan)!; bad["entrypoints"]![0]!["steps"]![0]!["inputs"] = new JsonObject();
    Reject(() => s.Kernel.LoadPlan(bad.ToJsonString(), Fixture.Permissions), "missing explicit recipient rejects");
    bad = JsonNode.Parse(plan)!; bad["entrypoints"]![0]!["steps"]![0]!["parameters"]!["amount"] = -5;
    Reject(() => s.Kernel.LoadPlan(bad.ToJsonString(), Fixture.Permissions), "negative effect amount rejects at plan boundary");
    bad = JsonNode.Parse(plan)!; bad["entrypoints"]![0]!["extra"] = true;
    Reject(() => s.Kernel.LoadPlan(bad.ToJsonString(), Fixture.Permissions), "unknown plan field rejects");
    Reject(() => RuntimeJson.Parse("{\"schemaVersion\":1,\"schemaVersion\":2}"), "duplicate JSON keys reject");
    var planned = Fixture.Module("example.planned"); var registry = JsonNode.Parse(planned.RegistryJson)!;
    registry["bindings"]![0]!["status"] = "planned";
    Reject(() => s.Kernel.RegisterModule(planned with { RegistryJson = registry.ToJsonString() }), "planned catalog entry cannot register execution");
}
{
    var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
    var common = Fixture.Module("forge.contract.test");
    var commonJson = JsonNode.Parse(common.RegistryJson)!;
    commonJson["bindings"] = new JsonArray();
    var commonHandle = kernel.RegisterModule(common with { RegistryJson = commonJson.ToJsonString(), Handlers = new Dictionary<string, CommandHandler>(), BindingSupport = Array.Empty<BindingSupport>() });
    var calls = 0; var handles = new List<RuntimeModuleHandle>();
    foreach (var provider in new[] { "example.alpha", "example.beta" })
    {
        var module = Fixture.Module(provider, _ => { calls++; return CommandResult.Succeeded(RuntimeJson.EmptyObject); });
        var json = JsonNode.Parse(module.RegistryJson)!; json["capabilities"] = new JsonArray();
        json["bindings"]![0]!["capabilityId"] = Fixture.TriggerCapability("forge.contract.test");
        json["bindings"]![1]!["capabilityId"] = Fixture.ActionCapability("forge.contract.test");
        handles.Add(kernel.RegisterModule(module with { RegistryJson = json.ToJsonString(), EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [provider] = _ => true } }));
        kernel.LoadPlan(Fixture.Plan(kernel, provider, provider), Fixture.Permissions);
    }
    Check(JsonDocument.Parse(kernel.ExportManifest()).RootElement.GetProperty("registry").GetProperty("capabilities").GetArrayLength() == 2, "two extension modules reuse canonical semantics without duplicate definitions");
    Reject(() => commonHandle.Dispose(), "referenced common contracts cannot unload under consumers");
    Check(commonHandle.IsRegistered, "rejected common unload is atomic");
    foreach (var handle in handles) handle.Publish(new RuntimeEvent(handle.ProviderId + ":event", Fixture.Trigger(handle.ProviderId), 1, 1, "common", RuntimeJson.From(new { target = new EntityReference(handle.ProviderId + ":1", 1, 1) })));
    Check(kernel.Advance(1, true).CommandsExecuted == 2 && calls == 2, "two extensions execute shared canonical actions through their independent handlers");
}

{
    var s = new Scenario(); RuntimeModuleHandle? a = null;
    a = s.Register("example.alpha", _ => { a!.CancelScope("shared-scope"); return CommandResult.Succeeded(RuntimeJson.EmptyObject); });
    s.Plan("alpha", "example.alpha", 2); a.Publish(s.Event("cancel-in-handler", "example.alpha"));
    var tick = s.Kernel.Advance(1, true);
    Check(tick.CommandsExecuted == 1 && tick.Commands.Last().Result.Code == "scope-cancelled", "cancellation during dispatch prevents later commands");
}
{
    var s = new Scenario(); var b = s.Register("example.beta");
    var a = s.Register("example.alpha", _ => CommandResult.Succeeded(RuntimeJson.EmptyObject, new RuntimeFact(Fixture.Trigger("example.beta"), RuntimeJson.From(new { target = new EntityReference("example.beta:1", 1, 1) }))));
    s.Plan("alpha", "example.alpha"); s.Plan("beta", "example.beta"); a.Publish(s.Event("foreign-fact", "example.alpha"));
    var tick = s.Kernel.Advance(1, true);
    Check(tick.CommandsExecuted == 1 && tick.Events.Any(e => e.Code == "binding-owner") && s.Applied.Count == 0, "committed fact cannot publish another module's event binding");
}
{
    var s = new Scenario(); RuntimeModuleHandle? a = null;
    a = s.Register("example.alpha", _ => { a!.Dispose(); return CommandResult.Succeeded(RuntimeJson.EmptyObject); });
    s.Plan("alpha", "example.alpha"); a.Publish(s.Event("reentrant", "example.alpha"));
    var tick = s.Kernel.Advance(1, true);
    Check(a.IsRegistered && tick.Commands.Single().Result.Status == "failed", "handler cannot mutate registrations reentrantly");
    Reject(() => Task.Run(() => s.Kernel.HasSubscribers(Fixture.Trigger("example.alpha"))).GetAwaiter().GetResult(), "SDK enforces simulation thread ownership");
}
{
    var s = new Scenario(); var a = s.Register("example.alpha"); s.Plan("alpha", "example.alpha");
    Check(a.Publish(s.Event("bad-source", "example.alpha") with { Source = new EntityReference("example.alpha:1", 1, RuntimeJson.MaxSafeInteger + 1) }).Code == "invalid-integer", "source epoch also rejects unsafe C# integer");
    a.Publish(s.Event("future", "example.alpha", 3));
    Check(s.Kernel.Advance(2, true).CommandsExecuted == 0 && s.Kernel.Advance(3, true).CommandsExecuted == 1, "future simulation event waits for its due tick");
    Reject(() => CommandResult.Succeeded(RuntimeJson.EmptyObject, Enumerable.Repeat(new RuntimeFact(Fixture.Trigger("example.alpha"), RuntimeJson.EmptyObject), 129).ToArray()), "unbounded committed facts reject");
    Reject(() => CommandResult.Succeeded(RuntimeJson.From(new { oversized = new string('x', 65536) })), "oversized command result rejects");
}

if (args.Length >= 2 && args[0] == "--fixtures")
{
    var directory = Path.GetFullPath(args[1]);
    var cases = RuntimeJson.Parse(File.ReadAllText(Path.Combine(directory, "cases.json")));
    var manifest = RuntimeJson.Parse(File.ReadAllText(Path.Combine(directory, cases.GetProperty("manifest").GetString()!)));
    var options = RuntimeJson.Parse(File.ReadAllText(Path.Combine(directory, cases.GetProperty("compileOptions").GetString()!)));
    var grants = options.GetProperty("grantedPermissions").EnumerateArray().Select(p => p.GetString()!).ToArray();
    RuntimeKernel Registered()
    {
        var kernel = new RuntimeKernel(JsonSerializer.Deserialize<RuntimeIdentity>(manifest.GetProperty("runtime"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        var registry = manifest.GetProperty("registry");
        var providers = registry.GetProperty("providers").EnumerateArray().OrderBy(p => registry.GetProperty("capabilities").EnumerateArray().Any(c => c.GetProperty("owner").GetString() == p.GetProperty("id").GetString()) ? 0 : 1);
        foreach (var provider in providers)
        {
            var id = provider.GetProperty("id").GetString()!;
            var bindings = registry.GetProperty("bindings").EnumerateArray().Where(b => b.GetProperty("providerId").GetString() == id).ToArray();
            var capabilities = registry.GetProperty("capabilities").EnumerateArray().Where(c => c.GetProperty("owner").GetString() == id).ToArray();
            var handlers = bindings.Where(b => b.GetProperty("role").GetString() == "execute").ToDictionary(b => b.GetProperty("handler").GetString()!, b => (CommandHandler)(_ => CommandResult.Succeeded(RuntimeJson.EmptyObject)));
            var support = manifest.GetProperty("bindingSupport").EnumerateArray().Where(b => bindings.Any(row => row.GetProperty("id").GetString() == b.GetProperty("bindingId").GetString())).Select(b => new BindingSupport(b.GetProperty("bindingId").GetString()!, b.GetProperty("verification").GetString()!, b.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!).ToArray())).ToArray();
            kernel.RegisterModule(new RuntimeModule("1.0.0", RuntimeJson.From(new { providers = new[] { provider }, capabilities, bindings }).GetRawText(), handlers, support));
        }
        return kernel;
    }
    var valid = Registered(); valid.LoadPlan(File.ReadAllText(Path.Combine(directory, cases.GetProperty("validPlan").GetString()!)), grants);
    Check(valid.LoadedPlans == 1, "actual TypeScript compiled native fixture consumed by C#");
    foreach (var row in cases.GetProperty("invalidPlans").EnumerateArray())
    {
        var json = File.ReadAllText(Path.Combine(directory, row.GetProperty("file").GetString()!));
        var permissions = row.TryGetProperty("grantedPermissions", out var granted) ? granted.EnumerateArray().Select(p => p.GetString()!).ToArray() : grants;
        Reject(() => Registered().LoadPlan(json, permissions), "shared invalid fixture " + row.GetProperty("id").GetString());
    }
}
checks += TimingTests.Run();
checks += ExecutionResultTests.Run();
Console.WriteLine($"Framework checks: {checks} passed.");

if (args.Contains("--benchmark"))
{
    // Fixed synthetic load receipt for the SDK timing/state paths. One synthetic module, one numeric-definition module and one LoadPlan; the numbers are desktop doubles, not GTFO or network performance.
    var s = new TimingScenario(); var a = s.Register("example.alpha"); s.StateContract(); s.Plan("bench", a.ProviderId);
    for (var i = 0; i < RuntimeKernel.MaximumScheduledPulsesPerTick; i++) { a.Publish(s.Event(a.ProviderId, "warm" + i, i)); s.Kernel.Advance(i, true); }
    (Stopwatch Clock, long Bytes) Measure(Action action)
    {
        GC.Collect(); var before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew(); action(); clock.Stop();
        return (clock, GC.GetAllocatedBytesForCurrentThread() - before);
    }
    void Receipt(string work, int operations, string unit, (Stopwatch Clock, long Bytes) measured, string note)
        => Console.WriteLine($"Synthetic {work}: {measured.Clock.Elapsed.TotalMilliseconds:F2} ms, {measured.Bytes} allocated bytes, {measured.Bytes / Math.Max(1, operations)} bytes/{unit}. Registry and plan were loaded once. {note}");

    const int events = 10000;
    var dispatched = Measure(() => { for (var i = 0; i < events; i++) { var tick = i + 100; a.Publish(s.Event(a.ProviderId, "bench" + i, tick)); s.Kernel.Advance(tick, true); } });
    Receipt($"{events} events / {events} commands", events, "event", dispatched, "This is not a GTFO frame rate.");

    const int pulses = 4096; var perTick = RuntimeKernel.MaximumScheduledPulsesPerTick; var dueTick = events + 100;
    if (a.Schedule(s.Event(a.ProviderId, "pulse-load", dueTick), new PulseSchedule(1, FirstPulse.Immediate, MissedPulsePolicy.CatchUp, pulses)).Status != "scheduled")
        throw new Exception("FAIL benchmark: fixed pulse load schedule was rejected");
    var scheduled = Measure(() => { for (var tick = dueTick + perTick - 1; tick < dueTick + pulses; tick += perTick) s.Kernel.Advance(tick, true); });
    Receipt($"{pulses} scheduled pulses over {pulses / perTick} advances", pulses, "pulse", scheduled, $"Dispatch stays bounded to {perTick} scheduled pulses per tick. This is not a GTFO frame rate.");

    var admitted = 0; var refusal = "none";
    var capacity = Measure(() => { for (var i = 0; i <= RuntimeKernel.MaximumSchedules; i++) { var result = a.Schedule(s.Event(a.ProviderId, "cap" + i, s.Kernel.CurrentTick), new PulseSchedule(1, FirstPulse.AfterInterval, MissedPulsePolicy.SkipMissed, 1)); if (result.Status == "scheduled") admitted++; else refusal = result.Code!; } });
    Receipt($"{RuntimeKernel.MaximumSchedules + 1} schedule requests ({admitted} admitted, 1 refused {refusal})", RuntimeKernel.MaximumSchedules + 1, "request", capacity, "Refusal is explicit; nothing is truncated or evicted.");

    var leases = 0; var leaseRefusal = "none";
    var leased = Measure(() => { for (var i = 0; i <= RuntimeKernel.MaximumStateLeases; i++) { var name = "load" + i; s.Lives[a.ProviderId + ":" + name] = 1; var result = a.AcquireNumericLease(s.Lease(a.ProviderId, "load" + i, s.Entity(a.ProviderId, name))); if (result.Status == "acquired") leases++; else leaseRefusal = result.Code!; } });
    Receipt($"{RuntimeKernel.MaximumStateLeases + 1} lease requests ({leases} admitted, 1 refused {leaseRefusal})", RuntimeKernel.MaximumStateLeases + 1, "request", leased, "Refusal is explicit; no implicit refresh or stacking.");
}

sealed class Scenario
{
    public readonly RuntimeKernel Kernel;
    public readonly List<string> Applied = new();
    public long World = 1, Life = 1;
    public Scenario(RuntimeLimits? limits = null) { Kernel = new RuntimeKernel(Fixture.Identity, limits); Kernel.BeginWorld(1); }
    public Dictionary<string, Func<EntityReference, bool>> Resolvers(string id) => new() { [id] = r => r.Id == id + ":1" && r.WorldEpoch == World && r.LifeEpoch == Life };
    public RuntimeModuleHandle Register(string id, CommandHandler? handler = null) => Kernel.RegisterModule(Fixture.Module(id, handler ?? (ctx => {
        Applied.Add(id + ":" + ctx.Parameters.GetProperty("amount").GetDouble()); return CommandResult.Succeeded(RuntimeJson.From(new { actual = ctx.Parameters.GetProperty("amount").GetDouble() }));
    })) with { EntityResolvers = Resolvers(id) });
    public void Plan(string plan, string provider, int steps = 1) => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, steps), Fixture.Permissions);
    public RuntimeEvent Event(string id, string provider, long tick = 1) => new(id, Fixture.Trigger(provider), World, tick, "shared-scope", RuntimeJson.From(new { target = new EntityReference(provider + ":1", World, Life) }));
}
static class Fixture
{
    public static readonly RuntimeIdentity Identity = new("forge.runtime", "1.2.0", "1.0.0", "20403457");
    public static readonly string[] Permissions = { "example.health.write" };
    public static string Trigger(string id) => id + ".binding.trigger";
    public static string TriggerCapability(string id) => id + ".trigger";
    public static string ActionCapability(string id) => id + ".apply";
    public static string Handler(string id) => id + ".handler.apply";
    public static BindingSupport[] Support(string id, IReadOnlyList<string>? permissions = null) => new[] {
        new BindingSupport(Trigger(id), "implementation-only", Array.Empty<string>()),
        new BindingSupport(id + ".binding.apply", "implementation-only", permissions ?? Permissions)
    };
    public static RuntimeModule Module(string id, CommandHandler? handler = null)
    {
        object Port(string name, string type) => new { id = name, type };
        var json = RuntimeJson.From(new {
            providers = new[] { new { id, kind = id.StartsWith("forge.") ? "native" : "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new object[] {
                new { id = TriggerCapability(id), owner = id, kind = "trigger", label = "Observed event", version = "1.0.0", parameters = new {}, graph = new { domains = new[] { "enemy", "weapon", "room", "map", "tool", "consumable", "logic" }, execution = "host", inputs = Array.Empty<object>(), outputs = new[] { Port("next", "execution"), Port("target", "entity") }, parameters = Array.Empty<object>() } },
                new { id = ActionCapability(id), owner = id, kind = "action", label = "Effect", version = "1.0.0", parameters = new {}, graph = new { domains = new[] { "enemy", "weapon", "room", "map", "tool", "consumable", "logic" }, execution = "host", inputs = new[] { Port("in", "execution"), Port("target", "entity") }, outputs = new[] { Port("next", "execution") }, parameters = new[] { new { id = "amount", type = "number", required = true, minimum = 1, maximum = 100 } }, recipients = new { input = "target", requires = new[] { "health.current" } } } }
            },
            bindings = new[] {
                new { id = Trigger(id), capabilityId = TriggerCapability(id), providerId = id, handler = id + ".handler.trigger", role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
                new { id = id + ".binding.apply", capabilityId = ActionCapability(id), providerId = id, handler = Handler(id), role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
            }
        });
        return new RuntimeModule("1.0.0", json.GetRawText(), new Dictionary<string, CommandHandler> { [Handler(id)] = handler ?? (_ => CommandResult.Succeeded(RuntimeJson.EmptyObject)) }, Support(id));
    }
    public static string Plan(RuntimeKernel kernel, string planId, string provider, int stepCount = 1)
    {
        var manifest = RuntimeJson.Parse(kernel.ExportManifest()); var registry = manifest.GetProperty("registry");
        var bindings = registry.GetProperty("bindings").EnumerateArray().Where(b => b.GetProperty("providerId").GetString() == provider).OrderBy(b => b.GetProperty("id").GetString(), StringComparer.Ordinal);
        var pins = bindings.Select(b => new {
            bindingId = b.GetProperty("id").GetString(), capabilityId = b.GetProperty("capabilityId").GetString(),
            capabilityVersion = registry.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("id").GetString() == b.GetProperty("capabilityId").GetString()).GetProperty("version").GetString(),
            providerId = provider, providerVersion = "1.0.0", handler = b.GetProperty("handler").GetString()
        }).ToArray();
        return RuntimeJson.From(new {
            schemaVersion = 1, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" }, runtime = kernel.Identity,
            domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions = Permissions, dependencies = Array.Empty<string>(),
            limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth }, bindings = pins,
            entrypoints = new[] { new { nodeId = "Entry", bindingId = Trigger(provider), parameters = new {}, steps = Enumerable.Range(0, stepCount).Select(i => new { nodeId = "Action" + i, bindingId = provider + ".binding.apply", parameters = new { amount = 5 }, inputs = new { target = new { fromEventPort = "target" } } }).ToArray() } }
        }).GetRawText();
    }
}
