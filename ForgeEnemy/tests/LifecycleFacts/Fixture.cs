using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

internal static class Fixture
{
    internal const string SinkRegistry = """
    {"providers":[{"id":"test.lifecycle","kind":"extension","version":"1.0.0","dependencies":[]}],
    "capabilities":[{"id":"test.lifecycle.action.record","owner":"test.lifecycle","kind":"action",
    "label":"QA record","version":"1.0.0","parameters":{},"graph":{"domains":["enemy"],"execution":"host",
    "inputs":[{"id":"in","type":"execution"},{"id":"target","type":"entity"},
    {"id":"limb_id","type":"integer","optional":true}],
    "outputs":[{"id":"next","type":"execution"},{"id":"result","type":"result","schema":"test.lifecycle.result.record"}],
    "parameters":[],"recipients":{"input":"target","target":"entity","cardinality":"one","requires":[],"result":"result"}}}],
    "bindings":[{"id":"test.lifecycle.binding.record","capabilityId":"test.lifecycle.action.record",
    "providerId":"test.lifecycle","handler":"test.record","role":"execute","status":"implemented","dependencies":[],"requires":[]}]}
    """;
    private const string RecordBinding = "test.lifecycle.binding.record";
    private static readonly string[] PortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };

    private static JsonElement Graph(RuntimeKernel kernel, string bindingId)
    {
        var registry = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry");
        var capability = registry.GetProperty("bindings").EnumerateArray()
            .Single(b => b.GetProperty("id").GetString() == bindingId).GetProperty("capabilityId").GetString();
        return registry.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == capability).GetProperty("graph");
    }

    /// <summary>schemaVersion 2 slot frame of a parameterless node, written independently of the SDK.
    /// The ports used here carry no value set or lifetime.</summary>
    internal static JsonNode Layout(RuntimeKernel kernel, string bindingId)
    {
        var graph = Graph(kernel, bindingId);
        object[] Slots(string side) => graph.GetProperty(side).EnumerateArray().Select((p, index) => (object)new {
            cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0, index, lifetime = -1,
            nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean(), optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(),
            type = Array.IndexOf(PortTypes, p.GetProperty("type").GetString()), valueSet = -1
        }).ToArray();
        return JsonSerializer.SerializeToNode(new { constants = Array.Empty<object>(), inputs = Slots("inputs"), outputs = Slots("outputs") })!;
    }

    internal static int Slot(RuntimeKernel kernel, string bindingId, string side, string port)
        => Graph(kernel, bindingId).GetProperty(side).EnumerateArray().Select((p, i) => (p, i))
            .Single(x => x.p.GetProperty("id").GetString() == port).i;

    internal static string Plan(RuntimeKernel kernel, string fixtures, string suffix)
    {
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "native-heal.plan.json")))!.AsObject();
        string binding = "forge.module.gtfo.enemy.binding." + suffix;
        string capability = suffix == "death_started" ? "forge.trigger.enemy.death_started" : "forge.trigger.combat.limb_broken";
        plan["planId"] = "test.lifecycle." + suffix;
        plan["resource"] = JsonSerializer.SerializeToNode(new { id = "test.lifecycle." + suffix, revision = "1" });
        var pins = new[] {
            new { bindingId = binding, capabilityId = capability, capabilityVersion = "1.0.0", handler = "gtfo.enemy." + suffix,
                providerId = "forge.module.gtfo.enemy", providerVersion = "1.0.0" },
            new { bindingId = RecordBinding, capabilityId = "test.lifecycle.action.record",
                capabilityVersion = "1.0.0", handler = "test.record", providerId = "test.lifecycle", providerVersion = "1.0.0" } }
            .OrderBy(p => p.bindingId, StringComparer.Ordinal).ToArray();
        int Index(string id) => Array.FindIndex(pins, p => p.bindingId == id);
        plan["bindings"] = JsonSerializer.SerializeToNode(pins);
        plan["permissions"] = JsonSerializer.SerializeToNode(new[] {
            suffix == "death_started" ? "gtfo.enemy.lifecycle.read" : "gtfo.enemy.limbs.read", "test.record" });
        var ports = suffix == "limb_broken" ? new[] { "target", "limb_id" } : new[] { "target" };
        var inputs = ports.Select(p => new { slot = Slot(kernel, RecordBinding, "inputs", p), fromEventSlot = Slot(kernel, binding, "outputs", p) })
            .OrderBy(x => x.slot).ToArray();
        plan["entrypoints"] = new JsonArray(new JsonObject {
            ["binding"] = Index(binding), ["layout"] = Layout(kernel, binding), ["nodeId"] = "NativeFact",
            ["steps"] = new JsonArray(new JsonObject {
                ["binding"] = Index(RecordBinding), ["inputs"] = JsonSerializer.SerializeToNode(inputs),
                ["layout"] = Layout(kernel, RecordBinding), ["nodeId"] = "Record" }) });
        return plan.ToJsonString();
    }
}
