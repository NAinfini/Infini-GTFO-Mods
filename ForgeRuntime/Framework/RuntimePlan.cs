using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

/// <summary>One resolved action input, either wired to an event output by frame slot or a plan-authored literal
/// (J-003/D-006①). <see cref="Port"/> is the wire-side value contract used to validate the value at dispatch:
/// the origin event port when wired (so a one→many wrap validates the raw single value before wrapping), or the
/// literal's own value when authored as a constant. <see cref="Wrap"/> marks a non-nullable "one" output wired
/// into a "many" input (D-006②): dispatch wraps the validated value into a one-element collection.</summary>
internal sealed record StepInput(string Name, string? EventPort, JsonElement? Literal, bool Wrap, JsonElement Port);
/// <summary>Promoted names are parameters whose value arrives through an input; dispatch merges and revalidates them.</summary>
internal sealed record ResolvedStep(string NodeId, string BindingId, JsonElement Parameters, IReadOnlyList<StepInput> Inputs, IReadOnlySet<string> Promoted);
internal sealed record ResolvedEntry(string NodeId, string BindingId, IReadOnlyList<ResolvedStep> Steps);
internal sealed record ResolvedPlan(string Id, string ResourceId, string ResourceRevision, string Domain, RuntimeLimits Limits,
    IReadOnlyList<ResolvedEntry> Entries, IReadOnlySet<string> Bindings, string Fingerprint, IReadOnlyList<string> Permissions);
/// <summary>The identity a plan file resolves to before the rest of its content is validated: enough to group files by
/// planId for D-009 conflict detection, or to attach `plan` to a later rejection of the same file.</summary>
internal sealed record PlanIdentity(string Id, string ResourceId, string ResourceRevision);

/// <summary>
/// schemaVersion 2 plan (Runtime API 2.0.0): positional binding pins, pre-resolved slot layouts,
/// positional constants and {slot, fromEventSlot} inputs. Every layout is re-derived from the
/// registered contract and must match exactly; nothing in the file is trusted.
/// </summary>
internal static class RuntimePlan
{
    private sealed record Node(string Id, string BindingId, JsonElement Parameters, JsonElement Contract, IReadOnlySet<string> Promoted);

    /// <summary>The first slice of validation, shared by <see cref="Parse"/> and <see cref="PeekIdentity"/>: shape, version,
    /// runtime lock, authority/failure policy and the planId/resource identity. A file that fails here has no identity and
    /// (per D-009) is rejected on its own error without joining conflict-by-planId grouping.</summary>
    private static (JsonElement Plan, PlanIdentity Identity) Identify(string json, RuntimeIdentity identity)
    {
        var plan = RuntimeJson.Parse(json);
        RuntimeJson.Shape(plan, "schemaVersion kind planId resource runtime domain authority failurePolicy permissions dependencies limits bindings entrypoints");
        RuntimeJson.Require(RuntimeJson.Integer(plan.GetProperty("schemaVersion")) == 2 && RuntimeJson.Text(plan, "kind") == "forge-runtime-plan", "plan-version", "Unsupported plan version.");
        RuntimeJson.Require(RuntimeJson.StableText(plan.GetProperty("runtime")) == RuntimeJson.StableText(RuntimeJson.From(identity)), "runtime-lock", "Runtime/API/game build lock mismatch.");
        RuntimeJson.Require(RuntimeJson.Text(plan, "authority") == "host" && RuntimeJson.Text(plan, "failurePolicy") == "stop-entrypoint", "execution-policy", "Plans require host and stop-entrypoint.");
        var id = RuntimeJson.Text(plan, "planId"); var resource = plan.GetProperty("resource");
        RuntimeJson.Shape(resource, "id revision"); var resourceId = RuntimeJson.Text(resource, "id"); var revision = RuntimeJson.Text(resource, "revision");
        return (plan, new PlanIdentity(id, resourceId, revision));
    }

    /// <summary>D-009 first pass: resolve just enough identity to group a discovered file by planId, without validating the
    /// rest of its content. Throws with the file's own error when even this much cannot be resolved.</summary>
    internal static PlanIdentity PeekIdentity(string json, RuntimeIdentity identity) => Identify(json, identity).Identity;

    internal static ResolvedPlan Parse(string json, RuntimeIdentity identity, RuntimeLimits ceiling, RuntimeRegistry registry)
    {
        var (plan, planIdentity) = Identify(json, identity);
        var (id, resourceId, revision) = (planIdentity.Id, planIdentity.ResourceId, planIdentity.ResourceRevision);
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
            RuntimeJson.Shape(layout, "inputs outputs constants promoted");
            var constants = RuntimeJson.Rows(layout, "constants");
            RuntimeJson.Require(constants.Length == definitions.Length, "constant-frame", nodeId);
            // Strictly increasing declaration indices; each promoted value arrives through an input slot after the resolved inputs.
            var promoted = new HashSet<string>(StringComparer.Ordinal); var last = -1;
            foreach (var frame in RuntimeJson.Rows(layout, "promoted"))
            {
                var index = Slot(frame, definitions.Length, "promotion-frame", nodeId);
                RuntimeJson.Require(index > last, "promotion-frame", nodeId); last = index;
                promoted.Add(RuntimeJson.Text(definitions[index], "id"));
            }
            // Positional constants rebuild the keyed bag; null is the compiled spelling of "not authored" or "promoted".
            var keyed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            for (var i = 0; i < definitions.Length; i++)
            {
                var name = RuntimeJson.Text(definitions[i], "id");
                if (promoted.Contains(name)) { RuntimeJson.Require(constants[i].ValueKind == JsonValueKind.Null, "promoted-constant", nodeId + "." + name); continue; }
                if (constants[i].ValueKind != JsonValueKind.Null) { keyed.Add(name, constants[i]); continue; }
                RuntimeJson.Require(!RuntimeJson.Flag(definitions[i], "required"), "missing-constant", nodeId + "." + name);
            }
            var parameters = RuntimeJson.From(keyed);
            RuntimeJson.Parameters(parameters, capability, promoted);
            var contract = RuntimeGraphContracts.Resolve(graph, parameters, promoted);
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
            return new Node(nodeId, bindingId, parameters, contract, promoted);
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
                    // The key set alone decides the source kind (J-003): a row carrying both is refused outright,
                    // never treated as either shape. Everything else keeps the original two-field row check.
                    RuntimeJson.Require(source.ValueKind == JsonValueKind.Object, "object-required", node.Id);
                    var literal = source.TryGetProperty("value", out var literalValue);
                    RuntimeJson.Require(!literal || !source.TryGetProperty("fromEventSlot", out _), "literal-with-event-source", node.Id);
                    RuntimeJson.Shape(source, literal ? "slot value" : "slot fromEventSlot");
                    var slot = Slot(source.GetProperty("slot"), targets.Length, "input-slot", node.Id);
                    // Sorted and strictly increasing: one driver per input, one canonical spelling per plan.
                    RuntimeJson.Require(slot > previous, "input-order", node.Id); previous = slot;
                    var target = targets[slot]; var name = RuntimeJson.Text(target, "id");
                    RuntimeJson.Require(RuntimeJson.Text(target, "type") != "execution", "execution-slot", node.Id + "." + name);
                    if (literal)
                    {
                        // D-006①: literals never target entity/resource/handle/event/result ports (the last four are
                        // already excluded upstream as unsupported input ports); entity is excluded here explicitly.
                        RuntimeJson.Require(RuntimeJson.Text(target, "type") != "entity", "literal-wrong-type", node.Id + "." + name);
                        try { RuntimeJson.ValidateValue(literalValue, target); }
                        catch (RuntimeContractException) { throw new RuntimeContractException("literal-wrong-type", node.Id + "." + name); }
                        inputs.Add(new StepInput(name, null, literalValue, false, target));
                    }
                    else
                    {
                        var eventSlot = Slot(source.GetProperty("fromEventSlot"), eventPorts.Length, "event-port-missing", node.Id);
                        var origin = eventPorts[eventSlot];
                        RuntimeJson.Require(RuntimeJson.Text(origin, "type") != "execution", "execution-slot", node.Id + "." + name);
                        RuntimeJson.Require(RuntimeGraphContracts.ValueTypeMatches(origin, target), "port-mismatch", node.Id + "." + name);
                        // D-006②: a non-nullable "one" output may wire into a "many" input; dispatch wraps it into a
                        // one-element collection. A nullable "one" cannot feed "many", and "many" can never feed "one".
                        var wrap = !RuntimeGraphContracts.Many(origin) && RuntimeGraphContracts.Many(target) && !RuntimeJson.Flag(origin, "nullable");
                        RuntimeJson.Require(RuntimeGraphContracts.Many(origin) == RuntimeGraphContracts.Many(target) || wrap, "port-mismatch", node.Id + "." + name);
                        RuntimeJson.Require(!RuntimeJson.Flag(origin, "nullable") || RuntimeJson.Flag(target, "nullable"), "nullable-port", node.Id + "." + name);
                        RuntimeJson.Require(!RuntimeJson.Flag(origin, "optional") || RuntimeJson.Flag(target, "optional"), "optional-event-port", node.Id + "." + name);
                        inputs.Add(new StepInput(name, RuntimeJson.Text(origin, "id"), null, wrap, origin));
                    }
                }
                foreach (var target in targets.Where(p => RuntimeJson.Text(p, "type") != "execution" && !RuntimeJson.Flag(p, "optional")))
                    RuntimeJson.Require(inputs.Any(i => i.Name == RuntimeJson.Text(target, "id")), "missing-input", node.Id + "." + RuntimeJson.Text(target, "id"));
                steps.Add(new ResolvedStep(node.Id, node.BindingId, node.Parameters, inputs, node.Promoted));
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
        return new ResolvedPlan(id, resourceId, revision, domain, limits, entries, closure, RuntimeJson.StableText(plan), permissions);
    }
}
