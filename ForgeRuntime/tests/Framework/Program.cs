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
    Reject(() => s.Kernel.RegisterModule(Fixture.Module("example.third") with { ApiVersion = "1.0.0" }), "API version mismatch");
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
    bad = JsonNode.Parse(plan)!; bad["schemaVersion"] = 1;
    Load(bad, "plan-version", "schemaVersion 1 plans are not read");
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
    Reject(() => s.Kernel.RegisterModule(planned with { RegistryJson = registry.ToJsonString() }), "planned catalog entry cannot register execution");
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
    var handle = s.Kernel.RegisterModule(module with { RegistryJson = seed.ToJsonString(), EntityResolvers = s.Resolvers(id) });
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
        var enumHandle = s.Kernel.RegisterModule(enumModule with { RegistryJson = enumSeed.ToJsonString(), EntityResolvers = s.Resolvers(enumId) });
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
        var opHandle = s.Kernel.RegisterModule(opModule with { RegistryJson = opSeed.ToJsonString(), EntityResolvers = s.Resolvers(enumId) });
        var opPlan = JsonNode.Parse(Fixture.Plan(s.Kernel, "promote_enum", enumId))!;
        opPlan["entrypoints"]![0]!["layout"]!["outputs"]![2]!["valueSet"] = 0;
        Step(opPlan)["layout"]!["constants"] = JsonNode.Parse("[5,null]"); Step(opPlan)["layout"]!["promoted"] = JsonNode.Parse("[1]");
        // compare_operator is the first declared enum set, so its compiled valueSet index is 0.
        Step(opPlan)["layout"]!["inputs"]!.AsArray().Add(JsonNode.Parse("{\"index\":2,\"type\":5,\"cardinality\":0,\"valueSet\":0,\"lifetime\":-1,\"optional\":true,\"nullable\":false}"));
        Step(opPlan)["inputs"]!.AsArray().Add(JsonNode.Parse("{\"slot\":2,\"fromEventSlot\":2}"));
        s.Kernel.LoadPlan(opPlan.ToJsonString());
        Check(s.Kernel.HasSubscribers(Fixture.Trigger(enumId)), "a legal enum promotion registers a subscription");
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
    // Forge Standard v0.2 registration rules mirrored from the website's validateCapabilityGraph.
    void Bad(Action<JsonNode> mutate, string? code, string name)
    {
        var module = Fixture.Module("example.contract"); var json = JsonNode.Parse(module.RegistryJson)!;
        mutate(json["capabilities"]![1]!["graph"]!);
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        void Register() => kernel.RegisterModule(module with { RegistryJson = json.ToJsonString() });
        if (code == null) Reject(Register, name); else RejectCode(Register, code, name);
    }
    Bad(g => g.AsObject().Remove("recipients"), "recipient-contract", "every action declares recipients");
    Bad(g => g["recipients"]!["result"] = "next", "recipient-result", "recipient result names a result output");
    Bad(g => g["parameters"]![0]!.AsObject().Remove("role"), null, "parameters declare a role");
    Bad(g => g["inputs"]![1]!["type"] = "entity-list", null, "entity-list is retired");
    Bad(g => g["inputs"]![1]!["cardinality"] = "many", "recipient-cardinality", "recipient cardinality matches its port");
    Bad(g => g["inputs"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"source\",\"type\":\"entity\",\"optional\":true}")), "context-role-port", "context role inputs are explicit");
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
        var handle = kernel.RegisterModule(module with { RegistryJson = json.ToJsonString() });
        Check(handle.IsRegistered, "a valid result-port codes array registers");
    }
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
            kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new { providers = new[] { provider }, capabilities, bindings }).GetRawText(), handlers, support));
        }
        return kernel;
    }
    var valid = Registered(); valid.LoadPlan(File.ReadAllText(Path.Combine(directory, cases.GetProperty("validPlan").GetString()!)));
    Check(valid.LoadedPlans == 1, "actual TypeScript compiled native fixture consumed by C#");
    foreach (var row in cases.GetProperty("invalidPlans").EnumerateArray())
    {
        var json = File.ReadAllText(Path.Combine(directory, row.GetProperty("file").GetString()!));
        Reject(() => Registered().LoadPlan(json), "shared invalid fixture " + row.GetProperty("id").GetString());
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
    public void Plan(string plan, string provider, int steps = 1) => Kernel.LoadPlan(Fixture.Plan(Kernel, plan, provider, steps));
    public RuntimeEvent Event(string id, string provider, long tick = 1) => new(id, Fixture.Trigger(provider), World, tick, "shared-scope", RuntimeJson.From(new { target = new EntityReference(provider + ":1", World, Life) }));
}
static class Fixture
{
    public static readonly RuntimeIdentity Identity = new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457");
    public static readonly string[] Permissions = { "example.health.write" };
    static readonly string[] PortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };
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
                new { id = ActionCapability(id), owner = id, kind = "action", label = "Effect", version = "1.0.0", parameters = new {}, graph = new { domains = new[] { "enemy", "weapon", "room", "map", "tool", "consumable", "logic" }, execution = "host", inputs = new[] { Port("in", "execution"), Port("target", "entity") }, outputs = new[] { Port("next", "execution"), new { id = "result", type = "result", schema = "example.result.apply" } }, parameters = new[] { new { id = "amount", type = "number", role = "value", required = true, minimum = 1, maximum = 100 } }, recipients = new { input = "target", target = "entity", cardinality = "one", requires = new[] { "health.current" }, result = "result" } } }
            },
            bindings = new[] {
                new { id = Trigger(id), capabilityId = TriggerCapability(id), providerId = id, handler = id + ".handler.trigger", role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
                new { id = id + ".binding.apply", capabilityId = ActionCapability(id), providerId = id, handler = Handler(id), role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
            }
        });
        return new RuntimeModule(RuntimeKernel.ApiVersion, json.GetRawText(), new Dictionary<string, CommandHandler> { [Handler(id)] = handler ?? (_ => CommandResult.Succeeded(RuntimeJson.EmptyObject)) }, Support(id));
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
        var ids = pins.Select(p => p.bindingId!).ToList();
        JsonElement Graph(string bindingId)
        {
            var capabilityId = registry.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("id").GetString() == bindingId).GetProperty("capabilityId").GetString();
            return registry.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("id").GetString() == capabilityId).GetProperty("graph");
        }
        // Wire frame written independently of the SDK: dense port-type and cardinality indexes; these fixture ports carry no value set or lifetime.
        object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new {
            index, type = Array.IndexOf(PortTypes, p.GetProperty("type").GetString()),
            cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0, valueSet = -1, lifetime = -1,
            optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
        }).ToArray();
        object Layout(JsonElement graph, object[] constants) => new { inputs = Slots(graph.GetProperty("inputs")), outputs = Slots(graph.GetProperty("outputs")), constants, promoted = Array.Empty<int>() };
        int Slot(JsonElement ports, string name) => ports.EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == name).i;
        var trigger = Graph(Trigger(provider)); var action = Graph(provider + ".binding.apply");
        return RuntimeJson.From(new {
            schemaVersion = 2, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" }, runtime = kernel.Identity,
            domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions = Permissions, dependencies = Array.Empty<string>(),
            limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth }, bindings = pins,
            entrypoints = new[] { new { nodeId = "Entry", binding = ids.IndexOf(Trigger(provider)), layout = Layout(trigger, Array.Empty<object>()), steps = Enumerable.Range(0, stepCount).Select(i => new { nodeId = "Action" + i, binding = ids.IndexOf(provider + ".binding.apply"), layout = Layout(action, new object[] { 5 }), inputs = new[] { new { slot = Slot(action.GetProperty("inputs"), "target"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "target") } } }).ToArray() } }
        }).GetRawText();
    }
}
