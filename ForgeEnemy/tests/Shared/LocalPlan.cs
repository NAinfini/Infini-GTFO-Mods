using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>
/// Legal schemaVersion 2 plans built from the kernel's own registry export. Pins, capability and provider
/// versions, permissions and slot frames all come from the registered contracts, so these tests follow the
/// SDK instead of a website fixture that predates the current catalog shape.
/// </summary>
internal static class LocalPlan
{
    internal const string RecordBinding = "test.enemy.binding.record";
    internal const string RecordPermission = "test.record";
    private const string RecorderRegistry = """
    {"providers":[{"id":"test.enemy","kind":"extension","version":"1.0.0","dependencies":[]}],
    "capabilities":[{"id":"test.enemy.action.record","owner":"test.enemy","kind":"action",
    "label":"QA record","version":"1.0.0","parameters":{},"graph":{"domains":["enemy"],"execution":"host",
    "inputs":[{"id":"in","type":"execution"},{"id":"target","type":"entity"},
    {"id":"limb_id","type":"integer","optional":true,"nullable":true}],
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
    }

    /// <summary>A parameterless recording action; every invocation succeeds after handing its context to the test.</summary>
    internal static RuntimeModule Recorder(Action<CommandContext> record) => new(RuntimeKernel.ApiVersion, RecorderRegistry,
        new Dictionary<string, CommandHandler> { ["test.record"] = context => { record(context); return CommandResult.Succeeded(RuntimeJson.EmptyObject); } },
        new[] { new BindingSupport(RecordBinding, "implementation-only", new[] { RecordPermission }) });

    /// <summary>One entrypoint: <paramref name="trigger"/> followed by one parameterless <paramref name="action"/> step.</summary>
    internal static Plan Build(RuntimeKernel kernel, string planId, string trigger, string action, params (string EventOutput, string ActionInput)[] wires)
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
        var contracts = pins.ToDictionary(p => p.bindingId, p => kernel.ResolveGraphContract(p.capabilityId, p.capabilityVersion, RuntimeJson.EmptyObject));
        JsonElement Frame(string id, string side) => (JsonElement)LayoutFrame.Invoke(null, new object[] { contracts[id], side })!;
        object Layout(string id) => new { inputs = Frame(id, "inputs"), outputs = Frame(id, "outputs"), constants = Array.Empty<object>(), promoted = Array.Empty<int>() };
        int Slot(string id, string side, string port) => contracts[id].GetProperty(side).EnumerateArray()
            .Select((p, index) => (p, index)).Single(x => x.p.GetProperty("id").GetString() == port).index;
        var inputs = wires.Select(w => new { slot = Slot(action, "inputs", w.ActionInput), fromEventSlot = Slot(trigger, "outputs", w.EventOutput) })
            .OrderBy(x => x.slot).ToArray();
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

    /// <summary>Loads a plan; every rejection propagates.</summary>
    internal static void Load(RuntimeKernel kernel, Plan plan) => kernel.LoadPlan(plan.Json);
}
