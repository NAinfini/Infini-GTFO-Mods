using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

/// <summary>Metadata validation/expansion only. It neither binds nor executes nodes.</summary>
internal static class RuntimeGraphContracts
{
    internal const int MaximumVariadicPorts = 32;
    internal static void ValidatePort(JsonElement port, string id)
    {
        RuntimeJson.Shape(port, "id type", "schema unit nullable optional");
        RuntimeJson.Require(Regex.IsMatch(RuntimeJson.Text(port, "id"), @"^[a-z][a-z0-9_]*$"), "port-name", id);
        var type = RuntimeJson.Text(port, "type");
        foreach (var key in new[] { "schema", "unit" })
            if (port.TryGetProperty(key, out var text)) RuntimeJson.Text(text);
        if (type is "event" or "result" or "resource")
            RuntimeJson.Require(port.TryGetProperty("schema", out _), "port-schema", id);
        RuntimeJson.Require(new[] { "execution", "boolean", "integer", "number", "string", "vector3",
            "entity", "entity-list", "resource", "event", "result", "timer", "reservation" }.Contains(type), "port-type", id);
        if (type == "execution") RuntimeJson.Require(!port.TryGetProperty("unit", out _)
            && !port.TryGetProperty("schema", out _) && !RuntimeJson.Flag(port, "nullable"), "execution-port", id);
        if (port.TryGetProperty("unit", out _))
            RuntimeJson.Require(type is "number" or "integer" or "vector3", "port-unit", id);
        foreach (var flag in new[] { "nullable", "optional" })
            if (port.TryGetProperty(flag, out var value))
                RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "port-flag", id);
    }
    internal static void ValidateVariadic(JsonElement graph, string id)
    {
        if (!graph.TryGetProperty("variadic", out var spec)) return;
        RuntimeJson.Shape(spec, "side parameter port");
        var side = RuntimeJson.Text(spec, "side");
        RuntimeJson.Require(side is "inputs" or "outputs", "variadic-side", id);
        var template = spec.GetProperty("port"); ValidatePort(template, id);
        RuntimeJson.Require(!RuntimeJson.Flag(template, "optional") && !RuntimeJson.Flag(template, "nullable"),
            "variadic-template", id);
        var name = RuntimeJson.Text(spec, "parameter");
        var count = RuntimeJson.Rows(graph, "parameters").FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        RuntimeJson.Require(count.ValueKind == JsonValueKind.Object, "variadic-count-parameter", id);
        RuntimeJson.Require(RuntimeJson.Text(count, "type") == "integer" && !RuntimeJson.Flag(count, "required"),
            "variadic-count-parameter", id);
        RuntimeJson.Require(count.TryGetProperty("minimum", out var minimum)
            && count.TryGetProperty("maximum", out _), "variadic-count-bounds", id);
        var lower = RuntimeJson.Integer(minimum, 2, MaximumVariadicPorts - 1);
        var upper = RuntimeJson.Integer(count.GetProperty("maximum"), lower + 1, MaximumVariadicPorts);
        var ports = RuntimeJson.Rows(graph, side);
        RuntimeJson.Require(ports.Length == lower, "variadic-base-count", id);
        foreach (var port in ports)
            RuntimeJson.Require(new[] { "type", "unit", "schema" }.All(key => Optional(port, key) == Optional(template, key))
                && !RuntimeJson.Flag(port, "nullable") && !RuntimeJson.Flag(port, "optional"), "variadic-port-contract", id);
        RuntimeJson.Require(RuntimeJson.Text(graph, "execution") != "pure"
            || RuntimeJson.Text(template, "type") != "execution", "pure-execution", id);
        var ids = ports.Select(p => RuntimeJson.Text(p, "id")).ToHashSet(StringComparer.Ordinal);
        for (long i = lower + 1; i <= upper; i++)
        {
            var added = PortId(template, i);
            RuntimeJson.Require(added.Length <= 256, "variadic-port-name-budget", id);
            RuntimeJson.Require(!ids.Contains(added), "variadic-port-conflict", id);
        }
    }
    private static string? Optional(JsonElement port, string key)
        => port.TryGetProperty(key, out var value) ? value.GetString() : null;
    private static string PortId(JsonElement template, long index)
        => RuntimeJson.Text(template, "id") + "_" + index.ToString(CultureInfo.InvariantCulture);

    internal static JsonElement Resolve(JsonElement graph, JsonElement parameters)
    {
        if (!graph.TryGetProperty("variadic", out var spec)) return graph.Clone();
        var side = RuntimeJson.Text(spec, "side"); var name = RuntimeJson.Text(spec, "parameter");
        var definition = RuntimeJson.Rows(graph, "parameters").Single(p => RuntimeJson.Text(p, "id") == name);
        var ports = RuntimeJson.Rows(graph, side).ToList();
        long count = parameters.TryGetProperty(name, out var value)
            ? RuntimeJson.Integer(value, ports.Count, RuntimeJson.Integer(definition.GetProperty("maximum"))) : ports.Count;
        var template = spec.GetProperty("port");
        for (long i = ports.Count + 1; i <= count; i++)
        {
            var fields = template.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            fields["id"] = RuntimeJson.From(PortId(template, i));
            ports.Add(RuntimeJson.From(fields));
        }
        var resolved = graph.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        resolved[side] = RuntimeJson.From(ports);
        return RuntimeJson.From(resolved);
    }
}
