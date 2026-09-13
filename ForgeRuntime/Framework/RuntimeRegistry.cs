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
    internal readonly Dictionary<string, (string Owner, Func<EntityReference, RuntimeEntitySnapshot?> Observe)> EntityObservers = new(StringComparer.Ordinal);

    internal RuntimeRegistry() { }
    private RuntimeRegistry(RuntimeRegistry source)
    {
        foreach (var x in source.Providers) Providers.Add(x.Key, x.Value);
        foreach (var x in source.Capabilities) Capabilities.Add(x.Key, x.Value);
        foreach (var x in source.Bindings) Bindings.Add(x.Key, x.Value);
        foreach (var x in source.Handlers) Handlers.Add(x.Key, x.Value);
        foreach (var x in source.Support) Support.Add(x.Key, x.Value);
        foreach (var x in source.Resolvers) Resolvers.Add(x.Key, x.Value);
        foreach (var x in source.EntityObservers) EntityObservers.Add(x.Key, x.Value);
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
        if (module.EntityObservers != null)
        {
            RuntimeJson.Require(module.EntityObservers.Count <= RuntimeKernel.MaximumEntityObservers,
                "entity-observer-budget", "Entity observer budget exceeded.");
            foreach (var item in module.EntityObservers)
            {
                RuntimeJson.Require(RuntimeJson.IsId(item.Key) && item.Value != null,
                    "entity-observer", "Invalid entity observer.");
                RuntimeJson.Require(next.Resolvers.TryGetValue(item.Key, out var resolver) && resolver.Owner == providerId,
                    "entity-observer-owner", "An observer requires this provider's resolver.");
                RuntimeJson.Require(next.EntityObservers.TryAdd(item.Key, (providerId, item.Value!)),
                    "entity-observer-conflict", item.Key);
            }
            RuntimeJson.Require(next.EntityObservers.Count <= RuntimeKernel.MaximumEntityObservers,
                "entity-observer-budget", "Entity observer budget exceeded.");
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
            if (c.TryGetProperty("graph", out var graph)) RuntimeGraphContracts.ValidateCapability(RuntimeJson.Text(c, "kind"), graph, id);
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
