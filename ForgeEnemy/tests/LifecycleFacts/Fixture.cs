using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Fixture
{
    internal const string SinkRegistry = """
    {"providers":[{"id":"test.lifecycle","kind":"extension","version":"1.0.0","dependencies":[]}],
    "capabilities":[{"id":"test.lifecycle.action.record","owner":"test.lifecycle","kind":"action",
    "label":"QA record","version":"1.0.0","parameters":{},"graph":{"domains":["enemy"],"execution":"host",
    "inputs":[{"id":"in","type":"execution"},{"id":"target","type":"entity"},
    {"id":"limb_id","type":"integer","optional":true}],"outputs":[{"id":"next","type":"execution"}],
    "parameters":[],"recipients":{"input":"target","requires":[]}}}],
    "bindings":[{"id":"test.lifecycle.binding.record","capabilityId":"test.lifecycle.action.record",
    "providerId":"test.lifecycle","handler":"test.record","role":"execute","status":"implemented","dependencies":[],"requires":[]}]}
    """;

    internal static string Plan(string fixtures, string suffix)
    {
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "native-heal.plan.json")))!.AsObject();
        string binding = "forge.module.gtfo.enemy.binding." + suffix;
        string capability = suffix == "death_started" ? "forge.trigger.enemy.death_started" : "forge.trigger.combat.limb_broken";
        plan["planId"] = "test.lifecycle." + suffix;
        plan["resource"] = JsonSerializer.SerializeToNode(new { id = "test.lifecycle." + suffix, revision = "1" });
        plan["bindings"] = JsonSerializer.SerializeToNode(new[] {
            new { bindingId = binding, capabilityId = capability, capabilityVersion = "1.0.0", handler = "gtfo.enemy." + suffix,
                providerId = "forge.module.gtfo.enemy", providerVersion = "1.0.0" },
            new { bindingId = "test.lifecycle.binding.record", capabilityId = "test.lifecycle.action.record",
                capabilityVersion = "1.0.0", handler = "test.record", providerId = "test.lifecycle", providerVersion = "1.0.0" } });
        plan["permissions"] = JsonSerializer.SerializeToNode(new[] {
            suffix == "death_started" ? "gtfo.enemy.lifecycle.read" : "gtfo.enemy.limbs.read", "test.record" });
        var inputs = new Dictionary<string, object> { ["target"] = new { fromEventPort = "target" } };
        if (suffix == "limb_broken") inputs["limb_id"] = new { fromEventPort = "limb_id" };
        plan["entrypoints"] = JsonSerializer.SerializeToNode(new[] {
            new { bindingId = binding, nodeId = "NativeFact", parameters = new { }, steps = new[] {
                new { bindingId = "test.lifecycle.binding.record", nodeId = "Record", parameters = new { }, inputs } } } });
        return plan.ToJsonString();
    }
}
