using System.Text.Json;
using ForgeDevelopment.Native;

internal static class ConversionTests
{
    internal static void Run(Suite suite)
    {
        Numbers(suite, "50.0", typeof(float), 50f);
        Numbers(suite, "50", typeof(int), 50);
        Numbers(suite, "\"12\"", typeof(float), 12f);
        Numbers(suite, "\"7.5\"", typeof(float), 7.5f);
        Converted(suite, "true", typeof(bool), true);
        Converted(suite, "\"true\"", typeof(bool), true);
        Converted(suite, "\"MainLayer\"", typeof(LevelGeneration.LG_LayerType), LevelGeneration.LG_LayerType.MainLayer);
        Converted(suite, "\"LG_LayerType.SecondaryLayer\"", typeof(LevelGeneration.LG_LayerType), LevelGeneration.LG_LayerType.SecondaryLayer);
        Converted(suite, "\"secondarylayer\"", typeof(LevelGeneration.LG_LayerType), LevelGeneration.LG_LayerType.SecondaryLayer);
        Converted(suite, "1", typeof(LevelGeneration.LG_LayerType), LevelGeneration.LG_LayerType.SecondaryLayer);

        suite.Check("convert.badEnum", !Try("\"Nope\"", typeof(LevelGeneration.LG_LayerType), out _, out var error) && error.Contains("MainLayer"), "an unknown enum member must list members");
        suite.Check("convert.badNumber", !Try("\"abc\"", typeof(float), out _, out error), "a non-numeric string must fail for a float");
        suite.Check("convert.commaNumber", !Try("\"1,5\"", typeof(int), out _, out error), "a localised separator must not become 15");
        suite.Check("convert.nullValueType", !Try("null", typeof(int), out _, out error), "null is not valid for int");
        suite.Check("convert.boolForNumber", !Try("true", typeof(float), out _, out error), "a boolean must not silently become a number");

        var found = ExperimentTypes.Find("LevelGeneration.LG_SecurityDoor");
        suite.Check("types.findFullName", found != null, "the interop assembly must expose LG_SecurityDoor");
        suite.Check("types.findShortName", found != null && ExperimentTypes.Find("LG_SecurityDoor") == found);
        suite.Check("types.findMissing", ExperimentTypes.Find("No.Such.Type") == null);
        suite.Check("types.wildcard", ExperimentTypes.Match("LevelGeneration.LG_*Door").Count > 0);
    }

    private static bool Try(string json, Type type, out object? value, out string error)
        => ExperimentConvert.TryConvert(Json.Element(json), type, out value, out error);

    private static void Converted(Suite suite, string json, Type type, object? expected)
    {
        var ok = Try(json, type, out var value, out var error);
        suite.Check("convert." + json + " -> " + type.Name,
            ok && (expected == null ? value == null : expected.Equals(value)),
            ok ? "expected <" + expected + "> got <" + value + ">" : error);
    }

    private static void Numbers(Suite suite, string json, Type type, params float[] expected)
    {
        if (!Try(json, type, out var value, out var error))
        {
            suite.Check("convert." + json + " -> " + type.Name, false, error);
            return;
        }
        var components = value switch
        {
            UnityEngine.Vector3 vector => new[] { vector.x, vector.y, vector.z },
            UnityEngine.Color color => new[] { color.r, color.g, color.b, color.a },
            null => Array.Empty<float>(),
            _ => new[] { System.Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture) }
        };
        var same = components.Length == expected.Length;
        for (var index = 0; same && index < components.Length; index++) same = Math.Abs(components[index] - expected[index]) < 0.0001f;
        suite.Check("convert." + json + " -> " + type.Name, same,
            "expected [" + string.Join(",", expected) + "] got [" + string.Join(",", components) + "]");
    }
}
