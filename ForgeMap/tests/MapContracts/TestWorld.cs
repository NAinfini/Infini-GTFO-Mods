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
        if (loadPlan && !Kernel.StartRuntime(() => Kernel.LoadPlan(Plan(), new[] { Permission })))
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
                        outputs = new[] { Port("next", "execution") }, parameters = Array.Empty<object>(),
                        recipients = new { input = "target", requires = new[] { "test.identity" } } } }
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
    public string Plan()
    {
        var manifest = RuntimeJson.Parse(Kernel.ExportManifest());
        var pins = manifest.GetProperty("registry").GetProperty("bindings").EnumerateArray()
            .Where(b => b.GetProperty("providerId").GetString() == Provider).Select(b => new {
                bindingId = b.GetProperty("id").GetString(), capabilityId = b.GetProperty("capabilityId").GetString(),
                capabilityVersion = "1.0.0", providerId = Provider, providerVersion = "1.0.0", handler = b.GetProperty("handler").GetString()
            }).ToArray();
        return RuntimeJson.From(new {
            schemaVersion = 1, kind = "forge-runtime-plan", planId = "map1.test.plan",
            resource = new { id = "map1.test.shared-room", revision = "fixture-revision" }, runtime = Kernel.Identity,
            domain = "map", authority = "host", failurePolicy = "stop-entrypoint", permissions = new[] { Permission }, dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth }, bindings = pins,
            entrypoints = new[] { new { nodeId = "Observed", bindingId = Trigger, parameters = new {},
                steps = new[] { new { nodeId = "RecordIdentity", bindingId = Action, parameters = new {}, inputs = new { target = new { fromEventPort = "target" } } } } } }
        }).GetRawText();
    }
}
