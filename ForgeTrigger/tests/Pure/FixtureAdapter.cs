using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

// Test-only mapping from existing authoring fixtures to typed helpers.
// Production contains no parallel canonical registry, JSON executor or lowering path.
internal static class FixtureAdapter
{
    internal static object Evaluate(JsonElement row)
    {
        var id = row.GetProperty("capabilityId").GetString()!;
        var p = row.GetProperty("parameters"); var input = row.GetProperty("inputs");
        double N(string key) => p.GetProperty(key).GetDouble();
        double V(string key) => input.GetProperty(key).GetDouble();
        long Seed() => input.GetProperty("seed").GetInt64();
        string S(string key) => p.GetProperty(key).GetString()!;
        return id switch
        {
            "forge.modifier.value.constant" => ScalarNodes.Constant(N("value")),
            "forge.modifier.value.add" => ScalarNodes.Binary(ScalarOperation.Add, V("a"), V("b")),
            "forge.modifier.value.subtract" => ScalarNodes.Binary(ScalarOperation.Subtract, V("a"), V("b")),
            "forge.modifier.value.multiply" => ScalarNodes.Binary(ScalarOperation.Multiply, V("a"), V("b")),
            "forge.modifier.value.divide" => ScalarNodes.Divide(V("a"), V("b"), S("zero_policy") switch
            {
                "reject" => DivisionZeroPolicy.Reject, "zero" => DivisionZeroPolicy.Zero, "passthrough" => DivisionZeroPolicy.Passthrough,
                _ => throw new Exception("Fixture has unknown zero policy.")
            }),
            "forge.modifier.value.minimum" => ScalarNodes.Binary(ScalarOperation.Minimum, V("a"), V("b")),
            "forge.modifier.value.maximum" => ScalarNodes.Binary(ScalarOperation.Maximum, V("a"), V("b")),
            "forge.modifier.value.clamp" => ScalarNodes.Clamp(V("value"), V("minimum"), V("maximum")),
            "forge.modifier.value.absolute" => ScalarNodes.Absolute(V("value")),
            "forge.modifier.value.round" => ScalarNodes.Round(V("value"), S("mode") switch
            {
                "floor" => ScalarRounding.Floor, "ceil" => ScalarRounding.Ceiling,
                "nearest" => ScalarRounding.Nearest, "truncate" => ScalarRounding.Truncate,
                _ => throw new Exception("Fixture has unknown rounding mode.")
            }),
            "forge.modifier.value.random_range" => SeededNodes.Uniform(V("minimum"), V("maximum"), Seed()),
            "forge.condition.predicate.compare" => PureConditions.Compare(V("left"), V("right"), input.GetProperty("operator").GetString() switch
            {
                "eq" => ScalarComparison.Equal, "ne" => ScalarComparison.NotEqual,
                "lt" => ScalarComparison.Less, "lte" => ScalarComparison.LessOrEqual,
                "gt" => ScalarComparison.Greater, "gte" => ScalarComparison.GreaterOrEqual,
                _ => throw new Exception("Fixture has unknown comparison mode.")
            }, V("tolerance")),
            "forge.condition.predicate.all" => PureConditions.All(input.GetProperty("a").GetBoolean(), input.GetProperty("b").GetBoolean()),
            "forge.condition.predicate.any" => PureConditions.Any(input.GetProperty("a").GetBoolean(), input.GetProperty("b").GetBoolean()),
            "forge.condition.predicate.not" => PureConditions.Not(input.GetProperty("input").GetBoolean()),
            _ => throw new RuntimeContractException("pure-operation", "Unknown fixture canonical ID: " + id)
        };
    }
}
