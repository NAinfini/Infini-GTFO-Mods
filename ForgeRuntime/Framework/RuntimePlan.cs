using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

internal sealed record ResolvedStep(string NodeId, string BindingId, JsonElement Parameters, IReadOnlyDictionary<string, string> Inputs, IReadOnlyList<JsonElement> Ports);
internal sealed record ResolvedEntry(string NodeId, string BindingId, IReadOnlyList<ResolvedStep> Steps);
internal sealed record ResolvedPlan(string Id, string ResourceId, string ResourceRevision, string Domain, RuntimeLimits Limits,
    IReadOnlyList<ResolvedEntry> Entries, IReadOnlySet<string> Bindings, string Fingerprint);

internal static class RuntimePlan
{
    internal static readonly string[] DataTypes = { "boolean", "integer", "number", "string", "vector3", "entity", "entity-list" };
    internal static ResolvedPlan Parse(string json, RuntimeIdentity identity, RuntimeLimits ceiling, RuntimeRegistry registry, IEnumerable<string> grantedPermissions)
    {
        var plan = RuntimeJson.Parse(json);
        RuntimeJson.Shape(plan, "schemaVersion kind planId resource runtime domain authority failurePolicy permissions dependencies limits bindings entrypoints");
        RuntimeJson.Require(RuntimeJson.Integer(plan.GetProperty("schemaVersion")) == 1 && RuntimeJson.Text(plan, "kind") == "forge-runtime-plan", "plan-version", "Unsupported plan version.");
        RuntimeJson.Require(RuntimeJson.StableText(plan.GetProperty("runtime")) == RuntimeJson.StableText(RuntimeJson.From(identity)), "runtime-lock", "Runtime/API/game build lock mismatch.");
        RuntimeJson.Require(RuntimeJson.Text(plan, "authority") == "host" && RuntimeJson.Text(plan, "failurePolicy") == "stop-entrypoint", "execution-policy", "v1 requires host and stop-entrypoint.");
        var id = RuntimeJson.Text(plan, "planId"); var resource = plan.GetProperty("resource");
        RuntimeJson.Shape(resource, "id revision"); var resourceId = RuntimeJson.Text(resource, "id"); var revision = RuntimeJson.Text(resource, "revision");
        var domain = RuntimeJson.Text(plan, "domain");
        var budget = plan.GetProperty("limits");
        RuntimeJson.Shape(budget, "maxEventsPerTick maxCommandsPerTick maxQueuedEvents maxCausalDepth");
        var limits = ceiling with {
            MaxEventsPerTick = (int)RuntimeJson.Integer(budget.GetProperty("maxEventsPerTick"), 1, ceiling.MaxEventsPerTick),
            MaxCommandsPerTick = (int)RuntimeJson.Integer(budget.GetProperty("maxCommandsPerTick"), 1, ceiling.MaxCommandsPerTick),
            MaxQueuedEvents = (int)RuntimeJson.Integer(budget.GetProperty("maxQueuedEvents"), 1, ceiling.MaxQueuedEvents),
            MaxCausalDepth = (int)RuntimeJson.Integer(budget.GetProperty("maxCausalDepth"), 1, ceiling.MaxCausalDepth)
        };
        var pins = RuntimeJson.Rows(plan, "bindings", 2048); var pinIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            RuntimeJson.Shape(pin, "bindingId capabilityId capabilityVersion providerId providerVersion handler");
            var bindingId = RuntimeJson.Text(pin, "bindingId");
            RuntimeJson.Require(pinIds.Add(bindingId), "duplicate-binding-pin", bindingId);
            RuntimeJson.Require(registry.Bindings.TryGetValue(bindingId, out var binding) && RuntimeJson.Text(binding, "status") == "implemented", "binding-unavailable", bindingId);
            var capabilityId = RuntimeJson.Text(binding, "capabilityId"); var providerId = RuntimeJson.Text(binding, "providerId");
            RuntimeJson.Require(RuntimeJson.Text(pin, "capabilityId") == capabilityId && RuntimeJson.Text(pin, "providerId") == providerId
                && RuntimeJson.Text(pin, "handler") == RuntimeJson.Text(binding, "handler")
                && RuntimeJson.Text(pin, "capabilityVersion") == RuntimeJson.Text(registry.Capabilities[capabilityId], "version")
                && RuntimeJson.Text(pin, "providerVersion") == RuntimeJson.Text(registry.Providers[providerId], "version"), "binding-lock", bindingId);
        }
        RuntimeJson.Require(pins.Select(p => RuntimeJson.Text(p, "bindingId")).SequenceEqual(pins.Select(p => RuntimeJson.Text(p, "bindingId")).OrderBy(x => x, StringComparer.Ordinal)), "binding-order", "Binding locks must be ordinal sorted.");
        foreach (var key in new[] { "permissions", "dependencies" }) {
            var values = RuntimeJson.Strings(plan.GetProperty(key));
            RuntimeJson.Require(values.SequenceEqual(values.OrderBy(x => x, StringComparer.Ordinal)), "plan-order", key);
        }
        var entryIds = RuntimeJson.Rows(plan, "entrypoints", ceiling.MaxEntrypoints).Select(e => RuntimeJson.Text(e, "nodeId")).ToArray();
        RuntimeJson.Require(entryIds.SequenceEqual(entryIds.OrderBy(x => x, StringComparer.Ordinal)), "entrypoint-order", "Entrypoints must be ordinal sorted.");
        var entries = new List<ResolvedEntry>(); var used = new HashSet<string>(StringComparer.Ordinal); var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        JsonElement Definition(string bindingId, string kind, JsonElement parameters, string nodeId)
        {
            RuntimeJson.Require(Regex.IsMatch(nodeId, @"^[A-Za-z][A-Za-z0-9_-]{0,127}$"), "node-id", nodeId);
            RuntimeJson.Require(nodeIds.Add(nodeId), "duplicate-node", nodeId);
            RuntimeJson.Require(pinIds.Contains(bindingId), "unlocked-binding", bindingId); used.Add(bindingId);
            var binding = registry.Bindings[bindingId]; var capability = registry.Capabilities[RuntimeJson.Text(binding, "capabilityId")];
            RuntimeJson.Require(RuntimeJson.Text(capability, "kind") == kind && capability.TryGetProperty("graph", out _), "node-kind", nodeId);
            var graph = capability.GetProperty("graph");
            RuntimeJson.Require(!graph.TryGetProperty("variadic", out _), "unsupported-variable-ports",
                "v1 cannot execute variable ports; an explicit graph lowering revision is required.");
            RuntimeJson.Require(RuntimeJson.Text(graph, "execution") == "host" && RuntimeJson.Strings(graph.GetProperty("domains")).Contains(domain, StringComparer.Ordinal), "node-domain-authority", nodeId);
            RuntimeJson.Parameters(parameters, capability);
            var inputExecution = RuntimeJson.Rows(graph, "inputs").Where(p => RuntimeJson.Text(p, "type") == "execution").ToArray();
            var outputExecution = RuntimeJson.Rows(graph, "outputs").Where(p => RuntimeJson.Text(p, "type") == "execution").ToArray();
            if (kind == "trigger")
            {
                RuntimeJson.Require(RuntimeJson.Rows(graph, "inputs").Length == 0 && RuntimeJson.Rows(graph, "parameters").Length == 0 && outputExecution.Length == 1, "trigger-shape", nodeId);
                foreach (var port in RuntimeJson.Rows(graph, "outputs").Where(p => RuntimeJson.Text(p, "type") != "execution"))
                    RuntimeJson.Require(DataTypes.Contains(RuntimeJson.Text(port, "type")), "unsupported-event-port", nodeId);
            }
            else
            {
                RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "execute" && registry.Handlers.ContainsKey(bindingId), "action-handler", nodeId);
                RuntimeJson.Require(inputExecution.Length == 1 && !RuntimeJson.Flag(inputExecution[0], "optional") && outputExecution.Length <= 1, "action-shape", nodeId);
            }
            return graph;
        }
        foreach (var entry in RuntimeJson.Rows(plan, "entrypoints", ceiling.MaxEntrypoints))
        {
            RuntimeJson.Shape(entry, "nodeId bindingId parameters steps");
            var entryNode = RuntimeJson.Text(entry, "nodeId"); var entryBinding = RuntimeJson.Text(entry, "bindingId");
            var triggerGraph = Definition(entryBinding, "trigger", entry.GetProperty("parameters"), entryNode);
            var outputs = RuntimeJson.Rows(triggerGraph, "outputs").Where(p => RuntimeJson.Text(p, "type") != "execution").ToDictionary(p => RuntimeJson.Text(p, "id"), StringComparer.Ordinal);
            var steps = new List<ResolvedStep>(); var continuation = true;
            foreach (var step in RuntimeJson.Rows(entry, "steps", ceiling.MaxStepsPerEntrypoint))
            {
                RuntimeJson.Require(continuation, "missing-execution-output", entryNode);
                RuntimeJson.Shape(step, "nodeId bindingId parameters inputs");
                var node = RuntimeJson.Text(step, "nodeId"); var bindingId = RuntimeJson.Text(step, "bindingId");
                var graph = Definition(bindingId, "action", step.GetProperty("parameters"), node);
                continuation = RuntimeJson.Rows(graph, "outputs").Any(p => RuntimeJson.Text(p, "type") == "execution");
                var dataInputs = RuntimeJson.Rows(graph, "inputs").Where(p => RuntimeJson.Text(p, "type") != "execution").ToArray();
                RuntimeJson.Shape(step.GetProperty("inputs"), string.Join(" ", dataInputs.Where(p => !RuntimeJson.Flag(p, "optional")).Select(p => RuntimeJson.Text(p, "id"))),
                    string.Join(" ", dataInputs.Where(p => RuntimeJson.Flag(p, "optional")).Select(p => RuntimeJson.Text(p, "id"))));
                var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var target in dataInputs)
                {
                    RuntimeJson.Require(DataTypes.Contains(RuntimeJson.Text(target, "type")), "unsupported-input-port", node);
                    var name = RuntimeJson.Text(target, "id"); if (!step.GetProperty("inputs").TryGetProperty(name, out var mapping)) continue;
                    RuntimeJson.Shape(mapping, "fromEventPort"); var sourceName = RuntimeJson.Text(mapping, "fromEventPort");
                    RuntimeJson.Require(outputs.TryGetValue(sourceName, out var source), "event-port-missing", sourceName);
                    foreach (var key in new[] { "type", "schema", "unit" })
                        RuntimeJson.Require(OptionalText(source, key) == OptionalText(target, key), "port-mismatch", node + "." + name);
                    RuntimeJson.Require(!RuntimeJson.Flag(source, "nullable") || RuntimeJson.Flag(target, "nullable"), "nullable-port", node);
                    RuntimeJson.Require(!RuntimeJson.Flag(source, "optional") || RuntimeJson.Flag(target, "optional"), "optional-event-port", node);
                    inputs.Add(name, sourceName);
                }
                steps.Add(new ResolvedStep(node, bindingId, step.GetProperty("parameters").Clone(), inputs, dataInputs));
            }
            RuntimeJson.Require(steps.Count > 0, "empty-entrypoint", entryNode); total += steps.Count;
            entries.Add(new ResolvedEntry(entryNode, entryBinding, steps));
        }
        RuntimeJson.Require(entries.Count > 0 && total <= ceiling.MaxTotalSteps, "plan-step-budget", id);
        foreach (var group in entries.GroupBy(e => e.BindingId)) RuntimeJson.Require(group.Sum(e => e.Steps.Count) <= limits.MaxCommandsPerTick, "event-command-budget", id);
        var closure = registry.Closure(used);
        RuntimeJson.ExactSet(pinIds, closure, "binding-closure");
        RuntimeJson.ExactSet(RuntimeJson.Strings(plan.GetProperty("dependencies")), registry.Packages(closure), "dependency-lock");
        var permissions = RuntimeJson.Strings(plan.GetProperty("permissions"));
        RuntimeJson.ExactSet(permissions, closure.SelectMany(b => registry.Support[b].RequiredPermissions), "permission-lock");
        var granted = new HashSet<string>(grantedPermissions, StringComparer.Ordinal);
        RuntimeJson.Require(permissions.All(granted.Contains), "permission-denied", "The host has not granted the required permissions.");
        return new ResolvedPlan(id, resourceId, revision, domain, limits, entries, closure, RuntimeJson.StableText(plan));
    }
    private static string? OptionalText(JsonElement value, string name) => value.TryGetProperty(name, out var field) ? field.GetString() : null;
}
