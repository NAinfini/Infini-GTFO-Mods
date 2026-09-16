using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeRuntime.Framework;

/// <summary>Where one resolved step input's value comes from at dispatch: a literal, a wired trigger event output
/// slot, or the value frame another step published within the same entrypoint.</summary>
internal readonly record struct StepSlotRef(int Step, int Port);

/// <summary>One resolved input, exactly one of <see cref="Literal"/>, <see cref="EventPort"/> or
/// <see cref="FromStep"/> is set. <see cref="Port"/> is the wire-side value contract used to validate the value at
/// dispatch. <see cref="Wrap"/> marks a non-nullable "one" output wired into a "many" input.</summary>
internal sealed record StepInput(string Name, string? EventPort, JsonElement? Literal, StepSlotRef? FromStep, bool Wrap, JsonElement Port);

/// <summary>One resolved step of a compiled entrypoint: <see cref="NodeKind"/> is `action`, `control`, `query` or
/// `pure`. <see cref="Successors"/> is empty for `pure`/`query` steps; for `action` it is 0 or 1 entries; for
/// `control` it has one entry per execution output, in declaration order, each either a step index within the same
/// entrypoint or null (path ends) — `pulse`/`body` included, since a region output names where the next activation
/// starts. <see cref="Control"/> is the control capability id the kernel dispatches by, or null elsewhere;
/// <see cref="ValuePort"/> is the output port id a when-present step publishes the value it tested under, or null
/// on every other step.
/// <see cref="Contract"/> is the resolved graph (inputs/outputs) used both to validate this step's own inputs and,
/// when this step is `pure`/`query`/`control`/`action`, to validate and address its outputs for downstream
/// `fromStepSlot` readers. <see cref="BindingIndex"/>/<see cref="HandlerIndex"/> are the plan's pin position and the kernel's
/// handler position; <see cref="Frames"/> is this step's slot table, built once with the plan (see
/// <see cref="PlanFrames"/>). <see cref="ProviderId"/>, <see cref="CapabilityId"/> and <see cref="Execution"/> are the
/// binding's own registration facts, read here because the plan is loaded against a frozen registry and the binding's
/// provider cannot change while the plan is live: a plan of an unregistered module is dropped with it, so the tier and
/// the owner are decided once instead of at every dispatch. <see cref="CommandSuffix"/> is the second half of this
/// step's command identity — the plan and the node, quoted by the JSON encoder — so a dispatch composes the identity
/// from two strings instead of serializing a four-element array per step.</summary>
internal sealed record ResolvedStep(string NodeId, string NodeKind, string BindingId, int BindingIndex, int HandlerIndex,
    JsonElement Parameters, IReadOnlyList<StepInput> Inputs, IReadOnlySet<string> Promoted, IReadOnlyList<int?> Successors,
    JsonElement Contract, StepFrames Frames, string ProviderId, string CapabilityId, string Execution, string CommandSuffix,
    string? Control = null, string? ValuePort = null);

/// <summary>One compiled entrypoint: dispatch starts at <see cref="Start"/> (an index into <see cref="Steps"/>,
/// always an `action` or `control` step) and follows `successors`; `pure` steps are read on demand, never dispatched.
/// <see cref="TriggerContract"/> is the entry's trigger graph: its outputs are the event payload frame's ports.
/// <see cref="Scope"/> is the map object this entry's trigger is attached to, as the canonical address the plan
/// carries, or null for a trigger attached to no one object (rule 143.11): an entry with a scope is dispatched only
/// for events whose own subject is that object, and an entry without one for every event of its binding.</summary>
internal sealed record ResolvedEntry(string NodeId, string BindingId, int Start, IReadOnlyList<ResolvedStep> Steps, JsonElement TriggerContract,
    string? Scope = null)
{
    /// <summary>Upper bound on commands one dispatch of this entry can produce: only `action`/`control` steps are
    /// ever placed on the walked path; `pure` and `query` steps are read on demand and never appear in
    /// <see cref="RuntimeKernel"/>'s command budgets.</summary>
    internal int DispatchableStepCount => Steps.Count(s => s.NodeKind is not ("pure" or "query"));
}

internal sealed record ResolvedPlan(string Id, string ResourceId, string ResourceRevision, string Domain, RuntimeLimits Limits,
    IReadOnlyList<ResolvedEntry> Entries, IReadOnlySet<string> Bindings, string Fingerprint, IReadOnlyList<string> Permissions,
    IReadOnlyList<PlanAttachment> Attachments, PlanFrames Frames, IReadOnlyList<VariableDeclaration> Variables);

/// <summary>One mount target of a plan: what the behaviour hangs on. Each kind is matched by the provider that
/// registered that kind's matcher, which either reads the event's subject or judges the target alone.</summary>
internal sealed record PlanAttachment(string Kind, string? Category, string Reference);
/// <summary>The identity a plan file resolves to before the rest of its content is validated: enough to group files by
/// planId for conflict detection, or to attach `plan` to a later rejection of the same file.</summary>
internal sealed record PlanIdentity(string Id, string ResourceId, string ResourceRevision);

/// <summary>
/// schemaVersion 4 plan (Runtime API 2.0.0): a graph of `action`/`control`/`query`/`pure` steps per entrypoint,
/// walked from `start` along explicit `successors`, plus the required non-empty `attachments[]` that says which
/// world objects the plan is mounted on. `pure` and `query` steps are read on demand by consumers via
/// `fromStepSlot` and evaluated at most once per activation; `query` may read the world through the kernel's
/// budgeted interface, `pure` may not touch it at all. Every layout, successor, attachment and slot reference is
/// re-derived from the registered contract and must match exactly; nothing in the file is trusted. There is no v3
/// fallback: a file whose `schemaVersion` is not 4 is rejected outright (`plan-version`).
/// </summary>
internal static class RuntimePlan
{
    /// <summary>One value port a control step's registered contract must declare, so the kernel can read the
    /// input the control is defined by without consulting a name it did not verify at load.</summary>
    private sealed record ControlPort(string Id, string Type, string? Unit = null, string? HandleKind = null, string? Lifetime = null, bool Many = false);
    /// <summary>The first-step control vocabulary, each keyed by its capability id. The kernel dispatches all of
    /// them from one place, so a control outside this table is refused by id (`control-unsupported`) rather than
    /// walked with guessed semantics. `Exits` is the execution outputs in declaration order, with `next` first;
    /// a region output (`pulse`/`body`) names the step the next activation starts from and is never an exit.</summary>
    private sealed record ControlShape(string[] Exits, ControlPort[] Inputs, ControlPort[] Outputs);
    /// <summary>Whether a control step's execution outputs are the ones its kind declares. A fixed shape lists
    /// them by id; a variadic one is judged by its pattern, which is the same rule the resolved contract already
    /// applied to the count.</summary>
    private static bool ExitsMatch(string capabilityId, string[] execIds, ControlShape shape)
    {
        if (shape.Exits.Length != 0) return execIds.SequenceEqual(shape.Exits);
        if (capabilityId == "forge.control.flow.sequence")
            return execIds.Length >= 1 && execIds.Select((id, index) => id == "step_" + (index + 1)).All(match => match);
        if (capabilityId is "forge.control.flow.parallel_all" or "forge.control.flow.random_branch")
        {
            // `branch_1`..`branch_N` in declaration order, then the single `next` the step continues from.
            if (execIds.Length < 2 || execIds[^1] != "next") return false;
            for (var index = 0; index < execIds.Length - 1; index++)
                if (execIds[index] != "branch_" + (index + 1)) return false;
            return true;
        }
        return execIds.Length == 0;
    }
    private static readonly Dictionary<string, ControlShape> ControlShapes = new(StringComparer.Ordinal)
    {
        ["forge.control.flow.branch"] = new(new[] { "then", "otherwise" },
            new[] { new ControlPort("condition", "boolean") }, Array.Empty<ControlPort>()),
        ["forge.control.flow.sequence"] = new(Array.Empty<string>(), Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        ["forge.control.flow.delay"] = new(new[] { "next" },
            new[] { new ControlPort("duration", "integer", "tick") },
            new[] { new ControlPort("timer", "handle", HandleKind: "timer", Lifetime: "encounter") }),
        ["forge.control.flow.interval"] = new(new[] { "next", "pulse" },
            new[] { new ControlPort("interval", "integer", "tick"), new ControlPort("count", "integer") },
            new[] { new ControlPort("timer", "handle", HandleKind: "timer", Lifetime: "encounter") }),
        ["forge.control.flow.repeat"] = new(new[] { "next", "body" },
            new[] { new ControlPort("count", "integer") },
            new[] { new ControlPort("index", "integer") }),
        ["forge.control.flow.for_each"] = new(new[] { "next", "body" },
            new[] { new ControlPort("candidates", "entity", Many: true), new ControlPort("budget", "integer") },
            new[] { new ControlPort("item", "entity"), new ControlPort("index", "integer") }),
        ["forge.control.flow.cancel"] = new(new[] { "next" },
            new[] { new ControlPort("task", "handle", HandleKind: "timer", Lifetime: "encounter") },
            new[] { new ControlPort("cancelled", "integer") }),
        // The rolling window: the same live timer handle, re-armed from now. Its ports are `cancel`'s with a boolean
        // answer instead of a count, because a restart either found a live schedule or failed by name.
        ["forge.control.flow.restart"] = new(new[] { "next" },
            new[] { new ControlPort("task", "handle", HandleKind: "timer", Lifetime: "encounter") },
            new[] { new ControlPort("restarted", "boolean") }),
        // The when-present guard (rule 142.3). Its two exits are its whole execution shape; its one value port is
        // the author's own class rather than one this table pins, so the pair is checked where the step is loaded
        // (`PresentValuePort`) instead of being restated here.
        ["forge.control.flow.present"] = new(new[] { "present", "missing" }, Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        // The two fan-outs. Their exits are variadic — `branch_1`..`branch_N` then `next`, with N from the node's
        // own `branch_count` — so the table declares no fixed exit list and the loader checks the pattern instead.
        // `random_branch` takes no seed: the host draws the branch when the step runs, and the draw is the
        // execution's own result rather than an author-supplied number two machines could disagree about.
        ["forge.control.flow.parallel_all"] = new(Array.Empty<string>(), Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        ["forge.control.flow.random_branch"] = new(Array.Empty<string>(), Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        // The variable, level-object and message rows the kernel dispatches itself. A row's `value` port is not
        // restated here: a `g-var` node resolves it through its own `value_type` constant, so the table fixes the
        // execution exits and the ports whose shape is the same for every use of the row.
        ["forge.variable.store.read"] = new(new[] { "next" },
            new[] { new ControlPort("subject", "entity"), new ControlPort("slot", "integer") }, Array.Empty<ControlPort>()),
        ["forge.variable.store.write"] = new(new[] { "next" },
            new[] { new ControlPort("subject", "entity"), new ControlPort("slot", "integer") },
            new[] { new ControlPort("written", "boolean") }),
        ["forge.object.named.read"] = new(new[] { "next" }, Array.Empty<ControlPort>(),
            new[]
            {
                new ControlPort("value", "entity"),
                new ControlPort("wave", "handle", HandleKind: "effect", Lifetime: "encounter")
            }),
        ["forge.object.named.write"] = new(new[] { "next" },
            new[]
            {
                new ControlPort("value", "entity"),
                new ControlPort("wave", "handle", HandleKind: "effect", Lifetime: "encounter")
            },
            new[] { new ControlPort("written", "boolean") }),
        ["forge.control.flow.once"] = new(new[] { "first", "later" }, Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        ["forge.control.flow.wait_event"] = new(new[] { "received", "timeout" }, Array.Empty<ControlPort>(),
            new[] { new ControlPort("message", "string"), new ControlPort("payload", "number") }),
        // A flow that ends has no exit: nothing after it runs, which is why the table declares no execution output.
        ["forge.control.flow.end"] = new(Array.Empty<string>(), Array.Empty<ControlPort>(), Array.Empty<ControlPort>()),
        ["forge.event.message.emit"] = new(new[] { "next" }, Array.Empty<ControlPort>(), Array.Empty<ControlPort>())
    };
    /// <summary>The when-present guard (rule 142.3): the one control whose value output the loader resolves from the
    /// plan's own contract instead of pinning it here, because the author chose the class it carries.</summary>
    private const string PresentControl = "forge.control.flow.present";
    /// <summary>The port id the kernel reads the tested value from. The forwarded value needs no such constant: the
    /// loader hands the kernel the port id the plan declared (`ResolvedStep.ValuePort`).</summary>
    private const string PresentValueInput = "value";

    /// <summary>
    /// The value ports of a when-present step, checked as the pair they are: the port the kernel reads the tested
    /// value from, and the one non-execution output the `present` branch forwards it through. Both carry one value
    /// contract of a class this runtime can move — the class itself is already resolved, because the plan's layout
    /// is proved equal to the registered contract re-derived from this node's own `value_type` — so the two ports
    /// are the value that was tested and never a second one. The returned id is the plan's own name for the forward
    /// port: the kernel publishes the value under it, so no spelling of it is repeated here.
    /// </summary>
    private static string PresentValuePort(JsonElement[] inputs, JsonElement[] outputs, string nodeId)
    {
        var tested = inputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == PresentValueInput);
        var forwarded = outputs.Where(p => RuntimeJson.Text(p, "type") != "execution").ToArray();
        RuntimeJson.Require(tested.ValueKind == JsonValueKind.Object
            && RuntimeGraphContracts.ValueTypeParameterTypes.Contains(RuntimeJson.Text(tested, "type")),
            RuntimeAbiCodes.ControlShape, nodeId + "." + PresentValueInput);
        RuntimeJson.Require(forwarded.Length == 1 && RuntimeGraphContracts.ValueTypeMatches(tested, forwarded[0]),
            RuntimeAbiCodes.ControlShape, nodeId + "." + PresentValueInput);
        return RuntimeJson.Text(forwarded[0], "id");
    }
    private sealed record Node(string Id, string Kind, string BindingId, int HandlerIndex, JsonElement Graph, JsonElement Parameters,
        JsonElement[] Constants, JsonElement Contract, IReadOnlySet<string> Promoted,
        string? Control = null, int BodyOutput = -1, string? ValuePort = null, string? TriggerAddress = null);

    /// <summary>The first slice of validation, shared by <see cref="Parse"/> and <see cref="PeekIdentity"/>: shape, version,
    /// runtime lock, authority/failure policy and the planId/resource identity. A file that fails here has no identity and
    /// is rejected on its own error without joining conflict-by-planId grouping.</summary>
    private static (JsonElement Plan, PlanIdentity Identity) Identify(string json, RuntimeIdentity identity)
    {
        var plan = RuntimeJson.Parse(json);
        RuntimeJson.Shape(plan, "schemaVersion kind planId resource runtime domain authority failurePolicy permissions dependencies limits bindings entrypoints attachments",
            "variables objects");
        RuntimeJson.Require(RuntimeJson.Integer(plan.GetProperty("schemaVersion")) == 4 && RuntimeJson.Text(plan, "kind") == "forge-runtime-plan", "plan-version", "Unsupported plan version.");
        RuntimeJson.Require(RuntimeJson.StableText(plan.GetProperty("runtime")) == RuntimeJson.StableText(RuntimeJson.From(identity)), "runtime-lock", "Runtime/API/game build lock mismatch.");
        RuntimeJson.Require(RuntimeJson.Text(plan, "authority") == "host" && RuntimeJson.Text(plan, "failurePolicy") == "stop-entrypoint", "execution-policy", "Plans require host and stop-entrypoint.");
        var id = RuntimeJson.Text(plan, "planId"); var resource = plan.GetProperty("resource");
        RuntimeJson.Shape(resource, "id revision"); var resourceId = RuntimeJson.Text(resource, "id"); var revision = RuntimeJson.Text(resource, "revision");
        return (plan, new PlanIdentity(id, resourceId, revision));
    }

    /// <summary>The first pass over a discovered file: resolve just enough identity to group it by planId, without
    /// validating the rest of its content. Throws with the file's own error when even this much cannot be resolved.</summary>
    internal static PlanIdentity PeekIdentity(string json, RuntimeIdentity identity) => Identify(json, identity).Identity;

    internal static ResolvedPlan Parse(string json, RuntimeIdentity identity, RuntimeLimits ceiling, RuntimeRegistry registry,
        RuntimeKernel kernel)
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
        // The handler table of this plan, in pin order: a step's handler position indexes it, so the tick path
        // reaches a handler through an integer instead of a binding-id dictionary lookup.
        var handlerNames = pins.Select(pin => RuntimeJson.Text(registry.Bindings[pin], "handler")).ToArray();
        foreach (var key in new[] { "permissions", "dependencies" }) {
            var values = RuntimeJson.Strings(plan.GetProperty(key));
            RuntimeJson.Require(values.SequenceEqual(values.OrderBy(x => x, StringComparer.Ordinal)), "plan-order", key);
        }
        var entryRows = RuntimeJson.Rows(plan, "entrypoints", ceiling.MaxEntrypoints);
        var attachments = ParseAttachments(plan, registry, ceiling);
        var entryIds = entryRows.Select(e => RuntimeJson.Text(e, "nodeId")).ToArray();
        RuntimeJson.Require(entryIds.SequenceEqual(entryIds.OrderBy(x => x, StringComparer.Ordinal)), "entrypoint-order", "Entrypoints must be ordinal sorted.");
        var used = new HashSet<string>(StringComparer.Ordinal); var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        int Slot(JsonElement value, int count, string code, string detail)
        {
            RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index >= 0 && index < count, code, detail);
            return value.GetInt32();
        }
        // kind: "trigger", "action", "control", "query" or "pure" (the last two select a
        // selector/condition/modifier capability, split by whether it touches the world).
        Node Load(JsonElement row, string kind)
        {
            var nodeId = RuntimeJson.Text(row, "nodeId");
            RuntimeJson.Require(Regex.IsMatch(nodeId, @"^[A-Za-z][A-Za-z0-9_-]{0,127}$"), "node-id", nodeId);
            RuntimeJson.Require(nodeIds.Add(nodeId), "duplicate-node", nodeId);
            var bindingId = pins[Slot(row.GetProperty("binding"), pins.Count, "binding-index", nodeId)]; used.Add(bindingId);
            var binding = registry.Bindings[bindingId]; var capability = registry.Capabilities[RuntimeJson.Text(binding, "capabilityId")];
            var capabilityId = RuntimeJson.Text(capability, "id");
            var capabilityKind = RuntimeJson.Text(capability, "kind");
            var expectedCapabilityKinds = kind switch
            {
                "trigger" => new[] { "trigger" },
                "action" => new[] { "action" },
                "control" => new[] { "control" },
                "pure" => new[] { "selector", "condition", "modifier" },
                // A query node is the on-demand read tier: a selector picks entities, a condition answers about
                // them and a value row (`kind: state`) answers a fact nobody publishes. `modifier` is the pure
                // layer's producing kind and is not a value row's kind, so it is not accepted here.
                "query" => new[] { "selector", "condition", "state" },
                _ => Array.Empty<string>()
            };
            RuntimeJson.Require(expectedCapabilityKinds.Contains(capabilityKind) && capability.TryGetProperty("graph", out _), "node-kind", nodeId);
            var graph = capability.GetProperty("graph");
            var executionAuthority = RuntimeJson.Text(graph, "execution");
            // An `action` step is `host` or `presentation`: both are executed, and which one it is decides who
            // executes it — the advance that owns the world, or the recipient the dispatch addresses. A `control`
            // is the kernel's own walker and belongs to no other tier.
            var expectedAuthority = kind switch
            {
                "pure" => new[] { "pure" },
                "query" => new[] { "query" },
                "control" => new[] { "host" },
                _ => new[] { "host", "presentation" }
            };
            RuntimeJson.Require(expectedAuthority.Contains(executionAuthority)
                && RuntimeJson.Strings(graph.GetProperty("domains")).Contains(domain, StringComparer.Ordinal), "node-domain-authority", nodeId);
            string? control = null; var bodyOutput = -1; string? valuePort = null; string? triggerAddress = null;
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
            // The bag is what a step's structural parameters and the resolvable variadic counts are read from; every
            // value parameter is validated once, against its own definition, where the frame builder encodes it.
            var keyed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            for (var i = 0; i < definitions.Length; i++)
            {
                var name = RuntimeJson.Text(definitions[i], "id");
                if (promoted.Contains(name)) { RuntimeJson.Require(constants[i].ValueKind == JsonValueKind.Null, "promoted-constant", nodeId + "." + name); continue; }
                if (constants[i].ValueKind != JsonValueKind.Null) { keyed.Add(name, constants[i]); continue; }
                RuntimeJson.Require(!RuntimeJson.Flag(definitions[i], "required"), "missing-constant", nodeId + "." + name);
            }
            var parameters = RuntimeJson.From(keyed);
            var contract = RuntimeGraphContracts.Resolve(graph, parameters, promoted);
            // The plan's own layout is the port type and width authority: it is what the frame is built from, and
            // the registered contract re-resolved from this node's parameters has to re-derive it field for field.
            // A port variant the compiler resolved to a different member on either side, a stale layout or a
            // tampered one therefore all fail here, by name, instead of a frame being built from one of the two.
            var declaredLayout = new Dictionary<string, JsonElement[]>(StringComparer.Ordinal);
            foreach (var side in new[] { "inputs", "outputs" })
            {
                declaredLayout[side] = RuntimeJson.Rows(layout, side);
                RuntimeJson.Require(RuntimeJson.StableText(layout.GetProperty(side)) == RuntimeJson.StableText(RuntimeGraphContracts.Layout(contract, side)),
                    "layout-mismatch", nodeId + "." + side);
            }
            var authoritative = AuthoritativeContract(contract, declaredLayout, nodeId);
            var inputs = RuntimeJson.Rows(authoritative, "inputs"); var outputs = RuntimeJson.Rows(authoritative, "outputs");
            var inputExecution = inputs.Where(p => RuntimeJson.Text(p, "type") == "execution").ToArray();
            var outputExecution = outputs.Where(p => RuntimeJson.Text(p, "type") == "execution").ToArray();
            // The one world-port fact both evaluated tiers are judged by: an entity, resource or handle port that
            // carries a value across this node's boundary, on either side.
            var WorldPorts = inputs.Concat(outputs).Any(p => RuntimeGraphContracts.WorldPort(RuntimeJson.Text(p, "type")));
            switch (kind)
            {
                case "trigger":
                    // A trigger declares no inputs. It may be told which author definition it subscribes to (a
                    // structural resource constant) and which map object it is attached to (a structural map object
                    // address, rule 143.11) — both compile-time constants the loader resolves, never a filter the
                    // dispatch would have to evaluate. `TriggerAddress` is read once here and carried on the entry.
                    RuntimeJson.Require(inputs.Length == 0 && outputExecution.Length == 1, "trigger-shape", nodeId);
                    triggerAddress = TriggerAddress(nodeId, definitions, constants);
                    break;
                case "action":
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "execute", "binding-role", nodeId);
                    RuntimeJson.Require(registry.Handlers.ContainsKey(bindingId), "action-handler", nodeId);
                    RuntimeJson.Require(inputExecution.Length == 1 && !RuntimeJson.Flag(inputExecution[0], "optional") && outputExecution.Length <= 1, "action-shape", nodeId);
                    break;
                case "control":
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "execute", "binding-role", nodeId);
                    RuntimeJson.Require(ControlShapes.TryGetValue(RuntimeJson.Text(capability, "id"), out var declaredShape), RuntimeAbiCodes.ControlUnsupported, nodeId);
                    var shape = declaredShape!;
                    RuntimeJson.Require(inputExecution.Length == 1 && !RuntimeJson.Flag(inputExecution[0], "optional"), RuntimeAbiCodes.ControlShape, nodeId);
                    var execIds = outputExecution.Select(p => RuntimeJson.Text(p, "id")).ToArray();
                    // A control's exits are judged three ways: a sequence and the two branch fan-outs are variadic
                    // (`step_N`, or `branch_1`..`branch_N` then `next`) and are checked by pattern; a control whose
                    // table entry declares no exits at all is a flow that ends, so it declares no execution output;
                    // every other kind declares its exits by id, in declaration order, with the region output —
                    // when it has one — last.
                    RuntimeJson.Require(ExitsMatch(RuntimeJson.Text(capability, "id"), execIds, shape),
                        RuntimeAbiCodes.ControlShape, nodeId + "." + string.Join(",", execIds));
                    foreach (var required in shape.Inputs)
                    {
                        var port = inputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == required.Id);
                        RuntimeJson.Require(port.ValueKind == JsonValueKind.Object && RuntimeJson.Text(port, "type") == required.Type
                            && OptionalText(port, "unit") == required.Unit && OptionalText(port, "handleKind") == required.HandleKind
                            && OptionalText(port, "lifetime") == required.Lifetime && RuntimeGraphContracts.Many(port) == required.Many,
                            RuntimeAbiCodes.ControlShape, nodeId + "." + required.Id);
                    }
                    foreach (var required in shape.Outputs)
                    {
                        var port = outputs.FirstOrDefault(p => RuntimeJson.Text(p, "id") == required.Id);
                        RuntimeJson.Require(port.ValueKind == JsonValueKind.Object && RuntimeJson.Text(port, "type") == required.Type
                            && OptionalText(port, "unit") == required.Unit && OptionalText(port, "handleKind") == required.HandleKind
                            && OptionalText(port, "lifetime") == required.Lifetime && RuntimeGraphContracts.Many(port) == required.Many,
                            RuntimeAbiCodes.ControlShape, nodeId + "." + required.Id);
                    }
                    control = RuntimeJson.Text(capability, "id");
                    bodyOutput = control is "forge.control.flow.interval" or "forge.control.flow.repeat" or "forge.control.flow.for_each" ? 1 : -1;
                    if (control == PresentControl) valuePort = PresentValuePort(inputs, outputs, nodeId);
                    break;
                case "pure":
                    // A pure step is read through its evaluator; a planned evaluate binding has none.
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") == "evaluate", "binding-role", nodeId);
                    RuntimeJson.Require(registry.Evaluators.ContainsKey(bindingId), "missing-evaluator", nodeId);
                    RuntimeJson.Require(inputExecution.Length == 0 && outputExecution.Length == 0, "pure-shape", nodeId);
                    // The step's own ports and its capability's declared reads are one jurisdiction: a declared
                    // read is a read even with no port behind it, so a pure step is refused for either.
                    RuntimeJson.Require(!WorldPorts && registry.Reads(capabilityId).Length == 0, RuntimeAbiCodes.PureWorldRead, nodeId);
                    break;
                case "query":
                    // A query is the same evaluator mechanism as `pure`, with the world reachable through the
                    // kernel's budgeted session: `observe` is its binding role, `evaluate` also accepted because a
                    // world-port observation and a value-only one share one registration table.
                    RuntimeJson.Require(RuntimeJson.Text(binding, "role") is "observe" or "evaluate", "binding-role", nodeId);
                    RuntimeJson.Require(registry.Evaluators.ContainsKey(bindingId), "missing-evaluator", nodeId);
                    RuntimeJson.Require(inputExecution.Length == 0 && outputExecution.Length == 0, "query-shape", nodeId);
                    // A world port or a declared read makes the observation a query; a step with neither may not
                    // claim the tier, which is what keeps `query` from becoming a second spelling of `pure`.
                    RuntimeJson.Require(WorldPorts || registry.Reads(capabilityId).Length > 0, RuntimeAbiCodes.QueryAuthority, nodeId);
                    break;
            }
            // Every non-execution port that carries a value across this boundary must be a type this runtime
            // validates: every step's inputs (same as v2), the trigger's own event outputs, and a `pure`/`query`
            // step's outputs — the latter are the frames later steps read through `fromStepSlot`. An `action`'s
            // outputs are judged where one is read rather than here: an output nothing reads crosses no boundary,
            // and the read is what makes it a value this runtime has to move (`MovablePort`). A `handle` is the one
            // exception that is carried but never decoded: it has an implemented frame form of its own and is
            // moved, not validated as a value.
            foreach (var port in (kind == "trigger" ? outputs : kind is "pure" or "query" ? outputs : inputs).Where(p => RuntimeJson.Text(p, "type") != "execution"))
                RuntimeJson.Require(MovablePort(port), kind == "trigger" ? "unsupported-event-port" : "unsupported-input-port", nodeId + "." + RuntimeJson.Text(port, "id"));
            return new Node(nodeId, kind, bindingId, HandlerIndex(bindingId), graph, parameters, constants, authoritative, promoted, control, bodyOutput, valuePort, triggerAddress);
        }

        /// <summary>
        /// The map object address an entrypoint's trigger is attached to, or null when it declares none (rule
        /// 143.11: a trigger bound to one object receives only that object's events, an unbound one receives every
        /// event of its binding). The address is a structural parameter of the trigger node and arrives as the node's
        /// own compile-time constant.
        ///
        /// The grammar is checked here in its one canonical form — five `/`-separated segments, none empty — because
        /// that is exactly the spelling the provider's own address record parses and the website's own address module
        /// renders; which categories exist and which coordinates a category accepts belong to the provider, and asking
        /// the kernel to know them would be a second copy of that grammar. A trigger may declare at most one such
        /// parameter: two addresses are not a filter this kernel could evaluate, and silently picking one would mount
        /// the plan on an object nobody named.
        /// </summary>
        static string? TriggerAddress(string nodeId, JsonElement[] definitions, JsonElement[] constants)
        {
            string? address = null;
            for (var i = 0; i < definitions.Length; i++)
            {
                if (RuntimeJson.Text(definitions[i], "type") != "object-address") continue;
                RuntimeJson.Require(RuntimeJson.Text(definitions[i], "role") == "structural",
                    "trigger-address-role", nodeId + "." + RuntimeJson.Text(definitions[i], "id"));
                RuntimeJson.Require(address == null, "trigger-address-count", nodeId);
                if (constants[i].ValueKind == JsonValueKind.Null) continue;
                var text = RuntimeJson.Text(constants[i]);
                var segments = text.Split('/');
                RuntimeJson.Require(segments.Length == 5 && segments.All(part => part.Length > 0),
                    "trigger-address", nodeId + "." + RuntimeJson.Text(definitions[i], "id"));
                address = text;
            }
            return address;
        }

        // The handler position of a bound handler name, or -1 for a step the kernel walks itself (`control`) and
        // for `pure` steps, whose names never reach the command handler table.
        int HandlerIndex(string bindingId) => Array.IndexOf(handlerNames, RuntimeJson.Text(registry.Bindings[bindingId], "handler"));

        /// <summary>
        /// The contract this node is compiled and dispatched against: the registered graph re-resolved from the
        /// plan's own parameters, with every port's type taken from the plan's layout instead of from the
        /// declaration. The layout indices are what the frame is built from, so a frame can never be addressed by a
        /// type the plan did not declare — and because the declared layout was proved equal to the re-derived one
        /// field for field just above, the two can only agree. Port metadata a layout index does not carry (an enum
        /// set, a resource or handle kind, a result row's columns) is the declaration's, by the same index.
        /// </summary>
        JsonElement AuthoritativeContract(JsonElement contract, Dictionary<string, JsonElement[]> declaredLayout, string nodeId)
        {
            var fields = contract.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            foreach (var side in new[] { "inputs", "outputs" })
            {
                var ports = RuntimeJson.Rows(contract, side);
                RuntimeJson.Require(declaredLayout[side].Length == ports.Length, "layout-mismatch", nodeId + "." + side);
                var typed = new JsonElement[ports.Length];
                for (var index = 0; index < ports.Length; index++)
                {
                    var type = -1;
                    var declared = declaredLayout[side][index].GetProperty("type");
                    RuntimeJson.Require(declared.ValueKind == JsonValueKind.Number && declared.TryGetInt32(out type)
                        && type >= 0 && type < RuntimeGraphContracts.PortTypes.Length, "layout-mismatch", nodeId + "." + side + "." + index);
                    RuntimeJson.Require(type == Array.IndexOf(RuntimeGraphContracts.PortTypes, RuntimeJson.Text(ports[index], "type")),
                        "layout-mismatch", nodeId + "." + side + "." + index);
                    var port = ports[index].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                    port["type"] = RuntimeJson.From(RuntimeGraphContracts.PortTypes[type]);
                    // A parameter's own definition types its port, so the value type parameter cannot survive onto
                    // the contract a handler shape resolves against: the port is already its member.
                    port.Remove("valueTypeParameter");
                    typed[index] = RuntimeJson.From(port);
                }
                fields[side] = RuntimeJson.From(typed);
            }
            return RuntimeJson.From(fields);
        }

        // Resolves one step's `inputs` row set against its own contract's input ports. `eventPorts` are the owning
        // entrypoint's trigger outputs; `nodes` are this entrypoint's already-loaded steps, used for `fromStepSlot`
        // bounds/kind checks, and `presentBranch` is the when-present region this step does or does not sit in. The
        // pure-step index vs. consumer-index ordering itself is checked later.
        List<StepInput> ResolveInputs(int stepIndex, JsonElement stepRow, Node node, JsonElement[] eventPorts, Node[] nodes, bool[] presentBranch)
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
                    // A literal is a value the plan compiled into its own frame. An entity is a live reference; a
                    // handle exists only once a step or an event produced it; an event and a result row are dispatch
                    // identities, not values; and a resource is the one reference a plan may compile, resolved
                    // through the kind's owner below. Each of the others is refused by its own code.
                    var targetType = RuntimeJson.Text(target, "type");
                    RuntimeJson.Require(targetType != "entity", "literal-wrong-type", node.Id + "." + name);
                    RuntimeJson.Require(targetType != "handle", RuntimeAbiCodes.HandleLiteral, node.Id + "." + name);
                    RuntimeJson.Require(targetType != "event", RuntimeAbiCodes.EventLiteral, node.Id + "." + name);
                    RuntimeJson.Require(targetType != "result", "literal-wrong-type", node.Id + "." + name);
                    if (targetType == "resource")
                    {
                        // A resource literal is the plan's own compiled reference, written in the document's
                        // `{id, revision}` form and resolved here through the kind's owner. The runtime value a
                        // handler sees afterwards (`resourceKind` plus `resourceId`) is a different boundary, so
                        // the compiled reference is not put through the runtime value validator.
                        ResolveResourceLiteral(kernel, literalValue, target, node.Id + "." + name);
                        inputs.Add(new StepInput(name, null, literalValue, null, false, target));
                        continue;
                    }
                    try { RuntimeJson.ValidateValue(literalValue, target); }
                    catch (RuntimeContractException) { throw new RuntimeContractException("literal-wrong-type", node.Id + "." + name); }
                    inputs.Add(new StepInput(name, null, literalValue, null, false, target));
                    continue;
                }
                JsonElement origin; string? eventPort = null; StepSlotRef? fromStep = null; var guarded = false;
                // A resource reference and an event row both move like every other value once something produced
                // them: an event payload hands one to a step exactly as a `fromStepSlot` output does. Whether the
                // reference is still there, and whether the event is the one this port declares, is asked where it
                // is read, not where it is carried — `event-port-kind` at the boundary.
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
                    // Any strictly earlier step may be read — not only a `pure` one. What a step publishes is the
                    // value frame it wrote: a `query` observation, a `pure` computation, a control's own `index`,
                    // `item`, `timer`, `cancelled` or guarded value, or an `action`'s own result row. Every node
                    // kind a loaded plan can carry publishes such a frame, so the only refusal the slot itself
                    // owes is an index that names no slot of this entry.
                    RuntimeJson.Require(stepField.ValueKind == JsonValueKind.Number && stepField.TryGetInt32(out var sourceStepIndex)
                        && sourceStepIndex >= 0 && sourceStepIndex < nodes.Length, "from-step-slot", node.Id);
                    var sourceStep = stepField.GetInt32();
                    var sourceOutputs = RuntimeJson.Rows(nodes[sourceStep].Contract, "outputs");
                    var portField = fromStepValue.GetProperty("port");
                    RuntimeJson.Require(portField.ValueKind == JsonValueKind.Number && portField.TryGetInt32(out var portIndex)
                        && portIndex >= 0 && portIndex < sourceOutputs.Length, "from-step-port", node.Id);
                    var portIndexValue = portField.GetInt32();
                    origin = sourceOutputs[portIndexValue];
                    RuntimeJson.Require(RuntimeJson.Text(origin, "type") != "execution", "from-step-port", node.Id);
                    // An `action`'s outputs kept their v2 treatment only while nothing read them; the read makes the
                    // port one this runtime has to move, so it is judged by the rule every declared input is.
                    RuntimeJson.Require(nodes[sourceStep].Kind != "action" || MovablePort(origin), "unsupported-input-port", node.Id + "." + name);
                    // A when-present step's forwarded value is the one value this runtime treats as there: the read
                    // is refused by name anywhere outside the region its `present` exit reaches.
                    guarded = nodes[sourceStep].Control == PresentControl;
                    RuntimeJson.Require(!guarded || presentBranch[stepIndex], "present-branch", node.Id + "." + name);
                    fromStep = new StepSlotRef(sourceStep, portIndexValue);
                }
                RuntimeJson.Require(RuntimeGraphContracts.ValueTypeMatches(origin, target), "port-mismatch", node.Id + "." + name);
                // A non-nullable "one" output may wire into a "many" input; dispatch wraps it into a
                // one-element collection. A nullable "one" cannot feed "many", and "many" can never feed "one".
                // Both rules ask about the value as the plan proved it: inside its own `present` region the guarded
                // value is there, which is what the branch check above established, so the declaration's `nullable`
                // is what the read would otherwise have been refused for.
                var nullable = RuntimeJson.Flag(origin, "nullable") && !guarded;
                var wrap = !RuntimeGraphContracts.Many(origin) && RuntimeGraphContracts.Many(target) && !nullable;
                RuntimeJson.Require(RuntimeGraphContracts.Many(origin) == RuntimeGraphContracts.Many(target) || wrap, "port-mismatch", node.Id + "." + name);
                RuntimeJson.Require(!nullable || RuntimeJson.Flag(target, "nullable"), "nullable-port", node.Id + "." + name);
                RuntimeJson.Require(!RuntimeJson.Flag(origin, "optional") || RuntimeJson.Flag(target, "optional"), "optional-event-port", node.Id + "." + name);
                inputs.Add(new StepInput(name, eventPort, null, fromStep, wrap, origin));
            }
            // A resource input is satisfied by its literal alone, so it is not required to appear in the wiring.
            foreach (var target in targets.Where(p => RuntimeJson.Text(p, "type") is not ("execution" or "resource") && !RuntimeJson.Flag(p, "optional")))
                RuntimeJson.Require(inputs.Any(i => i.Name == RuntimeJson.Text(target, "id")), "missing-input", node.Id + "." + RuntimeJson.Text(target, "id"));
            return inputs;
        }

        var total = 0;
        // Per-entry step material, kept until every entry has been proved canonical: a frame descriptor cannot be
        // built before the whole plan's step array is known, and the descriptor is what a ResolvedStep carries.
        var entryNodes = new List<Node[]>();
        var entryContracts = new List<EntryContract>();
        var entryStart = new List<int>();
        var entryTrigger = new List<(string NodeId, string BindingId, JsonElement Contract, string? TriggerAddress)>();
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
                RuntimeJson.Require(nodeKind is "action" or "control" or "pure" or "query", "node-kind", RuntimeJson.Text(stepRow, "nodeId"));
                nodes[i] = Load(stepRow, nodeKind);
            }
            // The entry's own start is proved before anything reads the walk: the when-present region below is a
            // property of that walk, and a plan whose start is not a step has no region to compute. The website's
            // plan reader validates `start` at the same point, so both sides refuse the same plan for the same
            // reason.
            var startRaw = entry.GetProperty("start");
            var startIndex = -1;
            RuntimeJson.Require(startRaw.ValueKind == JsonValueKind.Number && startRaw.TryGetInt32(out startIndex)
                && startIndex >= 0 && startIndex < stepRows.Length && nodes[startIndex].Kind is "action" or "control", "entry-start", trigger.Id);
            var successors = new int?[stepRows.Length][];
            for (var i = 0; i < stepRows.Length; i++)
            {
                var node = nodes[i];
                var expected = node.Kind is "pure" or "query" ? 0 : RuntimeJson.Rows(node.Contract, "outputs").Count(p => RuntimeJson.Text(p, "type") == "execution");
                var raw = RuntimeJson.Rows(stepRows[i], "successors", ceiling.MaxStepsPerEntrypoint);
                RuntimeJson.Require(raw.Length == expected, node.Kind is "pure" or "query" ? "pure-successor" : "successor-shape", node.Id);
                var successorsOfStep = new int?[raw.Length];
                for (var j = 0; j < raw.Length; j++)
                {
                    if (raw[j].ValueKind == JsonValueKind.Null) { successorsOfStep[j] = null; continue; }
                    RuntimeJson.Require(raw[j].ValueKind == JsonValueKind.Number && raw[j].TryGetInt32(out var successor)
                        && successor >= 0 && successor < stepRows.Length, "successor-shape", node.Id);
                    successorsOfStep[j] = raw[j].GetInt32();
                }
                successors[i] = successorsOfStep;
            }
            var presentBranch = PresentBranch(nodes, successors, startIndex);
            var stepInputs = new List<StepInput>[stepRows.Length];
            for (var i = 0; i < stepRows.Length; i++) stepInputs[i] = ResolveInputs(i, stepRows[i], nodes[i], eventPorts, nodes, presentBranch);
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
            // directly or transitively, by some reachable step. `start` itself was proved where the walk's
            // successors were read, because the when-present region is a property of that same walk.
            var start = startIndex;
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
            // Everything a frame descriptor needs about this entry, in the entry's own order: the builder runs once
            // for the whole plan, after this loop, because a plan-wide step index is what `fromStepSlot` addresses.
            entryContracts.Add(new EntryContract(trigger.Id, trigger.Contract, Enumerable.Range(0, stepRows.Length)
                .Select(i => new StepContract(nodes[i].Id, nodes[i].Kind, nodes[i].Graph, nodes[i].Contract, nodes[i].Constants, nodes[i].Promoted,
                    stepInputs[i], successors[i], pins.IndexOf(nodes[i].BindingId), nodes[i].HandlerIndex)).ToArray()));
            entryNodes.Add(nodes);
            entryStart.Add(start);
            entryTrigger.Add((trigger.Id, trigger.BindingId, trigger.Contract, trigger.TriggerAddress));
        }
        RuntimeJson.Require(entryContracts.Count > 0 && total <= ceiling.MaxTotalSteps, "plan-step-budget", id);
        // Per-trigger command bound: every entrypoint of one trigger contributes its dispatchable steps, and a
        // single event walks at most one path through each of them.
        foreach (var group in entryContracts.Select((entry, index) => (entry, trigger: entryTrigger[index].BindingId)).GroupBy(x => x.trigger))
            RuntimeJson.Require(group.Sum(x => x.entry.Steps.Count(step => step.NodeKind is not ("pure" or "query"))) <= limits.MaxCommandsPerTick, "event-command-budget", id);
        var closure = registry.Closure(used);
        RuntimeJson.ExactSet(pins, closure, "binding-closure");
        RuntimeJson.ExactSet(RuntimeJson.Strings(plan.GetProperty("dependencies")), registry.Packages(closure), "dependency-lock");
        var permissions = RuntimeJson.Strings(plan.GetProperty("permissions"));
        RuntimeJson.ExactSet(permissions, closure.SelectMany(b => registry.Support[b].RequiredPermissions), "permission-lock");
        // Frame descriptors are the last thing a plan gets: every step's slots, its copy sources and the constant
        // pool are fixed here, once, so dispatch never resolves a port name or a JSON value again. Steps are
        // addressed plan-wide (an entry's steps are contiguous from its own offset), which is what `fromStepSlot` names.
        // The plan's variables and level objects are one table: `variables[]` declares the per-scope names and
        // `objects[]` the named ones. Names are the world's addresses, so a name may appear once in a plan and the
        // store refuses one another loaded plan declares differently.
        var variableRows = RuntimeVariableContract.ParsePlanVariables(plan).Concat(RuntimeNamedObjectContract.ParsePlanObjects(plan)).ToArray();
        var variableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in variableRows) RuntimeJson.Require(variableNames.Add(row.Name), "duplicate-variable", row.Name);
        var frame = PlanFrames.Build(entryContracts, limits);
        var resolved = new List<ResolvedEntry>(entryContracts.Count);
        var stepOffset = frame.Start;
        for (var entry = 0; entry < entryContracts.Count; entry++)
        {
            var steps = new List<ResolvedStep>(entryContracts[entry].Steps.Count);
            for (var i = 0; i < entryContracts[entry].Steps.Count; i++)
            {
                var step = entryContracts[entry].Steps[i];
                var bindingId = entryNodes[entry][i].BindingId;
                var binding = registry.Bindings[bindingId];
                var capabilityId = RuntimeJson.Text(binding, "capabilityId");
                var capability = registry.Capabilities[capabilityId];
                var execution = capability.TryGetProperty("graph", out var graph) ? RuntimeJson.Text(graph, "execution") : "";
                steps.Add(new ResolvedStep(step.NodeId, step.NodeKind, bindingId, step.BindingIndex, step.HandlerIndex,
                    entryNodes[entry][i].Parameters, step.Inputs, step.Promoted, step.Successors, step.Contract, frame.Steps[stepOffset + i],
                    RuntimeJson.Text(binding, "providerId"), capabilityId, execution,
                    "," + RuntimeJson.Quote(id) + "," + RuntimeJson.Quote(step.NodeId) + "]",
                    entryNodes[entry][i].Control, entryNodes[entry][i].ValuePort));
            }
            stepOffset += steps.Count;
            resolved.Add(new ResolvedEntry(entryContracts[entry].NodeId, entryTrigger[entry].BindingId, entryStart[entry], steps, entryContracts[entry].Trigger, entryTrigger[entry].TriggerAddress));
        }
        return new ResolvedPlan(id, resourceId, revision, domain, limits, resolved, closure, RuntimeJson.StableText(plan), permissions, attachments, frame, variableRows);
    }

    /// <summary>
    /// One compiled resource reference, resolved once at load through the provider that owns the port's kind. The
    /// reference is the same one the constant pool holds, so a plan that names a resource nothing owns is refused
    /// before any step runs. Only the registry's static declaration is read here: a provider that owns the kind has
    /// the resource, and one that does not — or a kind no package registered at all — leaves the plan with a
    /// reference to a world that has ended, which is `stale-resource`. A provider's own answer cannot be inspected
    /// further here, because a plan is loaded before the world it was compiled for is necessarily the one in front
    /// of it.
    /// </summary>
    private static void ResolveResourceLiteral(RuntimeKernel kernel, JsonElement literal, JsonElement port, string at)
    {
        var kind = RuntimeJson.Text(port, "resourceKind");
        var id = RuntimeJson.ResourceLiteral(literal);
        RuntimeJson.Require(kernel.ResolveCompiledResource(kind, id) != null, RuntimeAbiCodes.StaleResource, at);
    }

    /// <summary>A declared port field that may legitimately be absent, read as text only when it is a string.</summary>
    private static string? OptionalText(JsonElement port, string key)
        => port.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Whether a port carries a value this runtime can move across a step boundary: every value type plus a
    /// handle — the same set the boundary validator resolves, so a port which passes here is one
    /// <see cref="RuntimeJson.ValidateValue"/> can judge. A collection is one head slot plus its elements, so it is
    /// the narrower set of kinds with an element form: `resource`, `event`, `result` and `policy` are reference or
    /// row carriers with no per-element form and are refused here, before a frame is built, rather than reserved a
    /// width nothing fills. Asked of every declared step input, of a trigger's own event outputs, and of an
    /// `action` output the moment a later step reads it.
    /// </summary>
    private static bool MovablePort(JsonElement port)
    {
        var type = RuntimeJson.Text(port, "type");
        return (RuntimeGraphContracts.RuntimeValueTypes.Contains(type) || type == "handle")
            && (!RuntimeGraphContracts.Many(port) || RuntimeGraphContracts.Segmented(type));
    }

    /// <summary>
    /// Which steps a when-present step's guarded value may be read in, within one entrypoint: the steps every path
    /// from the entry's `start` reaches through that step's `present` exit. Ruling 158.2 states the rule as
    /// dominance, and it is the website's own rule (`site/forge/control-lowering.ts` `presentRegionSteps`): a step
    /// two paths meet at after the guard has a way in that never tested the value, so reading the value there
    /// would read a possibly-absent one. It is the exit *edge* that is dominated, not the step it enters — a step
    /// another path also enters may run without the test — which is what removing that one edge and asking what is
    /// still reachable states exactly.
    /// </summary>
    private static bool[] PresentBranch(Node[] nodes, int?[][] successors, int start)
    {
        var branch = new bool[nodes.Length];
        var everything = Reachable(successors, start, null);
        for (var step = 0; step < nodes.Length; step++)
        {
            // The exit order is the shape table's: `present` is output 0, which `ExitsMatch` has already proved.
            if (nodes[step].Control != PresentControl || successors[step].Length == 0 || successors[step][0] is not int gate) continue;
            var without = Reachable(successors, start, (step, gate));
            for (var at = 0; at < nodes.Length; at++) if (everything[at] && !without[at]) branch[at] = true;
        }
        return branch;
    }

    /// <summary>The steps an execution walk from <paramref name="start"/> reaches, optionally with one successor
    /// edge taken out of the graph. A step is marked once, so a plan whose successors are not strictly forward
    /// still terminates — in the rejection those successors earn, which is checked right after this walk.</summary>
    private static bool[] Reachable(int?[][] successors, int start, (int From, int To)? without)
    {
        var found = new bool[successors.Length];
        if (start < 0 || start >= successors.Length) return found;
        var pending = new Stack<int>(); found[start] = true; pending.Push(start);
        while (pending.Count > 0)
        {
            var at = pending.Pop();
            foreach (var next in successors[at])
            {
                if (next is not int target) continue;
                if (without is { } edge && at == edge.From && target == edge.To) continue;
                if (!found[target]) { found[target] = true; pending.Push(target); }
            }
        }
        return found;
    }

    /// <summary>
    /// The plan's mount targets. The list is required and non-empty because a plan that hangs on nothing can only
    /// be a mistake; it is ordinal-sorted and duplicate-free so the same plan has one spelling; and every kind it
    /// names must have a matcher a provider registered — whether that matcher wants the event's subject or not —
    /// so a plan can never be accepted and then silently never dispatched.
    /// </summary>
    private static IReadOnlyList<PlanAttachment> ParseAttachments(JsonElement plan, RuntimeRegistry registry, RuntimeLimits limits)
    {
        var rows = RuntimeJson.Rows(plan, "attachments");
        RuntimeJson.Require(rows.Length > 0, RuntimeAbiCodes.AttachmentEmpty, "A plan declares at least one mount target.");
        RuntimeJson.Require(rows.Length <= limits.MaxAttachmentsPerPlan, RuntimeAbiCodes.AttachmentBudget,
            "A plan declares more mount targets than the per-plan budget allows.");
        var attachments = new List<PlanAttachment>(rows.Length);
        foreach (var row in rows)
        {
            RuntimeJson.Shape(row, "kind reference", "category");
            var kind = RuntimeJson.Text(row, "kind");
            RuntimeJson.Require(RuntimeGraphContracts.AttachmentKinds.Contains(kind), RuntimeAbiCodes.AttachmentKind, kind);
            var reference = RuntimeJson.Text(row, "reference");
            var category = row.TryGetProperty("category", out var value) ? RuntimeJson.Text(value) : null;
            // Only a map object is addressed by (category, address); the other kinds name a whole block or type.
            RuntimeJson.Require(kind == "map-object" ? category != null : category == null, RuntimeAbiCodes.AttachmentKind, kind);
            RuntimeJson.Require(registry.AttachmentMatchers.ContainsKey(kind), RuntimeAbiCodes.AttachmentKind, kind);
            attachments.Add(new PlanAttachment(kind, category, reference));
        }
        var sorted = attachments.OrderBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.Category ?? "", StringComparer.Ordinal).ThenBy(a => a.Reference, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < attachments.Count; index++)
        {
            var detail = attachments[index].Kind + "/" + (attachments[index].Category ?? "") + "/" + attachments[index].Reference;
            RuntimeJson.Require(attachments[index] == sorted[index], RuntimeAbiCodes.AttachmentOrder, detail);
            RuntimeJson.Require(index == 0 || attachments[index] != attachments[index - 1], RuntimeAbiCodes.AttachmentDuplicate, detail);
        }
        return attachments.AsReadOnly();
    }
}
