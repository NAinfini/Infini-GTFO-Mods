using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

/// <summary>Where one resolved step input's value comes from at dispatch: a literal, a wired trigger event output
/// slot, or (D-017 R4-a) another `pure` step's output frame within the same entrypoint.</summary>
internal readonly record struct StepSlotRef(int Step, int Port);

/// <summary>One resolved input, exactly one of <see cref="Literal"/>, <see cref="EventPort"/> or
/// <see cref="FromStep"/> is set. <see cref="Port"/> is the wire-side value contract used to validate the value at
/// dispatch. <see cref="Wrap"/> marks a non-nullable "one" output wired into a "many" input (D-006②).</summary>
internal sealed record StepInput(string Name, string? EventPort, JsonElement? Literal, StepSlotRef? FromStep, bool Wrap, JsonElement Port);

/// <summary>One resolved step of a compiled entrypoint (D-017 R4-a): <see cref="NodeKind"/> is `action`, `control`
/// or `pure`. <see cref="Successors"/> is empty for `pure` steps; for `action` it is 0 or 1 entries, for `control`
/// exactly 2 (`then`, `otherwise`), each either a step index within the same entrypoint or null (path ends).
/// <see cref="Contract"/> is the resolved graph (inputs/outputs) used both to validate this step's own inputs and,
/// when this step is `pure`, to validate and address its outputs for downstream `fromStepSlot` readers.</summary>
internal sealed record ResolvedStep(string NodeId, string NodeKind, string BindingId, JsonElement Parameters,
    IReadOnlyList<StepInput> Inputs, IReadOnlySet<string> Promoted, IReadOnlyList<int?> Successors, JsonElement Contract);

/// <summary>One compiled entrypoint: dispatch starts at <see cref="Start"/> (an index into <see cref="Steps"/>,
/// always an `action` or `control` step) and follows `successors`; `pure` steps are read on demand, never dispatched.</summary>
internal sealed record ResolvedEntry(string NodeId, string BindingId, int Start, IReadOnlyList<ResolvedStep> Steps)
{
    /// <summary>Upper bound on commands one dispatch of this entry can produce: only `action`/`control` steps are
    /// ever placed on the walked path; `pure` steps never appear in <see cref="RuntimeKernel"/>'s command budgets.</summary>
    internal int DispatchableStepCount => Steps.Count(s => s.NodeKind != "pure");
}

internal sealed record ResolvedPlan(string Id, string ResourceId, string ResourceRevision, string Domain, RuntimeLimits Limits,
    IReadOnlyList<ResolvedEntry> Entries, IReadOnlySet<string> Bindings, string Fingerprint, IReadOnlyList<string> Permissions);
/// <summary>The identity a plan file resolves to before the rest of its content is validated: enough to group files by
/// planId for D-009 conflict detection, or to attach `plan` to a later rejection of the same file.</summary>
internal sealed record PlanIdentity(string Id, string ResourceId, string ResourceRevision);

/// <summary>
/// schemaVersion 3 plan (Runtime API 2.0.0, D-017 R4-a): a graph of `action`/`control`/`pure` steps per entrypoint,
/// walked from `start` along explicit `successors`. `pure` steps are read on demand by consumers via `fromStepSlot`
/// and evaluated at most once per dispatch. Every layout, successor and slot reference is re-derived from the
/// registered contract and must match exactly; nothing in the file is trusted. There is no v2 fallback: a file
/// whose `schemaVersion` is not 3 is rejected outright (`plan-version`).
/// </summary>
internal static class RuntimePlan
{
    private const string BranchCapabilityId = "forge.control.flow.branch";
    private sealed record Node(string Id, string Kind, string BindingId, JsonElement Parameters, JsonElement Contract, IReadOnlySet<string> Promoted);

    /// <summary>The first slice of validation, shared by <see cref="Parse"/> and <see cref="PeekIdentity"/>: shape, version,
    /// runtime lock, authority/failure policy and the planId/resource identity. A file that fails here has no identity and
    /// (per D-009) is rejected on its own error without joining conflict-by-planId grouping.</summary>
    private static (JsonElement Plan, PlanIdentity Identity) Identify(string json, RuntimeIdentity identity)
    {
        var plan = RuntimeJson.Parse(json);
        RuntimeJson.Shape(plan, "schemaVersion kind planId resource runtime domain authority failurePolicy permissions dependencies limits bindings entrypoints");
        RuntimeJson.Require(RuntimeJson.Integer(plan.GetProperty("schemaVersion")) == 3 && RuntimeJson.Text(plan, "kind") == "forge-runtime-plan", "plan-version", "Unsupported plan version.");
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
        // kind: "trigger", "action", "control" or "pure" (the last selects a selector/condition/modifier capability).
        Node Load(JsonElement row, string kind)
        {
            var nodeId = RuntimeJson.Text(row, "nodeId");
            RuntimeJson.Require(Regex.IsMatch(nodeId, @"^[A-Za-z][A-Za-z0-9_-]{0,127}$"), "node-id", nodeId);
            RuntimeJson.Require(nodeIds.Add(nodeId), "duplicate-node", nodeId);
            var bindingId = pins[Slot(row.GetProperty("binding"), pins.Count, "binding-index", nodeId)]; used.Add(bindingId);
            var binding = registry.Bindings[bindingId]; var capability = registry.Capabilities[RuntimeJson.Text(binding, "capabilityId")];
            var capabilityKind = RuntimeJson.Text(capability, "kind");
            var expectedCapabilityKinds = kind switch
            {
                "trigger" => new[] { "trigger" },
                "action" => new[] { "action" },
                "control" => new[] { "control" },
                "pure" => new[] { "selector", "condition", "modifier" },
                _ => Array.Empty<string>()
            };
            RuntimeJson.Require(expectedCapabilityKinds.Contains(capabilityKind) && capability.TryGetProperty("graph", out _), "node-kind", nodeId);
            var graph = capability.GetProperty("graph");
            var executionAuthority = RuntimeJson.Text(graph, "execution");
            RuntimeJson.Require(executionAuthority == (kind == "pure" ? "pure" : "host")
                && RuntimeJson.Strings(graph.GetProperty("domains")).Contains(domain, StringComparer.Ordinal), "node-domain-authority", nodeId);
            if (kind == "control") RuntimeJson.Require(RuntimeJson.Text(capability, "id") == BranchCapabilityId, "control-unsupported", nodeId);
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
            switch (kind)
            {
                case "trigger":
                    RuntimeJson.Require(inputs.Length == 0 && definitions.Length == 0 && outputExecution.Length == 1, "trigger-shape", nodeId);
                    break;
                case "action":
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "execute", "binding-role", nodeId);
                    RuntimeJson.Require(registry.Handlers.ContainsKey(bindingId), "action-handler", nodeId);
                    RuntimeJson.Require(inputExecution.Length == 1 && !RuntimeJson.Flag(inputExecution[0], "optional") && outputExecution.Length <= 1, "action-shape", nodeId);
                    break;
                case "control":
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "execute", "binding-role", nodeId);
                    RuntimeJson.Require(inputExecution.Length == 1 && !RuntimeJson.Flag(inputExecution[0], "optional") && outputExecution.Length == 2, "control-shape", nodeId);
                    break;
                case "pure":
                    // D-017 R4-a: a pure step is read through its evaluator; a planned evaluate binding has none.
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "evaluate", "binding-role", nodeId);
                    RuntimeJson.Require(registry.Evaluators.ContainsKey(bindingId), "missing-evaluator", nodeId);
                    RuntimeJson.Require(inputExecution.Length == 0 && outputExecution.Length == 0, "pure-shape", nodeId);
                    RuntimeJson.Require(!inputs.Concat(outputs).Any(p => RuntimeJson.Text(p, "type") is "entity" or "resource" or "handle"), "pure-world-port", nodeId);
                    break;
            }
            // Every non-execution port that carries a value across this boundary must be a type this runtime
            // validates: every step's inputs (same as v2), the trigger's own event outputs, and a `pure` step's
            // outputs — the latter are the frames later steps read through `fromStepSlot`. An `action`'s outputs
            // keep their v2 treatment: R4-a never reads them, so their types are not this loader's business.
            foreach (var port in (kind == "trigger" ? outputs : kind == "pure" ? outputs : inputs).Where(p => RuntimeJson.Text(p, "type") != "execution"))
            {
                var type = RuntimeJson.Text(port, "type");
                RuntimeJson.Require(RuntimeGraphContracts.RuntimeValueTypes.Contains(type) && (!RuntimeGraphContracts.Many(port) || type == "entity"),
                    kind == "trigger" ? "unsupported-event-port" : "unsupported-input-port", nodeId + "." + RuntimeJson.Text(port, "id"));
            }
            return new Node(nodeId, kind, bindingId, parameters, contract, promoted);
        }

        // Resolves one step's `inputs` row set against its own contract's input ports. `eventPorts` are the owning
        // entrypoint's trigger outputs; `nodes`/`stepRows` are this entrypoint's already-loaded steps, used for
        // `fromStepSlot` bounds/kind checks (the pure-step index vs. consumer-index ordering itself is checked later).
        List<StepInput> ResolveInputs(JsonElement stepRow, Node node, JsonElement[] eventPorts, Node[] nodes)
        {
            var targets = RuntimeJson.Rows(node.Contract, "inputs");
            var inputs = new List<StepInput>(); var previous = -1;
            foreach (var source in RuntimeJson.Rows(stepRow, "inputs", targets.Length))
            {
                RuntimeJson.Require(source.ValueKind == JsonValueKind.Object, "object-required", node.Id);
                var hasLiteral = source.TryGetProperty("value", out var literalValue);
                var hasEvent = source.TryGetProperty("fromEventSlot", out _);
                var hasStep = source.TryGetProperty("fromStepSlot", out var fromStepValue);
                RuntimeJson.Require((hasLiteral ? 1 : 0) + (hasEvent ? 1 : 0) + (hasStep ? 1 : 0) == 1, "literal-with-event-source", node.Id);
                RuntimeJson.Shape(source, hasLiteral ? "slot value" : hasEvent ? "slot fromEventSlot" : "slot fromStepSlot");
                var slot = Slot(source.GetProperty("slot"), targets.Length, "input-slot", node.Id);
                // Sorted and strictly increasing: one driver per input, one canonical spelling per plan.
                RuntimeJson.Require(slot > previous, "input-order", node.Id); previous = slot;
                var target = targets[slot]; var name = RuntimeJson.Text(target, "id");
                RuntimeJson.Require(RuntimeJson.Text(target, "type") != "execution", "execution-slot", node.Id + "." + name);
                if (hasLiteral)
                {
                    // D-006①: literals never target entity/resource/handle/event/result ports (the last four are
                    // already excluded upstream as unsupported input ports); entity is excluded here explicitly.
                    RuntimeJson.Require(RuntimeJson.Text(target, "type") != "entity", "literal-wrong-type", node.Id + "." + name);
                    try { RuntimeJson.ValidateValue(literalValue, target); }
                    catch (RuntimeContractException) { throw new RuntimeContractException("literal-wrong-type", node.Id + "." + name); }
                    inputs.Add(new StepInput(name, null, literalValue, null, false, target));
                    continue;
                }
                JsonElement origin; string? eventPort = null; StepSlotRef? fromStep = null;
                if (hasEvent)
                {
                    var eventSlot = Slot(source.GetProperty("fromEventSlot"), eventPorts.Length, "event-port-missing", node.Id);
                    origin = eventPorts[eventSlot];
                    RuntimeJson.Require(RuntimeJson.Text(origin, "type") != "execution", "execution-slot", node.Id + "." + name);
                    eventPort = RuntimeJson.Text(origin, "id");
                }
                else
                {
                    RuntimeJson.Shape(fromStepValue, "step port");
                    var stepField = fromStepValue.GetProperty("step");
                    RuntimeJson.Require(stepField.ValueKind == JsonValueKind.Number && stepField.TryGetInt32(out var stepIndex)
                        && stepIndex >= 0 && stepIndex < nodes.Length && nodes[stepIndex].Kind == "pure", "from-step-kind", node.Id);
                    var stepIndexValue = stepField.GetInt32();
                    var sourceOutputs = RuntimeJson.Rows(nodes[stepIndexValue].Contract, "outputs");
                    var portField = fromStepValue.GetProperty("port");
                    RuntimeJson.Require(portField.ValueKind == JsonValueKind.Number && portField.TryGetInt32(out var portIndex)
                        && portIndex >= 0 && portIndex < sourceOutputs.Length, "from-step-port", node.Id);
                    var portIndexValue = portField.GetInt32();
                    origin = sourceOutputs[portIndexValue];
                    RuntimeJson.Require(RuntimeJson.Text(origin, "type") != "execution", "from-step-port", node.Id);
                    fromStep = new StepSlotRef(stepIndexValue, portIndexValue);
                }
                RuntimeJson.Require(RuntimeGraphContracts.ValueTypeMatches(origin, target), "port-mismatch", node.Id + "." + name);
                // D-006②: a non-nullable "one" output may wire into a "many" input; dispatch wraps it into a
                // one-element collection. A nullable "one" cannot feed "many", and "many" can never feed "one".
                var wrap = !RuntimeGraphContracts.Many(origin) && RuntimeGraphContracts.Many(target) && !RuntimeJson.Flag(origin, "nullable");
                RuntimeJson.Require(RuntimeGraphContracts.Many(origin) == RuntimeGraphContracts.Many(target) || wrap, "port-mismatch", node.Id + "." + name);
                RuntimeJson.Require(!RuntimeJson.Flag(origin, "nullable") || RuntimeJson.Flag(target, "nullable"), "nullable-port", node.Id + "." + name);
                RuntimeJson.Require(!RuntimeJson.Flag(origin, "optional") || RuntimeJson.Flag(target, "optional"), "optional-event-port", node.Id + "." + name);
                inputs.Add(new StepInput(name, eventPort, null, fromStep, wrap, origin));
            }
            foreach (var target in targets.Where(p => RuntimeJson.Text(p, "type") != "execution" && !RuntimeJson.Flag(p, "optional")))
                RuntimeJson.Require(inputs.Any(i => i.Name == RuntimeJson.Text(target, "id")), "missing-input", node.Id + "." + RuntimeJson.Text(target, "id"));
            return inputs;
        }

        var entries = new List<ResolvedEntry>(); var total = 0;
        foreach (var entry in entryRows)
        {
            RuntimeJson.Shape(entry, "nodeId binding layout start steps");
            var trigger = Load(entry, "trigger");
            var eventPorts = RuntimeJson.Rows(trigger.Contract, "outputs");
            var stepRows = RuntimeJson.Rows(entry, "steps", ceiling.MaxStepsPerEntrypoint);
            RuntimeJson.Require(stepRows.Length > 0, "empty-entrypoint", trigger.Id); total += stepRows.Length;
            var nodes = new Node[stepRows.Length];
            for (var i = 0; i < stepRows.Length; i++)
            {
                var stepRow = stepRows[i];
                RuntimeJson.Shape(stepRow, "nodeId nodeKind binding layout inputs successors");
                var nodeKind = RuntimeJson.Text(stepRow, "nodeKind");
                RuntimeJson.Require(nodeKind is "action" or "control" or "pure", "node-kind", RuntimeJson.Text(stepRow, "nodeId"));
                nodes[i] = Load(stepRow, nodeKind);
            }
            var successors = new int?[stepRows.Length][];
            for (var i = 0; i < stepRows.Length; i++)
            {
                var node = nodes[i];
                var expected = node.Kind == "pure" ? 0 : RuntimeJson.Rows(node.Contract, "outputs").Count(p => RuntimeJson.Text(p, "type") == "execution");
                var raw = RuntimeJson.Rows(stepRows[i], "successors", ceiling.MaxStepsPerEntrypoint);
                RuntimeJson.Require(raw.Length == expected, node.Kind == "pure" ? "pure-successor" : "successor-shape", node.Id);
                var resolved = new int?[raw.Length];
                for (var j = 0; j < raw.Length; j++)
                {
                    if (raw[j].ValueKind == JsonValueKind.Null) { resolved[j] = null; continue; }
                    RuntimeJson.Require(raw[j].ValueKind == JsonValueKind.Number && raw[j].TryGetInt32(out var target)
                        && target >= 0 && target < stepRows.Length, "successor-shape", node.Id);
                    resolved[j] = raw[j].GetInt32();
                }
                successors[i] = resolved;
            }
            var stepInputs = new List<StepInput>[stepRows.Length];
            for (var i = 0; i < stepRows.Length; i++) stepInputs[i] = ResolveInputs(stepRows[i], nodes[i], eventPorts, nodes);
            // Edge direction: a successor always names a strictly later step; a pure read always names a strictly earlier one.
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var next in successors[i])
                    if (next is int target) RuntimeJson.Require(target > i && nodes[target].Kind is "action" or "control", "successor-index", nodes[i].Id);
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var input in stepInputs[i])
                    if (input.FromStep is { } from) RuntimeJson.Require(from.Step < i, "from-step-slot", nodes[i].Id);
            // Canonical order: Kahn's algorithm over both edge kinds, ties broken by nodeId ordinal. The compiled
            // array must equal this order exactly, item by item.
            var indegree = new int[stepRows.Length];
            var dependents = new List<int>[stepRows.Length];
            for (var i = 0; i < stepRows.Length; i++) dependents[i] = new List<int>();
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var next in successors[i]) if (next is int target) { dependents[i].Add(target); indegree[target]++; }
            for (var i = 0; i < stepRows.Length; i++)
                foreach (var input in stepInputs[i]) if (input.FromStep is { } from) { dependents[from.Step].Add(i); indegree[i]++; }
            var visited = new bool[stepRows.Length];
            for (var position = 0; position < stepRows.Length; position++)
            {
                var candidate = -1; string? candidateId = null;
                for (var i = 0; i < stepRows.Length; i++)
                {
                    if (visited[i] || indegree[i] != 0) continue;
                    if (candidate == -1 || string.CompareOrdinal(nodes[i].Id, candidateId) < 0) { candidate = i; candidateId = nodes[i].Id; }
                }
                RuntimeJson.Require(candidate != -1, "step-order", trigger.Id);
                RuntimeJson.Require(candidate == position, "step-order", nodes[candidate].Id);
                visited[candidate] = true;
                foreach (var dependent in dependents[candidate]) indegree[dependent]--;
            }
            // Reachability: every action/control step must be reachable from `start`; every pure step must be read,
            // directly or transitively, by some reachable step.
            var startRaw = entry.GetProperty("start");
            RuntimeJson.Require(startRaw.ValueKind == JsonValueKind.Number && startRaw.TryGetInt32(out var startCheck)
                && startCheck >= 0 && startCheck < stepRows.Length && nodes[startCheck].Kind is "action" or "control", "entry-start", trigger.Id);
            var start = startRaw.GetInt32();
            var reachable = new bool[stepRows.Length]; reachable[start] = true;
            bool changed;
            do
            {
                changed = false;
                for (var i = 0; i < stepRows.Length; i++)
                {
                    if (!reachable[i]) continue;
                    foreach (var next in successors[i]) if (next is int target && !reachable[target]) { reachable[target] = true; changed = true; }
                    foreach (var input in stepInputs[i]) if (input.FromStep is { } from && !reachable[from.Step]) { reachable[from.Step] = true; changed = true; }
                }
            } while (changed);
            for (var i = 0; i < stepRows.Length; i++) RuntimeJson.Require(reachable[i], "unreachable-step", nodes[i].Id);
            var steps = new List<ResolvedStep>(stepRows.Length);
            for (var i = 0; i < stepRows.Length; i++)
                steps.Add(new ResolvedStep(nodes[i].Id, nodes[i].Kind, nodes[i].BindingId, nodes[i].Parameters, stepInputs[i], nodes[i].Promoted, successors[i], nodes[i].Contract));
            entries.Add(new ResolvedEntry(trigger.Id, trigger.BindingId, start, steps));
        }
        RuntimeJson.Require(entries.Count > 0 && total <= ceiling.MaxTotalSteps, "plan-step-budget", id);
        foreach (var group in entries.GroupBy(e => e.BindingId)) RuntimeJson.Require(group.Sum(e => e.DispatchableStepCount) <= limits.MaxCommandsPerTick, "event-command-budget", id);
        var closure = registry.Closure(used);
        RuntimeJson.ExactSet(pins, closure, "binding-closure");
        RuntimeJson.ExactSet(RuntimeJson.Strings(plan.GetProperty("dependencies")), registry.Packages(closure), "dependency-lock");
        var permissions = RuntimeJson.Strings(plan.GetProperty("permissions"));
        RuntimeJson.ExactSet(permissions, closure.SelectMany(b => registry.Support[b].RequiredPermissions), "permission-lock");
        return new ResolvedPlan(id, resourceId, revision, domain, limits, entries, closure, RuntimeJson.StableText(plan), permissions);
    }
}
