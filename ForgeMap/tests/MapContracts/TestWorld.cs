using System.Text.Json;
using ForgeRuntime.Framework;

// Synthetic SDK consumer, not a production Map resolver or native creation hook.
sealed class TestWorld
{
    public const string Provider = "map1.test", Prefix = "map1.test.object";
    public const string Trigger = "map1.test.observed", Action = "map1.test.touch";
    public const string Permission = "map1.test.identity.write";
    public readonly RuntimeKernel Kernel = new(new RuntimeIdentity("map1.test.runtime", "1.0.0", RuntimeKernel.ApiVersion, "offline-fixture"));
    public readonly Dictionary<string, long> Lives = new(StringComparer.Ordinal);
    public readonly List<EntityReference> Applied = new();
    public readonly RuntimeModuleHandle Handle;
    public TestWorld(bool loadPlan = true)
    {
        Kernel.BeginWorld(1);
        Kernel.RegisterModule(ForgeMap.ModuleDefinition.Create());
        Handle = Kernel.RegisterModule(Module());
        if (loadPlan && !Kernel.StartRuntime(() => Kernel.LoadPlan(Plan())))
            throw new InvalidOperationException("Synthetic host failed to start.");
    }
    public EntityReference Add(string key, long life = 1)
    {
        var id = Prefix + ":" + key; Lives[id] = life;
        return new EntityReference(id, Kernel.WorldEpoch, life);
    }
    public DispatchResult Publish(string id, EntityReference target, long tick = 1, long? world = null)
        => Handle.Publish(new RuntimeEvent(id, Trigger, world ?? Kernel.WorldEpoch, tick,
            "map1.scope", RuntimeJson.From(new { target })));
    public RuntimeModule Module()
    {
        object Port(string id, string type) => new { id, type };
        var seed = RuntimeJson.From(new {
            providers = new[] { new { id = Provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new object[] {
                new { id = "map1.test.trigger", owner = Provider, kind = "trigger", label = "Synthetic observation", version = "1.0.0", parameters = new {},
                    graph = new { domains = new[] { "map" }, execution = "host", inputs = Array.Empty<object>(),
                        outputs = new[] { Port("next", "execution"), Port("target", "entity") }, parameters = Array.Empty<object>() } },
                new { id = "map1.test.action", owner = Provider, kind = "action", label = "Synthetic identity receipt", version = "1.0.0", parameters = new {},
                    graph = new { domains = new[] { "map" }, execution = "host", inputs = new[] { Port("in", "execution"), Port("target", "entity") },
                        outputs = new[] { Port("next", "execution"), new { id = "result", type = "result", schema = "map1.test.result.identity" } },
                        parameters = Array.Empty<object>(),
                        recipients = new { input = "target", target = "entity", cardinality = "one", requires = new[] { "test.identity" }, result = "result" } } }
            },
            bindings = new[] {
                new { id = Trigger, capabilityId = "map1.test.trigger", providerId = Provider, handler = "map1.test.observe", role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
                new { id = Action, capabilityId = "map1.test.action", providerId = Provider, handler = "map1.test.record", role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
            }
        });
        return new RuntimeModule(RuntimeKernel.ApiVersion, seed.GetRawText(),
            new Dictionary<string, CommandHandler> { ["map1.test.record"] = context => {
                var target = context.GetEntityInput("target"); Applied.Add(target);
                return CommandResult.Succeeded(RuntimeJson.From(new { target }));
            } },
            new[] { new BindingSupport(Trigger, "implementation-only", Array.Empty<string>()),
                new BindingSupport(Action, "implementation-only", new[] { Permission }) },
            new Dictionary<string, Func<EntityReference, bool>> { [Prefix] = reference =>
                reference.WorldEpoch == Kernel.WorldEpoch && Lives.TryGetValue(reference.Id, out var life) && life == reference.LifeEpoch });
    }
    public string Plan(string[]? permissions = null)
    {
        var registry = RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry");
        var bindings = registry.GetProperty("bindings").EnumerateArray()
            .Where(b => b.GetProperty("providerId").GetString() == Provider)
            .OrderBy(b => b.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
        var pins = bindings.Select(b => new {
            bindingId = b.GetProperty("id").GetString(), capabilityId = b.GetProperty("capabilityId").GetString(),
            capabilityVersion = "1.0.0", providerId = Provider, providerVersion = "1.0.0", handler = b.GetProperty("handler").GetString()
        }).ToArray();
        int Binding(string id) => Array.FindIndex(pins, p => p.bindingId == id);
        JsonElement Graph(string id) => registry.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == bindings[Binding(id)].GetProperty("capabilityId").GetString()).GetProperty("graph");
        // Dense slot frame written independently of the SDK; these ports carry no value set or lifetime.
        string[] types = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };
        object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new {
            index, type = Array.IndexOf(types, p.GetProperty("type").GetString()),
            cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0, valueSet = -1, lifetime = -1,
            optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
        }).ToArray();
        object Layout(JsonElement graph) => new { inputs = Slots(graph.GetProperty("inputs")), outputs = Slots(graph.GetProperty("outputs")), constants = Array.Empty<object>(), promoted = Array.Empty<int>() };
        int Slot(JsonElement ports, string name) => ports.EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == name).i;
        var trigger = Graph(Trigger); var action = Graph(Action);
        return RuntimeJson.From(new {
            schemaVersion = 3, kind = "forge-runtime-plan", planId = "map1.test.plan",
            resource = new { id = "map1.test.shared-room", revision = "fixture-revision" }, runtime = Kernel.Identity,
            domain = "map", authority = "host", failurePolicy = "stop-entrypoint", permissions = permissions ?? new[] { Permission }, dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth }, bindings = pins,
            // schemaVersion 3 (D-017 R4-a): the record step owns the action's single execution successor, left
            // unwired so the entrypoint ends after it.
            entrypoints = new[] { new { nodeId = "Observed", binding = Binding(Trigger), layout = Layout(trigger), start = 0,
                steps = new[] { new { nodeId = "RecordIdentity", nodeKind = "action", binding = Binding(Action), layout = Layout(action),
                    inputs = new[] { new { slot = Slot(action.GetProperty("inputs"), "target"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "target") } },
                    successors = new int?[] { null } } } } }
        }).GetRawText();
    }
}
