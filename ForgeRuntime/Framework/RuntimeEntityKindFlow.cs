using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>Load-time proof that forwarding glue does not erase its producer's entity kind.</summary>
internal static class RuntimeEntityKindFlow
{
    internal static void Validate(JsonElement graph, string id)
    {
        var inputs = RuntimeJson.Rows(graph, "inputs");
        foreach (var input in inputs)
            RuntimeJson.Require(!input.TryGetProperty("entityKindsFrom", out _), "port-kind-flow", id);
        foreach (var output in RuntimeJson.Rows(graph, "outputs"))
        {
            if (!output.TryGetProperty("entityKindsFrom", out var field)) continue;
            RuntimeJson.Require(RuntimeJson.Text(output, "type") == "entity" && field.ValueKind == JsonValueKind.String
                && !output.TryGetProperty("entityKinds", out _), "port-kind-flow", id);
            var name = field.GetString();
            RuntimeJson.Require(inputs.Any(p => RuntimeJson.Text(p, "id") == name && RuntimeJson.Text(p, "type") == "entity"), "port-kind-flow", id);
        }
    }

    internal static JsonElement Resolve(JsonElement graph, Func<string, JsonElement?> producer)
    {
        if (!RuntimeJson.Rows(graph, "outputs").Any(port => port.TryGetProperty("entityKindsFrom", out _))) return graph;
        var fields = graph.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        fields["outputs"] = RuntimeJson.From(RuntimeJson.Rows(graph, "outputs").Select(port =>
        {
            if (!port.TryGetProperty("entityKindsFrom", out var source)) return port;
            var resolved = port.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            resolved.Remove("entityKindsFrom"); resolved.Remove("entityKinds");
            var origin = producer(source.GetString()!);
            if (RuntimeJson.Text(port, "type") == "entity" && origin is JsonElement value
                && value.TryGetProperty("entityKinds", out var kinds)) resolved["entityKinds"] = kinds.Clone();
            return RuntimeJson.From(resolved);
        }).ToArray());
        return RuntimeJson.From(fields);
    }
}
