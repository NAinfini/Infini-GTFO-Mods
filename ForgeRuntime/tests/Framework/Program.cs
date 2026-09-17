using System.Diagnostics;
using System.Reflection;
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
void RejectCode(Action action, string code, string name)
{
    try { action(); }
    catch (RuntimeContractException error)
    {
        if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
        checks++; return;
    }
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
    Reject(() => s.Kernel.RegisterModule(Fixture.Module("example.third") with { ApiVersion = "9.0.0" }, RuntimeLogLevel.Off), "API version mismatch");
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
    var a = s.Kernel.RegisterModule(module, RuntimeLogLevel.Off);
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
    var bad = JsonNode.Parse(plan)!; bad["runtime"]!["gameBuild"] = "wrong-build";
    Reject(() => s.Kernel.LoadPlan(bad.ToJsonString()), "game build is exactly pinned");
    bad = JsonNode.Parse(plan)!; bad["bindings"]![0]!["providerVersion"] = "9.0.0";
    Reject(() => s.Kernel.LoadPlan(bad.ToJsonString()), "provider version is exactly pinned");
    bad = JsonNode.Parse(plan)!; bad["entrypoints"]![0]!["steps"]![0]!["inputs"] = new JsonArray();
    RejectCode(() => s.Kernel.LoadPlan(bad.ToJsonString()), "missing-input", "missing explicit recipient rejects");
    bad = JsonNode.Parse(plan)!; bad["entrypoints"]![0]!["steps"]![0]!["layout"]!["constants"]![0] = -5;
    Reject(() => s.Kernel.LoadPlan(bad.ToJsonString()), "negative effect amount rejects at plan boundary");
    bad = JsonNode.Parse(plan)!; bad["entrypoints"]![0]!["extra"] = true;
    Reject(() => s.Kernel.LoadPlan(bad.ToJsonString()), "unknown plan field rejects");
    Reject(() => RuntimeJson.Parse("{\"schemaVersion\":1,\"schemaVersion\":2}"), "duplicate JSON keys reject");
    JsonNode Step(JsonNode node) => node["entrypoints"]![0]!["steps"]![0]!;
    void Load(JsonNode node, string code, string name) => RejectCode(() => s.Kernel.LoadPlan(node.ToJsonString()), code, name);
    bad = JsonNode.Parse(plan)!; bad["schemaVersion"] = 3;
    Load(bad, "plan-version", "schemaVersion 3 plans are not read");
    bad = JsonNode.Parse(plan)!; Step(bad)["layout"]!["inputs"]![1]!["cardinality"] = 1;
    Load(bad, "layout-mismatch", "file layout must equal the registered contract");
    bad = JsonNode.Parse(plan)!; Step(bad)["layout"]!["constants"]![0] = null;
    Load(bad, "missing-constant", "required constant cannot be null");
    bad = JsonNode.Parse(plan)!; Step(bad)["layout"]!["constants"]!.AsArray().Add(1);
    Load(bad, "constant-frame", "constant frame length is exact");
    bad = JsonNode.Parse(plan)!; Step(bad)["inputs"]!.AsArray().Add(JsonNode.Parse("{\"slot\":1,\"fromEventSlot\":1}"));
    Load(bad, "input-order", "one driver per input slot");
    bad = JsonNode.Parse(plan)!; Step(bad)["inputs"] = JsonNode.Parse("[{\"slot\":0,\"fromEventSlot\":0}]");
    Load(bad, "execution-slot", "execution ports are not data inputs");
    bad = JsonNode.Parse(plan)!; bad["entrypoints"]![0]!["binding"] = Step(bad)["binding"]!.GetValue<int>();
    Load(bad, "node-kind", "entrypoint binding index must name a trigger");
    var planned = Fixture.Module("example.planned"); var registry = JsonNode.Parse(planned.RegistryJson)!;
    registry["bindings"]![0]!["status"] = "planned";
    Reject(() => s.Kernel.RegisterModule(planned with { RegistryJson = registry.ToJsonString() }, RuntimeLogLevel.Off), "planned catalog entry cannot register execution");
}
{
    // Promotion: a value parameter becomes an input appended after the resolved inputs, driven by the event.
    var s = new Scenario(); const string id = "example.promote"; var seen = new List<string>();
    var module = Fixture.Module(id, ctx => {
        seen.Add(ctx.Parameters.GetProperty("amount").GetDouble() + ":" + ctx.Inputs.TryGetProperty("amount", out _));
        return CommandResult.Succeeded(RuntimeJson.EmptyObject);
    });
    var seed = JsonNode.Parse(module.RegistryJson)!;
    seed["capabilities"]![0]!["graph"]!["outputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"amount\",\"type\":\"number\"}"));
    var handle = s.Kernel.RegisterModule(module with { RegistryJson = seed.ToJsonString(), EntityResolvers = s.Resolvers(id) }, RuntimeLogLevel.Off);
    JsonNode Step(JsonNode plan) => plan["entrypoints"]![0]!["steps"]![0]!;
    JsonNode Promoted()
    {
        var plan = JsonNode.Parse(Fixture.Plan(s.Kernel, "promote", id))!; var layout = Step(plan)["layout"]!;
        layout["constants"]![0] = null; layout["promoted"] = JsonNode.Parse("[0]");
        layout["inputs"]!.AsArray().Add(JsonNode.Parse("{\"index\":2,\"type\":3,\"cardinality\":0,\"valueSet\":-1,\"lifetime\":-1,\"optional\":false,\"nullable\":false}"));
        Step(plan)["inputs"]!.AsArray().Add(JsonNode.Parse("{\"slot\":2,\"fromEventSlot\":2}"));
        return plan;
    }
    void Bad(Action<JsonNode> mutate, string code, string name)
    {
        var plan = Promoted(); mutate(plan);
        RejectCode(() => s.Kernel.LoadPlan(plan.ToJsonString()), code, name);
    }
    Bad(p => Step(p)["layout"]!["constants"]![0] = 5, "promoted-constant", "a promoted parameter has no constant");
    Bad(p => Step(p)["layout"]!["promoted"] = JsonNode.Parse("[0,0]"), "promotion-frame", "promoted indices are strictly increasing");
    Bad(p => Step(p)["layout"]!["promoted"] = JsonNode.Parse("[1]"), "promotion-frame", "a promoted index names a declared parameter");
    Bad(p => Step(p)["layout"]!["inputs"]!.AsArray().RemoveAt(2), "layout-mismatch", "the promoted slot is part of the derived layout");
    Bad(p => Step(p)["inputs"]!.AsArray().RemoveAt(1), "missing-input", "a required promoted input must be driven");
    Bad(p => Step(p)["layout"]!.AsObject().Remove("promoted"), "missing-field", "every layout names its promotions");
    {
        // Q3: an enum's wire value is a member-set index, never its name. This covers both literal shapes
        // (a structural parameter's own inline `values`, and a directly wired event slot) plus the shared
        // out-of-range/non-integer/string rejections and the handler-boundary conversion back to member names.
        const string enumId = "example.enum_value";
        var seenKind = new List<string?>(); var seenPolicy = new List<string?>();
        var enumModule = Fixture.Module(enumId, ctx => {
            seenKind.Add(ctx.Inputs.TryGetProperty("kind", out var kind) && kind.ValueKind != JsonValueKind.Null ? kind.GetString() : null);
            seenPolicy.Add(ctx.Parameters.GetProperty("policy").GetString());
            return CommandResult.Succeeded(RuntimeJson.EmptyObject);
        });
        var enumSeed = JsonNode.Parse(enumModule.RegistryJson)!;
        enumSeed["capabilities"]![0]!["graph"]!["outputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"kind\",\"type\":\"enum\",\"schema\":\"compare_operator\",\"nullable\":true}"));
        enumSeed["capabilities"]![1]!["graph"]!["inputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"kind\",\"type\":\"enum\",\"schema\":\"compare_operator\",\"nullable\":true}"));
        // A structural literal narrows the index basis to its own inline `values`, not the shared set.
        enumSeed["capabilities"]![1]!["graph"]!["parameters"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"policy\",\"type\":\"enum\",\"role\":\"structural\",\"required\":true,\"values\":[\"floor\",\"ceil\",\"nearest\"]}"));
        var enumHandle = s.Kernel.RegisterModule(enumModule with { RegistryJson = enumSeed.ToJsonString(),
            Shapes = new Dictionary<string, HandlerShape> { [Fixture.Handler(enumId)] = Fixture.Shape(enumId, new[] { "target", "kind" }, new[] { "amount", "policy" }) },
            EntityResolvers = s.Resolvers(enumId) }, RuntimeLogLevel.Off);
        JsonNode EnumPlan(string policyConstantJson)
        {
            var plan = JsonNode.Parse(Fixture.Plan(s.Kernel, "enum_value", enumId))!;
            plan["entrypoints"]![0]!["layout"]!["outputs"]![2]!["valueSet"] = 0; // compare_operator is the first declared enum set.
            Step(plan)["layout"]!["inputs"]![2]!["valueSet"] = 0;
            Step(plan)["layout"]!["constants"] = JsonNode.Parse("[5," + policyConstantJson + "]");
            Step(plan)["inputs"]!.AsArray().Add(JsonNode.Parse("{\"slot\":2,\"fromEventSlot\":2}"));
            return plan;
        }
        void BadPolicy(string policyConstantJson, string name)
            => RejectCode(() => s.Kernel.LoadPlan(EnumPlan(policyConstantJson).ToJsonString()), "invalid-enum", name);
        BadPolicy("\"ceil\"", "a literal enum constant can never be the member name");
        BadPolicy("99", "a literal enum constant respects its own inline values list, not the shared set");
        BadPolicy("1.5", "a literal enum constant must be an integer");
        var legalPlan = EnumPlan("1"); // "ceil"
        s.Kernel.LoadPlan(legalPlan.ToJsonString());
        Check(s.Kernel.HasSubscribers(Fixture.Trigger(enumId)), "a legal enum event slot and literal register a subscription");
        RuntimeEvent KindEvent(string eventId, object? kind) => new(eventId, Fixture.Trigger(enumId), s.World, 1, "shared-scope",
            RuntimeJson.From(new { target = new EntityReference(enumId + ":1", s.World, s.Life), kind }));
        Check(enumHandle.Publish(KindEvent("kind-lt", 2)).Status == "queued", "a legal enum event index queues");
        Check(enumHandle.Publish(KindEvent("kind-null", null)).Status == "queued", "a nullable enum event slot accepts null");
        Check(enumHandle.Publish(KindEvent("kind-range", 99)).Code == "invalid-enum", "an out-of-range enum event index is rejected at publish");
        Check(enumHandle.Publish(KindEvent("kind-string", "lt")).Code == "invalid-enum", "an enum member name is never accepted on the wire");
        Check(enumHandle.Publish(KindEvent("kind-fraction", 1.5)).Code == "invalid-enum", "a non-integer enum event value is rejected");
        s.Kernel.Advance(1, true);
        Check(seenKind.SequenceEqual(new[] { "lt", null }), "the handler reads the wired enum input as its member name, never its index");
        Check(seenPolicy.SequenceEqual(new[] { "ceil", "ceil" }), "the handler reads the literal enum parameter as its member name, never its index");
    }
    {
        // Q3: a promoted enum parameter carries the same index representation as any other; only the handler
        // boundary differs from a wired input in which JSON bag (Parameters, not Inputs) receives the name.
        const string enumId = "example.promote_enum";
        var seenOp = new List<string?>();
        var opModule = Fixture.Module(enumId, ctx => {
            seenOp.Add(ctx.Parameters.GetProperty("op").GetString());
            return CommandResult.Succeeded(RuntimeJson.EmptyObject);
        });
        var opSeed = JsonNode.Parse(opModule.RegistryJson)!;
        opSeed["capabilities"]![0]!["graph"]!["outputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"op\",\"type\":\"enum\",\"schema\":\"compare_operator\"}"));
        opSeed["capabilities"]![1]!["graph"]!["parameters"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"op\",\"type\":\"enum\",\"role\":\"value\",\"required\":false,\"set\":\"compare_operator\"}"));
        var opHandle = s.Kernel.RegisterModule(opModule with { RegistryJson = opSeed.ToJsonString(),
            Shapes = new Dictionary<string, HandlerShape> { [Fixture.Handler(enumId)] = Fixture.Shape(enumId, parameters: new[] { "amount", "op" }) },
            EntityResolvers = s.Resolvers(enumId) }, RuntimeLogLevel.Off);
        var opPlan = JsonNode.Parse(Fixture.Plan(s.Kernel, "promote_enum", enumId))!;
        opPlan["entrypoints"]![0]!["layout"]!["outputs"]![2]!["valueSet"] = 0;
        Step(opPlan)["layout"]!["constants"] = JsonNode.Parse("[5,null]"); Step(opPlan)["layout"]!["promoted"] = JsonNode.Parse("[1]");
        // compare_operator is the first declared enum set, so its compiled valueSet index is 0.
        Step(opPlan)["layout"]!["inputs"]!.AsArray().Add(JsonNode.Parse("{\"index\":2,\"type\":5,\"cardinality\":0,\"valueSet\":0,\"lifetime\":-1,\"optional\":true,\"nullable\":false}"));
        Step(opPlan)["inputs"]!.AsArray().Add(JsonNode.Parse("{\"slot\":2,\"fromEventSlot\":2}"));
        s.Kernel.LoadPlan(opPlan.ToJsonString());
        Check(s.Kernel.HasSubscribers(Fixture.Trigger(enumId)), "a legal enum promotion registers a subscription");
        // The promoted parameter is not a constant: it reads the input port `Resolve` appended after the declared
        // inputs, so the frame carries the very slot the event copy targets and no pool entry at all.
        var opFrames = s.Kernel.Resolved("promote_enum")!.Frames!.Steps[0];
        Check(opFrames.Inputs.Length == 3 && opFrames.Inputs[2].Kind == ValueKind.Enum && opFrames.Inputs[2].Source == PortSource.Event,
            "a promoted parameter keeps its appended input port");
        Check(opFrames.Parameters.Length == 2 && opFrames.Parameters[1].Source == PortSource.Event
            && opFrames.Parameters[1].Kind == ValueKind.Enum && opFrames.Parameters[1].Slot == opFrames.Inputs[2].Slot,
            "a promoted parameter reads its input slot instead of a constant");
        Check(opFrames.Parameters[0].Source == PortSource.Constant && opFrames.Parameters[0].Slot == 0,
            "an authored parameter beside a promoted one still reads the plan's constant pool");
        RuntimeEvent OpEvent(string eventId, object op) => new(eventId, Fixture.Trigger(enumId), s.World, 1, "shared-scope",
            RuntimeJson.From(new { target = new EntityReference(enumId + ":1", s.World, s.Life), op }));
        Check(opHandle.Publish(OpEvent("op-lt", 2)).Status == "queued", "a legal promoted enum index queues");
        Check(opHandle.Publish(OpEvent("op-range", 99)).Code == "invalid-enum", "an out-of-range promoted enum index is rejected at publish");
        Check(opHandle.Publish(OpEvent("op-string", "eq")).Code == "invalid-enum", "a promoted enum member name is never accepted on the wire");
        Check(opHandle.Publish(OpEvent("op-fraction", 1.5)).Code == "invalid-enum", "a non-integer promoted enum value is rejected");
        s.Kernel.Advance(1, true);
        Check(seenOp.SequenceEqual(new[] { "lt" }), "the handler reads the promoted enum value as its member name, never its index");
    }
    Check(!s.Kernel.HasSubscribers(Fixture.Trigger(id)), "rejected promotion plans leave no subscription");
    s.Kernel.LoadPlan(Promoted().ToJsonString());
    RuntimeEvent Observed(string eventId, double amount) => new(eventId, Fixture.Trigger(id), s.World, 1, "shared-scope",
        RuntimeJson.From(new { target = new EntityReference(id + ":1", s.World, s.Life), amount }));
    Check(handle.Publish(Observed("in-range", 7)).Status == "queued", "promoted value event queues");
    Check(handle.Publish(Observed("over-range", 500)).Status == "queued", "an event value outside the parameter bounds is still a valid event");
    var tick = s.Kernel.Advance(1, true);
    Check(seen.SequenceEqual(new[] { "7:False" }), "the handler reads the promoted value as its parameter, not as a second input");
    Check(tick.Commands.Count == 2 && tick.Commands[1].Result.Status == "rejected" && tick.Commands[1].Result.Code == "parameter-maximum",
        "an out-of-bounds promoted value is rejected before invocation, never clamped");
}
{
    // A schemaVersion 1 plan is a graph, not a chain. This provider registers its own canonical
    // compare-shaped condition beside the recorded-event trigger and action, so these cases exercise the loader and
    // the kernel without depending on the ForgeTrigger package; the control step uses the SDK's real branch contract.
    const string id = "example.graph";
    // The loader recomputes the canonical step order (Kahn, nodeId ordinal tiebreak) and compares item by item, so
    // the fixture derives the very same order locally instead of hand-sorting its node list.
    static object[] Order(string[] nodeIds, (string From, string To)[] edges)
    {
        var indegree = nodeIds.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        foreach (var edge in edges) indegree[edge.To]++;
        var order = new List<object>(); var done = new HashSet<string>(StringComparer.Ordinal);
        while (order.Count < nodeIds.Length)
        {
            var next = nodeIds.Where(n => !done.Contains(n) && indegree[n] == 0).OrderBy(n => n, StringComparer.Ordinal).First();
            order.Add(next); done.Add(next);
            foreach (var edge in edges.Where(e => e.From == next)) indegree[edge.To]--;
        }
        return order.ToArray();
    }
    static object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new {
        index, type = Array.IndexOf(Fixture.WirePortTypes, p.GetProperty("type").GetString()),
        cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0, valueSet = -1, lifetime = -1,
        optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
    }).ToArray();
    static object Layout(JsonElement contract, object[] constants) => new { inputs = Slots(contract.GetProperty("inputs")), outputs = Slots(contract.GetProperty("outputs")), constants, promoted = Array.Empty<int>() };
    var graphKernel = new RuntimeKernel(Fixture.Identity); graphKernel.BeginWorld(1);
    graphKernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
    graphKernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
    var branchBinding = "forge.contract.control.binding.branch";
    // One module: the recorded-event trigger, the recorded action, and one pure condition (role evaluate, one
    // evaluator). The trigger gains a numeric output so the pure step is fed from a real event slot.
    var graphModule = Fixture.Module(id, _ => CommandResult.Succeeded(RuntimeJson.EmptyObject));
    var graphSeed = JsonNode.Parse(graphModule.RegistryJson)!;
    graphSeed["capabilities"]![0]!["graph"]!["outputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"amount\",\"type\":\"number\"}"));
    var alias = JsonNode.Parse(graphSeed["capabilities"]![1]!.ToJsonString())!;
    alias["id"] = id + ".condition.alias"; alias["kind"] = "condition";
    alias["graph"]!["execution"] = "pure";
    alias["graph"]!["inputs"] = JsonNode.Parse("[{\"id\":\"left\",\"type\":\"number\"},{\"id\":\"right\",\"type\":\"number\"}]");
    alias["graph"]!["outputs"] = JsonNode.Parse("[{\"id\":\"value\",\"type\":\"boolean\"}]");
    alias["graph"]!["parameters"] = new JsonArray();
    alias["graph"]!.AsObject().Remove("recipients");
    graphSeed["capabilities"]!.AsArray().Add(alias);
    graphSeed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new {
        id = id + ".binding.compare", capabilityId = id + ".condition.alias", providerId = id, handler = id + ".handler.compare",
        role = "evaluate", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }).GetRawText()));
    graphKernel.RegisterModule(graphModule with { RegistryJson = graphSeed.ToJsonString(),
        Evaluators = new Dictionary<string, EvaluatorHandler> { [id + ".handler.compare"] = _ => RuntimeJson.From(new { value = true }) },
        Shapes = new Dictionary<string, HandlerShape> { [Fixture.Handler(id)] = Fixture.Shape(id), [id + ".handler.compare"] = Fixture.CompareShape() },
        BindingSupport = new[] { Fixture.Support(id)[0], Fixture.Support(id)[1], new BindingSupport(id + ".binding.compare", "implementation-only", Array.Empty<string>()) } }, RuntimeLogLevel.Off);
    var graph = RuntimeJson.Parse(graphKernel.ExportManifest()).GetProperty("registry");
    JsonElement Row(string list, string rowId) => graph.GetProperty(list).EnumerateArray().Single(r => r.GetProperty("id").GetString() == rowId);
    var pinIds = new[] { Fixture.Trigger(id), id + ".binding.apply", id + ".binding.compare", branchBinding }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    var pins = JsonNode.Parse("[" + string.Join(",", pinIds.Select(pin => { var binding = Row("bindings", pin);
        var capabilityId = binding.GetProperty("capabilityId").GetString()!; var providerId = binding.GetProperty("providerId").GetString()!;
        return RuntimeJson.From(new { bindingId = pin, capabilityId, capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
            providerId, providerVersion = Row("providers", providerId).GetProperty("version").GetString()!, handler = binding.GetProperty("handler").GetString()! }).GetRawText(); })) + "]")!;
    var triggerContract = graphKernel.ResolveGraphContract(Fixture.TriggerCapability(id), "1.0.0", RuntimeJson.EmptyObject);
    var actionContract = graphKernel.ResolveGraphContract(Fixture.ActionCapability(id), "1.0.0", RuntimeJson.From(new { amount = 5 }));
    var aliasContract = graphKernel.ResolveGraphContract(id + ".condition.alias", "1.0.0", RuntimeJson.EmptyObject);
    var branchContract = graphKernel.ResolveGraphContract("forge.control.flow.branch", "1.0.0", RuntimeJson.EmptyObject);
    int Slot(JsonElement contract, string side, string port) => contract.GetProperty(side).EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == port).i;
    var actionLayout = JsonNode.Parse(RuntimeJson.From(Layout(actionContract, new object[] { 5 })).GetRawText())!;
    var aliasLayout = JsonNode.Parse(RuntimeJson.From(Layout(aliasContract, Array.Empty<object>())).GetRawText())!;
    var branchLayout = JsonNode.Parse(RuntimeJson.From(Layout(branchContract, Array.Empty<object>())).GetRawText())!;
    // The graph: trigger -> Compare (pure) -> Branch (control) -> Guard (action); otherwise ends the path.
    Dictionary<string, object> Nodes(string branchId = "Branch", string guardId = "Guard") => new(StringComparer.Ordinal) {
        [branchId] = new { nodeId = branchId, nodeKind = "control", binding = Array.IndexOf(pinIds, branchBinding), layout = branchLayout,
            inputs = new object[] { new { slot = 1, fromStepSlot = new { step = 0, port = 0 } } }, successors = new int?[] { 2, null } },
        ["Compare"] = new { nodeId = "Compare", nodeKind = "pure", binding = Array.IndexOf(pinIds, id + ".binding.compare"), layout = aliasLayout,
            inputs = new object[] { new { slot = 0, fromEventSlot = Slot(triggerContract, "outputs", "amount") }, new { slot = 1, fromEventSlot = Slot(triggerContract, "outputs", "amount") } },
            successors = Array.Empty<int?>() },
        [guardId] = new { nodeId = guardId, nodeKind = "action", binding = Array.IndexOf(pinIds, id + ".binding.apply"), layout = actionLayout,
            inputs = new object[] { new { slot = Slot(actionContract, "inputs", "target"), fromEventSlot = Slot(triggerContract, "outputs", "target") } },
            successors = new int?[] { null } }
    };
    var nodes = Nodes();
    // Edges of the intended graph; the canonical order is derived from them, never hand-written.
    object[] OrderOf(string[] nodeIds) => Order(nodeIds, new[] { ("Compare", nodeIds[1]), ("Compare", nodeIds[2]), (nodeIds[1], nodeIds[2]) });
    JsonNode Plan()
    {
        var canonical = OrderOf(new[] { "Compare", "Branch", "Guard" });
        Check(canonical.SequenceEqual(new object[] { "Compare", "Branch", "Guard" }), "the fixture graph has the expected canonical order");
        // Successor entries are array positions of that canonical order, so the array below is written in it.
        return JsonNode.Parse(RuntimeJson.From(new {
            schemaVersion = 1, kind = "forge-runtime-plan", planId = "graph", resource = new { id = "author.resource", revision = "revision-1" },
            runtime = graphKernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions = Fixture.Permissions,
            dependencies = Array.Empty<string>(),
            limits = new { graphKernel.Limits.MaxEventsPerTick, graphKernel.Limits.MaxCommandsPerTick, graphKernel.Limits.MaxQueuedEvents, graphKernel.Limits.MaxCausalDepth },
            bindings = pins, attachments = Fixture.Attachments,
            entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(pinIds, Fixture.Trigger(id)), layout = Layout(triggerContract, Array.Empty<object>()),
                start = 1, steps = canonical.Select(n => nodes[(string)n]).ToArray() } }
        }).GetRawText())!;
    }
    // `step-order` has no hand-written negative case here: with a linear graph every action/control step has exactly
    // one predecessor, so any array whose edges all point forward and whose steps are all reachable is already the
    // canonical order. The case needs an unreachable node, which the website's own invalid/step-order.plan.json
    // supplies and the --fixtures run asserts against this same code.
    // The loader is single-file-exact and holds one plan per planId, so each negative case needs its own kernel.
    (RuntimeKernel Kernel, RuntimeModuleHandle Module) GraphKernel(EvaluatorHandler? evaluator = null, CommandHandler? action = null)
    {
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        kernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
        kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        var module = kernel.RegisterModule(graphModule with { RegistryJson = graphSeed.ToJsonString(),
            Handlers = action == null ? graphModule.Handlers : new Dictionary<string, CommandHandler> { [Fixture.Handler(id)] = action },
            Evaluators = new Dictionary<string, EvaluatorHandler> { [id + ".handler.compare"] = evaluator ?? (_ => RuntimeJson.From(new { value = true })) },
            Shapes = new Dictionary<string, HandlerShape> { [Fixture.Handler(id)] = Fixture.Shape(id), [id + ".handler.compare"] = Fixture.CompareShape() },
            BindingSupport = new[] { Fixture.Support(id)[0], Fixture.Support(id)[1], new BindingSupport(id + ".binding.compare", "implementation-only", Array.Empty<string>()) },
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [id] = r => r.Id == id + ":1" && r.WorldEpoch == 1 && r.LifeEpoch == 1 } }, RuntimeLogLevel.Off);
        return (kernel, module);
    }
    void Bad(Action<JsonNode> mutate, string code, string name)
    {
        var plan = Plan(); mutate(plan);
        RejectCode(() => GraphKernel().Kernel.LoadPlan(plan.ToJsonString()), code, name);
    }
    JsonNode Step(JsonNode plan, int index) => plan["entrypoints"]![0]!["steps"]![index]!;
    graphKernel.LoadPlan(Plan().ToJsonString());
    Check(graphKernel.HasSubscribers(Fixture.Trigger(id)), "an action/pure/control graph loads");
    // A control capability the SDK does not own, for the two control-side rejections. It reuses the action graph
    // shape (one required execution input, two execution outputs) under `kind: control`.
    var otherModule = Fixture.Module("example.other");
    var otherSeed = JsonNode.Parse(otherModule.RegistryJson)!;
    var sequence = JsonNode.Parse(otherSeed["capabilities"]![1]!.ToJsonString())!;
    sequence["id"] = "example.other.control.sequence"; sequence["kind"] = "control";
    sequence["graph"]!.AsObject().Remove("recipients");
    sequence["graph"]!["parameters"] = new JsonArray();
    otherSeed["capabilities"] = new JsonArray(JsonNode.Parse(otherSeed["capabilities"]![0]!.ToJsonString()), sequence);
    otherSeed["bindings"] = new JsonArray(JsonNode.Parse(otherSeed["bindings"]![0]!.ToJsonString()), JsonNode.Parse(otherSeed["bindings"]![1]!.ToJsonString()));
    otherSeed["bindings"]![1]!["id"] = "example.other.binding.sequence";
    otherSeed["bindings"]![1]!["capabilityId"] = "example.other.control.sequence";
    var otherKernel = new RuntimeKernel(Fixture.Identity); otherKernel.BeginWorld(1);
    otherKernel.RegisterModule(otherModule with { RegistryJson = otherSeed.ToJsonString(),
        Handlers = new Dictionary<string, CommandHandler>(), Shapes = new Dictionary<string, HandlerShape>(),
        BindingSupport = new[] { new BindingSupport(Fixture.Trigger("example.other"), "implementation-only", Array.Empty<string>()),
            new BindingSupport("example.other.binding.sequence", "implementation-only", Array.Empty<string>()) } }, RuntimeLogLevel.Off);
    var otherRegistry = RuntimeJson.Parse(otherKernel.ExportManifest()).GetProperty("registry");
    JsonElement OtherRow(string list, string rowId) => otherRegistry.GetProperty(list).EnumerateArray().Single(r => r.GetProperty("id").GetString() == rowId);
    JsonNode OtherPin(string pin)
    {
        var binding = OtherRow("bindings", pin); var capabilityId = binding.GetProperty("capabilityId").GetString()!;
        var providerId = binding.GetProperty("providerId").GetString()!;
        return JsonNode.Parse(RuntimeJson.From(new { bindingId = pin, capabilityId,
            capabilityVersion = OtherRow("capabilities", capabilityId).GetProperty("version").GetString()!,
            providerId, providerVersion = OtherRow("providers", providerId).GetProperty("version").GetString()!,
            handler = binding.GetProperty("handler").GetString()! }).GetRawText())!;
    }
    // The pin table is ordinal sorted by bindingId; bindings and steps address it by index, never by name.
    var sequencePins = new[] { OtherPin(Fixture.Trigger("example.other")), OtherPin("example.other.binding.sequence") }
        .OrderBy(p => p["bindingId"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
    int OtherPinIndex(string pin) => sequencePins.Select((p, index) => (p, index)).Single(x => x.p["bindingId"]!.GetValue<string>() == pin).index;
    var otherContract = otherKernel.ResolveGraphContract("example.other.control.sequence", "1.0.0", RuntimeJson.EmptyObject);
    var otherTrigger = otherKernel.ResolveGraphContract(Fixture.TriggerCapability("example.other"), "1.0.0", RuntimeJson.EmptyObject);
    var otherLayout = JsonNode.Parse(RuntimeJson.From(Layout(otherContract, Array.Empty<object>())).GetRawText())!;
    var otherPlanJson = JsonNode.Parse(RuntimeJson.From(new {
        schemaVersion = 1, kind = "forge-runtime-plan", planId = "other", resource = new { id = "author.resource", revision = "revision-1" },
        runtime = otherKernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions = Array.Empty<string>(),
        dependencies = Array.Empty<string>(),
        limits = new { otherKernel.Limits.MaxEventsPerTick, otherKernel.Limits.MaxCommandsPerTick, otherKernel.Limits.MaxQueuedEvents, otherKernel.Limits.MaxCausalDepth },
        bindings = sequencePins, attachments = Fixture.Attachments,
        entrypoints = new[] { new { nodeId = "Entry", binding = OtherPinIndex(Fixture.Trigger("example.other")), layout = Layout(otherTrigger, Array.Empty<object>()), start = 0,
            steps = new object[] { new { nodeId = "Sequence", nodeKind = "control", binding = OtherPinIndex("example.other.binding.sequence"), layout = otherLayout, inputs = Array.Empty<object>(), successors = new int?[] { null, null } } } } }
    }).GetRawText())!.ToJsonString();
    // The kernel routes the control ids its own vocabulary declares; a control capability outside it is refused by
    // id, and a node whose declared nodeKind does not match its capability fails earlier.
    void BadOther(Action<JsonNode> mutate, string code, string name)
    {
        var candidate = JsonNode.Parse(otherPlanJson)!;
        mutate(candidate);
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        kernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
        kernel.RegisterModule(otherModule with { RegistryJson = otherSeed.ToJsonString(),
            Handlers = new Dictionary<string, CommandHandler>(), Shapes = new Dictionary<string, HandlerShape>(),
            BindingSupport = new[] { new BindingSupport(Fixture.Trigger("example.other"), "implementation-only", Array.Empty<string>()),
                new BindingSupport("example.other.binding.sequence", "implementation-only", Array.Empty<string>()) } }, RuntimeLogLevel.Off);
        RejectCode(() => kernel.LoadPlan(candidate.ToJsonString()), code, name);
    }
    BadOther(p => { }, "control-unsupported", "a control capability outside the kernel's vocabulary is refused by id");
    BadOther(p => p["entrypoints"]![0]!["steps"]![0]!["nodeKind"] = "action", "node-kind", "an action nodeKind cannot bind a control capability");
    // The evaluator table is exact in both directions: every implemented `evaluate` binding needs one, and nothing
    // else may be registered — a stale entry is a provider-side bug, not a spare handler the kernel can ignore.
    {
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1); kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        RejectCode(() => kernel.RegisterModule(graphModule with { RegistryJson = graphSeed.ToJsonString(),
            BindingSupport = new[] { Fixture.Support(id)[0], Fixture.Support(id)[1], new BindingSupport(id + ".binding.compare", "implementation-only", Array.Empty<string>()) } }, RuntimeLogLevel.Off),
            "missing-evaluator", "an evaluate binding without an evaluator rejects");
    }
    {
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1); kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        RejectCode(() => kernel.RegisterModule(graphModule with { RegistryJson = graphSeed.ToJsonString(),
            Evaluators = new Dictionary<string, EvaluatorHandler> { [id + ".handler.compare"] = _ => RuntimeJson.From(new { value = true }), [id + ".handler.spare"] = _ => RuntimeJson.From(new { value = true }) },
            Shapes = new Dictionary<string, HandlerShape> { [Fixture.Handler(id)] = Fixture.Shape(id), [id + ".handler.compare"] = Fixture.CompareShape(), [id + ".handler.spare"] = Fixture.CompareShape() },
            BindingSupport = new[] { Fixture.Support(id)[0], Fixture.Support(id)[1], new BindingSupport(id + ".binding.compare", "implementation-only", Array.Empty<string>()) } }, RuntimeLogLevel.Off),
            "unused-evaluator", "an evaluator with no evaluate binding rejects");
    }
    // The shape table is exact in the same two directions as the handler and evaluator tables: a registered
    // execute handler must bring its own shape, a shape no binding resolves against is a provider-side bug, and a
    // shape may only name ports the capability it is resolved against really declares.
    {
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        RejectCode(() => kernel.RegisterModule(Fixture.Module(id) with { Shapes = new Dictionary<string, HandlerShape>() }, RuntimeLogLevel.Off),
            "missing-shape", "an execute handler without a declared shape rejects");
    }
    {
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        RejectCode(() => kernel.RegisterModule(Fixture.Module(id) with { Shapes = new Dictionary<string, HandlerShape> {
            [Fixture.Handler(id)] = Fixture.Shape(id), [id + ".handler.spare"] = Fixture.Shape(id) } }, RuntimeLogLevel.Off),
            "unused-shape", "a shape with no binding to resolve it rejects");
    }
    {
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        RejectCode(() => kernel.RegisterModule(Fixture.Module(id) with { Shapes = new Dictionary<string, HandlerShape> {
            [Fixture.Handler(id)] = new HandlerShape().Inputs(id + ".undeclared") } }, RuntimeLogLevel.Off),
            "shape-port", "a shape naming a port the capability does not declare rejects");
    }
    // Contract I-PLAN: `pure` authority means no world access, so a condition may not declare an entity input at
    // all. The rejection happens at registration, not at plan load: a runnable pure step could otherwise be handed
    // an entity it has no way to act on.
    {
        var seed = JsonNode.Parse(graphSeed.ToJsonString())!;
        var pure = seed["capabilities"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == id + ".condition.alias");
        pure!["graph"]!["inputs"] = JsonNode.Parse("[{\"id\":\"target\",\"type\":\"entity\"}]");
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1); kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        RejectCode(() => kernel.RegisterModule(graphModule with { RegistryJson = seed.ToJsonString(),
            Evaluators = new Dictionary<string, EvaluatorHandler> { [id + ".handler.compare"] = _ => RuntimeJson.From(new { value = true }) },
            Shapes = new Dictionary<string, HandlerShape> { [Fixture.Handler(id)] = Fixture.Shape(id), [id + ".handler.compare"] = new HandlerShape().Outputs("value") },
            BindingSupport = new[] { Fixture.Support(id)[0], Fixture.Support(id)[1], new BindingSupport(id + ".binding.compare", "implementation-only", Array.Empty<string>()) } }, RuntimeLogLevel.Off),
            "pure-world-port", "a pure capability cannot declare an entity port");
    }
    Bad(p => Step(p, 0)["nodeKind"] = "condition", "node-kind", "a step nodeKind outside action/control/pure rejects");
    Bad(p => Step(p, 0)["nodeKind"] = "control", "node-kind", "a step nodeKind must match the capability it binds");
    Bad(p => Step(p, 1)["successors"] = new JsonArray(), "successor-shape", "a control successor frame matches its execution outputs");
    Bad(p => Step(p, 1)["successors"]![0] = 9, "successor-shape", "a successor outside the step array rejects");
    Bad(p => p["entrypoints"]![0]!["start"] = 0, "entry-start", "start must name an action or control step");
    Bad(p => p["entrypoints"]![0]!["start"] = 9, "entry-start", "start must be inside the step array");
    Bad(p => Step(p, 0)["successors"] = JsonNode.Parse("[0]"), "pure-successor", "a pure step never owns a successor");
    Bad(p => { Step(p, 1)["nodeKind"] = "pure"; Step(p, 1)["successors"] = new JsonArray(); }, "node-kind", "a control step cannot declare itself pure");
    Bad(p => { Step(p, 2)["nodeKind"] = "pure"; Step(p, 2)["successors"] = new JsonArray(); }, "node-kind", "a pure step cannot bind an action capability");
    Bad(p => Step(p, 1)["successors"]![0] = 0, "successor-index", "a successor never points backwards");
    // A read names a slot of this entrypoint; an index outside the step array names no slot, which is the slot
    // check's own case. That a read may name an action's declared output, and that a port it names has to be one
    // this runtime can move, is PlanAbiTests' case: reading an execution output is `from-step-port` whatever step
    // owns it.
    Bad(p => Step(p, 0)["inputs"] = JsonNode.Parse("[{\"slot\":0,\"fromStepSlot\":{\"step\":9,\"port\":0}},{\"slot\":1,\"fromEventSlot\":0}]"),
        "from-step-slot", "a fromStepSlot read outside the step array rejects");
    Bad(p => Step(p, 0)["inputs"] = JsonNode.Parse("[{\"slot\":0,\"fromStepSlot\":{\"step\":2,\"port\":0}},{\"slot\":1,\"fromEventSlot\":0}]"),
        "from-step-port", "an execution output is not a readable value");
    Bad(p => Step(p, 2)["inputs"]![0] = JsonNode.Parse("{\"slot\":1,\"fromStepSlot\":{\"step\":1,\"port\":0}}"), "from-step-port", "a control's execution output is not a readable value");
    Bad(p => Step(p, 1)["inputs"]![0] = JsonNode.Parse("{\"slot\":1,\"fromStepSlot\":{\"step\":0,\"port\":1}}"), "from-step-port", "a read port must exist on the pure step");
    Bad(p => Step(p, 2)["inputs"]![0] = JsonNode.Parse("{\"slot\":0,\"fromStepSlot\":{\"step\":0,\"port\":0}}"), "execution-slot", "a boolean output is not an execution input");    Bad(p => Step(p, 1)["inputs"]![0] = JsonNode.Parse("{\"slot\":1,\"fromEventSlot\":2}"), "port-mismatch", "a number event output is not a boolean condition");
    Bad(p => Step(p, 2)["inputs"]![0] = JsonNode.Parse("{\"slot\":1,\"fromEventSlot\":99}"), "event-port-missing", "an event slot outside the trigger frame rejects");
    Bad(p => { Step(p, 1)["successors"] = JsonNode.Parse("[null,null]"); }, "unreachable-step", "dropping the then-target leaves Guard unreachable");
    Bad(p => { Step(p, 0)["inputs"] = new JsonArray(); }, "missing-input", "a pure step must still drive every required input");
    // Routing itself: the kernel evaluates the pure step on demand, feeds it to the branch, and follows exactly one
    // execution output — `then` reaches the action, `otherwise` ends the entrypoint with nothing dispatched.
    // The trigger's frame carries `target` and the extra `amount` the pure condition compares.
    RuntimeEvent GraphEvent(string eventId, long tick = 1) => new(eventId, Fixture.Trigger(id), 1, tick, "shared-scope",
        RuntimeJson.From(new { target = new EntityReference(id + ":1", 1, 1), amount = 5d }));
    foreach (var condition in new[] { true, false })
    {
        var applied = new List<string>();
        var routed = GraphKernel(_ => RuntimeJson.From(new { value = condition }),
            _ => { applied.Add("guard"); return CommandResult.Succeeded(RuntimeJson.EmptyObject); });
        var plan = JsonNode.Parse(Plan().ToJsonString())!;
        plan["planId"] = "graph-route";
        routed.Kernel.LoadPlan(plan.ToJsonString());
        var queued = routed.Module.Publish(GraphEvent("branch-" + condition));
        Check(queued.Status == "queued", "the branch plan queues its trigger event instead of " + queued.Status + "/" + queued.Code);
        var tick = routed.Kernel.Advance(1, true);
        Check(applied.Count == (condition ? 1 : 0) && tick.CommandsExecuted == (condition ? 1 : 0),
            condition ? "then routes into the action" : "otherwise ends the entrypoint");
    }
    // The planned frames: trigger -> Compare (pure) -> Branch (control) -> Guard (action), one root frame, one
    // pure memo region and one result-row region. Every assertion below is about a slot number the loader fixed,
    // never about a name; the tick path itself still runs on JSON.
    {
        var kernel = GraphKernel().Kernel;
        var graphPlan = JsonNode.Parse(Plan().ToJsonString())!; graphPlan["planId"] = "graph-frames";
        kernel.LoadPlan(graphPlan.ToJsonString());
        var resolved = kernel.Resolved("graph-frames")!;
        var frames = resolved.Frames!;
        var root = frames.Entries[0];
        Check(frames.Steps.Length == 3 && frames.Entries.Length == 1 && resolved.Entries[0].Steps.Count == 3,
            "the loaded plan carries one frame descriptor per step");
        Check(root.Base == 0 && root.Start == 0 && root.StepCount == 3, "the plan's root frame starts at slot 0 and owns all three steps");
        Check(root.EventPorts.Length == 3 && root.EventPorts[0].Slot == -1 && root.EventPorts[1].Kind == ValueKind.Entity
            && root.EventPorts[1].Width == 1 && root.EventPorts[2].Kind == ValueKind.Number && root.EventPorts[2].Slot == 1,
            "the trigger's execution output has no slot and its two value outputs own the frame's first slots");
        var entity = frames.Steps[2].Inputs.Single(p => p.Kind == ValueKind.Entity);
        Check(entity.Width == 1 && entity.Source == PortSource.Event && entity.SourceSlot == 1, "the action's entity input is a one-element event copy");
        var compare = frames.Steps[0];
        Check(compare.PureMemoBase == compare.InputBase + 2 && compare.OutputBase == -1 && compare.Span == 3,
            "a pure step's outputs are its memo region, not a result row");
        Check(compare.Outputs[0].Kind == ValueKind.Boolean && compare.Outputs[0].Width == 1 && compare.Outputs[0].Slot == compare.PureMemoBase,
            "the pure step's boolean output frame is laid out in the memo region");
        var branch = frames.Steps[1];
        Check(branch.Inputs.Single(p => p.Kind == ValueKind.Boolean) is { Source: PortSource.Pure, SourceSlot: 0, SourceStep: 0 },
            "the branch condition reads the pure memo through its step index and port order");
        Check(branch.Successors.SequenceEqual(new[] { 2, -1 }), "a null successor is the frame's -1, not a nullable array");
        Check(branch.OutputBase >= 0 && branch.Outputs.All(p => p.Width == 0), "a control step owns a result-row region its execution outputs never occupy");
        var guard = frames.Steps[2];
        Check(guard.Inputs.Single(p => p.Kind == ValueKind.Entity) is { Source: PortSource.Event, SourceSlot: 1 },
            "the action's entity input copies the same event slot the control step read");
        Check(guard.Inputs.All(p => p.Kind != ValueKind.Number), "the action's promoted amount arrives through its constant slot, not an input slot");
        Check(guard.Parameters.Length == 1 && guard.Parameters[0].Source == PortSource.Constant
            && guard.Parameters[0].Kind == ValueKind.Number && guard.Parameters[0].Slot == 0,
            "a compiled parameter is addressed through the constant pool, not by name");
        Check(frames.Constants.SlotCount == 1 && frames.Constants.Slots[0].Kind == ValueKind.Number
            && frames.Constants.Slots[0].Number == 5, "one literal is encoded once into the plan's constant pool");
        Check(frames.StepSlots <= frames.MaxValueSlotsPerStep && frames.ByteCount <= kernel.Limits.MaxDispatchFrameBytes,
            "every step stays inside the per-step slot and per-plan byte budgets");
    }
}
{
    // The production `evaluate` module end to end: ForgeTrigger's real compare condition is fed two numbers, an
    // operator name and a tolerance through the wire, evaluated on demand, and its boolean drives the SDK's branch.
    // Both outcomes are asserted, so neither a constant-true evaluator nor an ignored operator could pass.
    const string Provider = "example.resolve";
    const string CompareBinding = ForgeTrigger.ModuleDefinition.ProviderId + ".binding.compare";
    const string BranchBinding = "forge.contract.control.binding.branch";
    static object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new {
        index, type = Array.IndexOf(Fixture.WirePortTypes, p.GetProperty("type").GetString()),
        cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0,
        valueSet = p.GetProperty("type").GetString() == "enum" ? Fixture.EnumSetIndex(p.GetProperty("schema").GetString()!) : -1,
        lifetime = -1,
        optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
    }).ToArray();
    static object Layout(JsonElement contract, object[] constants) => new { inputs = Slots(contract.GetProperty("inputs")), outputs = Slots(contract.GetProperty("outputs")), constants, promoted = Array.Empty<int>() };
    static (RuntimeKernel Kernel, RuntimeModuleHandle Handle) CompareKernel(List<string> applied)
    {
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        kernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
        kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        kernel.RegisterModule(ForgeTrigger.ModuleDefinition.Create(), RuntimeLogLevel.Off);
        var handle = kernel.RegisterModule(Fixture.Module(Provider, _ => { applied.Add("action"); return CommandResult.Succeeded(RuntimeJson.EmptyObject); })
            with { EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [Provider] = r => r.Id == Provider + ":1" && r.WorldEpoch == 1 && r.LifeEpoch == 1 } }, RuntimeLogLevel.Off);
        return (kernel, handle);
    }
    // Tolerance widens equality and shifts the ordered comparisons towards acceptance, so the last rows are chosen
    // to flip the outcome rather than to repeat it.
    foreach (var (left, right, op, tolerance, expect) in new[] {
        (5d, 5d, 0, 0d, true), (5d, 6d, 0, 0d, false), (5d, 5.5d, 3, 0.5d, true),
        (5d, 4.5d, 3, 0.25d, false), (5d, 6d, 1, 0d, true), (5d, 4.5d, 4, 0.25d, true), (5d, 5.5d, 4, 0.25d, false) })
    {
        var applied = new List<string>();
        var (kernel, handle) = CompareKernel(applied);
        var manifest = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry");
        JsonElement Row(string list, string id) => manifest.GetProperty(list).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        var pinIds = new[] { Fixture.Trigger(Provider), Provider + ".binding.apply", CompareBinding, BranchBinding }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var pins = JsonNode.Parse("[" + string.Join(",", pinIds.Select(pin => { var binding = Row("bindings", pin);
            var capabilityId = binding.GetProperty("capabilityId").GetString()!; var providerId = binding.GetProperty("providerId").GetString()!;
            return RuntimeJson.From(new { bindingId = pin, capabilityId, capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
                providerId, providerVersion = Row("providers", providerId).GetProperty("version").GetString()!, handler = binding.GetProperty("handler").GetString()! }).GetRawText(); })) + "]")!;
        var compareContract = kernel.ResolveGraphContract("forge.condition.predicate.compare", "1.0.0", RuntimeJson.EmptyObject);
        var branchContract = kernel.ResolveGraphContract("forge.control.flow.branch", "1.0.0", RuntimeJson.EmptyObject);
        var triggerContract = kernel.ResolveGraphContract(Fixture.TriggerCapability(Provider), "1.0.0", RuntimeJson.EmptyObject);
        var actionContract = kernel.ResolveGraphContract(Fixture.ActionCapability(Provider), "1.0.0", RuntimeJson.From(new { amount = 5 }));
        int Port(JsonElement contract, string side, string port) => contract.GetProperty(side).EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == port).i;
        var plan = RuntimeJson.From(new {
            schemaVersion = 1, kind = "forge-runtime-plan", planId = "compare-" + op + "-" + left + "-" + right, resource = new { id = "author.resource", revision = "revision-1" },
            runtime = kernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions = Fixture.Permissions, dependencies = Array.Empty<string>(),
            limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth },
            bindings = pins, attachments = Fixture.Attachments,
            entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(pinIds, Fixture.Trigger(Provider)),
                layout = Layout(triggerContract, Array.Empty<object>()), start = 1, steps = new object[] {
                    new { nodeId = "Compare", nodeKind = "query", binding = Array.IndexOf(pinIds, CompareBinding), layout = Layout(compareContract, new object[] { null! }),
                        inputs = new object[] { new { slot = Port(compareContract, "inputs", "left"), value = left }, new { slot = Port(compareContract, "inputs", "right"), value = right },
                            new { slot = Port(compareContract, "inputs", "operator"), value = op }, new { slot = Port(compareContract, "inputs", "tolerance"), value = tolerance } },
                        successors = Array.Empty<int?>() },
                    new { nodeId = "Branch", nodeKind = "control", binding = Array.IndexOf(pinIds, BranchBinding), layout = Layout(branchContract, Array.Empty<object>()),
                        inputs = new object[] { new { slot = 1, fromStepSlot = new { step = 0, port = 0 } } }, successors = new int?[] { 2, null } },
                    new { nodeId = "Resolve", nodeKind = "action", binding = Array.IndexOf(pinIds, Provider + ".binding.apply"), layout = Layout(actionContract, new object[] { 5 }),
                        inputs = new object[] { new { slot = Port(actionContract, "inputs", "target"), fromEventSlot = Port(triggerContract, "outputs", "target") } }, successors = new int?[] { null } } } } }
        }).GetRawText();
        kernel.LoadPlan(plan);
        Check(handle.Publish(new RuntimeEvent("compare-" + op, Fixture.Trigger(Provider), 1, 1, "shared-scope",
            RuntimeJson.From(new { target = new EntityReference(Provider + ":1", 1, 1) }))).Status == "queued", "the compare plan queues its trigger event");
        kernel.Advance(1, true);
        Check(applied.Count == (expect ? 1 : 0), $"compare {left} {(new[] { "eq", "ne", "lt", "lte", "gt", "gte" })[op]} {right} tolerance {tolerance} must be {expect}");
    }
}
{
    // Forge Standard v0.2 registration rules mirrored from the website's validateCapabilityGraph.
    void Bad(Action<JsonNode> mutate, string? code, string name)
    {
        var module = Fixture.Module("example.contract"); var json = JsonNode.Parse(module.RegistryJson)!;
        mutate(json["capabilities"]![1]!["graph"]!);
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        void Register() => kernel.RegisterModule(module with { RegistryJson = json.ToJsonString() }, RuntimeLogLevel.Off);
        if (code == null) Reject(Register, name); else RejectCode(Register, code, name);
    }
    // An action row may declare no recipients — the level-level writes have nobody to address — and its permission
    // is then its binding's alone. A row that writes a `requires` of its own is refused by name: permissions live on
    // the binding, and a second list is the mistake rather than an alternative spelling of the first.
    Bad(g => g["requires"] = JsonNode.Parse("[\"example.health.write\"]"), "row-shape", "a row declares no permission list");
    {
        var module = Fixture.Module("example.contract"); var json = JsonNode.Parse(module.RegistryJson)!;
        json["capabilities"]![1]!["graph"]!.AsObject().Remove("recipients");
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        var handle = kernel.RegisterModule(module with { RegistryJson = json.ToJsonString() }, RuntimeLogLevel.Off);
        Check(handle.IsRegistered, "an action without recipients registers and is judged by its binding alone");
    }
    Bad(g => g["recipients"]!["result"] = "next", "recipient-result", "recipient result names a result output");
    Bad(g => g["parameters"]![0]!.AsObject().Remove("role"), null, "parameters declare a role");
    Bad(g => g["inputs"]![1]!["type"] = "entity-list", null, "entity-list is retired");
    Bad(g => g["inputs"]![1]!["cardinality"] = "many", "recipient-cardinality", "recipient cardinality matches its port");
    // A context role may be left unwired only where the row's own contract says so (rule 146.4g makes
    // `combat.damage.source` optional: environmental damage has no dealer). Nullable stays refused on every row —
    // a wire that may hand the row nobody is ambiguous. The accepted optional case is the combat contract's own
    // row, which the catalog comparison registers.
    Bad(g => g["inputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"source\",\"type\":\"entity\",\"nullable\":true}")), "context-role-port", "a context role is never nullable");
    Bad(g => g["parameters"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"kind\",\"type\":\"enum\",\"role\":\"value\",\"required\":false,\"values\":[\"a\"]}")), "parameter-set", "promotable enums name a shared set");
    Bad(g => g["parameters"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"target\",\"type\":\"number\",\"role\":\"value\",\"required\":false}")), "parameter-collision", "value parameters cannot shadow an input");
    // Result-port reason codes (GraphPort.codes), mirrored from the website's port() field.
    Bad(g => g["outputs"]![0]!["codes"] = JsonNode.Parse("[\"ok-code\"]"), "port-codes", "codes on a nonresult port rejects");
    Bad(g => g["outputs"]![1]!["codes"] = "not-an-array", "port-codes", "codes must be an array");
    Bad(g => g["outputs"]![1]!["codes"] = JsonNode.Parse("[\"Bad_Code\"]"), "port-codes", "codes must match the reason-code pattern");
    Bad(g => g["outputs"]![1]!["codes"] = JsonNode.Parse("[\"dup\",\"dup\"]"), "port-codes", "codes cannot repeat");
    {
        var module = Fixture.Module("example.contract_codes"); var json = JsonNode.Parse(module.RegistryJson)!;
        json["capabilities"]![1]!["graph"]!["outputs"]![1]!["codes"] = JsonNode.Parse("[\"first-code\",\"second-code\"]");
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        var handle = kernel.RegisterModule(module with { RegistryJson = json.ToJsonString() }, RuntimeLogLevel.Off);
        Check(handle.IsRegistered, "a valid result-port codes array registers");
    }
}
{
    var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
    kernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
    var common = Fixture.Module("forge.contract.test");
    var commonJson = JsonNode.Parse(common.RegistryJson)!;
    commonJson["bindings"] = new JsonArray();
    var commonHandle = kernel.RegisterModule(common with { RegistryJson = commonJson.ToJsonString(), Handlers = new Dictionary<string, CommandHandler>(), Shapes = new Dictionary<string, HandlerShape>(), BindingSupport = Array.Empty<BindingSupport>() }, RuntimeLogLevel.Off);
var calls = 0; var handles = new List<RuntimeModuleHandle>();
foreach (var provider in new[] { "example.alpha", "example.beta" })
{
    var module = Fixture.Module(provider, _ => { calls++; return CommandResult.Succeeded(RuntimeJson.EmptyObject); });
    var json = JsonNode.Parse(module.RegistryJson)!; json["capabilities"] = new JsonArray();
    json["bindings"]![0]!["capabilityId"] = Fixture.TriggerCapability("forge.contract.test");
    json["bindings"]![1]!["capabilityId"] = Fixture.ActionCapability("forge.contract.test");
    handles.Add(kernel.RegisterModule(module with { RegistryJson = json.ToJsonString(), EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [provider] = _ => true } }, RuntimeLogLevel.Off));
    kernel.LoadPlan(Fixture.Plan(kernel, provider, provider));
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
Check(a.Publish(s.Event("bad-epoch", "example.alpha") with { Outputs = RuntimeJson.From(new { target = new EntityReference("example.alpha:1", 1, RuntimeJson.MaxSafeInteger + 1) }) }).Code == "invalid-integer", "a payload entity's epoch also rejects unsafe C# integer");
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
    // No mount kind belongs to the kernel, so the fixture kernel stands its owner up: the levels are the ones the
    // fixture plans themselves declare, so a plan mounted on a level this fixture set does not name is not claimed.
    // This branch never dispatches — it loads real website plans and checks the frames they resolve to — so what is
    // under test here is the loader and the layout, not the mount comparison.
    var levels = new List<string>();
    foreach (var row in cases.GetProperty("plans").EnumerateArray())
        foreach (var attachment in RuntimeJson.Rows(RuntimeJson.Parse(
            File.ReadAllText(Path.Combine(directory, row.GetProperty("plan").GetString()!))), "attachments"))
            if (RuntimeJson.Text(attachment, "kind") == "level" && !levels.Contains(RuntimeJson.Text(attachment, "reference")))
                levels.Add(RuntimeJson.Text(attachment, "reference"));
    RuntimeKernel Registered()
    {
        var kernel = new RuntimeKernel(JsonSerializer.Deserialize<RuntimeIdentity>(manifest.GetProperty("runtime"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        kernel.RegisterModule(Fixture.MountOwner(levels), RuntimeLogLevel.Off);
        var registry = manifest.GetProperty("registry");
        JsonElement Capability(JsonElement binding) => registry.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == binding.GetProperty("capabilityId").GetString());
        var providers = registry.GetProperty("providers").EnumerateArray().OrderBy(p => registry.GetProperty("capabilities").EnumerateArray().Any(c => c.GetProperty("owner").GetString() == p.GetProperty("id").GetString()) ? 0 : 1);
        // An `evaluate` binding needs a real evaluator, and the kernel's exact-set rule rejects any name
        // the module's own bindings do not use, so each module gets exactly its own rows. The double returns a zero
        // value for every output port, so the fixture's wiring — not its arithmetic — is what these cases check.
        var doubles = registry.GetProperty("bindings").EnumerateArray().Where(b => b.GetProperty("role").GetString() == "evaluate")
            .ToDictionary(b => b.GetProperty("handler").GetString()!, b => (EvaluatorHandler)(_ => RuntimeJson.From(registry.GetProperty("capabilities").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == b.GetProperty("capabilityId").GetString())
                .GetProperty("graph").GetProperty("outputs").EnumerateArray()
                .ToDictionary(port => port.GetProperty("id").GetString()!, port => port.GetProperty("type").GetString() switch
                {
                    "boolean" => (object)false, "integer" => 0, "number" => 0d, "string" => "", "enum" => 0,
                    "vector3" => new { x = 0d, y = 0d, z = 0d }, _ => (object?)null
                }))));
        foreach (var provider in providers)
        {
            var id = provider.GetProperty("id").GetString()!;
            var bindings = registry.GetProperty("bindings").EnumerateArray().Where(b => b.GetProperty("providerId").GetString() == id).ToArray();
            var capabilities = registry.GetProperty("capabilities").EnumerateArray().Where(c => c.GetProperty("owner").GetString() == id).ToArray();
            // The kernel only looks up an `execute` handler for action-kind capabilities (branch routing is internal),
            // and its exact-set rule rejects a supplied name that no such binding uses.
            var handlers = bindings.Where(b => b.GetProperty("role").GetString() == "execute" && Capability(b).GetProperty("kind").GetString() == "action")
                .ToDictionary(b => b.GetProperty("handler").GetString()!, b => (CommandHandler)(_ => CommandResult.Succeeded(RuntimeJson.EmptyObject)));
            var evaluators = bindings.Where(b => b.GetProperty("role").GetString() == "evaluate").ToDictionary(b => b.GetProperty("handler").GetString()!, b => doubles[b.GetProperty("handler").GetString()!]);
            // A real module declares the ports its handler reads; these doubles read every value port of the
            // capability they implement, so the shape is derived from the same registry rows the plan was compiled
            // against. Resolving it here is also what checks the shape addresses the real catalog's ports.
            var shapes = handlers.Keys.Concat(evaluators.Keys).ToDictionary(handler => handler, handler =>
            {
                var binding = bindings.Single(b => b.GetProperty("handler").GetString() == handler);
                var graph = Capability(binding).GetProperty("graph");
                string[] ValuePorts(string side) => graph.GetProperty(side).EnumerateArray()
                    .Where(p => p.GetProperty("type").GetString() != "execution")
                    .Select(p => p.GetProperty("id").GetString()!).ToArray();
                return new HandlerShape().Inputs(ValuePorts("inputs")).Outputs(ValuePorts("outputs"))
                    .Parameters(graph.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToArray());
            });
            var support = manifest.GetProperty("bindingSupport").EnumerateArray().Where(b => bindings.Any(row => row.GetProperty("id").GetString() == b.GetProperty("bindingId").GetString())).Select(b => new BindingSupport(b.GetProperty("bindingId").GetString()!, b.GetProperty("verification").GetString()!, b.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!).ToArray())).ToArray();
            kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new { providers = new[] { provider }, capabilities, bindings }).GetRawText(), handlers, support) { Evaluators = evaluators, Shapes = shapes }, RuntimeLogLevel.Off);
        }
        return kernel;
    }
    foreach (var row in cases.GetProperty("plans").EnumerateArray())
    {
        var id = row.GetProperty("id").GetString()!;
        var file = File.ReadAllText(Path.Combine(directory, row.GetProperty("plan").GetString()!));
        // The case id names the fixture, not the plan: a loaded plan is addressed by the planId it declares.
        var planId = RuntimeJson.Text(RuntimeJson.Parse(file), "planId");
        var kernel = Registered();
        try { kernel.LoadPlan(file); }
        catch (RuntimeContractException error) { throw new Exception($"FAIL: fixture plan {id} rejected with {error.Code}: {error.Message}"); }
        Check(kernel.LoadedPlans == 1, "actual TypeScript compiled native fixture " + id + " consumed by C#");
        // The compiled plan is laid out into frames with the same step count the file declares, and every step's
        // own contract ports get exactly one slot table entry each — the loader's table and the website's agree.
        var resolved = kernel.Resolved(planId)!;
        var frames = resolved.Frames;
        Check(frames.Steps.Length == resolved.Entries.Sum(entry => entry.Steps.Count) && frames.Entries.Length == resolved.Entries.Count,
            "actual TypeScript compiled fixture " + id + " has one frame descriptor per step and entrypoint");
        for (var entry = 0; entry < frames.Entries.Length; entry++)
            for (var step = 0; step < frames.Entries[entry].StepCount; step++)
            {
                var descriptor = frames.Steps[frames.Entries[entry].Start + step];
                var contract = resolved.Entries[entry].Steps[step].Contract;
                Check(descriptor.Inputs.Length == RuntimeJson.Rows(contract, "inputs").Length
                    && descriptor.Outputs.Length == RuntimeJson.Rows(contract, "outputs").Length,
                    "actual TypeScript compiled fixture " + id + " keeps every port addressable in its step frame");
            }
    }
    foreach (var row in cases.GetProperty("invalidPlans").EnumerateArray())
    {
        var json = File.ReadAllText(Path.Combine(directory, row.GetProperty("file").GetString()!));
        var id = row.GetProperty("id").GetString();
        if (row.TryGetProperty("code", out var code)) RejectCode(() => Registered().LoadPlan(json), code.GetString()!, "shared invalid fixture " + id);
        else Reject(() => Registered().LoadPlan(json), "shared invalid fixture " + id);
    }
}
checks += TimingTests.Run();
checks += PerfFixSemanticsTests.Run();
checks += ExecutionResultTests.Run();
checks += PlanAbiTests.Run();
checks += PresentationDispatchTests.Run();
checks += AttachmentRegistryTests.Run();
checks += TriggerScopeTests.Run();
checks += TriggerGateTests.Run();
checks += GateOptionTests.Run();
checks += EntityInstanceTests.Run();
checks += EffectLifecycleTests.Run();
checks += RuntimeContextTests.Run();
checks += RuntimeCandidateSourceTests.Run();
checks += ResourceRegistryTests.Run();
checks += QueryPresenceTests.Run();
checks += ZoneReadTests.Run();
checks += QuerySnapshotTests.Run();
checks += FrameShapeTests.Run();
checks += PlanContractTests.Run();
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
    /// <summary>The mount owner is registered first, because a plan's mount kind has to be owned before the plan
    /// that declares it loads; the scenarios here register one or two behaviour providers on top of it.
    /// <paramref name="subjectMounts"/> adds the mount kind that accepts an entity, which a per-instance trigger
    /// option needs before a plan may scope anything to one.</summary>
    public Scenario(RuntimeLimits? limits = null, bool subjectMounts = false)
    {
        Kernel = new RuntimeKernel(Fixture.Identity, limits); Kernel.BeginWorld(1);
        Kernel.RegisterModule(subjectMounts ? Fixture.MountOwnerWithSubjects() : Fixture.MountOwner(), RuntimeLogLevel.Off);
    }
    /// <summary>The kinds this scenario answers for: every entity of the provider's own kind. The index after the
    /// colon is what tells a per-player option's players apart — that clock is charged against the entity the event's
    /// own `instigator` port names, and an instigator that does not resolve is refused before any gate sees the
    /// event — while every other case here names the one entity its provider owns.</summary>
    public Dictionary<string, Func<EntityReference, bool>> Resolvers(string id)
    {
        bool Entity(EntityReference r) => r.WorldEpoch == World && r.LifeEpoch == Life
            && r.Id.StartsWith(id + ":", StringComparison.Ordinal) && int.TryParse(r.Id[(id.Length + 1)..], out _);
        return new() { [id] = Entity };
    }
    /// <summary>The one player of a scenario's provider kind: the entity a per-player clock is charged against.</summary>
    public EntityReference Player(string id, int index) => new(id + ":" + index, World, Life);
    public RuntimeModuleHandle Register(string id, CommandHandler? handler = null, bool amountPort = false, bool instigatorPort = false,
        bool effectPort = false, EffectRestoreHandler? restore = null, bool endOn = false, bool manyPort = false) => Kernel.RegisterModule(Fixture.Module(id, handler ?? (ctx => {
        Applied.Add(id + ":" + ctx.Parameters.GetProperty("amount").GetDouble()); return CommandResult.Succeeded(RuntimeJson.From(new { actual = ctx.Parameters.GetProperty("amount").GetDouble() }));
    }), amountPort, instigatorPort, effectPort, restore, endOn, manyPort) with { EntityResolvers = Resolvers(id) }, RuntimeLogLevel.Off);
    public void Plan(string plan, string provider, int steps = 1, bool amountPort = false, bool instigatorPort = false,
        bool effectPort = false, string? effect = null)
        => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, steps, null, null, amountPort, instigatorPort, effectPort, effect));
    /// <summary>A plan whose entry point declares a trigger option block, written as the JSON a compiler emits.</summary>
    public void Gated(string plan, string provider, string gate, bool amountPort = false, bool
instigatorPort = false)
        => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, 1, null, gate, amountPort, instigatorPort));
    /// <summary>The same gated entry point mounted on a target that accepts an entity: what a per-instance trigger
    /// option books its counters against is the entity this mount matched.</summary>
    public void GatedOnSubject(string plan, string provider, string gate)
        => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, 1, Fixture.SubjectAttachments, gate));
    /// <summary>An effect card that names the receiver's own event in `effect.end_on`. The event's trigger is a
    /// binding of the same module, and the card mounts no entry point on it: an ending is read from the dispatch
    /// itself, so nothing has to listen on the receiver's trigger for the effect to end.</summary>
    public void EndOnPlan(string plan, string provider, string effect, bool effectPort = true)
        => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, 1, null, null, false, false, effectPort, effect));
    public RuntimeEvent Event(string id, string provider, long tick = 1) => new(id, Fixture.Trigger(provider), World, tick, "shared-scope", RuntimeJson.From(new { target = new EntityReference(provider + ":1", World, Life) }));
}
static class Fixture
{
    public static readonly RuntimeIdentity Identity = new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457");
    public static readonly string[] Permissions = { "example.health.write" };
    // Wire-side dense port-type indexes; test fixtures write frames independently of the SDK.
    internal static readonly string[] WirePortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };
    /// <summary>An enum port's frame carries the declaration index of its member set; the table itself is the SDK's,
    /// so the fixture reads it rather than restating the order and drifting from it.</summary>
    internal static int EnumSetIndex(string schema) => Array.FindIndex(
        ((string Name, string[] Members)[])typeof(RuntimeGraphContracts).GetField("EnumSetTable", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!,
        set => set.Name == schema);
    /// <summary>One of the shared port tables — handle kinds, handle lifetimes — read by name, so a fixture port that
    /// declares one writes the same dense index the runtime's own layout does instead of a restatement of the order.</summary>
    internal static int TableIndex(string table, string name) => Array.IndexOf(
        (string[])typeof(RuntimeGraphContracts).GetField(table, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!, name);
    static readonly string[] PortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };
    public static string Trigger(string id) => id + ".binding.trigger";
    public static string TriggerCapability(string id) => id + ".trigger";
    /// <summary>A second entity port on the fixture trigger, so one event can carry two entities a subject-matching
    /// mount accepts: what a per-instance option has to refuse, because one activation is not one instance.</summary>
    public static readonly string ManyPort = "targets";
    /// <summary>The second trigger a card's `effect.end_on` names: the receiver's own event, which is an ability of
    /// its own row rather than of the entry point that applied the effect.</summary>
    public static string EndTrigger(string id) => id + ".binding.ended";
    public static string EndTriggerCapability(string id) => id + ".ended";
    public static string ActionCapability(string id) => id + ".apply";
    public static string Handler(string id) => id + ".handler.apply";
    /// <summary>The port one effect handle is published through, spelled as the capability declares it: the same
    /// three facts a module hands the kernel's own cancel entry point when it stops an effect itself.</summary>
    public static JsonElement EffectPort() => RuntimeJson.Parse("""{"type":"handle","handleKind":"effect","lifetime":"entity_life"}""");
    /// <summary>The fixture action handler's own ports. A test that widens the fixture capability declares the
    /// extra names it made the handler read, so every shape in this file still describes its handler exactly.</summary>
    public static HandlerShape Shape(string id, IReadOnlyList<string>? inputs = null, IReadOnlyList<string>? parameters = null)
        => new HandlerShape().Inputs((inputs ?? new[] { "target" }).ToArray()).Outputs("result")
            .Parameters((parameters ?? new[] { "amount" }).ToArray());
    /// <summary>The fixture result schema's row: the four fixed columns in their fixed order, then the actual
    /// amount and the number of affected targets, exactly as the contract's result-row rule spells them.</summary>
    public static readonly object[] ResultFields = {
        new { id = "target", type = "entity" },
        new { id = "status", type = "enum", schema = "execution_outcome" },
        new { id = "committed", type = "enum", schema = "commit_state" },
        new { id = "code", type = "string" },
        new { id = "amount", type = "number", unit = "hp" },
        new { id = "target_count", type = "integer" }
    };
    /// <summary>Every fixture plan declares one mount target. A plan hangs on something, and a mount kind is only
    /// loadable while the provider that owns it has registered its matcher; the fixture provider in these files
    /// claims this kind.</summary>
    /// <summary>The mount kind the fixture plans declare and the provider that owns it. No kind belongs to the
    /// kernel any more, so a fixture that loads a plan has to stand its owner up.</summary>
    public static readonly string MountProvider = "example.mounts";
    public static readonly string MountReference = "fixture-level";
    /// <summary>Every fixture plan declares one mount target, written in the reference the mount owner answers.</summary>
    public static readonly object[] Attachments = { new { kind = "level", reference = MountReference } };
    /// <summary>The mount a per-instance trigger option needs: one that accepts an entity, so the plan has an
    /// instance to count.</summary>
    public static readonly object[] SubjectAttachments = { new { kind = "map-object", category = "fixture", reference = "any" } };
    /// <summary>The graph fixture's pure compare handler: two numbers in, one boolean out.</summary>
    public static HandlerShape CompareShape() => new HandlerShape().Inputs("left", "right").Outputs("value");
    public static BindingSupport[] Support(string id, IReadOnlyList<string>? permissions = null, bool endOn = false)
    {
        var support = new List<BindingSupport>
        {
            new(Trigger(id), "implementation-only", Array.Empty<string>()),
            new(id + ".binding.apply", "implementation-only", permissions ?? Permissions)
        };
        // The receiver's own event is a binding of this provider too, and a binding is supported or the module
        // does not register: it is declared only by the tests that give a card an `end_on` ability.
        if (endOn) support.Add(new BindingSupport(EndTrigger(id), "implementation-only", Array.Empty<string>()));
        return support.ToArray();
    }
    /// <summary>`amountPort` and `instigatorPort` widen the fixture trigger with the optional output ports a test
    /// needs the matched event to be able to carry: a number a trigger option accumulates, and the player a
    /// per-player option is charged against. Both are off by default because a declared port is part of the
    /// trigger's shape — an event may only carry the ports its capability declares — while a test that does not need
    /// one would otherwise have to carry a value for it in every event it publishes.</summary>
    public static RuntimeModule Module(string id, CommandHandler? handler = null, bool amountPort = false, bool instigatorPort = false,
        bool effectPort = false, EffectRestoreHandler? restore = null, bool endOn = false, bool manyPort = false)
    {
        object Port(string name, string type) => new { id = name, type };
        var domains = new[] { "enemy", "weapon", "room", "map", "tool", "consumable", "logic" };
        object[] TriggerPorts()
        {
            // The synthetic event targets the same player kind as ActionRow and EndTriggerRow.
            var ports = new List<object> { Port("next", "execution"), new { id = "target", type = "entity", entityKinds = new[] { "gtfo.player" } } };
            if (instigatorPort) ports.Add(new { id = "instigator", type = "entity", optional = true });
            if (amountPort) ports.Add(new { id = "amount", type = "number", optional = true });
            if (manyPort) ports.Add(new { id = ManyPort, type = "entity", cardinality = "many", optional = true });
            return ports.ToArray();
        }
        // The action row's own ports. A test that asks for an effect handle gets the port the kernel publishes
        // into — and the recipients contract that names it — because a handle with no declared output would be a
        // duration nothing could ever hold.
        object[] ActionPorts(bool handle)
        {
            var ports = new List<object> { Port("next", "execution"), new { id = "result", type = "result", schema = "example.result.apply", fields = ResultFields } };
            if (handle) ports.Add(new { id = "effect", type = "handle", handleKind = "effect", lifetime = "entity_life" });
            return ports.ToArray();
        }
        object Recipients(bool handle) => handle
            ? new { input = "target", target = "entity", cardinality = "one", requires = new[] { "health.current" }, result = "result", handle = "effect" }
            : (object)new { input = "target", target = "entity", cardinality = "one", requires = new[] { "health.current" }, result = "result" };
        var json = RuntimeJson.From(new {
            providers = new[] { new { id, kind = id.StartsWith("forge.") ? "native" : "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = endOn
                ? new object[] { TriggerRow(id), EndTriggerRow(id), ActionRow(id, effectPort) }
                : new object[] { TriggerRow(id), ActionRow(id, effectPort) },
            bindings = endOn
                ? new[] { TriggerBinding(id), EndTriggerBinding(id), ActionBinding(id) }
                : new[] { TriggerBinding(id), ActionBinding(id) }
        });
        object TriggerRow(string owner) => new { id = TriggerCapability(owner), owner, kind = "trigger", label = "Observed event", version = "1.0.0", parameters = new {}, graph = new { domains = domains, execution = "host", inputs = Array.Empty<object>(), outputs = TriggerPorts(), parameters = Array.Empty<object>() } };
        // The receiver's own event: one entity, and the kinds it can be about are the ones the covering check reads.
        object EndTriggerRow(string owner) => new { id = EndTriggerCapability(owner), owner, kind = "trigger", label = "Receiver event", version = "1.0.0", parameters = new {}, graph = new { domains = domains, execution = "host", inputs = Array.Empty<object>(), outputs = new object[] { Port("next", "execution"), new { id = "target", type = "entity", entityKinds = new[] { "gtfo.player" } } }, parameters = Array.Empty<object>() } };
        object ActionRow(string owner, bool handle) => new { id = ActionCapability(owner), owner, kind = "action", label = "Effect", version = "1.0.0", parameters = new {}, graph = new { domains = domains, execution = "host", inputs = new[] { Port("in", "execution"), new { id = "target", type = "entity", entityKinds = new[] { "gtfo.player" } } }, outputs = ActionPorts(handle), parameters = new[] { new { id = "amount", type = "number", role = "value", required = true, minimum = 1, maximum = 100 } }, recipients = Recipients(handle) } };
        object TriggerBinding(string owner) => new { id = Trigger(owner), capabilityId = TriggerCapability(owner), providerId = owner, handler = owner + ".handler.trigger", role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() };
        object EndTriggerBinding(string owner) => new { id = EndTrigger(owner), capabilityId = EndTriggerCapability(owner), providerId = owner, handler = owner + ".handler.ended", role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() };
        object ActionBinding(string owner) => new { id = owner + ".binding.apply", capabilityId = ActionCapability(owner), providerId = owner, handler = Handler(owner), role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() };
        return new RuntimeModule(RuntimeKernel.ApiVersion, json.GetRawText(), new Dictionary<string, CommandHandler> { [Handler(id)] = handler ?? (_ => CommandResult.Succeeded(RuntimeJson.EmptyObject)) }, Support(id, endOn: endOn))
        {
            Shapes = new Dictionary<string, HandlerShape> { [Handler(id)] = Shape(id) },
            // One restore callback per handler name, exactly as the handler table is keyed: the fixture's effect
            // row is only loadable when a test registered the callback that ends it.
            EffectRestores = restore == null ? null : new Dictionary<string, EffectRestoreHandler> { [Handler(id)] = restore }
        };
    }
    /// <summary>The provider that owns the fixture plans' own mount kind. It declares a scope matcher — no event
    /// subject is needed — and answers only the references it was given.</summary>
    public static RuntimeModule MountOwner() => MountOwner(new[] { MountReference });

    /// <summary>The same owner carrying both mount kinds the fixture's plans use: the scope matcher above, and a
    /// subject matcher that accepts every entity a trigger payload carries. A per-instance trigger option is booked
    /// against exactly that accepted entity, so it is the kind a plan scope `instance` has to mount on.</summary>
    public static RuntimeModule MountOwnerWithSubjects() => MountOwner() with
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == MountReference),
            ["map-object"] = AttachmentMatcherRegistration.BySubject((category, reference, subject) => !string.IsNullOrEmpty(subject.Id))
        }
    };

    /// <summary>The same owner scripted with another set of levels: a fixture that loads plans from a foreign
    /// source names the levels those plans mount on, so the double answers exactly those and nothing else.</summary>
    public static RuntimeModule MountOwner(IReadOnlyCollection<string> references) => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = MountProvider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                category == null && references.Contains(reference))
        }
    };
    /// <summary>The fixture plan's entry point, with the optional trigger options block written as JSON so a test
    /// spells the wire shape it is testing rather than a C# object that could drift from it.</summary>
    public static string Plan(RuntimeKernel kernel, string planId, string provider, int stepCount = 1, object[]? attachments = null, string? gate = null, bool amountPort = false, bool instigatorPort = false, bool effectPort = false, string? effect = null)
    {
        var manifest = RuntimeJson.Parse(kernel.ExportManifest()); var registry = manifest.GetProperty("registry");
        var bindings = registry.GetProperty("bindings").EnumerateArray().Where(b => b.GetProperty("providerId").GetString() == provider).OrderBy(b => b.GetProperty("id").GetString(), StringComparer.Ordinal);
        var pins = bindings.Select(b => new {
            bindingId = b.GetProperty("id").GetString(), capabilityId = b.GetProperty("capabilityId").GetString(),
            capabilityVersion = registry.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("id").GetString() == b.GetProperty("capabilityId").GetString()).GetProperty("version").GetString(),
            providerId = provider, providerVersion = "1.0.0", handler = b.GetProperty("handler").GetString()
        }).ToArray();
        var ids = pins.Select(p => p.bindingId!).ToList();
        JsonElement Graph(string bindingId)
        {
            var capabilityId = registry.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("id").GetString() == bindingId).GetProperty("capabilityId").GetString();
            return registry.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("id").GetString() == capabilityId).GetProperty("graph");
        }
        // Wire frame written independently of the SDK: dense port-type and cardinality indexes; these fixture ports
        // carry no value set or lifetime beyond the ones they declare — a handle port's own kind and lifetime are
        // part of the layout the plan has to re-derive, so a fixture port that names either writes its index.
        object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new {
            index, type = Array.IndexOf(PortTypes, p.GetProperty("type").GetString()),
            cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0,
            valueSet = p.TryGetProperty("handleKind", out var k) ? TableIndex("HandleKinds", k.GetString()!) : -1,
            lifetime = p.TryGetProperty("lifetime", out var l) ? TableIndex("HandleLifetimes", l.GetString()!) : -1,
            optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
        }).ToArray();
        object Layout(JsonElement graph, object[] constants) => new { inputs = Slots(graph.GetProperty("inputs")), outputs = Slots(graph.GetProperty("outputs")), constants, promoted = Array.Empty<int>() };
        int Slot(JsonElement ports, string name) => ports.EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == name).i;
        var trigger = Graph(Trigger(provider)); var action = Graph(provider + ".binding.apply");
        var inputs = new[] { new { slot = Slot(action.GetProperty("inputs"), "target"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "target") } };
        // schemaVersion 1: a linear action chain still names its entry point and every successor explicitly, and a
        // chain is already in nodeId ordinal order, so it is its own canonical step order.
        var steps = Enumerable.Range(0, stepCount).Select(i => new {
            nodeId = "Action" + i, nodeKind = "action", binding = ids.IndexOf(provider + ".binding.apply"), layout = Layout(action, new object[] { 5 }),
            inputs, successors = new int?[] { i + 1 < stepCount ? i + 1 : null }
        }).ToArray();
        // Built as a node rather than one anonymous object so a test's option block is spliced in verbatim: the
        // fixture carries the wire shape the compiler writes, not a C# restatement of it.
        var entries = new List<object>();
        entries.Add(new { nodeId = "Entry", binding = ids.IndexOf(Trigger(provider)), layout = Layout(trigger, Array.Empty<object>()), start = 0, steps });
        var document = JsonNode.Parse(RuntimeJson.From(new {
            schemaVersion = 1, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" }, runtime = kernel.Identity,
            domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions = Permissions, dependencies = Array.Empty<string>(),
            limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth }, bindings = pins,
            attachments = attachments ?? Attachments,
            entrypoints = entries.ToArray()
        }).GetRawText())!;
        var main = 0;
        if (gate != null) document["entrypoints"]![main]!["gate"] = JsonNode.Parse(gate);
        // The duration option block, spliced into the first step the same way: the fixture carries the wire shape
        // a compiler emits rather than a C# restatement of it.
        if (effect != null) document["entrypoints"]![main]!["steps"]![0]!["effect"] = JsonNode.Parse(effect);
        return document.ToJsonString();
    }
}
