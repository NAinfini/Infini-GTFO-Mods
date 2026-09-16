using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// The world one variable case runs in: a kernel with the Runtime's own control and variable rows, a test provider
/// that supplies the one trigger and the one recording action the plans hang on, and a plan builder that derives
/// every pin, port slot and constant frame from the kernel's own registry — the suite never restates a shape it is
/// checking.
/// </summary>
internal sealed class VariableFixture
{
    internal const string PingBinding = "test.vars.binding.ping";
    internal const string PingCapability = "test.vars.ping";
    internal const string LevelMountProvider = "test.vars.mount";

    internal readonly RuntimeKernel Kernel;
    internal readonly RuntimeModuleHandle Owner;
    internal readonly List<string> Plans = new();
    internal EntityReference Target => new("test.entity:1", Kernel.WorldEpoch, 1);
    internal EntityReference SecondTarget => new("test.entity:2", Kernel.WorldEpoch, 1);

    internal VariableFixture()
    {
        Kernel = new(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "test-no-game"));
        Kernel.BeginWorld(1);
        Kernel.RegisterModule(LevelMount(), RuntimeLogLevel.Off);
        Owner = Kernel.RegisterModule(TestProvider(), RuntimeLogLevel.Off);
        Kernel.RegisterBuiltinModule(ControlContracts.Module());
        Kernel.RegisterBuiltinModule(VariableContracts.Module());
        Kernel.StartRuntime(() => { });
    }

    /// <summary>Every plan the fixture built, as the candidate list a checkpoint restore re-arms from.</summary>
    internal IReadOnlyList<PlanCandidate> Candidates()
        => Plans.Select((json, index) => PlanCandidate.Loaded("test/plan" + index + ".plan.json", json)).ToArray();

    internal void Load(string plan)
    {
        Plans.Add(plan);
        var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/plan" + (Plans.Count - 1) + ".plan.json", plan) })[0];
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
    }

    /// <summary>Publishes the fixture's own trigger. The entity and the handle it carries are the subjects a plan's
    /// scoped steps read.</summary>
    internal void Ping(string eventId, EntityReference? target = null, JsonElement? wave = null, long tick = 10)
    {
        var outputs = wave is { } handle
            ? RuntimeJson.From(new { target = target ?? Target, wave = handle })
            : RuntimeJson.From(new { target = (object?)(target ?? Target), wave = (JsonElement?)null });
        var result = Owner.Publish(new RuntimeEvent(eventId, PingBinding, Kernel.WorldEpoch, tick, "test.vars.scope", outputs));
        if (result.Status != "queued") throw new InvalidOperationException("Ping was not queued: " + result.Status + "/" + result.Code);
    }

    internal TickResult Advance(long tick) => Kernel.Advance(tick, true);

    // -------------------------------------------------------------------------------------------------------
    // The test provider
    // -------------------------------------------------------------------------------------------------------
    private RuntimeModule TestProvider() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = "test.vars", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = new object[]
        {
            new { id = PingCapability, owner = "test.vars", kind = "trigger", version = "1.0.0", label = "test ping",
                parameters = new { description = "The suite's own trigger: its payload carries the subjects a scoped step reads." },
                graph = new { domains = new[] { "logic" }, execution = "host", inputs = Array.Empty<object>(),
                    outputs = new object[] { Port("next", "execution"), Port("target", "entity", nullable: true), Port("wave", "handle", nullable: true, handleKind: "effect", lifetime: "encounter") },
                    parameters = Array.Empty<object>() } }
        },
        bindings = new object[]
        {
            new { id = PingBinding, capabilityId = PingCapability, providerId = "test.vars", handler = "test.vars.ping", role = "observe",
                status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
        }
    }).GetRawText(),
    new Dictionary<string, CommandHandler>(),
    new[] { new BindingSupport(PingBinding, "implementation-only", Array.Empty<string>()) },
    new Dictionary<string, Func<EntityReference, bool>>
    {
        ["test.entity"] = reference => reference == Target || reference == SecondTarget
    });

    private static object Port(string id, string type, bool optional = false, bool nullable = false, string? handleKind = null, string? lifetime = null)
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal) { ["id"] = id, ["type"] = type };
        if (optional) fields["optional"] = true;
        if (nullable) fields["nullable"] = true;
        if (handleKind != null) fields["handleKind"] = handleKind;
        if (lifetime != null) fields["lifetime"] = lifetime;
        return fields;
    }

    /// <summary>The one attachment kind the fixture's plans mount on: the whole level, the way a test world has no
    /// map object to name.</summary>
    private static RuntimeModule LevelMount() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = LevelMountProvider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == "test.level")
        }
    };

    // -------------------------------------------------------------------------------------------------------
    // Registry reads the plan builder derives from
    // -------------------------------------------------------------------------------------------------------
    internal JsonElement Manifest() => RuntimeJson.Parse(Kernel.ExportManifest());

    internal JsonElement Registry() => Manifest().GetProperty("registry");

    internal JsonElement Row(string list, string id)
        => Registry().GetProperty(list).EnumerateArray().Single(row => RuntimeJson.Text(row, "id") == id);

    /// <summary>The graph of one capability, resolved with the node's own parameter values: a `value_type` port is
    /// only a port once its member is known, so the plan's layout is a function of the node's constants.</summary>
    internal JsonElement Contract(string bindingId, IReadOnlyDictionary<string, object?> parameters)
        => RuntimeGraphContracts.Resolve(Row("capabilities", RuntimeJson.Text(Row("bindings", bindingId), "capabilityId")).GetProperty("graph"), RuntimeJson.From(parameters));
}

/// <summary>One plan, built the way the website compiler builds it: pins locked to the live registry, one layout per
/// node derived from that node's own resolved contract, and steps in the canonical walk order the loader requires
/// (every successor names a later step).</summary>
internal sealed class PlanBuilder
{
    private sealed record StepRow(string NodeId, string Kind, string BindingId, JsonElement Parameters, object[] Inputs, int?[] Successors);
    private sealed record VariableRow(string Id, string Scope, string Type, object? Initial);

    private readonly VariableFixture fixture;
    private readonly string triggerBinding;
    private readonly IReadOnlyDictionary<string, object?> triggerParameters;
    private readonly List<VariableRow> variables = new();
    private readonly List<object> objects = new();
    private readonly List<string> boundBindings = new();
    private readonly List<StepRow> steps = new();
    private string planId = "test.vars.plan";

    internal PlanBuilder(VariableFixture fixture, string triggerBinding, IReadOnlyDictionary<string, object?>? triggerParameters = null)
    {
        this.fixture = fixture; this.triggerBinding = triggerBinding;
        this.triggerParameters = triggerParameters ?? new Dictionary<string, object?>();
        Use(triggerBinding);
    }

    internal PlanBuilder Named(string id) { planId = id; return this; }

    private void Use(string bindingId) { if (!boundBindings.Contains(bindingId)) boundBindings.Add(bindingId); }

    internal PlanBuilder Variable(string id, string scope, string type, object? initial)
    {
        variables.Add(new VariableRow(id, scope, type, initial));
        return this;
    }

    /// <summary>Sets the declared initial value of a variable this builder already declared.</summary>
    internal PlanBuilder Initial(string id, object initial)
    {
        var index = variables.FindIndex(row => row.Id == id);
        if (index < 0) throw new InvalidOperationException("No variable " + id);
        variables[index] = variables[index] with { Initial = initial };
        return this;
    }

    /// <summary>A level object: the `named` scope's declaration, whose type is the half it holds — an entity or a
    /// handle.</summary>
    internal PlanBuilder Object(string id, string type)
    {
        objects.Add(new { id, type });
        return this;
    }

    /// <summary>Adds one step and returns its index. `inputs` are the compiled input rows — `{slot, value}`,
    /// `{slot, fromEventSlot}` or `{slot, fromStepSlot}` — sorted by slot, as the loader requires.</summary>
    internal int Step(string nodeId, string kind, string bindingId, IReadOnlyDictionary<string, object?> parameters, object[] inputs, int?[] successors)
    {
        Use(bindingId);
        var index = steps.Count;
        steps.Add(new StepRow(nodeId, kind, bindingId, RuntimeJson.From(parameters), inputs, successors));
        return index;
    }

    /// <summary>An input row read from the entrypoint trigger's own payload port.</summary>
    internal static object FromEvent(int slot, int eventSlot) => new { slot, fromEventSlot = eventSlot };

    /// <summary>An input row carrying a value the plan compiled in.</summary>
    internal static object Literal(int slot, object value) => new { slot, value };

    /// <summary>An input row read from an earlier control step's output frame. The port is that step's own output
    /// port index, which <see cref="OutputSlot"/> resolves from the contract.</summary>
    internal static object FromStep(int slot, int step, int port) => new { slot, fromStepSlot = new { step, port } };

    /// <summary>The index of one named port, so a case wires by name instead of an index it would have to keep in
    /// step with the contract.</summary>
    internal int Port(string side, string bindingId, IReadOnlyDictionary<string, object?> parameters, string port)
    {
        var ports = fixture.Contract(bindingId, parameters).GetProperty(side).EnumerateArray().ToArray();
        for (var index = 0; index < ports.Length; index++)
            if (RuntimeJson.Text(ports[index], "id") == port) return index;
        throw new InvalidOperationException("No " + side + " port " + port + " on " + bindingId);
    }

    internal string Build(int start = 0)
    {
        var pins = boundBindings.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        int Pin(string bindingId) => Array.IndexOf(pins, bindingId);
        var pinRows = pins.Select(id =>
        {
            var binding = fixture.Row("bindings", id);
            var capabilityId = RuntimeJson.Text(binding, "capabilityId");
            var providerId = RuntimeJson.Text(binding, "providerId");
            return new
            {
                bindingId = id, capabilityId,
                capabilityVersion = RuntimeJson.Text(fixture.Row("capabilities", capabilityId), "version"),
                providerId,
                providerVersion = RuntimeJson.Text(fixture.Row("providers", providerId), "version"),
                handler = RuntimeJson.Text(binding, "handler")
            };
        }).ToArray();
        var permissions = fixture.Manifest().GetProperty("bindingSupport").EnumerateArray()
            .Where(support => pins.Contains(RuntimeJson.Text(support, "bindingId")))
            .SelectMany(support => support.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var triggerCapability = fixture.Row("capabilities", RuntimeJson.Text(fixture.Row("bindings", triggerBinding), "capabilityId"));
        var trigger = RuntimeGraphContracts.Resolve(triggerCapability.GetProperty("graph"), RuntimeJson.From(triggerParameters));
        return RuntimeJson.From(new
        {
            schemaVersion = 4, kind = "forge-runtime-plan", planId, resource = new { id = planId, revision = "1" },
            runtime = fixture.Kernel.Identity, domain = "logic", authority = "host", failurePolicy = "stop-entrypoint",
            permissions, dependencies = Array.Empty<string>(),
            limits = new
            {
                fixture.Kernel.Limits.MaxEventsPerTick, fixture.Kernel.Limits.MaxCommandsPerTick,
                fixture.Kernel.Limits.MaxQueuedEvents, fixture.Kernel.Limits.MaxCausalDepth
            },
            variables = variables.Select(row => new { id = row.Id, scope = row.Scope, type = row.Type, initial = row.Initial }).ToArray(),
            objects = objects.ToArray(),
            bindings = pinRows,
            attachments = new[] { new { kind = "level", reference = "test.level" } },
            entrypoints = new[]
            {
                new
                {
                    nodeId = "Entry", binding = Pin(triggerBinding),
                    layout = new
                    {
                        inputs = RuntimeGraphContracts.Layout(trigger, "inputs"),
                        outputs = RuntimeGraphContracts.Layout(trigger, "outputs"),
                        constants = triggerCapability.GetProperty("graph").GetProperty("parameters").EnumerateArray()
                            .Select(definition => triggerParameters.TryGetValue(RuntimeJson.Text(definition, "id"), out var value) ? value : null).ToArray(),
                        promoted = Array.Empty<int>()
                    },
                    start, steps = steps.Select(row => Layout(row, Pin(row.BindingId))).ToArray()
                }
            }
        }).GetRawText();
    }

    private object Layout(StepRow row, int pin)
    {
        var capability = fixture.Row("capabilities", RuntimeJson.Text(fixture.Row("bindings", row.BindingId), "capabilityId"));
        var resolved = RuntimeGraphContracts.Resolve(capability.GetProperty("graph"), row.Parameters);
        return new
        {
            nodeId = row.NodeId, nodeKind = row.Kind, binding = pin,
            layout = new
            {
                inputs = RuntimeGraphContracts.Layout(resolved, "inputs"),
                outputs = RuntimeGraphContracts.Layout(resolved, "outputs"),
                constants = capability.GetProperty("graph").GetProperty("parameters").EnumerateArray()
                    .Select(definition => row.Parameters.TryGetProperty(RuntimeJson.Text(definition, "id"), out var value) ? (object?)value : null).ToArray(),
                promoted = Array.Empty<int>()
            },
            inputs = row.Inputs, successors = row.Successors
        };
    }
}

