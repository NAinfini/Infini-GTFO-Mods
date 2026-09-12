using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

internal sealed class RuntimeRegistry
{
    internal readonly Dictionary<string, JsonElement> Providers = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, JsonElement> Capabilities = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, JsonElement> Bindings = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, CommandHandler> Handlers = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, BindingSupport> Support = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, (string Owner, Func<EntityReference, bool> Resolve)> Resolvers = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, string> CapabilityRegistrants = new(StringComparer.Ordinal);

    internal RuntimeRegistry() { }
    private RuntimeRegistry(RuntimeRegistry source)
    {
        foreach (var x in source.Providers) Providers.Add(x.Key, x.Value);
        foreach (var x in source.Capabilities) Capabilities.Add(x.Key, x.Value);
        foreach (var x in source.Bindings) Bindings.Add(x.Key, x.Value);
        foreach (var x in source.Handlers) Handlers.Add(x.Key, x.Value);
        foreach (var x in source.Support) Support.Add(x.Key, x.Value);
        foreach (var x in source.Resolvers) Resolvers.Add(x.Key, x.Value);
        foreach (var x in source.CapabilityRegistrants) CapabilityRegistrants.Add(x.Key, x.Value);
    }
    internal RuntimeRegistry WithModule(RuntimeModule module, string apiVersion, out string providerId)
    {
        RuntimeJson.Require(module.ApiVersion == apiVersion, "api-version", "Module API version does not match runtime.");
        var seed = RuntimeJson.Parse(module.RegistryJson);
        RuntimeJson.Shape(seed, "providers capabilities bindings");
        var providers = RuntimeJson.Rows(seed, "providers");
        RuntimeJson.Require(providers.Length == 1, "module-provider", "Each module must own exactly one provider.");
        providerId = RuntimeJson.Id(providers[0], "id");
        var next = new RuntimeRegistry(this);
        Add(next.Providers, providers, "provider-conflict");
        var capabilities = RuntimeJson.Rows(seed, "capabilities");
        foreach (var capability in capabilities)
        {
            RuntimeJson.Require(RuntimeJson.Text(capability, "owner") == providerId, "capability-owner", "A module can only declare its own capabilities; reference existing canonical IDs instead.");
            RuntimeJson.Require(next.CapabilityRegistrants.TryAdd(RuntimeJson.Id(capability, "id"), providerId), "capability-conflict", RuntimeJson.Text(capability, "id"));
        }
        Add(next.Capabilities, capabilities, "capability-conflict");
        var bindings = RuntimeJson.Rows(seed, "bindings");
        foreach (var binding in bindings)
            RuntimeJson.Require(RuntimeJson.Text(binding, "providerId") == providerId, "binding-owner", "A module can only register its own bindings.");
        Add(next.Bindings, bindings, "binding-conflict");
        var suppliedHandlers = new Dictionary<string, CommandHandler>(module.Handlers, StringComparer.Ordinal);
        var usedHandlers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            var id = RuntimeJson.Text(binding, "id");
            RuntimeJson.Require(RuntimeJson.Text(binding, "status") == "implemented", "binding-unimplemented", id);
            if (RuntimeJson.Text(binding, "status") != "implemented" || RuntimeJson.Text(binding, "role") != "execute") continue;
            RuntimeJson.Require(next.Capabilities.TryGetValue(RuntimeJson.Text(binding, "capabilityId"), out var capability), "missing-capability", id);
            if (RuntimeJson.Text(capability, "kind") != "action") continue;
            var handlerName = RuntimeJson.Text(binding, "handler");
            RuntimeJson.Require(suppliedHandlers.TryGetValue(handlerName, out var handler) && handler != null, "missing-handler", id);
            next.Handlers.Add(id, handler!); usedHandlers.Add(handlerName);
        }
        RuntimeJson.ExactSet(suppliedHandlers.Keys, usedHandlers, "unused-handler");
        foreach (var row in module.BindingSupport)
        {
            RuntimeJson.Require(bindings.Any(b => RuntimeJson.Text(b, "id") == row.BindingId), "support-owner", row.BindingId);
            RuntimeJson.Require(row.Verification is "implementation-only" or "game-verified", "verification", row.BindingId);
            var permissions = row.RequiredPermissions.ToArray();
            RuntimeJson.Require(permissions.All(RuntimeJson.IsId) && permissions.Distinct(StringComparer.Ordinal).Count() == permissions.Length, "permissions", row.BindingId);
            RuntimeJson.Require(next.Support.TryAdd(row.BindingId, new BindingSupport(row.BindingId, row.Verification, Array.AsReadOnly(permissions))), "support-conflict", row.BindingId);
        }
        RuntimeJson.ExactSet(bindings.Where(b => RuntimeJson.Text(b, "status") == "implemented").Select(b => RuntimeJson.Text(b, "id")),
            module.BindingSupport.Select(s => s.BindingId), "binding-support-mismatch");
        if (module.EntityResolvers != null)
        {
            foreach (var resolver in module.EntityResolvers)
            {
                RuntimeJson.Require(RuntimeJson.IsId(resolver.Key) && resolver.Value != null, "entity-namespace", resolver.Key);
                RuntimeJson.Require(next.Resolvers.TryAdd(resolver.Key, (providerId, resolver.Value!)), "entity-namespace-conflict", resolver.Key);
            }
        }
        next.Validate();
        return next;
    }
    private static void Add(Dictionary<string, JsonElement> target, IEnumerable<JsonElement> rows, string code)
    {
        foreach (var row in rows)
        { var id = RuntimeJson.Id(row, "id"); RuntimeJson.Require(target.TryAdd(id, row.Clone()), code, id); }
    }
    private void Validate()
    {
        foreach (var p in Providers.Values)
        {
            RuntimeJson.Shape(p, "id kind version dependencies");
            var id = RuntimeJson.Id(p, "id"); var kind = RuntimeJson.Text(p, "kind");
            RuntimeJson.Require(kind is "native" or "adapter" or "extension", "provider-kind", id);
            RuntimeJson.Require(!id.StartsWith("forge.", StringComparison.Ordinal) || kind == "native", "reserved-provider", id);
            RuntimeJson.Version(p, "version"); ValidatePackages(RuntimeJson.Strings(p.GetProperty("dependencies")));
        }
        foreach (var c in Capabilities.Values)
        {
            RuntimeJson.Shape(c, "id owner kind label version parameters", "graph");
            var id = RuntimeJson.Id(c, "id"); var owner = RuntimeJson.Text(c, "owner");
            RuntimeJson.Require(Providers.TryGetValue(owner, out var provider), "capability-owner", id);
            RuntimeJson.Require(id.StartsWith("forge.", StringComparison.Ordinal)
                ? RuntimeJson.Text(provider, "kind") == "native" && owner.StartsWith("forge.", StringComparison.Ordinal)
                : id.StartsWith(owner + ".", StringComparison.Ordinal), "capability-namespace", id);
            RuntimeJson.Require(new[] { "trigger", "selector", "condition", "modifier", "control", "action", "variable", "state", "event", "component" }.Contains(RuntimeJson.Text(c, "kind")), "capability-kind", id);
            RuntimeJson.Text(c, "label"); RuntimeJson.Version(c, "version");
            RuntimeJson.Require(c.GetProperty("parameters").ValueKind == JsonValueKind.Object, "parameter-metadata", id);
            if (!c.TryGetProperty("graph", out var graph)) continue;
            RuntimeJson.Shape(graph, "domains execution inputs outputs parameters", "recipients");
            var domains = RuntimeJson.Strings(graph.GetProperty("domains"));
            RuntimeJson.Require(domains.Length > 0 && domains.All(x => new[] { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "session", "logic", "editor" }.Contains(x)), "graph-domain", id);
            RuntimeJson.Require(new[] { "pure", "host", "owner", "presentation" }.Contains(RuntimeJson.Text(graph, "execution")), "graph-authority", id);
            foreach (var direction in new[] { "inputs", "outputs" })
            {
                var ports = RuntimeJson.Rows(graph, direction);
                RuntimeJson.Require(ports.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == ports.Length, "duplicate-port", id);
                foreach (var port in ports)
                {
                    RuntimeJson.Shape(port, "id type", "schema unit nullable optional");
                    RuntimeJson.Require(Regex.IsMatch(RuntimeJson.Text(port, "id"), @"^[a-z][a-z0-9_]*$"), "port-name", id);
                    foreach (var key in new[] { "schema", "unit" }) if (port.TryGetProperty(key, out var text)) RuntimeJson.Text(text);
                    if (RuntimeJson.Text(port, "type") is "event" or "result" or "resource") RuntimeJson.Require(port.TryGetProperty("schema", out _), "port-schema", id);
                    RuntimeJson.Require(new[] { "execution", "boolean", "integer", "number", "string", "vector3", "entity", "entity-list", "resource", "event", "result", "timer", "reservation" }.Contains(RuntimeJson.Text(port, "type")), "port-type", id);
                    var type = RuntimeJson.Text(port, "type");
                    if (type == "execution") RuntimeJson.Require(!port.TryGetProperty("unit", out _) && !port.TryGetProperty("schema", out _) && !RuntimeJson.Flag(port, "nullable"), "execution-port", id);
                    if (port.TryGetProperty("unit", out _)) RuntimeJson.Require(type is "number" or "integer" or "vector3", "port-unit", id);
                    foreach (var flag in new[] { "nullable", "optional" }) if (port.TryGetProperty(flag, out var v)) RuntimeJson.Require(v.ValueKind is JsonValueKind.True or JsonValueKind.False, "port-flag", id);
                }
            }
            var parameters = RuntimeJson.Rows(graph, "parameters");
            RuntimeJson.Require(parameters.Select(p => RuntimeJson.Text(p, "id")).Distinct(StringComparer.Ordinal).Count() == parameters.Length, "duplicate-parameter", id);
            foreach (var parameter in parameters)
            {
                RuntimeJson.Shape(parameter, "id type required", "minimum maximum values");
                RuntimeJson.Require(Regex.IsMatch(RuntimeJson.Text(parameter, "id"), @"^[a-z][a-z0-9_]*$"), "parameter-name", id);
                RuntimeJson.Require(new[] { "boolean", "integer", "number", "string", "enum", "vector3", "recipient-policy" }.Contains(RuntimeJson.Text(parameter, "type")), "parameter-type", id);
                if (RuntimeJson.Text(parameter, "type") == "enum") RuntimeJson.Require(RuntimeJson.Strings(parameter.GetProperty("values")).Length > 0, "enum-values", id);
                if (RuntimeJson.Text(parameter, "type") != "enum") RuntimeJson.Require(!parameter.TryGetProperty("values", out _), "parameter-values", id);
                foreach (var bound in new[] { "minimum", "maximum" }) if (parameter.TryGetProperty(bound, out var value)) {
                    RuntimeJson.Require(RuntimeJson.Text(parameter, "type") is "number" or "integer", "parameter-bound-type", id);
                    RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number), "parameter-bound", id);
                    if (RuntimeJson.Text(parameter, "type") == "integer") RuntimeJson.Integer(value, -RuntimeJson.MaxSafeInteger);
                }
                if (parameter.TryGetProperty("minimum", out var minimum) && parameter.TryGetProperty("maximum", out var maximum)) RuntimeJson.Require(minimum.GetDouble() <= maximum.GetDouble(), "parameter-bounds", id);
                RuntimeJson.Require(parameter.GetProperty("required").ValueKind is JsonValueKind.True or JsonValueKind.False, "parameter-required", id);
            }
            var kind = RuntimeJson.Text(c, "kind"); var execution = RuntimeJson.Text(graph, "execution");
            var inputPorts = RuntimeJson.Rows(graph, "inputs"); var outputPorts = RuntimeJson.Rows(graph, "outputs");
            if (execution == "pure") RuntimeJson.Require(!inputPorts.Concat(outputPorts).Any(p => RuntimeJson.Text(p, "type") == "execution"), "pure-execution", id);
            if (kind is "selector" or "condition" or "modifier") RuntimeJson.Require(execution == "pure", "query-authority", id);
            if (kind is "trigger" or "action" or "control") RuntimeJson.Require(execution != "pure", "executable-authority", id);
            if (kind == "trigger") RuntimeJson.Require(!inputPorts.Any(p => RuntimeJson.Text(p, "type") == "execution"), "trigger-input", id);
            if (kind == "action" && inputPorts.Any(p => RuntimeJson.Text(p, "type") is "entity" or "entity-list"))
                RuntimeJson.Require(graph.TryGetProperty("recipients", out _), "recipient-contract", id);
            if (graph.TryGetProperty("recipients", out var recipients))
            {
                RuntimeJson.Require(kind == "action", "recipient-owner", id);
                RuntimeJson.Shape(recipients, "input requires");
                var recipient = RuntimeJson.Text(recipients, "input");
                RuntimeJson.Require(inputPorts.Any(p => RuntimeJson.Text(p, "id") == recipient && RuntimeJson.Text(p, "type") is "entity" or "entity-list" && !RuntimeJson.Flag(p, "optional") && !RuntimeJson.Flag(p, "nullable")), "recipient-port", id);
                var requirements = RuntimeJson.Strings(recipients.GetProperty("requires"));
                RuntimeJson.Require(requirements.Length <= 128 && requirements.All(RuntimeJson.IsId), "recipient-requirements", id);
            }
        }
        foreach (var b in Bindings.Values)
        {
            RuntimeJson.Shape(b, "id capabilityId providerId handler role status dependencies requires");
            var id = RuntimeJson.Id(b, "id"); var providerId = RuntimeJson.Text(b, "providerId");
            RuntimeJson.Require(Providers.TryGetValue(providerId, out var provider) && id.StartsWith(providerId + ".", StringComparison.Ordinal), "binding-provider", id);
            RuntimeJson.Require(Capabilities.TryGetValue(RuntimeJson.Text(b, "capabilityId"), out var capability), "binding-capability", id);
            RuntimeJson.Require(RuntimeJson.Text(b, "status") is "planned" or "implemented", "binding-status", id);
            RuntimeJson.Require(RuntimeJson.Text(b, "role") is "execute" or "observe", "binding-role", id);
            RuntimeJson.Text(b, "handler"); ValidatePackages(RuntimeJson.Strings(b.GetProperty("dependencies")));
            foreach (var required in RuntimeJson.Strings(b.GetProperty("requires"))) RuntimeJson.Require(Bindings.ContainsKey(required), "required-binding", required);
            if (RuntimeJson.Text(capability, "kind") == "trigger" && RuntimeJson.Text(capability, "id").StartsWith("forge.", StringComparison.Ordinal) && RuntimeJson.Text(provider, "kind") != "native")
                RuntimeJson.Require(RuntimeJson.Text(b, "role") == "observe", "trigger-owner", id);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            RuntimeJson.Require(visiting.Add(id), "binding-cycle", id);
            foreach (var required in RuntimeJson.Strings(Bindings[id].GetProperty("requires"))) Visit(required);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in Bindings.Keys) Visit(id);
        foreach (var id in Bindings.Keys) Packages(Closure(new[] { id }));
    }
    internal static void ValidatePackages(IEnumerable<string> packages)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var match = Regex.Match(package, @"^([A-Za-z0-9_]+-[A-Za-z0-9_]+)-((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$");
            RuntimeJson.Require(match.Success, "package-version", package);
            RuntimeJson.Require(!versions.TryGetValue(match.Groups[1].Value, out var version) || version == match.Groups[2].Value, "package-conflict", package);
            versions[match.Groups[1].Value] = match.Groups[2].Value;
        }
    }
    internal HashSet<string> Closure(IEnumerable<string> selected)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void AddBinding(string id)
        {
            RuntimeJson.Require(Bindings.TryGetValue(id, out var binding) && RuntimeJson.Text(binding, "status") == "implemented", "binding-unavailable", id);
            if (!result.Add(id)) return;
            foreach (var dependency in RuntimeJson.Strings(binding.GetProperty("requires"))) AddBinding(dependency);
        }
        foreach (var id in selected) AddBinding(id);
        return result;
    }
    internal string[] Packages(IEnumerable<string> bindings)
    {
        var packages = bindings.SelectMany(id => RuntimeJson.Strings(Bindings[id].GetProperty("dependencies"))
            .Concat(RuntimeJson.Strings(Providers[RuntimeJson.Text(Bindings[id], "providerId")].GetProperty("dependencies")))).Distinct(StringComparer.Ordinal).ToArray();
        ValidatePackages(packages); return packages;
    }
    internal object Snapshot() => new {
        providers = Providers.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray(),
        capabilities = Capabilities.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray(),
        bindings = Bindings.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray()
    };
}
