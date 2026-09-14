using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

/// <summary>
/// Forge Standard v0.2 graph metadata: validation, variable port expansion and the dense
/// slot layout a compiled plan carries. It neither binds nor executes nodes. Every table
/// mirrors site/forge/contracts.ts; array order is part of the wire because compiled
/// layouts store indices into it.
/// </summary>
internal static class RuntimeGraphContracts
{
    internal const int MaximumVariadicPorts = 32;
    /// <summary>Mirrors site/forge/graph-schema.ts DOMAIN_REASON_CODE_MAX_LENGTH / DOMAIN_REASON_CODE_PATTERN.</summary>
    internal const int DomainReasonCodeMaxLength = 64;
    private static readonly Regex DomainReasonCodePattern = new(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    internal static readonly string[] PortTypes = { "execution", "boolean", "integer", "number", "string", "enum",
        "vector3", "entity", "resource", "handle", "event", "result", "policy" };
    internal static readonly string[] Cardinalities = { "one", "many" };
    internal static readonly string[] ResourceKinds = { "map", "room", "enemy", "weapon", "tool", "consumable", "item",
        "objective", "encounter", "wave", "spawn_pool", "behavior_graph", "ability", "path", "area_field", "animation",
        "audio", "effect", "material", "model", "profile", "pool" };
    internal static readonly string[] HandleKinds = { "timer", "reservation", "lease", "subscription", "transaction",
        "status", "effect", "audio", "animation", "deployment", "cooldown", "charge", "pool_membership" };
    internal static readonly string[] HandleLifetimes = { "invocation", "resource_instance", "entity_life", "encounter",
        "expedition", "session" };
    /// <summary>Declaration order is the compiled valueSet index of an enum port.</summary>
    private static readonly (string Name, string[] Members)[] EnumSetTable = {
        ("compare_operator", new[] { "eq", "ne", "lt", "lte", "gt", "gte" }),
        ("boundary_mode", new[] { "inclusive", "exclusive" }),
        ("rounding_mode", new[] { "floor", "ceil", "nearest", "truncate" }),
        ("command_phase", new[] { "requested", "accepted", "committed", "rejected", "cancelled" }),
        ("execution_outcome", new[] { "succeeded", "partial", "rejected", "failed", "cancelled", "expired" }),
        ("interaction_phase", new[] { "requested", "started", "completed", "cancelled", "failed" }),
        ("damage_kind", new[] { "direct", "melee", "explosion", "dot", "shrapnel", "collision", "fall", "environment", "reflection" }),
        ("ai_state", new[] { "sleeping", "waking", "idle", "investigating", "alerted", "pursuing", "attacking", "recovering", "disabled", "dead" }),
        ("status_kind", new[] { "slow", "haste", "root", "stun", "paralysis", "freeze", "foam", "blind", "deaf", "silence", "disarm",
            "weaken", "vulnerability", "burn", "bleed", "poison", "corrosion", "infection", "regeneration", "shield", "cloak", "taunt",
            "fear", "reveal", "mark" }),
        ("stack_policy", new[] { "refresh", "extend", "add", "replace", "strongest", "independent-per-source" }),
        ("query_shape", new[] { "room", "area", "zone", "sphere", "cone", "box", "cylinder", "capsule", "path" }),
        ("empty_policy", new[] { "emit-empty", "skip", "fail" }),
        ("equipment_action", new[] { "primary", "secondary", "reload", "interact", "recall", "alternate", "custom-binding" }),
        ("lifetime_scope", HandleLifetimes),
        ("variable_value_type", new[] { "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle" }),
        ("recipient_sort", new[] { "stable-id", "nearest", "farthest" }),
        ("recipient_anchor", new[] { "self", "source", "owner", "instigator", "event-target" }),
        ("recipient_relation", new[] { "self", "ally", "hostile", "neutral", "unknown" }),
        ("recipient_life_state", new[] { "alive", "downed", "dead" }),
        ("value_operation", new[] { "set", "add", "subtract", "multiply", "minimum", "maximum" }),
        ("coordinate_space", new[] { "world", "local", "view" }),
        ("pulse_start", new[] { "immediate", "after_interval" }),
    };
    internal static readonly IReadOnlyDictionary<string, string[]> EnumSets =
        EnumSetTable.ToDictionary(x => x.Name, x => x.Members, StringComparer.Ordinal);
    internal static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "session", "logic", "editor" };
    /// <summary>No implicit source/owner fallback: a declared context role input is always explicit.</summary>
    private static readonly string[] ContextRolePorts = { "self", "source", "owner", "instigator", "event_target" };
    private static readonly string[] ParameterTypes = { "boolean", "integer", "number", "string", "enum", "vector3", "recipient-policy" };
    private static readonly string[] RecipientTargets = { "entity", "resource", "handle" };
    /// <summary>Value types with an implemented runtime validator. "many" is entity-only (the former entity-list).</summary>
    internal static readonly string[] RuntimeValueTypes = { "boolean", "integer", "number", "string", "vector3", "entity" };

    private static bool IsName(string value) => Regex.IsMatch(value, @"^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
    private static string? Optional(JsonElement value, string key) => value.TryGetProperty(key, out var field) ? field.GetString() : null;
    internal static string Cardinality(JsonElement port) => Optional(port, "cardinality") ?? "one";
    internal static bool Many(JsonElement port) => Cardinality(port) == "many";
    /// <summary>Full value contract equality, used by repeated ports and by plan input wiring.</summary>
    internal static bool SameValue(JsonElement a, JsonElement b)
        => RuntimeJson.Text(a, "type") == RuntimeJson.Text(b, "type") && Cardinality(a) == Cardinality(b)
           && new[] { "schema", "unit", "resourceKind", "handleKind", "lifetime" }.All(key => Optional(a, key) == Optional(b, key));

    internal static void ValidatePort(JsonElement port, string id)
    {
        RuntimeJson.Shape(port, "id type", "cardinality schema resourceKind handleKind lifetime unit nullable optional codes");
        RuntimeJson.Require(IsName(RuntimeJson.Text(port, "id")), "port-name", id);
        var type = RuntimeJson.Text(port, "type");
        RuntimeJson.Require(PortTypes.Contains(type), "port-type", id);
        foreach (var flag in new[] { "nullable", "optional" })
            if (port.TryGetProperty(flag, out var value))
                RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "port-flag", id);
        foreach (var key in new[] { "cardinality", "schema", "resourceKind", "handleKind", "lifetime", "unit" })
            if (port.TryGetProperty(key, out var text)) RuntimeJson.Text(text);
        if (port.TryGetProperty("cardinality", out _)) RuntimeJson.Require(Cardinalities.Contains(Cardinality(port)), "port-cardinality", id);
        var schema = Optional(port, "schema");
        if (type is "event" or "result" or "resource" or "policy") RuntimeJson.Require(schema != null, "port-schema", id);
        if (type == "enum") RuntimeJson.Require(schema != null && EnumSets.ContainsKey(schema), "port-enum-set", id);
        var resourceKind = Optional(port, "resourceKind");
        RuntimeJson.Require(type == "resource" ? resourceKind != null && ResourceKinds.Contains(resourceKind) : resourceKind == null, "port-resource-kind", id);
        var handleKind = Optional(port, "handleKind"); var lifetime = Optional(port, "lifetime");
        RuntimeJson.Require(type == "handle"
            ? handleKind != null && HandleKinds.Contains(handleKind) && lifetime != null && HandleLifetimes.Contains(lifetime)
            : handleKind == null && lifetime == null, "port-handle-kind", id);
        if (type == "execution")
            RuntimeJson.Require(!port.TryGetProperty("unit", out _) && schema == null && !RuntimeJson.Flag(port, "nullable")
                && !port.TryGetProperty("cardinality", out _), "execution-port", id);
        if (port.TryGetProperty("unit", out _)) RuntimeJson.Require(type is "number" or "integer" or "vector3", "port-unit", id);
        if (port.TryGetProperty("codes", out var codes))
        {
            RuntimeJson.Require(type == "result", "port-codes", id);
            RuntimeJson.Require(codes.ValueKind == JsonValueKind.Array, "port-codes", id);
            var items = codes.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : null).ToArray();
            RuntimeJson.Require(items.All(c => c != null && c.Length <= DomainReasonCodeMaxLength && DomainReasonCodePattern.IsMatch(c)), "port-codes", id);
            RuntimeJson.Require(items.Distinct(StringComparer.Ordinal).Count() == items.Length, "port-codes", id);
        }
    }

    /// <summary>Forge Standard v0.2 metadata plus the capability-kind rules every registry applies.</summary>
    internal static void ValidateCapability(string kind, JsonElement graph, string id)
    {
        RuntimeJson.Shape(graph, "domains execution inputs outputs parameters", "recipients variadic portGroups");
        var domains = RuntimeJson.Strings(graph.GetProperty("domains"));
        RuntimeJson.Require(domains.Length > 0 && domains.All(Domains.Contains), "graph-domain", id);
        var execution = RuntimeJson.Text(graph, "execution");
        RuntimeJson.Require(execution is "pure" or "host" or "owner" or "presentation", "graph-authority", id);
        var inputs = RuntimeJson.Rows(graph, "inputs"); var outputs = RuntimeJson.Rows(graph, "outputs");
        foreach (var ports in new[] { inputs, outputs })
        {
            foreach (var port in ports) ValidatePort(port, id);
            RuntimeJson.Require(ports.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == ports.Length, "duplicate-port", id);
        }
        foreach (var port in inputs.Where(p => ContextRolePorts.Contains(RuntimeJson.Text(p, "id"))))
            RuntimeJson.Require(!RuntimeJson.Flag(port, "optional") && !RuntimeJson.Flag(port, "nullable"), "context-role-port", id);
        if (graph.TryGetProperty("recipients", out var recipients)) ValidateRecipients(recipients, inputs, outputs, id);
        if (execution == "pure")
            RuntimeJson.Require(!inputs.Concat(outputs).Any(p => RuntimeJson.Text(p, "type") == "execution"), "pure-execution", id);
        var parameters = RuntimeJson.Rows(graph, "parameters");
        RuntimeJson.Require(parameters.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == parameters.Length, "duplicate-parameter", id);
        foreach (var parameter in parameters) ValidateParameter(parameter, inputs, id);
        ValidateVariadic(graph, id);
        ValidatePortGroups(graph, id);
        if (kind is "selector" or "condition" or "modifier") RuntimeJson.Require(execution == "pure", "query-authority", id);
        if (kind is "trigger" or "action" or "control") RuntimeJson.Require(execution != "pure", "executable-authority", id);
        if (kind == "trigger") RuntimeJson.Require(!inputs.Any(p => RuntimeJson.Text(p, "type") == "execution"), "trigger-input", id);
        RuntimeJson.Require(kind == "action" || recipients.ValueKind == JsonValueKind.Undefined, "recipient-owner", id);
        // Every action, not only the ones that happen to take an entity.
        if (kind == "action") RuntimeJson.Require(recipients.ValueKind != JsonValueKind.Undefined, "recipient-contract", id);
    }
    private static void ValidateRecipients(JsonElement spec, JsonElement[] inputs, JsonElement[] outputs, string id)
    {
        RuntimeJson.Shape(spec, "input target cardinality requires result", "handle");
        var target = RuntimeJson.Text(spec, "target"); var cardinality = RuntimeJson.Text(spec, "cardinality");
        RuntimeJson.Require(RecipientTargets.Contains(target), "recipient-target", id);
        RuntimeJson.Require(Cardinalities.Contains(cardinality), "recipient-cardinality", id);
        var inputName = RuntimeJson.Text(spec, "input");
        var input = inputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == inputName);
        RuntimeJson.Require(input.ValueKind == JsonValueKind.Object && RuntimeJson.Text(input, "type") == target
            && !RuntimeJson.Flag(input, "optional") && !RuntimeJson.Flag(input, "nullable"), "recipient-port", id);
        RuntimeJson.Require(Cardinality(input) == cardinality, "recipient-cardinality", id);
        var requirements = RuntimeJson.Strings(spec.GetProperty("requires"));
        RuntimeJson.Require(requirements.Length <= 128 && requirements.All(RuntimeJson.IsId), "recipient-requirements", id);
        var resultName = RuntimeJson.Text(spec, "result");
        var result = outputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == resultName);
        RuntimeJson.Require(result.ValueKind == JsonValueKind.Object && RuntimeJson.Text(result, "type") == "result"
            && !RuntimeJson.Flag(result, "optional") && !RuntimeJson.Flag(result, "nullable"), "recipient-result", id);
        if (!spec.TryGetProperty("handle", out _)) return;
        var handleName = RuntimeJson.Text(spec, "handle");
        RuntimeJson.Require(outputs.Any(p => RuntimeJson.Text(p, "id") == handleName && RuntimeJson.Text(p, "type") == "handle"), "recipient-handle", id);
    }
    private static void ValidateParameter(JsonElement parameter, JsonElement[] inputs, string id)
    {
        RuntimeJson.Shape(parameter, "id type role required", "minimum maximum values set unit");
        var name = RuntimeJson.Text(parameter, "id"); var type = RuntimeJson.Text(parameter, "type"); var role = RuntimeJson.Text(parameter, "role");
        RuntimeJson.Require(IsName(name), "parameter-name", id);
        RuntimeJson.Require(ParameterTypes.Contains(type), "parameter-type", id);
        RuntimeJson.Require(role is "value" or "structural", "parameter-role", id);
        RuntimeJson.Require(parameter.GetProperty("required").ValueKind is JsonValueKind.True or JsonValueKind.False, "parameter-required", id);
        foreach (var bound in new[] { "minimum", "maximum" }) if (parameter.TryGetProperty(bound, out var value))
        {
            RuntimeJson.Require(type is "number" or "integer", "parameter-bound-type", id);
            RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number), "parameter-bound", id);
            if (type == "integer") RuntimeJson.Integer(value, -RuntimeJson.MaxSafeInteger);
        }
        if (parameter.TryGetProperty("minimum", out var minimum) && parameter.TryGetProperty("maximum", out var maximum))
            RuntimeJson.Require(minimum.GetDouble() <= maximum.GetDouble(), "parameter-bounds", id);
        var hasValues = parameter.TryGetProperty("values", out var values); var set = Optional(parameter, "set");
        if (type != "enum") RuntimeJson.Require(!hasValues && set == null, "parameter-values", id);
        else
        {
            // A promotable enum names a whole shared set (a promoted port carries the set, not a subset).
            // A structural enum inlines its members, names a whole set, or narrows one to a proper subset in set order.
            RuntimeJson.Require(set == null ? role == "structural" : EnumSets.ContainsKey(set), "parameter-set", id);
            RuntimeJson.Require(!hasValues || role == "structural", "parameter-set", id);
            if (hasValues)
            {
                var members = RuntimeJson.Strings(values);
                RuntimeJson.Require(members.Length > 0 && members.Distinct(StringComparer.Ordinal).Count() == members.Length
                    && members.All(m => Regex.IsMatch(m, @"^[a-z][a-z0-9_-]*$", RegexOptions.CultureInvariant)), "enum-values", id);
                if (set != null)
                {
                    var positions = members.Select(m => Array.IndexOf(EnumSets[set], m)).ToArray();
                    RuntimeJson.Require(positions.All(p => p >= 0) && positions.Zip(positions.Skip(1)).All(p => p.First < p.Second)
                        && positions.Length < EnumSets[set].Length, "enum-values", id);
                }
            }
        }
        if (parameter.TryGetProperty("unit", out var unit))
        { RuntimeJson.Text(unit); RuntimeJson.Require(type is "integer" or "number" or "vector3", "parameter-unit", id); }
        // A promoted value parameter becomes an input port with the same id.
        if (role == "value") RuntimeJson.Require(!inputs.Any(p => RuntimeJson.Text(p, "id") == name), "parameter-collision", id);
    }
    private static (long Minimum, long Maximum) CountParameter(JsonElement graph, string name, string id)
    {
        var count = RuntimeJson.Rows(graph, "parameters").FirstOrDefault(p => RuntimeJson.Text(p, "id") == name);
        RuntimeJson.Require(count.ValueKind == JsonValueKind.Object && RuntimeJson.Text(count, "type") == "integer"
            && RuntimeJson.Text(count, "role") == "structural" && !RuntimeJson.Flag(count, "required"), "variadic-count-parameter", id);
        RuntimeJson.Require(count.TryGetProperty("minimum", out _) && count.TryGetProperty("maximum", out _), "variadic-count-bounds", id);
        var lower = RuntimeJson.Integer(count.GetProperty("minimum"), 2, MaximumVariadicPorts - 1);
        return (lower, RuntimeJson.Integer(count.GetProperty("maximum"), lower + 1, MaximumVariadicPorts));
    }
    private static void CheckTemplate(JsonElement template, string executionAuthority, string id)
    {
        ValidatePort(template, id);
        RuntimeJson.Require(!RuntimeJson.Flag(template, "optional") && !RuntimeJson.Flag(template, "nullable"), "variadic-template", id);
        RuntimeJson.Require(executionAuthority != "pure" || RuntimeJson.Text(template, "type") != "execution", "pure-execution", id);
    }
    private static void CheckAdded(HashSet<string> ids, string added, string id)
    {
        RuntimeJson.Require(added.Length <= 256, "variadic-port-name-budget", id);
        RuntimeJson.Require(!ids.Contains(added), "variadic-port-conflict", id);
    }
    private static string PortId(string template, long index) => template + "_" + index.ToString(CultureInfo.InvariantCulture);
    private static void ValidateVariadic(JsonElement graph, string id)
    {
        if (!graph.TryGetProperty("variadic", out var spec)) return;
        RuntimeJson.Shape(spec, "side parameter port");
        var side = RuntimeJson.Text(spec, "side");
        RuntimeJson.Require(side is "inputs" or "outputs", "variadic-side", id);
        var template = spec.GetProperty("port"); CheckTemplate(template, RuntimeJson.Text(graph, "execution"), id);
        var (lower, upper) = CountParameter(graph, RuntimeJson.Text(spec, "parameter"), id);
        var ports = RuntimeJson.Rows(graph, side);
        RuntimeJson.Require(ports.Length == lower, "variadic-base-count", id);
        foreach (var port in ports)
            RuntimeJson.Require(SameValue(port, template) && !RuntimeJson.Flag(port, "nullable") && !RuntimeJson.Flag(port, "optional"), "variadic-port-contract", id);
        var ids = ports.Select(p => RuntimeJson.Text(p, "id")).ToHashSet(StringComparer.Ordinal);
        for (var i = lower + 1; i <= upper; i++) CheckAdded(ids, PortId(RuntimeJson.Text(template, "id"), i), id);
    }
    /// <summary>A typed tuple repeated as a block; the whole block exists at minimum and is declared, not implied.</summary>
    private static void ValidatePortGroups(JsonElement graph, string id)
    {
        if (!graph.TryGetProperty("portGroups", out _)) return;
        var groups = RuntimeJson.Rows(graph, "portGroups", 2);
        RuntimeJson.Require(groups.Length > 0, "port-groups", id);
        var execution = RuntimeJson.Text(graph, "execution");
        var variadicSide = graph.TryGetProperty("variadic", out var variadic) ? RuntimeJson.Text(variadic, "side") : null;
        var sides = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in groups)
        {
            RuntimeJson.Shape(spec, "id side parameter minimum maximum slots");
            RuntimeJson.Require(IsName(RuntimeJson.Text(spec, "id")) && names.Add(RuntimeJson.Text(spec, "id")), "port-group-name", id);
            var side = RuntimeJson.Text(spec, "side");
            RuntimeJson.Require(side is "inputs" or "outputs", "variadic-side", id);
            RuntimeJson.Require(side != variadicSide && sides.Add(side), "port-group-side", id);
            var slots = RuntimeJson.Rows(spec, "slots", 8);
            RuntimeJson.Require(slots.Length > 0 && slots.Select(s => RuntimeJson.Text(s, "id")).Distinct(StringComparer.Ordinal).Count() == slots.Length, "port-group-slots", id);
            foreach (var slot in slots) CheckTemplate(slot, execution, id);
            var (lower, upper) = CountParameter(graph, RuntimeJson.Text(spec, "parameter"), id);
            RuntimeJson.Require(RuntimeJson.Integer(spec.GetProperty("minimum"), 0, MaximumVariadicPorts) == lower
                && RuntimeJson.Integer(spec.GetProperty("maximum"), 0, MaximumVariadicPorts) == upper, "variadic-count-bounds", id);
            var ports = RuntimeJson.Rows(graph, side);
            var start = Array.FindIndex(ports, p => RuntimeJson.Text(p, "id") == PortId(RuntimeJson.Text(slots[0], "id"), 1));
            RuntimeJson.Require(start >= 0 && start + lower * slots.Length <= ports.Length, "port-group-base", id);
            for (var i = 1; i <= lower; i++)
                for (var j = 0; j < slots.Length; j++)
                {
                    var port = ports[start + (i - 1) * slots.Length + j];
                    RuntimeJson.Require(RuntimeJson.Text(port, "id") == PortId(RuntimeJson.Text(slots[j], "id"), i), "port-group-base", id);
                    RuntimeJson.Require(SameValue(port, slots[j]) && !RuntimeJson.Flag(port, "optional") && !RuntimeJson.Flag(port, "nullable"), "variadic-port-contract", id);
                }
            var ids = ports.Select(p => RuntimeJson.Text(p, "id")).ToHashSet(StringComparer.Ordinal);
            for (var i = lower + 1; i <= upper; i++)
                foreach (var slot in slots) CheckAdded(ids, PortId(RuntimeJson.Text(slot, "id"), i), id);
        }
    }

    /// <summary>Expands variable ports and port groups, then appends promoted value parameters as inputs; metadata and port order are preserved.</summary>
    internal static JsonElement Resolve(JsonElement graph, JsonElement parameters, IReadOnlySet<string>? promoted = null)
    {
        var resolved = graph.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        long Size(string name, long minimum)
        {
            var definition = RuntimeJson.Rows(graph, "parameters").Single(p => RuntimeJson.Text(p, "id") == name);
            return parameters.TryGetProperty(name, out var value)
                ? RuntimeJson.Integer(value, minimum, RuntimeJson.Integer(definition.GetProperty("maximum"))) : minimum;
        }
        JsonElement Port(JsonElement template, long index)
        {
            var fields = template.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            fields["id"] = RuntimeJson.From(PortId(RuntimeJson.Text(template, "id"), index));
            return RuntimeJson.From(fields);
        }
        if (graph.TryGetProperty("variadic", out var spec))
        {
            var side = RuntimeJson.Text(spec, "side"); var ports = RuntimeJson.Rows(graph, side).ToList();
            var count = Size(RuntimeJson.Text(spec, "parameter"), ports.Count);
            for (long i = ports.Count + 1; i <= count; i++) ports.Add(Port(spec.GetProperty("port"), i));
            resolved[side] = RuntimeJson.From(ports);
        }
        if (graph.TryGetProperty("portGroups", out var groups))
            foreach (var group in groups.EnumerateArray())
            {
                var side = RuntimeJson.Text(group, "side"); var slots = RuntimeJson.Rows(group, "slots");
                var lower = RuntimeJson.Integer(group.GetProperty("minimum"));
                var count = Size(RuntimeJson.Text(group, "parameter"), lower);
                var ports = resolved[side].EnumerateArray().ToList();
                var end = ports.FindIndex(p => RuntimeJson.Text(p, "id") == PortId(RuntimeJson.Text(slots[0], "id"), 1)) + (int)lower * slots.Length;
                var added = new List<JsonElement>();
                for (var i = lower + 1; i <= count; i++) added.AddRange(slots.Select(slot => Port(slot, i)));
                ports.InsertRange(end, added);
                resolved[side] = RuntimeJson.From(ports);
            }
        if (promoted is { Count: > 0 })
        {
            var definitions = RuntimeJson.Rows(graph, "parameters");
            // Declaration order, not authoring order: the compiled frame must not depend on click order.
            var moved = definitions.Where(p => promoted.Contains(RuntimeJson.Text(p, "id"))).ToArray();
            var names = string.Join(" ", moved.Select(p => RuntimeJson.Text(p, "id")));
            RuntimeJson.Require(moved.Length == promoted.Count && moved.All(p => RuntimeJson.Text(p, "role") == "value"), "promotion-role", names);
            var inputs = resolved["inputs"].EnumerateArray().Concat(moved.Select(PromotedPort)).ToArray();
            RuntimeJson.Require(inputs.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == inputs.Length, "promotion-collision", names);
            resolved["inputs"] = RuntimeJson.From(inputs);
            resolved["parameters"] = RuntimeJson.From(definitions.Where(p => !promoted.Contains(RuntimeJson.Text(p, "id"))).ToArray());
        }
        return RuntimeJson.From(resolved);
    }
    /// <summary>A literal moved onto an input keeps its value contract; bounds and members are rechecked per dispatch.</summary>
    private static JsonElement PromotedPort(JsonElement parameter)
    {
        var type = RuntimeJson.Text(parameter, "type");
        var port = new Dictionary<string, JsonElement>(StringComparer.Ordinal) {
            ["id"] = parameter.GetProperty("id").Clone(), ["type"] = RuntimeJson.From(type == "recipient-policy" ? "policy" : type)
        };
        if (type == "recipient-policy") port["schema"] = RuntimeJson.From("forge.policy.recipient");
        else if (Optional(parameter, "set") is { } set) port["schema"] = RuntimeJson.From(set);
        if (parameter.TryGetProperty("unit", out var unit)) port["unit"] = unit.Clone();
        if (!RuntimeJson.Flag(parameter, "required")) port["optional"] = RuntimeJson.From(true);
        return RuntimeJson.From(port);
    }
    /// <summary>Dense slot frame of one resolved side, identical to the website's compiledNodeLayout.</summary>
    internal static JsonElement Layout(JsonElement resolved, string side)
        => RuntimeJson.From(RuntimeJson.Rows(resolved, side).Select((port, index) => new {
            index, type = Array.IndexOf(PortTypes, RuntimeJson.Text(port, "type")),
            cardinality = Array.IndexOf(Cardinalities, Cardinality(port)), valueSet = ValueSet(port),
            lifetime = Optional(port, "lifetime") is { } lifetime ? Array.IndexOf(HandleLifetimes, lifetime) : -1,
            optional = RuntimeJson.Flag(port, "optional"), nullable = RuntimeJson.Flag(port, "nullable")
        }).ToArray());
    private static int ValueSet(JsonElement port) => RuntimeJson.Text(port, "type") switch {
        "enum" => Array.FindIndex(EnumSetTable, set => set.Name == Optional(port, "schema")),
        "resource" => Array.IndexOf(ResourceKinds, Optional(port, "resourceKind")),
        "handle" => Array.IndexOf(HandleKinds, Optional(port, "handleKind")),
        _ => -1
    };
}
