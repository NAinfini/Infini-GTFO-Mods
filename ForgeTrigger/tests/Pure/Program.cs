using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

if (args.Length != 2) throw new ArgumentException("Usage: PureTests <pure-reference.json> <result.json>");
var reference = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement.Clone();
var assertions = 0;
var failures = new List<string>();
void Check(bool value, string name)
{
    assertions++;
    if (!value) { failures.Add(name); Console.WriteLine("FAIL: " + name); }
}
void Reject(string name, string code, Action action)
{
    try { action(); Check(false, name + " unexpectedly accepted"); }
    catch (RuntimeContractException error) { Check(error.Code == code, name + ": " + error.Code + " != " + code); }
    catch (Exception error) { Check(false, name + " wrong exception: " + error.GetType().Name); }
}
bool Same(JsonElement actual, JsonElement expected, bool approximatePower)
{
    if (actual.ValueKind != expected.ValueKind) return false;
    if (actual.ValueKind == JsonValueKind.Number)
    {
        var a = actual.GetDouble(); var b = expected.GetDouble();
        if (!double.IsFinite(a) || !double.IsFinite(b)) return false;
        return a == b || approximatePower && Math.Abs(a - b) <= 1e-14 * Math.Max(Math.Abs(a), Math.Abs(b));
    }
    if (actual.ValueKind == JsonValueKind.Array)
        return actual.GetArrayLength() == expected.GetArrayLength()
            && actual.EnumerateArray().Zip(expected.EnumerateArray()).All(pair => Same(pair.First, pair.Second, approximatePower));
    return actual.GetRawText() == expected.GetRawText();
}
Check(reference.GetProperty("kind").GetString() == "test-only-pure-reference-results", "test-only reference provenance");
Check(reference.GetProperty("gameVerified").GetBoolean() == false, "reference is not game evidence");
var vectors = reference.GetProperty("cases");
var canonicalIds = vectors.EnumerateArray().Select(row => row.GetProperty("capabilityId").GetString()).Distinct().ToArray();
Check(canonicalIds.Length == 23, "23 existing canonical computations, not a new registry");
foreach (var row in vectors.EnumerateArray())
{
    var name = row.GetProperty("id").GetString()!;
    if (row.GetProperty("outcome").GetString() == "rejected")
    {
        Reject("shared rejection " + name, row.GetProperty("expectedCode").GetString()!, () => FixtureAdapter.Evaluate(row));
        continue;
    }
    try
    {
        var actual = JsonSerializer.SerializeToElement(FixtureAdapter.Evaluate(row));
        var expected = row.GetProperty("outputs").GetProperty("value");
        var power = row.GetProperty("capabilityId").GetString() == "forge.modifier.value.power";
        Check(Same(actual, expected, power), "cross-language " + name + ": " + actual + " != " + expected);
        Check(actual.GetRawText() == JsonSerializer.SerializeToElement(FixtureAdapter.Evaluate(row)).GetRawText(), "repeat has no hidden state " + name);
    }
    catch (Exception error) { Check(false, "shared value " + name + ": " + error.GetType().Name + " " + error.Message); }
}

// These cannot be represented in JSON fixtures; exercise the actual public C# methods directly.
foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
{
    var calls = new (string Name, Action Call)[]
    {
        ("constant", () => ScalarNodes.Constant(invalid)),
        ("binary-a", () => ScalarNodes.Binary(ScalarOperation.Add, invalid, 1)),
        ("binary-b", () => ScalarNodes.Binary(ScalarOperation.Add, 1, invalid)),
        ("clamp-value", () => ScalarNodes.Clamp(invalid, 0, 1)),
        ("clamp-min", () => ScalarNodes.Clamp(0, invalid, 1)),
        ("clamp-max", () => ScalarNodes.Clamp(0, 0, invalid)),
        ("absolute", () => ScalarNodes.Absolute(invalid)),
        ("round", () => ScalarNodes.Round(invalid, ScalarRounding.Nearest)),
        ("lerp-a", () => ScalarNodes.Lerp(invalid, 1, 0)),
        ("lerp-b", () => ScalarNodes.Lerp(0, invalid, 0)),
        ("lerp-weight", () => ScalarNodes.Lerp(0, 1, invalid)),
        ("select-unselected-true", () => ScalarNodes.SelectValue(false, invalid, 1)),
        ("select-unselected-false", () => ScalarNodes.SelectValue(true, 1, invalid)),
        ("compare-a", () => PureConditions.Compare(invalid, 1, ScalarComparison.Equal)),
        ("compare-b", () => PureConditions.Compare(1, invalid, ScalarComparison.Equal)),
        ("range-value", () => PureConditions.InRange(invalid, 0, 1, IntervalBoundary.Inclusive)),
        ("range-min", () => PureConditions.InRange(0, invalid, 1, IntervalBoundary.Inclusive)),
        ("range-max", () => PureConditions.InRange(0, 0, invalid, IntervalBoundary.Inclusive)),
        ("chance", () => SeededNodes.Chance(invalid, 0)),
        ("random-min", () => SeededNodes.Uniform(invalid, 1, 0)),
        ("random-max", () => SeededNodes.Uniform(0, invalid, 0)),
        ("vector-x", () => VectorNodes.ComposeMetres(invalid, 0, 0)),
        ("vector-y", () => VectorNodes.ComposeMetres(0, invalid, 0)),
        ("vector-z", () => VectorNodes.ComposeMetres(0, 0, invalid)),
        ("vector-add", () => VectorNodes.AddMetres(new[] { 0d, invalid, 0d }, new double[3])),
        ("vector-scale", () => VectorNodes.ScaleMetres(new[] { 0d, 0d, invalid }, 0)),
        ("vector-factor", () => VectorNodes.ScaleMetres(new double[3], invalid))
    };
    foreach (var call in calls) Reject(call.Name + " " + invalid, "pure-invalid-number", call.Call);
}
Reject("unknown binary", "pure-operation", () => ScalarNodes.Binary((ScalarOperation)999, 1, 1));
Reject("unknown rounding", "pure-operation", () => ScalarNodes.Round(0, (ScalarRounding)999));
Reject("unknown comparison", "pure-operation", () => PureConditions.Compare(0, 0, (ScalarComparison)999));
Reject("unknown interval boundary", "pure-operation", () => PureConditions.InRange(0, 0, 1, (IntervalBoundary)999));
Reject("minimum long seed", "pure-seed", () => SeededNodes.Uniform(0, 1, long.MinValue));
Reject("maximum long seed", "pure-seed", () => SeededNodes.Uniform(0, 1, long.MaxValue));
Reject("zero chance still validates seed", "pure-seed", () => SeededNodes.Chance(0, -1));
Reject("unit chance still validates seed", "pure-seed", () => SeededNodes.Chance(1, -1));
Reject("equal bounds still validate seed", "pure-seed", () => SeededNodes.Uniform(5, 5, -1));
var original = new[] { 1d, 2d, 3d };
var sum = VectorNodes.AddMetres(original, original);
Check(original.SequenceEqual(new[] { 1d, 2d, 3d }), "vector arithmetic does not mutate aliased inputs");
Check(!ReferenceEquals(sum, original) && sum.SequenceEqual(new[] { 2d, 4d, 6d }), "vector result is newly owned");
sum[0] = 100;
Check(original[0] == 1, "mutating result does not change input");
Check(BitConverter.DoubleToInt64Bits(ScalarNodes.Constant(-0d)) == 0, "negative zero is normalized");
Check(ScalarNodes.Round(-1.5, ScalarRounding.Nearest) == -1, "negative midpoint rounds towards positive infinity");
Check(ScalarNodes.Round(4503599627370495d, ScalarRounding.Nearest) == 4503599627370495d, "large integer is not incremented by a half-add trick");
Check(ScalarNodes.Lerp(-double.MaxValue, double.MaxValue, 0.5) == 0, "weighted interpolation avoids subtractive intermediate overflow");
var first = SeededNodes.Uniform(0, 1, 42);
for (var seed = 0; seed < 128; seed++)
{
    var value = SeededNodes.Uniform(0, 1, seed);
    Check(value >= 0 && value < 1, "seeded unit range " + seed);
    Check(!SeededNodes.Chance(0, seed) && SeededNodes.Chance(1, seed), "probability endpoints " + seed);
}
Check(SeededNodes.Uniform(0, 1, 42) == first, "other calls do not advance an implicit random stream");

CollectionTests.Run(Path.Combine(Path.GetDirectoryName(args[0])!, "collections-reference.json"), Check);
NumericBoundaryTests.Run(Check);
// Public R3 consumers run in tests/R3Consumers and the full validation entrypoint.

var module = ForgeTrigger.ModuleDefinition.Create();
Check(module.Handlers.Count == 0 && module.BindingSupport.Count == 0, "helpers are not advertised as runtime handlers");
var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "pure-test-no-game"));
using (kernel.RegisterModule(module))
{
    var before = kernel.ExportManifest();
    ScalarNodes.Binary(ScalarOperation.Add, 1, 2); SeededNodes.Uniform(0, 1, 42);
    Check(kernel.ExportManifest() == before && kernel.QueuedEvents == 0 && kernel.LoadedPlans == 0, "pure calls cannot schedule gameplay or alter registration");
    var registry = JsonDocument.Parse(before).RootElement.GetProperty("registry");
    Check(registry.GetProperty("capabilities").GetArrayLength() == 0 && registry.GetProperty("bindings").GetArrayLength() == 0, "production provider remains unbound");
}
var assembly = typeof(ScalarNodes).Assembly;
Check(assembly.GetType(typeof(RuntimeKernel).FullName!) == null, "production does not embed a second kernel");
Check(assembly.GetReferencedAssemblies().Single(a => a.Name == "ForgeRuntime.Framework").FullName == typeof(RuntimeKernel).Assembly.GetName().FullName, "single public compiled SDK identity");
Check(assembly.GetTypes().Where(t => t.Namespace == "ForgeTrigger.Pure").SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)).All(field => field.IsLiteral), "pure helpers contain no static mutable state");
var report = new { status = failures.Count == 0 ? "passed" : "failed", gameVerified = false,
    primitiveCount = canonicalIds.Length, vectorCases = vectors.GetArrayLength(), assertions, failures };
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine(JsonSerializer.Serialize(new { report.status, report.primitiveCount, report.vectorCases, assertions, failureCount = failures.Count }));
return failures.Count == 0 ? 0 : 1;
