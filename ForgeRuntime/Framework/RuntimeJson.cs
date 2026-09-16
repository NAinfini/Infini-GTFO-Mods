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
    /// <summary>One text as the JSON writer spells it, without the surrounding quotes. The encoder is the one the
    /// whole-document spelling uses, so a text composed from quoted pieces is character for character the text
    /// `StableText` produced for the same array — which is what lets an identity be assembled from parts computed
    /// once instead of being serialized again on every dispatch.</summary>
    internal static string Quote(string text) { var raw = From(new[] { text }).GetRawText(); return raw.Substring(1, raw.Length - 2); }
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
    /// <summary>The same rule against a field list that is already known: no split, no set built per call. The field
    /// list is the whole shape — there are no optional fields in this form — and the violation order is the one
    /// above, unknown field before missing field.</summary>
    internal static void Shape(JsonElement value, string[] fields)
    {
        Require(value.ValueKind == JsonValueKind.Object, "object-required", "Expected JSON object.");
        foreach (var property in value.EnumerateObject())
        {
            var known = false;
            foreach (var field in fields) if (string.Equals(field, property.Name, StringComparison.Ordinal)) { known = true; break; }
            Require(known, "unknown-field", property.Name);
        }
        foreach (var field in fields) Require(value.TryGetProperty(field, out _), "missing-field", field);
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
    /// <summary>The namespace an entity reference is routed by: everything before its first colon. The kernel, the
    /// registry and the providers all split a reference this one way, so a kind is read, not re-derived — which is
    /// also why a game-independent contract that compares two references of its own provider reads it here.</summary>
    public static string KindOf(string entityId)
    {
        var split = entityId.IndexOf(':');
        return split > 0 ? entityId[..split] : "";
    }
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
    private static readonly string[] EntityFields = { "id", "worldEpoch", "lifeEpoch" };
    /// <summary>One entity reference read on the kernel's own hot path, where the shape is already a fixed list: the
    /// same three fields and the same two violations, without rebuilding the field set for every reference of a
    /// candidate list.</summary>
    internal static EntityReference EntityRow(JsonElement value)
    {
        Shape(value, EntityFields);
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
    /// <summary>
    /// One value against the port that will carry it. A port's own type decides the value's form, and the two
    /// reference kinds are the reason this is a boundary at all: a resource value is the kind and id a frame's two
    /// slots hold, and an event value is the row index of one dispatch's event. <paramref name="events"/> is the
    /// table those indices are counted in — null where no dispatch is running, which is also where no event value
    /// can be read.
    /// </summary>
    internal static void ValidateValue(JsonElement value, JsonElement port, RuntimeEventRows? events = null)
    {
        if (value.ValueKind == JsonValueKind.Null)
        { Require(Flag(port, "nullable"), "null-input", Text(port, "id")); return; }
        var type = Text(port, "type");
        // A collection is the same value repeated: only the kinds with an element form have a collection form,
        // and every element is validated exactly as a single value of that kind is. An entity set keeps its own
        // rule because its members are references a later step resolves, not plain values.
        if (RuntimeGraphContracts.Many(port))
        {
            Require(RuntimeGraphContracts.Segmented(type), "unsupported-port", type + " many");
            Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 256, "invalid-array", type + " many");
            if (type != "entity")
            {
                var fields = port.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                fields["cardinality"] = From("one");
                var element = RuntimeJson.From(fields);
                foreach (var item in value.EnumerateArray()) ValidateValue(item, element, events);
                return;
            }
        }
        switch (type)
        {
            case "entity" when RuntimeGraphContracts.Many(port):
                Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 256, "invalid-entities", "Entity set exceeds 256.");
                var refs = value.EnumerateArray().Select(Entity).ToArray();
                Require(refs.Distinct().Count() == refs.Length, "duplicate-entity", "Repeated recipients require explicit semantics."); break;
            case "entity": Entity(value); break;
            case "boolean": Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "invalid-boolean", "Expected bool."); break;
            case "integer": Integer(value, -MaxSafeInteger); break;
            case "number": Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n), "invalid-number", "Expected finite number."); break;
            case "string": Require(value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 4096, "invalid-string", "Expected bounded string."); break;
            case "vector3":
                Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3 && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n)), "invalid-vector", "Expected finite vector3."); break;
            case "enum":
                // Q3: the wire value is a member-set index, never the member name. A port always indexes its
                // whole named set (ports carry no inline subset), so the port's own schema is the index basis.
                var count = RuntimeGraphContracts.EnumSets[Text(port, "schema")].Length;
                Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var member) && double.IsFinite(member)
                    && member == Math.Truncate(member) && member >= 0 && member < count, "invalid-enum", "Expected enum member index."); break;
            case "handle": Handle(value); break;
            // A resource value is the two-field reference a frame carries in two slots, written out: the kind it
            // belongs to and the id its owner answers with. The port's own `resourceKind` is the authority on the
            // kind, so a value that names another one is refused here and never reaches a handler as a reference
            // the port did not declare. A plan's own compiled reference is not this value: it is written in the
            // document's `{id, revision}` form, translated by the loader, and validated there.
            case "resource":
                var resource = ResourceRefOf(value);
                Require(resource.ResourceKind == Text(port, "resourceKind"), RuntimeAbiCodes.ResourceKind, resource.ResourceId);
                break;
            // An event value is one dispatch's event row, written out as its index. Two things make it a value of
            // the port it arrives on and neither is a property of the number: the row has to exist in the table of
            // the dispatch that is reading it, and that row's own type has to be the event the port declares. A
            // row index of another dispatch, or one past the table, names no event and is refused rather than
            // resolved against whatever sits at that index.
            case "event":
                Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var index)
                    && double.IsFinite(index) && index == Math.Truncate(index) && index >= 0 && index < (events?.Count ?? 0),
                    RuntimeAbiCodes.EventPortKind, Text(port, "id"));
                Require(events!.Matches((long)value.GetDouble(), Text(port, "schema")), RuntimeAbiCodes.EventPortKind, Text(port, "id"));
                break;
                        default: throw new RuntimeContractException("unsupported-port", type);
        }
    }
    /// <summary>One handle at the JSON dispatch boundary: the same four components the frame slot carries —
    /// the identity triple plus the registration index of the provider that created it. Nothing here says what
    /// the handle is worth; whether it is live, of the right kind and within its lifetime is decided by the
    /// kernel when the step that consumes it runs (`CheckHandle`).</summary>
    internal static void Handle(JsonElement value)
    {
        Shape(value, "worldEpoch lifeEpoch local provider");
        Integer(value.GetProperty("worldEpoch")); Integer(value.GetProperty("lifeEpoch"));
        Integer(value.GetProperty("local")); Integer(value.GetProperty("provider"));
    }
    /// <summary>One resource reference at the JSON boundary: the kind and the id, and nothing else. The shape is
    /// the frame's own two slots written out, so a resource value round-trips between a payload, a handler and a
    /// frame without a second spelling for any of them.</summary>
    public static ResourceRef ResourceRefOf(JsonElement value)
    {
        Shape(value, "resourceKind resourceId");
        return new ResourceRef(Text(value, "resourceKind"), Text(value, "resourceId"));
    }
    /// <summary>
    /// One resource reference as a compiled plan writes it: the document names the instance by its own id and the
    /// revision it was authored against, and the port it feeds names the kind. That is the shape the constant pool
    /// encoder reads into two slots, so it is also the shape every load-time check of a compiled reference reads
    /// here; the runtime value a handler sees afterwards is <see cref="ResourceRefOf"/>'s.
    /// </summary>
    internal static string ResourceLiteral(JsonElement value)
    {
        Shape(value, "id", "revision");
        return Text(value, "id");
    }
    internal static void Parameters(JsonElement parameters, JsonElement capability, IReadOnlySet<string>? promoted = null)
    {
        var definitions = Rows(capability.GetProperty("graph"), "parameters").Where(p => promoted == null || !promoted.Contains(Text(p, "id"))).ToArray();
        Shape(parameters, string.Join(" ", definitions.Where(p => Flag(p, "required")).Select(p => Text(p, "id"))),
            string.Join(" ", definitions.Where(p => !Flag(p, "required")).Select(p => Text(p, "id"))));
        foreach (var definition in definitions)
        {
            if (!parameters.TryGetProperty(Text(definition, "id"), out var value)) continue;
            ValidateParameter(value, definition);
        }
    }
    /// <summary>One compiled parameter value against its own definition: the type, the member set it indexes, and
    /// its declared bounds. Shared by the load-time frame encoder, which writes the value into the plan's constant
    /// pool, and by the dispatch-time re-validation of a value that arrived through a promoted input port.</summary>
    internal static void ValidateParameter(JsonElement value, JsonElement definition)
    {
        var type = Text(definition, "type");
        RuntimeJson.Require(type != "recipient-policy", "unsupported-parameter", "The runtime cannot evaluate recipient-policy.");
        if (type == "enum")
        {
            // Q3: the compiled/wire value is a member-set index, never the member name. Inline `values` narrow
            // the index basis to that list; a promotable enum always names a whole set instead.
            var members = RuntimeGraphContracts.EnumMembers(definition);
            Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var index) && double.IsFinite(index)
                && index == Math.Truncate(index) && index >= 0 && index < members.Length, "invalid-enum", Text(definition, "id"));
        }
        else ValidateValue(value, definition);
        if (value.ValueKind != JsonValueKind.Number) return;
        var number = value.GetDouble();
        if (definition.TryGetProperty("minimum", out var min)) Require(number >= min.GetDouble(), "parameter-minimum", Text(definition, "id"));
        if (definition.TryGetProperty("maximum", out var max)) Require(number <= max.GetDouble(), "parameter-maximum", Text(definition, "id"));
    }
    /// <summary>Handler boundary (Q3): a wired value already validated against `port` keeps its compiled index
    /// representation everywhere in the kernel; only here, right before a handler reads it, an enum index is
    /// resolved to its member name so existing handlers keep comparing strings.</summary>
    internal static JsonElement EnumPortToHandlerValue(JsonElement value, JsonElement port)
        => Text(port, "type") == "enum" && value.ValueKind == JsonValueKind.Number
            ? From(RuntimeGraphContracts.EnumSets[Text(port, "schema")][(int)value.GetDouble()]) : value;
    /// <summary>Handler boundary (Q3) for parameters: every enum-typed definition on `capability` (constants and,
    /// once merged back in, promoted values alike) is resolved from its compiled index to its member name.
    /// Called once, after every other parameter validation for this dispatch has already run on the indices.</summary>
    internal static JsonElement ResolveEnumParameters(JsonElement parameters, JsonElement capability)
    {
        var definitions = Rows(capability.GetProperty("graph"), "parameters").Where(p => Text(p, "type") == "enum").ToArray();
        if (definitions.Length == 0) return parameters;
        var fields = parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var name = Text(definition, "id");
            if (fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number)
                fields[name] = From(RuntimeGraphContracts.EnumMembers(definition)[(int)value.GetDouble()]);
        }
        return From(fields);
    }
}
