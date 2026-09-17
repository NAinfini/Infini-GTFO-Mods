using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

if (args.Length != 2) throw new ArgumentException("Usage: PureTests <pure-reference.json> <result.json>");
var assertions = 0;
var failures = new List<string>();
var primitiveCount = 0;
var vectorCases = 0;
Run();

// A mutation can turn a checked refusal into an exception no call site expects. The report must still exist so the
// run ends as a detected failure with evidence rather than as a crash the validator cannot read.
void Run()
{
    try
    {
        var reference = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement.Clone();
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
        bool Same(JsonElement actual, JsonElement expected)
        {
            if (actual.ValueKind != expected.ValueKind) return false;
            if (actual.ValueKind == JsonValueKind.Number)
            {
                var a = actual.GetDouble(); var b = expected.GetDouble();
                if (!double.IsFinite(a) || !double.IsFinite(b)) return false;
                return a == b;
            }
            if (actual.ValueKind == JsonValueKind.Array)
                return actual.GetArrayLength() == expected.GetArrayLength()
                    && actual.EnumerateArray().Zip(expected.EnumerateArray()).All(pair => Same(pair.First, pair.Second));
            return actual.GetRawText() == expected.GetRawText();
        }
        Check(reference.GetProperty("kind").GetString() == "test-only-pure-reference-results", "test-only reference provenance");
        Check(reference.GetProperty("gameVerified").GetBoolean() == false, "reference is not game evidence");
        var vectors = reference.GetProperty("cases");
        var canonicalIds = vectors.EnumerateArray().Select(row => row.GetProperty("capabilityId").GetString()).Distinct().ToArray();
        Check(canonicalIds.Length == 15, "15 existing canonical computations, not a new registry");
        primitiveCount = canonicalIds.Length;
        vectorCases = vectors.GetArrayLength();
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
                Check(Same(actual, expected), "cross-language " + name + ": " + actual + " != " + expected);
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
                ("compare-a", () => PureConditions.Compare(invalid, 1, ScalarComparison.Equal, 0)),
                ("compare-b", () => PureConditions.Compare(1, invalid, ScalarComparison.Equal, 0)),
                ("compare-tolerance", () => PureConditions.Compare(1, 1, ScalarComparison.Equal, invalid)),
                ("divide-a", () => ScalarNodes.Divide(invalid, 1, DivisionZeroPolicy.Passthrough)),
                ("divide-b", () => ScalarNodes.Divide(1, invalid, DivisionZeroPolicy.Zero)),
                ("random-min", () => SeededNodes.Uniform(invalid, 1, 0)),
                ("random-max", () => SeededNodes.Uniform(0, invalid, 0)),
                ("text-value", () => TextNodes.Compose("{0:0}", invalid))
            };
            foreach (var call in calls) Reject(call.Name + " " + invalid, "pure-invalid-number", call.Call);
        }
        Reject("unknown binary", "pure-operation", () => ScalarNodes.Binary((ScalarOperation)999, 1, 1));
        Reject("unknown rounding", "pure-operation", () => ScalarNodes.Round(0, (ScalarRounding)999));
        Reject("unknown comparison", "pure-operation", () => PureConditions.Compare(0, 0, (ScalarComparison)999, 0));
        Reject("unknown zero policy", "pure-operation", () => ScalarNodes.Divide(1, 0, (DivisionZeroPolicy)999));
        Reject("minimum long seed", "pure-seed", () => SeededNodes.Uniform(0, 1, long.MinValue));
        Reject("maximum long seed", "pure-seed", () => SeededNodes.Uniform(0, 1, long.MaxValue));
        Reject("equal bounds still validate seed", "pure-seed", () => SeededNodes.Uniform(5, 5, -1));
        Check(BitConverter.DoubleToInt64Bits(ScalarNodes.Constant(-0d)) == 0, "negative zero is normalized");
        Check(ScalarNodes.Round(-1.5, ScalarRounding.Nearest) == -1, "negative midpoint rounds towards positive infinity");
        Check(ScalarNodes.Round(4503599627370495d, ScalarRounding.Nearest) == 4503599627370495d, "large integer is not incremented by a half-add trick");
        var first = SeededNodes.Uniform(0, 1, 42);
        for (var seed = 0; seed < 128; seed++)
        {
            var value = SeededNodes.Uniform(0, 1, seed);
            Check(value >= 0 && value < 1, "seeded unit range " + seed);
        }
        Check(SeededNodes.Uniform(0, 1, 42) == first, "other calls do not advance an implicit random stream");
        
        CollectionTests.Run(Check);
        TextTests.Run(Check, Reject);
        NumericBoundaryTests.Run(Check);
        VectorTests.Run(Check);
        EnumTests.Run(Check);
        // Public R3 consumers run in tests/R3Consumers and the full validation entrypoint.
        
        // The declaration tables are the advertised vocabulary: every helper exercised above stays a method unless a
        // table names it, and the registered manifest carries exactly those rows — the value-only ones as `evaluate`
        // bindings and the ones that observe the world as `observe`.
        var module = ForgeTrigger.ModuleDefinition.Create();
        var declared = ForgeTrigger.Pure.PureModule.Nodes
            .Select(node => (node.CapabilityId, node.BindingId, node.HandlerName, Role: "evaluate"))
            .Concat(ForgeTrigger.Targeting.ObservedQueryModule.Nodes
                .Select(node => (node.CapabilityId, node.BindingId, node.HandlerName, Role: "observe")))
            .Concat(ForgeTrigger.Targeting.ObservedSpaceNodes.Nodes
                .Select(node => (node.CapabilityId, node.BindingId, node.HandlerName, Role: "observe")))
            .ToArray();
        Check(module.Handlers.Count == 0 && module.Evaluators.Count == declared.Length && module.Shapes.Count == declared.Length
            && declared.All(node => module.Evaluators.ContainsKey(node.HandlerName) && module.Shapes.ContainsKey(node.HandlerName))
            && module.BindingSupport.Count == declared.Length
            && declared.All(node => module.BindingSupport.Any(support => support.BindingId == node.BindingId)),
            "helpers are not advertised as runtime handlers beyond the declaration table");
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "pure-test-no-game"));
        using (kernel.RegisterModule(module, RuntimeLogLevel.Off))
        {
            var before = kernel.ExportManifest();
            ScalarNodes.Binary(ScalarOperation.Add, 1, 2); SeededNodes.Uniform(0, 1, 42); TextNodes.Compose("{0:0}", 1);
            Check(kernel.ExportManifest() == before && kernel.QueuedEvents == 0 && kernel.LoadedPlans == 0, "pure calls cannot schedule gameplay or alter registration");
            var registry = JsonDocument.Parse(before).RootElement.GetProperty("registry");
            var capabilities = registry.GetProperty("capabilities").EnumerateArray().ToArray();
            var bindings = registry.GetProperty("bindings").EnumerateArray().ToArray();
            Check(capabilities.Select(row => row.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(declared.Select(node => node.CapabilityId).OrderBy(id => id, StringComparer.Ordinal))
                && bindings.Length == declared.Length
                && bindings.All(row => declared.Any(node => node.BindingId == row.GetProperty("id").GetString()
                    && node.Role == row.GetProperty("role").GetString())),
                "production provider exports exactly the declared rows with their own binding roles");
        }
        var assembly = typeof(ScalarNodes).Assembly;
        Check(assembly.GetType(typeof(RuntimeKernel).FullName!) == null, "production does not embed a second kernel");
        Check(assembly.GetReferencedAssemblies().Single(a => a.Name == "ForgeRuntime.Framework").FullName == typeof(RuntimeKernel).Assembly.GetName().FullName, "single public compiled SDK identity");
        // The declaration table adds readonly metadata (its catalog shapes and the helper member orders); that is still
        // no mutable state, so a static field must be a literal or init-only. Compiler-generated closure types are
        // skipped: their fields are Roslyn's cached delegates, not state this package wrote.
        Check(assembly.GetTypes().Where(t => t.Namespace == "ForgeTrigger.Pure" && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)).SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)).All(field => field.IsLiteral || field.IsInitOnly), "pure helpers contain no static mutable state");
        var report = new { status = failures.Count == 0 ? "passed" : "failed", gameVerified = false, abnormalExit = false,
            primitiveCount, vectorCases, assertions, failures };
        File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine(JsonSerializer.Serialize(new { report.status, report.primitiveCount, report.vectorCases, assertions, failureCount = failures.Count }));
        Environment.Exit(failures.Count == 0 ? 0 : 1);
    }
    catch (Exception error)
    {
        // The counters are still valid up to the escape, so the report says how far the run got and why it stopped:
        // a mutation killed by an unexpected exception stays auditable instead of leaving no result at all.
        failures.Add("unhandled " + error.GetType().Name + ": " + error.Message);
        Console.WriteLine("FAIL: unhandled " + error);
        File.WriteAllText(args[1], JsonSerializer.Serialize(new { status = "failed", gameVerified = false, abnormalExit = true,
            primitiveCount, vectorCases, assertions, failures }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Environment.Exit(1);
    }
}
