using System.Text.Json;
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
        double[] Vector(string key) => input.GetProperty(key).EnumerateArray().Select(v => v.GetDouble()).ToArray();
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
            "forge.modifier.value.power" => ScalarNodes.Binary(ScalarOperation.Power, V("value"), V("exponent")),
            "forge.modifier.value.clamp" => ScalarNodes.Clamp(V("value"), V("minimum"), V("maximum")),
            "forge.modifier.value.absolute" => ScalarNodes.Absolute(V("value")),
            "forge.modifier.value.round" => ScalarNodes.Round(V("value"), S("mode") switch
            {
                "floor" => ScalarRounding.Floor, "ceil" => ScalarRounding.Ceiling,
                "nearest" => ScalarRounding.Nearest, "truncate" => ScalarRounding.Truncate,
                _ => throw new Exception("Fixture has unknown rounding mode.")
            }),
            "forge.modifier.value.lerp" => ScalarNodes.Lerp(V("from"), V("to"), V("factor")),
            "forge.modifier.value.select_value" => ScalarNodes.SelectValue(input.GetProperty("condition").GetBoolean(), V("when_true"), V("when_false")),
            "forge.modifier.value.random_range" => SeededNodes.Uniform(V("minimum"), V("maximum"), Seed()),
            "forge.modifier.value.vector_compose" => VectorNodes.ComposeMetres(V("x"), V("y"), V("z")),
            "forge.modifier.value.vector_add" => VectorNodes.AddMetres(Vector("a"), Vector("b")),
            "forge.modifier.value.vector_scale" => VectorNodes.ScaleMetres(Vector("a"), V("factor")),
            "forge.condition.predicate.compare" => PureConditions.Compare(V("left"), V("right"), input.GetProperty("operator").GetString() switch
            {
                "eq" => ScalarComparison.Equal, "ne" => ScalarComparison.NotEqual,
                "lt" => ScalarComparison.Less, "lte" => ScalarComparison.LessOrEqual,
                "gt" => ScalarComparison.Greater, "gte" => ScalarComparison.GreaterOrEqual,
                _ => throw new Exception("Fixture has unknown comparison mode.")
            }, V("tolerance")),
            "forge.condition.predicate.range" => PureConditions.InRange(V("value"), V("minimum"), V("maximum"), S("boundary") switch
            {
                "inclusive" => IntervalBoundary.Inclusive, "exclusive" => IntervalBoundary.Exclusive,
                _ => throw new Exception("Fixture has unknown boundary mode.")
            }),
            "forge.condition.predicate.all" => PureConditions.All(input.GetProperty("a").GetBoolean(), input.GetProperty("b").GetBoolean()),
            "forge.condition.predicate.any" => PureConditions.Any(input.GetProperty("a").GetBoolean(), input.GetProperty("b").GetBoolean()),
            "forge.condition.predicate.not" => PureConditions.Not(input.GetProperty("input").GetBoolean()),
            "forge.condition.predicate.chance" => SeededNodes.Chance(V("probability"), Seed()),
            _ => throw new Exception("Unknown fixture canonical ID: " + id)
        };
    }
}
