using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

public static class RuntimeJson
{
    public const long MaxSafeInteger = 9007199254740991;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 32 };
    public static JsonElement EmptyObject { get; } = Parse("{}");
    public static JsonElement Parse(string json)
    {
        Require(json != null && Encoding.UTF8.GetByteCount(json) <= 4 * 1024 * 1024, "json-size", "JSON exceeds 4 MiB.");
        try
        {
            using var document = JsonDocument.Parse(json!, new JsonDocumentOptions { MaxDepth = 32 });
            ValidateJson(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (JsonException ex) { throw new RuntimeContractException("invalid-json", ex.Message); }
    }
    public static JsonElement From(object value) => JsonSerializer.SerializeToElement(value, Options);
    internal static string StableText(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteStable(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void WriteStable(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteStable(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteStable(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    internal static void Require(bool condition, string code, string message)
    { if (!condition) throw new RuntimeContractException(code, message); }
    internal static void Shape(JsonElement value, string required, string optional = "")
    {
        Require(value.ValueKind == JsonValueKind.Object, "object-required", "Expected JSON object.");
        var keys = required.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowed = new HashSet<string>(keys.Concat(optional.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) Require(allowed.Contains(property.Name), "unknown-field", property.Name);
        foreach (var key in keys) Require(value.TryGetProperty(key, out _), "missing-field", key);
    }
    internal static string Text(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String, "string-required", "Expected string.");
        return Text(value.GetString()!);
    }
    internal static string Text(string text)
    {
        Require(text != null && text.Length is > 0 and <= 256 && text.Trim() == text, "invalid-string", "Text must contain 1..256 trimmed characters without controls.");
        foreach (var c in text!) Require(c > 31 && c != 127, "invalid-string", "Control character in text.");
        return text;
    }
    internal static long Integer(long value, long minimum = 0, long maximum = MaxSafeInteger)
    { Require(value >= minimum && value <= maximum, "invalid-integer", "Expected bounded safe integer."); return value; }
    internal static string Text(JsonElement value, string key) => Text(value.GetProperty(key));
    internal static string Id(JsonElement value, string key)
    {
        var text = Text(value, key); Require(IsId(text), "invalid-id", text); return text;
    }
    internal static bool IsId(string value) => Regex.IsMatch(value, @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant);
    internal static string Version(JsonElement value, string key)
    {
        var text = Text(value, key);
        Require(Regex.IsMatch(text, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"), "invalid-version", text); return text;
    }
    internal static long Integer(JsonElement value, long minimum = 0, long maximum = MaxSafeInteger)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n)
            && n == Math.Truncate(n) && n >= minimum && n <= maximum, "invalid-integer", "Expected bounded safe integer.");
        return (long)value.GetDouble();
    }
    internal static string[] Strings(JsonElement value, bool unique = true)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 2048, "array-required", "Expected bounded string array.");
        var result = value.EnumerateArray().Select(Text).ToArray();
        Require(!unique || result.Distinct(StringComparer.Ordinal).Count() == result.Length, "duplicate-value", "Duplicate array entry.");
        return result;
    }
    internal static JsonElement[] Rows(JsonElement value, string name, int maximum = 2048)
    {
        var rows = value.GetProperty(name);
        Require(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() <= maximum, "invalid-array", name);
        return rows.EnumerateArray().ToArray();
    }
    internal static void ExactSet(IEnumerable<string> actual, IEnumerable<string> expected, string code)
        => Require(actual.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)), code, "Exact set mismatch.");
    public static EntityReference Entity(JsonElement value)
    {
        Shape(value, "id worldEpoch lifeEpoch");
        return new EntityReference(Text(value, "id"), Integer(value.GetProperty("worldEpoch")), Integer(value.GetProperty("lifeEpoch")));
    }
    internal static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { Require(keys.Add(property.Name), "duplicate-key", property.Name); ValidateJson(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateJson(item);
        else if (value.ValueKind == JsonValueKind.Number)
            Require(value.TryGetDouble(out var n) && double.IsFinite(n), "invalid-number", "Non-finite JSON number.");
        else Require(value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null,
            "invalid-json", "Undefined JSON value.");
    }
    internal static bool Flag(JsonElement value, string key) => value.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True;
    internal static void ValidateValue(JsonElement value, JsonElement port)
    {
        if (value.ValueKind == JsonValueKind.Null)
        { Require(Flag(port, "nullable"), "null-input", Text(port, "id")); return; }
        var type = Text(port, "type");
        switch (type)
        {
            case "entity": Entity(value); break;
            case "entity-list":
                Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 256, "invalid-entities", "Entity-list exceeds 256.");
                var refs = value.EnumerateArray().Select(Entity).ToArray();
                Require(refs.Distinct().Count() == refs.Length, "duplicate-entity", "Repeated recipients require explicit semantics."); break;
            case "boolean": Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "invalid-boolean", "Expected bool."); break;
            case "integer": Integer(value, -MaxSafeInteger); break;
            case "number": Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n), "invalid-number", "Expected finite number."); break;
            case "string": Require(value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 4096, "invalid-string", "Expected bounded string."); break;
            case "vector3":
                Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3 && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n)), "invalid-vector", "Expected finite vector3."); break;
                        default: throw new RuntimeContractException("unsupported-port", type);
        }
    }
    internal static void Parameters(JsonElement parameters, JsonElement capability)
    {
        var definitions = Rows(capability.GetProperty("graph"), "parameters");
        Shape(parameters, string.Join(" ", definitions.Where(p => Flag(p, "required")).Select(p => Text(p, "id"))),
            string.Join(" ", definitions.Where(p => !Flag(p, "required")).Select(p => Text(p, "id"))));
        foreach (var definition in definitions)
        {
            if (!parameters.TryGetProperty(Text(definition, "id"), out var value)) continue;
            var type = Text(definition, "type");
            if (type == "enum") Require(Strings(definition.GetProperty("values")).Contains(Text(value), StringComparer.Ordinal), "invalid-enum", Text(definition, "id"));
            else { Require(type != "recipient-policy", "unsupported-parameter", "v1 cannot evaluate recipient-policy."); ValidateValue(value, definition); }
            if (value.ValueKind != JsonValueKind.Number) continue;
            var number = value.GetDouble();
            if (definition.TryGetProperty("minimum", out var min)) Require(number >= min.GetDouble(), "parameter-minimum", Text(definition, "id"));
            if (definition.TryGetProperty("maximum", out var max)) Require(number <= max.GetDouble(), "parameter-maximum", Text(definition, "id"));
        }
    }
}
