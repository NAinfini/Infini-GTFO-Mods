using System.Text.Json;

if (args.Length != 3) throw new ArgumentException("Usage: R3ConsumerTests <result.json> <spatial-reference.json> <recipient-filter-reference.json>");
var assertions = 0;
var failures = new List<string>();
void Check(bool value, string name)
{
    assertions++;
    if (!value) { failures.Add(name); Console.WriteLine("FAIL: " + name); }
}
// A failed integration keeps its own nonzero exit code and durable error evidence.
void Group(string name, Action action)
{
    try { action(); }
    catch (Exception error) { Check(false, name + ": " + error.GetType().Name + ": " + error.Message); }
}
Group("actors", () => R3ConsumerTests.Run(Check));
Group("spatial", () => SpatialTests.Run(args[1], Check));
Group("observers", () => ObserverContractTests.Run(Check));
Group("observed-query", () => ObservedQueryTests.Run(Check));
Group("observed-filter", () => ObservedFilterTests.Run(args[2], Check));
Group("observed-space", () => ObservedSpaceTests.Run(Check));
Group("observed-list", () => ObservedListTests.Run(Check));
Group("observed-entity-state", () => ObservedEntityStateTests.Run(Check));
Group("weighted-composition", () => WeightedCompositionTests.Run(Check));
Group("registry", () => RegistryTests.Run(Check, args[0]));
var result = new { status = failures.Count == 0 ? "passed" : "failed", scope = "public-r3-consumer-integration",
    gameVerified = false, assertions, failures };
File.WriteAllText(args[0], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine(JsonSerializer.Serialize(result));
return failures.Count == 0 ? 0 : 1;
