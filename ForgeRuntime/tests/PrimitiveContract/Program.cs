using System.Text.Json;
using ForgeRuntime.Framework;

if (args.Length != 1) throw new ArgumentException("Pass the website primitive/primitives.json path.");
using var source = JsonDocument.Parse(File.ReadAllText(args[0]));
var primitives = source.RootElement.GetProperty("primitives").EnumerateArray().ToArray();
int passed = 0;
foreach (var p in primitives)
{
    // This proves schema acceptance only, including proposed rows. It does not register handlers.
    RuntimeGraphContracts.ValidateCapability(p.GetProperty("projection").GetProperty("category").GetString()!,
        p.GetProperty("graph"), p.GetProperty("id").GetString()!);
    passed++;
}
int entityCases = 0;
foreach (var c in source.RootElement.GetProperty("connectionCases").EnumerateArray())
{
    var output = c.GetProperty("output"); var input = c.GetProperty("input");
    // The shared kind gate is a distinct part of plan validation, not a second test-only checker.
    if (output.GetProperty("type").GetString() != "entity" || input.GetProperty("type").GetString() != "entity"
        || output.TryGetProperty("cardinality", out _) || input.TryGetProperty("cardinality", out _)
        || output.TryGetProperty("nullable", out _) || output.TryGetProperty("optional", out _)) continue;
    if (RuntimeGraphContracts.EntityKindsNarrow(output, input) != c.GetProperty("expected").GetBoolean())
        throw new Exception("Entity connection mismatch: " + c.GetProperty("id").GetString());
    entityCases++; passed++;
}
using var actual = JsonDocument.Parse(CombatContracts.Module().RegistryJson);
var heal = actual.RootElement.GetProperty("capabilities").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "forge.action.combat.heal");
var expected = primitives.Single(p => p.GetProperty("id").GetString() == "forge.action.combat.heal");
if (JsonSerializer.Serialize(heal.GetProperty("graph")) != JsonSerializer.Serialize(expected.GetProperty("graph")))
    throw new Exception("Runtime healing graph is not the Primitive projection.");
passed++;
var result = heal.GetProperty("graph").GetProperty("outputs").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "result");
if (!result.GetProperty("fields").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "amount" && p.GetProperty("unit").GetString() == "hp"))
    throw new Exception("Healing must report actual health restored.");
passed++;
using var controlRows = JsonDocument.Parse(ControlContracts.Module().RegistryJson);
foreach (var primitive in primitives.Where(p => p.GetProperty("kind").GetString() == "control" && p.GetProperty("review").GetProperty("status").GetString() == "reviewed"))
{
    var row = controlRows.RootElement.GetProperty("capabilities").EnumerateArray().Single(p => p.GetProperty("id").GetString() == primitive.GetProperty("id").GetString());
    if (row.GetProperty("graph").GetRawText() != primitive.GetProperty("graph").GetRawText())
    {
        if (JsonSerializer.Serialize(row.GetProperty("graph")) != JsonSerializer.Serialize(primitive.GetProperty("graph")))
            throw new Exception("Control primitive projection mismatch.");
    }
    passed++;
}
var each = PrimitiveContracts.ControlGraphs.GetProperty("for_each");
foreach (var kind in new[] { "gtfo.player", "gtfo.enemy", "gtfo.equipment" })
{
    var upstream = RuntimeJson.From(new { id = "targets", type = "entity", cardinality = "many", entityKinds = new[] { kind } });
    var resolved = RuntimeEntityKindFlow.Resolve(each, input => input == "candidates" ? upstream : null);
    var item = RuntimeJson.Rows(resolved, "outputs").Single(p => RuntimeJson.Text(p, "id") == "item");
    if (RuntimeJson.Strings(item.GetProperty("entityKinds")).Single() != kind) throw new Exception("Lost collection item kind.");
    passed++;
}
var unknown = RuntimeEntityKindFlow.Resolve(each, _ => RuntimeJson.From(new { id = "targets", type = "entity" }));
var unknownItem = RuntimeJson.Rows(unknown, "outputs").Single(p => RuntimeJson.Text(p, "id") == "item");
if (RuntimeGraphContracts.EntityKindsNarrow(unknownItem, RuntimeJson.From(new { id = "player", type = "entity", entityKinds = new[] { "gtfo.player" } }))) throw new Exception("Unknown entity became player.");
passed++;
Console.WriteLine($"PASS {passed}: {primitives.Length} schemas, {entityCases} shared kind fixtures, runtime heal projection and actual delta.");
Console.WriteLine("Contract/managed validation only; no native game execution is certified.");
