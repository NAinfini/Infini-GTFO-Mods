using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>Kernel-side I-DIAG fixture: three managed test providers with real declared graph contracts, one compiled
/// plan and the action results a record-point check needs. The trigger and the two action providers are deliberately
/// different packages, so the attribution rules are observable: a step belongs to the provider that executes it, not to
/// the entry's trigger. It shares nothing with the domain packages — the kernel records it exercises are the same ones
/// any provider reaches through the public SDK.</summary>
internal sealed class RecordFixture
{
    internal const string Runtime = "forge.runtime";
    internal const string TriggerProvider = "test.records.trigger";
    internal const string ActionProvider = "test.records.action";
    internal const string SecondActionProvider = "test.records.second";
    internal const string TriggerBinding = TriggerProvider + ".binding.fired";
    internal const string ActionBinding = ActionProvider + ".binding.act";
    internal const string FailBinding = ActionProvider + ".binding.fail";
    internal const string SecondActionBinding = SecondActionProvider + ".binding.act";
    internal const string SecondTriggerBinding = SecondActionProvider + ".binding.fired";
    internal const string TriggerCapability = TriggerProvider + ".capability.fired";
    internal const string ActionCapability = ActionProvider + ".capability.act";
    internal const string FailCapability = ActionProvider + ".capability.fail";
    internal const string SecondActionCapability = SecondActionProvider + ".capability.act";
    internal const string SecondTriggerCapability = SecondActionProvider + ".capability.fired";

    internal readonly RuntimeKernel Kernel;
    internal readonly CaptureSink Sink;
    internal readonly RuntimeModuleHandle Trigger;
    internal readonly RuntimeModuleHandle Action;
    internal readonly RuntimeModuleHandle SecondAction;
    internal string Plan = "";

    internal RecordFixture(RuntimeLogLevel runtimeLevel, RuntimeLimits? limits = null)
    {
        Sink = new CaptureSink();
        Kernel = new RuntimeKernel(new RuntimeIdentity(Runtime, "1.0.0", RuntimeKernel.ApiVersion, "managed-test"),
            limits ?? new RuntimeLimits(), Sink, runtimeLevel);
        // A kernel without a sink keeps no level table, so a module level is whatever the fixture was asked to run at.
        var moduleLevel = runtimeLevel == RuntimeLogLevel.Off ? RuntimeLogLevel.Off : RuntimeLogLevel.Info;
        Kernel.RegisterModule(LevelMount(), RuntimeLogLevel.Off);
        Trigger = Kernel.RegisterModule(TriggerModule(), moduleLevel);
        Action = Kernel.RegisterModule(ActionModule(), moduleLevel);
        SecondAction = Kernel.RegisterModule(SecondActionModule(), moduleLevel);
    }

    internal RuntimeLogRecord[] Records(string code, string? provider = null)
        => Sink.Records.Where(r => r.Code == code && (provider == null || r.Provider == provider)).ToArray();

    internal RuntimeModuleHandle AddTriggerBinding(string provider, string binding)
        => Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new[] { new { id = provider + ".capability.fired", owner = provider, kind = "trigger", version = "1.0.0",
                label = "test trigger", parameters = new { }, graph = Graph("trigger", "next") } },
            bindings = new[] { new { id = binding, capabilityId = provider + ".capability.fired", providerId = provider, handler = provider + ".handler",
                role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() } }
        }).GetRawText(), new Dictionary<string, CommandHandler>(), new[] { new BindingSupport(binding, "implementation-only", Array.Empty<string>()) }),
            RuntimeLogLevel.Info);

    internal readonly record struct ActionDefinition(string CapabilityId, string Kind, string Execution, string Wiring);

    /// <summary>Every fixture plan mounts the whole level, and no mount kind belongs to the kernel any more: the
    /// provider that owns the kind has to be registered before the plan loads. This double owns it and answers the
    /// one reference the fixture plans carry, so the record points stay observable without a domain provider.</summary>
    private static RuntimeModule LevelMount() => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = "test.records.level", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == LevelReference)
        }
    };
    private const string LevelReference = "test.records.level";
    private static readonly object[] Attachments = { new { kind = "level", reference = LevelReference } };

    /// <summary>One plan: a trigger entry whose entity output feeds a parameterless action, and a second entry behind its
    /// own trigger whose first action a test can fail while a successor step owned by another provider is still unexecuted.
    /// The two entries use different trigger bindings, so a test publishes to exactly one of them.</summary>
    internal string CompiledPlan(string planId = "test.records.plan", int maxEventsPerTick = 8, int maxCommandsPerTick = 8, int maxQueuedEvents = 8)
    {
        // The plan validator requires the pin table in ordinal order.
        var pins = new[] { ActionBinding, FailBinding, SecondActionBinding, SecondTriggerBinding, TriggerBinding }
            .Select(id => Pin(id)).ToArray();
        var triggerLayout = Layout(TriggerCapability, RuntimeJson.EmptyObject);
        var actionLayout = Layout(ActionCapability, RuntimeJson.EmptyObject);
        var failLayout = Layout(FailCapability, RuntimeJson.EmptyObject);
        var secondLayout = Layout(SecondActionCapability, RuntimeJson.EmptyObject);
        var secondTriggerLayout = Layout(SecondTriggerCapability, RuntimeJson.EmptyObject);
        // The action takes its entity target from the trigger's event slot and its amount as a literal.
        object[] ActionInputs() => new object[] { new { slot = 1, fromEventSlot = 1 }, new { slot = 2, value = 3 } };
        return RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId, resource = new { id = planId, revision = "1" },
            runtime = Kernel.Identity, domain = "logic", authority = "host", failurePolicy = "stop-entrypoint",
            permissions = Array.Empty<string>(), dependencies = Array.Empty<string>(),
            limits = new { maxEventsPerTick, maxCommandsPerTick, maxQueuedEvents, maxCausalDepth = 4 },
            bindings = pins, attachments = Attachments,
            entrypoints = new object[]
            {
                new { nodeId = "A", binding = Index(pins, TriggerBinding), layout = triggerLayout, start = 0, steps = new object[]
                {
                    new { nodeId = "B", nodeKind = "action", binding = Index(pins, ActionBinding), layout = actionLayout,
                        inputs = ActionInputs(), successors = new int?[] { null } }
                } },
                new { nodeId = "C", binding = Index(pins, SecondTriggerBinding), layout = secondTriggerLayout, start = 0, steps = new object[]
                {
                    new { nodeId = "D", nodeKind = "action", binding = Index(pins, FailBinding), layout = failLayout,
                        inputs = ActionInputs(), successors = new int?[] { 1 } },
                    new { nodeId = "E", nodeKind = "action", binding = Index(pins, SecondActionBinding), layout = secondLayout,
                        inputs = ActionInputs(), successors = new int?[] { null } }
                } }
            }
        }).GetRawText();
    }

    internal static EntityReference Target(long worldEpoch) => new("test.entity:records", worldEpoch, 1);

    internal RuntimeEvent Event(string eventId, string binding = TriggerBinding, long tick = 1)
        => new(eventId, binding, Kernel.WorldEpoch, tick, "test.records.scope",
            RuntimeJson.From(new { target = Target(Kernel.WorldEpoch) }));

    /// <summary>Starts the runtime with the one plan loaded, then advances once so the kernel is ready to dispatch.</summary>
    internal void Start(string planId = "test.records.plan", int maxEventsPerTick = 8, int maxCommandsPerTick = 8, int maxQueuedEvents = 8)
    {
        Plan = CompiledPlan(planId, maxEventsPerTick, maxCommandsPerTick, maxQueuedEvents);
        Kernel.BeginWorld(1);
        Kernel.StartRuntime(() =>
        {
            var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded("pack/plans/" + planId + ".plan.json", Plan) })[0];
            if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
        });
        Kernel.Advance(0, true);
    }

    internal RuntimeLogRecord[] AdvanceAndCollect(string eventId, string binding = TriggerBinding, long tick = 1)
    {
        // The kernel only accepts an event from the provider that owns the binding, and the second entry's trigger
        // belongs to the second package.
        var dispatch = Owner(binding).Publish(Event(eventId, binding, Math.Max(0, Kernel.CurrentTick)));
        if (dispatch.Status != "queued") throw new Exception("publish refused: " + dispatch.Code);
        Kernel.Advance(tick, true);
        return Sink.Records.ToArray();
    }

    private RuntimeModuleHandle Owner(string binding) => binding == SecondTriggerBinding ? SecondAction : Trigger;

    private object Pin(string bindingId)
    {
        var manifest = JsonDocument.Parse(Kernel.ExportManifest()).RootElement;
        var registry = manifest.GetProperty("registry");
        JsonElement Row(string list, string id) => registry.GetProperty(list).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        var binding = Row("bindings", bindingId);
        var capabilityId = binding.GetProperty("capabilityId").GetString()!;
        var providerId = binding.GetProperty("providerId").GetString()!;
        return new { bindingId, capabilityId, capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
            providerId, providerVersion = Row("providers", providerId).GetProperty("version").GetString()!, handler = binding.GetProperty("handler").GetString()! };
    }

    private int Index(object[] pins, string bindingId)
    {
        for (var i = 0; i < pins.Length; i++)
            if ((string)pins[i].GetType().GetProperty("bindingId")!.GetValue(pins[i])! == bindingId) return i;
        throw new InvalidOperationException("pin missing: " + bindingId);
    }

    private object Layout(string capabilityId, JsonElement parameters)
    {
        var resolved = Kernel.ResolveGraphContract(capabilityId, "1.0.0", parameters);
        return new { inputs = Slots(resolved, "inputs"), outputs = Slots(resolved, "outputs"),
            constants = JsonDocument.Parse(RuntimeJson.From(parameters).GetRawText()).RootElement.EnumerateObject().Select(p => (object?)p.Value).ToArray(),
            promoted = Array.Empty<int>() };
    }

    private static readonly MethodInfo LayoutOf = typeof(RuntimeKernel).Assembly
        .GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!.GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static object Slots(JsonElement resolved, string side)
        => LayoutOf.Invoke(null, new object[] { resolved, side })!;

    /// <summary>The fixture result schema's row: the four shared columns in their fixed order, then the amount the
    /// action applied and the number of targets it reached. Nothing writes a row here — the declared shape is what
    /// makes the capability registrable, and the same shape covers all three action capabilities.</summary>
    private static readonly object[] ResultFields =
    {
        new { id = "target", type = "entity" },
        new { id = "status", type = "enum", schema = "execution_outcome" },
        new { id = "committed", type = "enum", schema = "commit_state" },
        new { id = "code", type = "string" },
        new { id = "amount", type = "number", unit = "hp" },
        new { id = "target_count", type = "integer" }
    };

    private static object Graph(string kind, string executionPort)
    {
        if (kind != "action")
            return new Dictionary<string, object?>
            {
                ["domains"] = new[] { "logic" }, ["execution"] = "host", ["inputs"] = Array.Empty<object>(),
                ["outputs"] = new object[] { new { id = executionPort, type = "execution" }, new { id = "target", type = "entity" } },
                ["parameters"] = Array.Empty<object>()
            };
        // An action needs a recipient contract: it ties the entity input and the result output together. The kernel
        // never applies the recipient here — a test provider has no native receiver — so the rows stay declarative.
        return new Dictionary<string, object?>
        {
            ["domains"] = new[] { "logic" }, ["execution"] = "host",
            ["inputs"] = new object[] { new { id = "in", type = "execution" }, new { id = "target", type = "entity" },
                new { id = "amount", type = "number" } },
            ["outputs"] = new object[] { new { id = executionPort, type = "execution" },
                new { id = "outcome", type = "result", schema = "test.result", fields = ResultFields } },
            ["parameters"] = Array.Empty<object>(),
            ["recipients"] = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "outcome" }
        };
    }

    private static RuntimeModule TriggerModule() => PublicTriggerModule();

    /// <summary>The same trigger provider without a fixture, for checks that need their own sink or kernel.</summary>
    internal static RuntimeModule PublicTriggerModule() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = TriggerProvider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = new[] { new { id = TriggerCapability, owner = TriggerProvider, kind = "trigger", version = "1.0.0",
            label = "test trigger", parameters = new { }, graph = Graph("trigger", "next") } },
        bindings = new[] { new { id = TriggerBinding, capabilityId = TriggerCapability, providerId = TriggerProvider, handler = "test.trigger",
            role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() } }
    }).GetRawText(), new Dictionary<string, CommandHandler>(), new[] { new BindingSupport(TriggerBinding, "implementation-only", Array.Empty<string>()) });

    private RuntimeModule ActionModule()
    {
        var capabilities = new[] { ActionCapability, FailCapability }.Select(id => new { id, owner = ActionProvider, kind = "action",
            version = "1.0.0", label = "test action", parameters = new { }, graph = Graph("action", "next") }).ToArray();
        var bindings = new[] { (ActionBinding, ActionCapability, "test.action"), (FailBinding, FailCapability, "test.fail") }
            .Select(x => new { id = x.Item1, capabilityId = x.Item2, providerId = ActionProvider, handler = x.Item3, role = "execute",
                status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }).ToArray();
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            ["test.action"] = _ => CommandResult.Succeeded(RuntimeJson.EmptyObject),
            // A handler that throws after invocation is the kernel's own `handler-exception` path: failed with an unknown
            // commit state, which the kernel records at error level.
            ["test.fail"] = _ => throw new InvalidOperationException("test refused the command")
        };
        return new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = ActionProvider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities, bindings
        }).GetRawText(), handlers, bindings.Select(b => new BindingSupport(b.id, "implementation-only", Array.Empty<string>())).ToArray())
        {
            // Both doubles answer the whole command without reading a port, so their shapes declare none.
            Shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
            { ["test.action"] = new HandlerShape(), ["test.fail"] = new HandlerShape() },
            // The plan wires an entity from the trigger event into the action, so the kernel resolves it through the
            // entity reference contract: this namespace is what makes such an input valid.
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            { ["test.entity"] = reference => reference.Id == Target(reference.WorldEpoch).Id }
        };
    }

    /// <summary>A second extension package: its own trigger entry and its own action, so a step's provider differs from
    /// another entry's trigger provider and the two entries answer different trigger bindings.</summary>
    private RuntimeModule SecondActionModule() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = SecondActionProvider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = new[]
        {
            new { id = SecondActionCapability, owner = SecondActionProvider, kind = "action", version = "1.0.0",
                label = "test second action", parameters = new { }, graph = Graph("action", "next") },
            new { id = SecondTriggerCapability, owner = SecondActionProvider, kind = "trigger", version = "1.0.0",
                label = "test second trigger", parameters = new { }, graph = Graph("trigger", "next") }
        },
        bindings = new[]
        {
            new { id = SecondActionBinding, capabilityId = SecondActionCapability, providerId = SecondActionProvider,
                handler = "test.second", role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
            new { id = SecondTriggerBinding, capabilityId = SecondTriggerCapability, providerId = SecondActionProvider,
                handler = "test.second.trigger", role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
        }
    }).GetRawText(), new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
    { ["test.second"] = _ => CommandResult.Succeeded(RuntimeJson.EmptyObject) },
        new[] { new BindingSupport(SecondActionBinding, "implementation-only", Array.Empty<string>()),
            new BindingSupport(SecondTriggerBinding, "implementation-only", Array.Empty<string>()) })
    {
        Shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal) { ["test.second"] = new HandlerShape() }
    };
}
