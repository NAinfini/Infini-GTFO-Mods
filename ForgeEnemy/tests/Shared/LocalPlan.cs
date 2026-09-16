using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// Legal schemaVersion 4 plans built from the kernel's own registry export. Pins, capability and provider
/// versions, permissions, slot frames and positional constants all come from the registered contracts, so these
/// tests follow the SDK instead of a website fixture that predates the current catalog shape.
/// </summary>
internal static class LocalPlan
{
    internal const string RecordBinding = "test.enemy.binding.record";
    internal const string RecordPermission = "test.record";
    // value/delta are optional so fact plans that only forward the subject keep their slot frames.
    private const string RecorderRegistry = """
    {"providers":[{"id":"test.enemy","kind":"extension","version":"1.0.0","dependencies":[]}],
    "capabilities":[{"id":"test.enemy.action.record","owner":"test.enemy","kind":"action",
    "label":"QA record","version":"1.0.0","parameters":{},"graph":{"domains":["enemy"],"execution":"host",
    "inputs":[{"id":"in","type":"execution"},{"id":"target","type":"entity"},
    {"id":"limb_id","type":"integer","optional":true,"nullable":true},
    {"id":"value","type":"number","unit":"hp","optional":true,"nullable":true},
    {"id":"delta","type":"number","unit":"hp","optional":true,"nullable":true}],
    "outputs":[{"id":"next","type":"execution"},{"id":"result","type":"result","schema":"test.enemy.result.record",
    "fields":[{"id":"target","type":"entity"},{"id":"status","type":"enum","schema":"execution_outcome"},
    {"id":"committed","type":"enum","schema":"commit_state"},{"id":"code","type":"string"}]}],
    "parameters":[],"recipients":{"input":"target","target":"entity","cardinality":"one","requires":[],"result":"result"}}}],
    "bindings":[{"id":"test.enemy.binding.record","capabilityId":"test.enemy.action.record",
    "providerId":"test.enemy","handler":"test.record","role":"execute","status":"implemented","dependencies":[],"requires":[]}]}
    """;
    // The slot frame is the SDK's own derivation: these suites test Enemy facts, not layout encoding,
    // and enum ports (damage_kind) carry value-set indexes that only the SDK table defines.
    private static readonly MethodInfo LayoutFrame = typeof(RuntimeKernel).Assembly
        .GetType("ForgeRuntime.Framework.RuntimeGraphContracts", true)!
        .GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>The provider that owns the mount kind every plan here declares, and the one level identity it
    /// answers. No mount kind belongs to the kernel any more, so a plan whose kind nothing owns is refused at
    /// load: the builder stands the owner up in the kernel it is handed, because a fixture plan has to be
    /// loadable in the kernel it was built for.</summary>
    internal const string MountProvider = "test.enemy.mounts";
    internal const string MountReference = "31:A:0";
    internal static RuntimeModule LevelMount() => new(RuntimeKernel.ApiVersion, """
    {"providers":[{"id":"test.enemy.mounts","kind":"extension","version":"1.0.0","dependencies":[]}],
    "capabilities":[],"bindings":[]}
    """, new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            // A level names no event subject, so the kind is judged from the mount target alone.
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                category == null && reference == MountReference)
        }
    };
    /// <summary>Registers the mount owner in a kernel that has not started yet. A suite calls this next to its
    /// other registrations: the kernel freezes registration at startup, so the owner of a plan's mount kind has
    /// to be in place before the plan loads, and a plan whose kind nothing owns is refused at load.</summary>
    internal static void OwnMounts(RuntimeKernel kernel)
    {
        var providers = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry").GetProperty("providers");
        if (providers.EnumerateArray().Any(p => p.GetProperty("id").GetString() == MountProvider)) return;
        kernel.RegisterModule(LevelMount(), RuntimeLogLevel.Off);
    }

    /// <summary>Every plan mounts the whole level. The reference is the one identity <see cref="LevelMount"/>
    /// answers, in the spelling the matcher parses; a plan mounted on nothing could never be dispatched.</summary>
    private static readonly object[] Attachments = { new { kind = "level", reference = MountReference } };

    /// <summary>An `enemy-type` mount: the official enemy block's persistentID as canonical decimal text, which
    /// is the one spelling the website exports and the module's matcher parses (ruling 61).</summary>
    internal static object[] EnemyTypeMount(uint enemyTypeId)
        => new object[] { new { kind = "enemy-type", reference = enemyTypeId.ToString(CultureInfo.InvariantCulture) } };

    /// <summary>The same one-row mount list with a reference the test spells itself, for cases that must prove a
    /// spelling is refused rather than reinterpreted.</summary>
    internal static object[] Mount(string kind, string reference) => new object[] { new { kind, reference } };

    internal sealed record Plan(string Json, string[] Permissions)
    {
        /// <summary>The same plan under an explicit per-plan budget, for cases that exercise budget rejection.</summary>
        internal Plan WithLimits(RuntimeLimits limits)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(Json)!.AsObject();
            node["limits"] = System.Text.Json.Nodes.JsonNode.Parse(RuntimeJson.From(new
                { limits.MaxEventsPerTick, limits.MaxCommandsPerTick, limits.MaxQueuedEvents, limits.MaxCausalDepth }).GetRawText());
            return this with { Json = node.ToJsonString() };
        }

        /// <summary>The same plan declaring a different permissions array, for cases that exercise permission-lock:
        /// the runtime requires the declared set to exactly match the binding closure's required permissions.</summary>
        internal Plan WithPermissions(string[] permissions)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(Json)!.AsObject();
            node["permissions"] = System.Text.Json.Nodes.JsonNode.Parse(RuntimeJson.From(permissions).GetRawText());
            return this with { Json = node.ToJsonString(), Permissions = permissions };
        }
    }

    /// <summary>A parameterless recording action; every invocation succeeds after handing its context to the test.</summary>
    internal static RuntimeModule Recorder(Action<CommandContext> record) => new(RuntimeKernel.ApiVersion, RecorderRegistry,
        new Dictionary<string, CommandHandler> { ["test.record"] = context => { record(context); return CommandResult.Succeeded(RuntimeJson.EmptyObject); } },
        new[] { new BindingSupport(RecordBinding, "implementation-only", new[] { RecordPermission }) })
    {
        // The recorder reads no port: it hands the whole dispatch context to the test.
        Shapes = new Dictionary<string, HandlerShape> { ["test.record"] = new HandlerShape() }
    };

    /// <summary>One entrypoint: <paramref name="trigger"/> followed by one parameterless <paramref name="action"/> step fed only by event slots.</summary>
    internal static Plan Build(RuntimeKernel kernel, string planId, string trigger, string action, params (string EventOutput, string ActionInput)[] wires)
        => Build(kernel, planId, trigger, action, wires, Array.Empty<(string, object)>(), RuntimeJson.EmptyObject);

    /// <summary>One entrypoint: <paramref name="trigger"/> followed by one <paramref name="action"/> step whose inputs are
    /// event-slot wires and <c>{slot, value}</c> literals, and whose constants are <paramref name="parameters"/> laid out
    /// positionally over the capability's parameter definitions (absent ones are null). Enum parameters must already be
    /// member-set indexes, exactly as a compiled plan carries them. <paramref name="attachments"/> is the plan's own
    /// mount list — the level by default, an `enemy-type` mount for the cases that ask whether a plan fires.
    /// <paramref name="triggerParameters"/> is the same positional frame for the entrypoint's own trigger, whose
    /// structural parameters an author fills by hand — a wave row's `resource` address is the only one today, and a
    /// required parameter left null is refused with `missing-constant`.</summary>
    internal static Plan Build(RuntimeKernel kernel, string planId, string trigger, string action,
        (string EventOutput, string ActionInput)[] wires, (string ActionInput, object Value)[] literals, JsonElement parameters,
        object[]? attachments = null, JsonElement? triggerParameters = null)
    {
        var manifest = RuntimeJson.Parse(kernel.ExportManifest());
        var registry = manifest.GetProperty("registry");
        JsonElement Row(string list, string id) => registry.GetProperty(list).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        var ids = new[] { trigger, action }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var pins = ids.Select(id =>
        {
            var binding = Row("bindings", id);
            string capabilityId = binding.GetProperty("capabilityId").GetString()!, providerId = binding.GetProperty("providerId").GetString()!;
            return new { bindingId = id, capabilityId, capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
                providerId, providerVersion = Row("providers", providerId).GetProperty("version").GetString()!, handler = binding.GetProperty("handler").GetString()! };
        }).ToArray();
        var permissions = manifest.GetProperty("bindingSupport").EnumerateArray()
            .Where(support => ids.Contains(support.GetProperty("bindingId").GetString()!))
            .SelectMany(support => support.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        JsonElement Parameters(string id) => id == action ? parameters : triggerParameters ?? RuntimeJson.EmptyObject;
        var contracts = pins.ToDictionary(p => p.bindingId, p => kernel.ResolveGraphContract(p.capabilityId, p.capabilityVersion, Parameters(p.bindingId)));
        JsonElement Frame(string id, string side) => (JsonElement)LayoutFrame.Invoke(null, new object[] { contracts[id], side })!;
        object?[] Constants(string id)
        {
            var capabilityId = pins.Single(p => p.bindingId == id).capabilityId;
            return Row("capabilities", capabilityId).GetProperty("graph").GetProperty("parameters").EnumerateArray()
                .Select(definition => Parameters(id).TryGetProperty(definition.GetProperty("id").GetString()!, out var value) ? (object?)value : null)
                .ToArray();
        }
        object Layout(string id) => new { inputs = Frame(id, "inputs"), outputs = Frame(id, "outputs"), constants = Constants(id), promoted = Array.Empty<int>() };
        int Slot(string id, string side, string port) => contracts[id].GetProperty(side).EnumerateArray()
            .Select((p, index) => (p, index)).Single(x => x.p.GetProperty("id").GetString() == port).index;
        var inputs = wires.Select(w => (Slot: Slot(action, "inputs", w.ActionInput), Row: (object)new { slot = Slot(action, "inputs", w.ActionInput), fromEventSlot = Slot(trigger, "outputs", w.EventOutput) }))
            .Concat(literals.Select(l => (Slot: Slot(action, "inputs", l.ActionInput), Row: (object)new { slot = Slot(action, "inputs", l.ActionInput), value = l.Value })))
            .OrderBy(x => x.Slot).Select(x => x.Row).ToArray();
        var json = RuntimeJson.From(new
        {
            schemaVersion = 4, kind = "forge-runtime-plan", planId, resource = new { id = planId, revision = "1" }, runtime = kernel.Identity,
            domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions, dependencies = Array.Empty<string>(),
            limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth },
            bindings = pins,
            attachments = attachments ?? Attachments,
            // One action step is the whole graph; the last execution output is left unwired, so the entrypoint ends there.
            entrypoints = new[] { new { nodeId = "Fact", binding = Array.IndexOf(ids, trigger), layout = Layout(trigger), start = 0,
                steps = new[] { new { nodeId = "Step", nodeKind = "action", binding = Array.IndexOf(ids, action), layout = Layout(action), inputs, successors = new int?[] { null } } } } }
        }).GetRawText();
        return new(json, permissions);
    }

    /// <summary>fact -> forge.action.combat.heal: the fact's subject is both the single wrapped target and the source,
    /// amount is the literal <paramref name="amount"/>, and overheal_policy is clamp (member index 0).</summary>
    internal static Plan Heal(RuntimeKernel kernel, string planId, string trigger, string subject, double amount = 5)
        => Build(kernel, planId, trigger, "forge.module.gtfo.enemy.binding.heal",
            new[] { (subject, "targets"), (subject, "source") }, new[] { ("amount", (object)amount) },
            RuntimeJson.From(new { overheal_policy = 0 }));

    /// <summary>Loads a plan through the host's own candidate entry point; every rejection propagates as the
    /// contract failure the kernel classified it with, so a case can assert the code and nothing is swallowed.</summary>
    internal static void Load(RuntimeKernel kernel, Plan plan)
    {
        string planId = RuntimeJson.Parse(plan.Json).GetProperty("planId").GetString()!;
        var outcome = kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/" + planId + ".plan.json", plan.Json) }).Single();
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code, outcome.Detail);
    }
}
