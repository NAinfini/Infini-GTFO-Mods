using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

// Synthetic SDK consumer, not a production Map resolver or native creation hook.
sealed class TestWorld : IDisposable
{
    public const string Provider = "map1.test", Prefix = "map1.test.object";
    public const string Trigger = "map1.test.observed", Action = "map1.test.touch";
    public const string Permission = "map1.test.identity.write";
    /// <summary>The mount kind the synthetic plan declares and the provider that owns it. The game-independent
    /// Map definition declares no matcher — the native half adds the real ones — so this world stands its own
    /// owner up, and the kind is judged from the mount target alone, which is what a whole-level mount is.</summary>
    public const string MountProvider = "map1.test.mounts", MountReference = "31:A:0";
    private static readonly MapLevelReference MountedLevel = MapLevelReference.TryParse(MountReference)!.Value;
    public static RuntimeModule MountOwner() => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = MountProvider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                category == null && MapLevelReference.TryParse(reference) == MountedLevel)
        }
    };
    public readonly RuntimeKernel Kernel = new(new RuntimeIdentity("map1.test.runtime", "1.0.0", RuntimeKernel.ApiVersion, "offline-fixture"));
    public readonly Dictionary<string, long> Lives = new(StringComparer.Ordinal);
    public readonly List<EntityReference> Applied = new();
    public readonly RuntimeModuleHandle Handle;
    public TestWorld(bool loadPlan = true)
    {
        Kernel.BeginWorld(1);
        Kernel.RegisterModule(MountOwner(), RuntimeLogLevel.Off);
        // The host registers the runtime's own contract providers before any domain package: the Map definition
        // binds Trigger capabilities the trigger contract owns.
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(MapDefinition(), RuntimeLogLevel.Off);
        Handle = Kernel.RegisterModule(Module(), RuntimeLogLevel.Off);
        if (loadPlan && !Kernel.StartRuntime(() => Kernel.LoadPlan(Plan())))
            throw new InvalidOperationException("Synthetic host failed to start.");
    }

    /// <summary>The game-independent Map definition plus the evaluators its on-demand bindings must carry: the
    /// three the declaration itself owns, and the seven player value rows this project has no reader for. This
    /// project compiles no native code, so every evaluator is a stub: a binding's own behaviour is the native
    /// suite's subject, and what is under test here is that the rows register at all. Each handler's shape is the
    /// declaration's own; only the evaluators are added here.</summary>
    internal static RuntimeModule MapDefinition()
    {
        // The stubs answer for exactly the bindings the definition carries whose role resolves an evaluator, so
        // the registration's own both-ways check passes: a stub for a row the declaration does not carry would be
        // an unused evaluator, which is the registration refusing to be declared twice.
        using var registry = JsonDocument.Parse(ForgeMap.ModuleDefinition.Create().RegistryJson);
        var capabilities = registry.RootElement.GetProperty("capabilities").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var stubbed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in registry.RootElement.GetProperty("bindings").EnumerateArray())
        {
            if (row.GetProperty("status").GetString() != "implemented") continue;
            var role = row.GetProperty("role").GetString();
            if (role != "observe" && role != "evaluate") continue;
            // A binding whose capability this provider does not declare is one the runtime's own trigger contract
            // owns: it is a trigger row, so it resolves no evaluator and carries no stub here.
            if (!capabilities.TryGetValue(row.GetProperty("capabilityId").GetString()!, out var capability)) continue;
            var kind = capability.GetProperty("kind").GetString();
            if (role == "observe" && kind is not ("selector" or "condition" or "state")) continue;
            stubbed.Add(row.GetProperty("handler").GetString()!);
        }
        var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal);
        foreach (var name in stubbed)
            evaluators[name] = _ => throw new InvalidOperationException("The synthetic world does not evaluate " + name + ".");
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        foreach (var name in stubbed)
        {
            if (ForgeMap.ModuleDefinition.FormShapes.TryGetValue(name, out var own)) shapes[name] = own;
            else if (name == ForgeMap.DoorQueryContract.HandlerName) shapes[name] = ForgeMap.DoorQueryContract.Shape;
            else if (ForgeMap.PlayerStateContract.ValueShapes().TryGetValue(name, out var value)) shapes[name] = value;
            // The value rows the definition binds carry their shapes in their own contract, exactly as the rows
            // do; a stub whose shape were missing is a registration the runtime refuses. The generator row is the
            // map-object category's own (ruling 148.4) and declares its shape the same way.
            else if (ForgeMap.LevelObjectContract.Shapes().TryGetValue(name, out var level)) shapes[name] = level;
            else if (ForgeMap.GeneratorContract.Shapes().TryGetValue(name, out var generator)) shapes[name] = generator;
        }
        return ForgeMap.ModuleDefinition.Create() with { Evaluators = evaluators, Shapes = shapes };
    }

    /// <summary>The handler names the definition's own stub table answers for, for a case that needs to know.</summary>
    internal static IReadOnlyCollection<string> StubbedHandlers() => MapDefinition().Evaluators.Keys.ToArray();
    public EntityReference Add(string key, long life = 1)
    {
        var id = Prefix + ":" + key; Lives[id] = life;
        return new EntityReference(id, Kernel.WorldEpoch, life);
    }
    public void Dispose()
    {
        if (Kernel.StartupState != RuntimeStartupState.Registering) Kernel.StopRuntime();
    }
    public DispatchResult Publish(string id, EntityReference target, long tick = 1, long? world = null)
        => Handle.Publish(new RuntimeEvent(id, Trigger, world ?? Kernel.WorldEpoch, tick,
            "map1.scope", RuntimeJson.From(new { target })));
    public RuntimeModule Module()
    {
        object Port(string id, string type) => new { id, type };
        // A result port declares its row shape: the four shared columns first, in the framework's fixed order.
        object[] IdentityRow() => new object[]
        {
            new { id = "target", type = "entity" }, new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" }, new { id = "code", type = "string" }
        };
        var seed = RuntimeJson.From(new {
            providers = new[] { new { id = Provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new object[] {
                new { id = "map1.test.trigger", owner = Provider, kind = "trigger", label = "Synthetic observation", version = "1.0.0", parameters = new {},
                    graph = new { domains = new[] { "map" }, execution = "host", inputs = Array.Empty<object>(),
                        outputs = new[] { Port("next", "execution"), Port("target", "entity") }, parameters = Array.Empty<object>() } },
                new { id = "map1.test.action", owner = Provider, kind = "action", label = "Synthetic identity receipt", version = "1.0.0", parameters = new {},
                    graph = new { domains = new[] { "map" }, execution = "host", inputs = new[] { Port("in", "execution"), Port("target", "entity") },
                        outputs = new[] { Port("next", "execution"), new { id = "result", type = "result", schema = "map1.test.result.identity", fields = IdentityRow() } },
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
                reference.WorldEpoch == Kernel.WorldEpoch && Lives.TryGetValue(reference.Id, out var life) && life == reference.LifeEpoch })
        {
            // The record handler reads the action's recipient entity and writes the result row by name at this stage.
            Shapes = new Dictionary<string, HandlerShape> { ["map1.test.record"] = new HandlerShape().Inputs("target").Outputs("result") }
        };
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
            schemaVersion = 1, kind = "forge-runtime-plan", planId = "map1.test.plan",
            resource = new { id = "map1.test.shared-room", revision = "fixture-revision" }, runtime = Kernel.Identity,
            domain = "map", authority = "host", failurePolicy = "stop-entrypoint", permissions = permissions ?? new[] { Permission }, dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth }, bindings = pins,
            // A plan declares which mount target it belongs to; the synthetic mount owner answers this one, so
            // the plan is dispatched only while the world it names is the world the kernel is in.
            attachments = new[] { new { kind = "level", reference = MountReference } },
            // schemaVersion 1: the record step owns the action's single execution successor, left
            // unwired so the entrypoint ends after it.
            entrypoints = new[] { new { nodeId = "Observed", binding = Binding(Trigger), layout = Layout(trigger), start = 0,
                steps = new[] { new { nodeId = "RecordIdentity", nodeKind = "action", binding = Binding(Action), layout = Layout(action),
                    inputs = new[] { new { slot = Slot(action.GetProperty("inputs"), "target"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "target") } },
                    successors = new int?[] { null } } } } }
        }).GetRawText();
    }
}
