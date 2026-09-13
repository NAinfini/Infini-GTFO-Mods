using System.Text.Json;
using ForgeRuntime.Framework;

static class WireCases
{
    public static void Run(string path)
    {
        var document = RuntimeJson.Parse(File.ReadAllText(path));
        int cases = 0, differences = 0;
        foreach (var row in document.GetProperty("cases").EnumerateArray())
        {
            var name = row.GetProperty("id").GetString()!;
            bool accepted = true; string? relation = null;
            try
            {
                var input = row.GetProperty("input");
                switch (row.GetProperty("kind").GetString())
                {
                    case "reference": RuntimeEntityReferences.Validate(RuntimeJson.Entity(input)); break;
                    case "snapshot": RuntimeEntitySnapshot.FromJson(input); break;
                    case "actors": RuntimeActorContext.FromJson(input); break;
                    case "relations": RuntimeFactionRelations.FromJson(input); break;
                    case "relationship":
                        relation = RuntimeFactionRelations.FromJson(input.GetProperty("rules")).Resolve(
                            RuntimeEntitySnapshot.FromJson(input.GetProperty("anchor")), RuntimeEntitySnapshot.FromJson(input.GetProperty("recipient"))); break;
                    default: throw new InvalidOperationException("Unknown fixture kind: " + name);
                }
            }
            catch (Exception error) when (error is RuntimeContractException or ArgumentException) { accepted = false; }
            bool expected = row.GetProperty("runtimeAccept").GetBoolean();
            Check.That(accepted == expected, "wire " + name);
            if (accepted && row.TryGetProperty("relation", out var expectedRelation))
                Check.That(relation == expectedRelation.GetString(), "wire relation " + name);
            if (row.GetProperty("websiteAccept").GetBoolean() != expected) differences++;
            cases++;
        }
        Console.WriteLine($"Shared website-source cases: {cases}; explicitly recorded acceptance differences: {differences}.");
    }
}
