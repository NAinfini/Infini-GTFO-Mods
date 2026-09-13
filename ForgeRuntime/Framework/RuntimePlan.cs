using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

/// <summary>One wired action input. The plan names both ends by frame slot; names are resolved once at load.</summary>
internal sealed record StepInput(string Name, string EventPort, JsonElement Port);
internal sealed record ResolvedStep(string NodeId, string BindingId, JsonElement Parameters, IReadOnlyList<StepInput> Inputs);
internal sealed record ResolvedEntry(string NodeId, string BindingId, IReadOnlyList<ResolvedStep> Steps);
internal sealed record ResolvedPlan(string Id, string ResourceId, string ResourceRevision, string Domain, RuntimeLimits Limits,
    IReadOnlyList<ResolvedEntry> Entries, IReadOnlySet<string> Bindings, string Fingerprint);

/// <summary>
/// schemaVersion 2 plan (Runtime API 2.0.0): positional binding pins, pre-resolved slot layouts,
/// positional constants and {slot, fromEventSlot} inputs. Every layout is re-derived from the
/// registered contract and must match exactly; nothing in the file is trusted.
/// </summary>
internal static class RuntimePlan
{
    private sealed record Node(string Id, string BindingId, JsonElement Parameters, JsonElement Contract);

    internal static ResolvedPlan Parse(string json, RuntimeIdentity identity, RuntimeLimits ceiling, RuntimeRegistry registry, IEnumerable<string> grantedPermissions)
    {
        var plan = RuntimeJson.Parse(json);
        RuntimeJson.Shape(plan, "schemaVersion kind planId resource runtime domain authority failurePolicy permissions dependencies limits bindings entrypoints");
        RuntimeJson.Require(RuntimeJson.Integer(plan.GetProperty("schemaVersion")) == 2 && RuntimeJson.Text(plan, "kind") == "forge-runtime-plan", "plan-version", "Unsupported plan version.");
        RuntimeJson.Require(RuntimeJson.StableText(plan.GetProperty("runtime")) == RuntimeJson.StableText(RuntimeJson.From(identity)), "runtime-lock", "Runtime/API/game build lock mismatch.");
        RuntimeJson.Require(RuntimeJson.Text(plan, "authority") == "host" && RuntimeJson.Text(plan, "failurePolicy") == "stop-entrypoint", "execution-policy", "Plans require host and stop-entrypoint.");
        var id = RuntimeJson.Text(plan, "planId"); var resource = plan.GetProperty("resource");
        RuntimeJson.Shape(resource, "id revision"); var resourceId = RuntimeJson.Text(resource, "id"); var revision = RuntimeJson.Text(resource, "revision");
        var domain = RuntimeJson.Text(plan, "domain");
        RuntimeJson.Require(RuntimeGraphContracts.Domains.Contains(domain), "plan-domain", domain);
        var budget = plan.GetProperty("limits");
        RuntimeJson.Shape(budget, "maxEventsPerTick maxCommandsPerTick maxQueuedEvents maxCausalDepth");
        var limits = ceiling with {
            MaxEventsPerTick = (int)RuntimeJson.Integer(budget.GetProperty("maxEventsPerTick"), 1, ceiling.MaxEventsPerTick),
            MaxCommandsPerTick = (int)RuntimeJson.Integer(budget.GetProperty("maxCommandsPerTick"), 1, ceiling.MaxCommandsPerTick),
            MaxQueuedEvents = (int)RuntimeJson.Integer(budget.GetProperty("maxQueuedEvents"), 1, ceiling.MaxQueuedEvents),
            MaxCausalDepth = (int)RuntimeJson.Integer(budget.GetProperty("maxCausalDepth"), 1, ceiling.MaxCausalDepth)
        };
        // The pin table is positional: nodes name their binding by index, never by string.
        var pins = new List<string>();
        foreach (var pin in RuntimeJson.Rows(plan, "bindings", 2048))
        {
            RuntimeJson.Shape(pin, "bindingId capabilityId capabilityVersion providerId providerVersion handler");
            var bindingId = RuntimeJson.Text(pin, "bindingId");
            RuntimeJson.Require(!pins.Contains(bindingId), "duplicate-binding-pin", bindingId);
            RuntimeJson.Require(registry.Bindings.TryGetValue(bindingId, out var binding) && RuntimeJson.Text(binding, "status") == "implemented", "binding-unavailable", bindingId);
            var capabilityId = RuntimeJson.Text(binding, "capabilityId"); var providerId = RuntimeJson.Text(binding, "providerId");
            RuntimeJson.Require(RuntimeJson.Text(pin, "capabilityId") == capabilityId && RuntimeJson.Text(pin, "providerId") == providerId
                && RuntimeJson.Text(pin, "handler") == RuntimeJson.Text(binding, "handler")
                && RuntimeJson.Text(pin, "capabilityVersion") == RuntimeJson.Text(registry.Capabilities[capabilityId], "version")
                && RuntimeJson.Text(pin, "providerVersion") == RuntimeJson.Text(registry.Providers[providerId], "version"), "binding-lock", bindingId);
            pins.Add(bindingId);
        }
        RuntimeJson.Require(pins.SequenceEqual(pins.OrderBy(x => x, StringComparer.Ordinal)), "binding-order", "Binding locks must be ordinal sorted.");
        foreach (var key in new[] { "permissions", "dependencies" }) {
            var values = RuntimeJson.Strings(plan.GetProperty(key));
            RuntimeJson.Require(values.SequenceEqual(values.OrderBy(x => x, StringComparer.Ordinal)), "plan-order", key);
        }
        var entryRows = RuntimeJson.Rows(plan, "entrypoints", ceiling.MaxEntrypoints);
        var entryIds = entryRows.Select(e => RuntimeJson.Text(e, "nodeId")).ToArray();
        RuntimeJson.Require(entryIds.SequenceEqual(entryIds.OrderBy(x => x, StringComparer.Ordinal)), "entrypoint-order", "Entrypoints must be ordinal sorted.");
        var used = new HashSet<string>(StringComparer.Ordinal); var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        int Slot(JsonElement value, int count, string code, string detail)
        {
            RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index >= 0 && index < count, code, detail);
            return value.GetInt32();
        }
        Node Load(JsonElement row, string kind)
        {
            var nodeId = RuntimeJson.Text(row, "nodeId");
            RuntimeJson.Require(Regex.IsMatch(nodeId, @"^[A-Za-z][A-Za-z0-9_-]{0,127}$"), "node-id", nodeId);
            RuntimeJson.Require(nodeIds.Add(nodeId), "duplicate-node", nodeId);
            var bindingId = pins[Slot(row.GetProperty("binding"), pins.Count, "binding-index", nodeId)]; used.Add(bindingId);
            var binding = registry.Bindings[bindingId]; var capability = registry.Capabilities[RuntimeJson.Text(binding, "capabilityId")];
            RuntimeJson.Require(RuntimeJson.Text(capability, "kind") == kind && capability.TryGetProperty("graph", out _), "node-kind", nodeId);
            var graph = capability.GetProperty("graph");
            RuntimeJson.Require(RuntimeJson.Text(graph, "execution") == "host" && RuntimeJson.Strings(graph.GetProperty("domains")).Contains(domain, StringComparer.Ordinal), "node-domain-authority", nodeId);
            var definitions = RuntimeJson.Rows(graph, "parameters");
            RuntimeJson.Require(definitions.All(p => RuntimeJson.Text(p, "type") != "recipient-policy"), "unsupported-parameter", nodeId);
            var layout = row.GetProperty("layout");
            RuntimeJson.Shape(layout, "inputs outputs constants");
            var constants = RuntimeJson.Rows(layout, "constants");
            RuntimeJson.Require(constants.Length == definitions.Length, "constant-frame", nodeId);
            // Positional constants rebuild the keyed bag; null is the compiled spelling of "not authored".
            var keyed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            for (var i = 0; i < definitions.Length; i++)
            {
                if (constants[i].ValueKind != JsonValueKind.Null) { keyed.Add(RuntimeJson.Text(definitions[i], "id"), constants[i]); continue; }
                RuntimeJson.Require(!RuntimeJson.Flag(definitions[i], "required"), "missing-constant", nodeId + "." + RuntimeJson.Text(definitions[i], "id"));
            }
            var parameters = RuntimeJson.From(keyed);
            RuntimeJson.Parameters(parameters, capability);
            var contract = RuntimeGraphContracts.Resolve(graph, parameters);
            foreach (var side in new[] { "inputs", "outputs" })
                RuntimeJson.Require(RuntimeJson.StableText(layout.GetProperty(side)) == RuntimeJson.StableText(RuntimeGraphContracts.Layout(contract, side)), "layout-mismatch", nodeId + "." + side);
            var inputs = RuntimeJson.Rows(contract, "inputs"); var outputs = RuntimeJson.Rows(contract, "outputs");
            var inputExecution = inputs.Where(p => RuntimeJson.Text(p, "type") == "execution").ToArray();
            var outputExecution = outputs.Where(p => RuntimeJson.Text(p, "type") == "execution").ToArray();
            if (kind == "trigger")
                RuntimeJson.Require(inputs.Length == 0 && definitions.Length == 0 && outputExecution.Length == 1, "trigger-shape", nodeId);
            else
            {
                RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "execute" && registry.Handlers.ContainsKey(bindingId), "action-handler", nodeId);
                RuntimeJson.Require(inputExecution.Length == 1 && !RuntimeJson.Flag(inputExecution[0], "optional") && outputExecution.Length <= 1, "action-shape", nodeId);
            }
            foreach (var port in (kind == "trigger" ? outputs : inputs).Where(p => RuntimeJson.Text(p, "type") != "execution"))
            {
                var type = RuntimeJson.Text(port, "type");
                RuntimeJson.Require(RuntimeGraphContracts.RuntimeValueTypes.Contains(type) && (!RuntimeGraphContracts.Many(port) || type == "entity"),
                    kind == "trigger" ? "unsupported-event-port" : "unsupported-input-port", nodeId + "." + RuntimeJson.Text(port, "id"));
            }
            return new Node(nodeId, bindingId, parameters, contract);
        }

        var entries = new List<ResolvedEntry>(); var total = 0;
        foreach (var entry in entryRows)
        {
            RuntimeJson.Shape(entry, "nodeId binding layout steps");
            var trigger = Load(entry, "trigger");
            var eventPorts = RuntimeJson.Rows(trigger.Contract, "outputs");
            var steps = new List<ResolvedStep>(); var continuation = true;
            foreach (var step in RuntimeJson.Rows(entry, "steps", ceiling.MaxStepsPerEntrypoint))
            {
                RuntimeJson.Require(continuation, "missing-execution-output", trigger.Id);
                RuntimeJson.Shape(step, "nodeId binding layout inputs");
                var node = Load(step, "action");
                continuation = RuntimeJson.Rows(node.Contract, "outputs").Any(p => RuntimeJson.Text(p, "type") == "execution");
                var targets = RuntimeJson.Rows(node.Contract, "inputs");
                var inputs = new List<StepInput>(); var previous = -1;
                foreach (var source in RuntimeJson.Rows(step, "inputs", targets.Length))
                {
                    RuntimeJson.Shape(source, "slot fromEventSlot");
                    var slot = Slot(source.GetProperty("slot"), targets.Length, "input-slot", node.Id);
                    var eventSlot = Slot(source.GetProperty("fromEventSlot"), eventPorts.Length, "event-port-missing", node.Id);
                    // Sorted and strictly increasing: one driver per input, one canonical spelling per plan.
                    RuntimeJson.Require(slot > previous, "input-order", node.Id); previous = slot;
                    var target = targets[slot]; var origin = eventPorts[eventSlot]; var name = RuntimeJson.Text(target, "id");
                    RuntimeJson.Require(RuntimeJson.Text(target, "type") != "execution" && RuntimeJson.Text(origin, "type") != "execution", "execution-slot", node.Id + "." + name);
                    RuntimeJson.Require(RuntimeGraphContracts.SameValue(origin, target), "port-mismatch", node.Id + "." + name);
                    RuntimeJson.Require(!RuntimeJson.Flag(origin, "nullable") || RuntimeJson.Flag(target, "nullable"), "nullable-port", node.Id + "." + name);
                    RuntimeJson.Require(!RuntimeJson.Flag(origin, "optional") || RuntimeJson.Flag(target, "optional"), "optional-event-port", node.Id + "." + name);
                    inputs.Add(new StepInput(name, RuntimeJson.Text(origin, "id"), target));
                }
                foreach (var target in targets.Where(p => RuntimeJson.Text(p, "type") != "execution" && !RuntimeJson.Flag(p, "optional")))
                    RuntimeJson.Require(inputs.Any(i => i.Name == RuntimeJson.Text(target, "id")), "missing-input", node.Id + "." + RuntimeJson.Text(target, "id"));
                steps.Add(new ResolvedStep(node.Id, node.BindingId, node.Parameters, inputs));
            }
            RuntimeJson.Require(steps.Count > 0, "empty-entrypoint", trigger.Id); total += steps.Count;
            entries.Add(new ResolvedEntry(trigger.Id, trigger.BindingId, steps));
        }
        RuntimeJson.Require(entries.Count > 0 && total <= ceiling.MaxTotalSteps, "plan-step-budget", id);
        foreach (var group in entries.GroupBy(e => e.BindingId)) RuntimeJson.Require(group.Sum(e => e.Steps.Count) <= limits.MaxCommandsPerTick, "event-command-budget", id);
        var closure = registry.Closure(used);
        RuntimeJson.ExactSet(pins, closure, "binding-closure");
        RuntimeJson.ExactSet(RuntimeJson.Strings(plan.GetProperty("dependencies")), registry.Packages(closure), "dependency-lock");
        var permissions = RuntimeJson.Strings(plan.GetProperty("permissions"));
        RuntimeJson.ExactSet(permissions, closure.SelectMany(b => registry.Support[b].RequiredPermissions), "permission-lock");
        var granted = new HashSet<string>(grantedPermissions, StringComparer.Ordinal);
        RuntimeJson.Require(permissions.All(granted.Contains), "permission-denied", "The host has not granted the required permissions.");
        return new ResolvedPlan(id, resourceId, revision, domain, limits, entries, closure, RuntimeJson.StableText(plan));
    }
}
