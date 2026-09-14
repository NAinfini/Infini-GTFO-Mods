using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// Legal schemaVersion 2 plans built from the kernel's own registry export. Pins, capability and provider
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
    "outputs":[{"id":"next","type":"execution"},{"id":"result","type":"result","schema":"test.enemy.result.record"}],
    "parameters":[],"recipients":{"input":"target","target":"entity","cardinality":"one","requires":[],"result":"result"}}}],
    "bindings":[{"id":"test.enemy.binding.record","capabilityId":"test.enemy.action.record",
    "providerId":"test.enemy","handler":"test.record","role":"execute","status":"implemented","dependencies":[],"requires":[]}]}
    """;
    // The slot frame is the SDK's own derivation: these suites test Enemy facts, not layout encoding,
    // and enum ports (damage_kind) carry value-set indexes that only the SDK table defines.
    private static readonly MethodInfo LayoutFrame = typeof(RuntimeKernel).Assembly
        .GetType("ForgeRuntime.Framework.RuntimeGraphContracts", true)!
        .GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic)!;

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
        new[] { new BindingSupport(RecordBinding, "implementation-only", new[] { RecordPermission }) });

    /// <summary>One entrypoint: <paramref name="trigger"/> followed by one parameterless <paramref name="action"/> step fed only by event slots.</summary>
    internal static Plan Build(RuntimeKernel kernel, string planId, string trigger, string action, params (string EventOutput, string ActionInput)[] wires)
        => Build(kernel, planId, trigger, action, wires, Array.Empty<(string, object)>(), RuntimeJson.EmptyObject);

    /// <summary>One entrypoint: <paramref name="trigger"/> followed by one <paramref name="action"/> step whose inputs are
    /// event-slot wires and <c>{slot, value}</c> literals, and whose constants are <paramref name="parameters"/> laid out
    /// positionally over the capability's parameter definitions (absent ones are null). Enum parameters must already be
    /// member-set indexes, exactly as a compiled plan carries them.</summary>
    internal static Plan Build(RuntimeKernel kernel, string planId, string trigger, string action,
        (string EventOutput, string ActionInput)[] wires, (string ActionInput, object Value)[] literals, JsonElement parameters)
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
        JsonElement Parameters(string id) => id == action ? parameters : RuntimeJson.EmptyObject;
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
            schemaVersion = 2, kind = "forge-runtime-plan", planId, resource = new { id = planId, revision = "1" }, runtime = kernel.Identity,
            domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint", permissions, dependencies = Array.Empty<string>(),
            limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth },
            bindings = pins,
            entrypoints = new[] { new { nodeId = "Fact", binding = Array.IndexOf(ids, trigger), layout = Layout(trigger),
                steps = new[] { new { nodeId = "Step", binding = Array.IndexOf(ids, action), layout = Layout(action), inputs } } } }
        }).GetRawText();
        return new(json, permissions);
    }

    /// <summary>fact -> forge.action.combat.heal: the fact's subject is both the single wrapped target and the source,
    /// amount is the literal <paramref name="amount"/>, and overheal_policy is clamp (member index 0).</summary>
    internal static Plan Heal(RuntimeKernel kernel, string planId, string trigger, string subject, double amount = 5)
        => Build(kernel, planId, trigger, "forge.module.gtfo.enemy.binding.heal",
            new[] { (subject, "targets"), (subject, "source") }, new[] { ("amount", (object)amount) },
            RuntimeJson.From(new { overheal_policy = 0 }));

    /// <summary>Loads a plan; every rejection propagates.</summary>
    internal static void Load(RuntimeKernel kernel, Plan plan) => kernel.LoadPlan(plan.Json);
}
