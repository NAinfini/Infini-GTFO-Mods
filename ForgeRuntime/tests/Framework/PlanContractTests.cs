using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

/// <summary>
/// Plan-layer checks that need a registry of their own: the plan's `layout` as the port type and width authority,
/// the port variants a structural enum types, the seven collection element kinds, the declared reads a `query`
/// step's authority may come from, and the carriers a plan literal may not spell. Every case writes the graph it
/// registers and the plan it loads from one text, the way the compiler does — one pin for the binding the entry
/// uses, the layout the registered contract resolves to, constants in parameter declaration order — and names the
/// refusal code it expects.
/// </summary>
internal static class PlanContractTests
{
    private const string Id = "example.contract";
    private const string ProbeCapability = Id + ".probe";
    private const string ProbeBinding = Id + ".binding.probe";
    private const string ValueTypeParameter = "value_type";
    private static readonly string[] NoPermissions = Array.Empty<string>();
    /// <summary>The trigger capability's output ports, by the order `fromEventSlot` addresses them in.</summary>
    private static readonly string[] TriggerPorts = { "next", "target" };

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
        void RejectCode(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL: " + name + " did not reject");
        }

        // The plan's layout is the type and width authority: a layout that disagrees with the contract the plan was
        // compiled from is refused by name, whichever side is wrong, before a frame exists to be built from either.
        {
            var h = new Harness(Probe(Execution() + ";" + Variant("value", "number")), "boolean");
            var plan = h.Plan("tamper-ok", new[] { "value" });
            h.Load(plan);
            Check(true, "a plan whose layout is the registered contract's own layout loads");
            var step = JsonNode.Parse(plan)!["entrypoints"]![0]!["steps"]![0]!;
            Check(step["layout"]!["inputs"]!.AsArray()[2]!["type"]!.GetValue<int>()
                == Array.IndexOf(RuntimeGraphContracts.PortTypes, "boolean"),
                "the compiled step layout carries the resolved member's type index");
            // One index moved along graphPortTypes: the layout claims a number where the contract resolved the
            // structural parameter to a boolean. Every tampered plan is its own file, so the refusal it earns is
            // the layout's and not the duplicate planId's.
            var moved = JsonNode.Parse(plan)!;
            moved["planId"] = "tamper-index";
            moved["entrypoints"]![0]!["steps"]![0]!["layout"]!["inputs"]!.AsArray()[2]!["type"] = Array.IndexOf(RuntimeGraphContracts.PortTypes, "number");
            RejectCode(() => h.Load(moved.ToJsonString()), "layout-mismatch",
                "a layout whose type index is not the resolved contract's is refused");
            // The same fact on the entry's own trigger layout: the entity port claims a number.
            var triggerTamper = JsonNode.Parse(plan)!;
            triggerTamper["planId"] = "tamper-trigger";
            triggerTamper["entrypoints"]![0]!["layout"]!["outputs"]!.AsArray()[1]!["type"] = Array.IndexOf(RuntimeGraphContracts.PortTypes, "number");
            RejectCode(() => h.Load(triggerTamper.ToJsonString()), "layout-mismatch",
                "a trigger layout whose entity port claims another type is refused");
            // And a layout that lost a slot: a port without an entry of its own is the same disagreement.
            var shortened = JsonNode.Parse(plan)!;
            shortened["planId"] = "tamper-short";
            shortened["entrypoints"]![0]!["steps"]![0]!["layout"]!["inputs"]!.AsArray().RemoveAt(2);
            RejectCode(() => h.Load(shortened.ToJsonString()), "layout-mismatch",
                "a step layout with fewer slots than ports is refused");
        }

        // A structural enum member types its port, and the frame is built from the member the plan named.
        foreach (var (member, kind) in new[]
        {
            ("boolean", ValueKind.Boolean), ("integer", ValueKind.Integer), ("number", ValueKind.Number),
            ("string", ValueKind.String), ("vector3", ValueKind.Vector3), ("entity", ValueKind.Entity),
            ("handle", ValueKind.Handle)
        })
        {
            var extra = member == "handle" ? ", \"handleKind\":\"timer\", \"lifetime\":\"invocation\"" : "";
            var h = new Harness(Probe(Execution() + ";" + Variant("value", "string", extra)), member);
            Check(RuntimeJson.Text(RuntimeJson.Rows(h.Contract, "inputs")[2], "type") == member,
                "a typed port resolves to the member's own type [" + member + "]");
            h.Load(h.Plan("variant-" + member, member == "string" ? new[] { "value" } : Array.Empty<string>()));
            var slot = h.Slot("variant-" + member, "value");
            Check(slot.Kind == kind && slot.Width == RuntimeFrames.ValueWidth(member),
                "the frame slot of a typed port is the member's own kind and width [" + member + ": " + slot.Kind + "/" + slot.Width + "]");
        }

        // A typed port the contract cannot type: the parameter has to be a structural enum whose member list offers
        // the chosen type, and the port has to survive the member's own rules.
        {
            RejectCode(() => new Harness(Probe(Execution() + ";" + "{\"id\":\"value\",\"type\":\"number\",\"valueTypeParameter\":\"missing_parameter\"}"), "boolean"),
                "port-type", "a typed port naming no declared parameter is refused");
            RejectCode(() => new Harness(Probe(Execution() + ";" + Variant("value", "number"),
                parameter: "{\"id\":\"" + ValueTypeParameter + "\",\"type\":\"string\",\"role\":\"structural\",\"required\":true}"), "boolean"),
                "port-type", "a typed port whose parameter is not an enum is refused");
            RejectCode(() => new Harness(Probe(Execution() + ";" + "{\"id\":\"value\",\"type\":\"execution\",\"valueTypeParameter\":\""
                + ValueTypeParameter + "\"}"), "boolean"),
                "port-type", "a typed port declared as a class no member could be is refused");
            // The parameter's own member list is the closed basis of the port's type and of the constant that names
            // its member, whether that list is a whole shared set or the subset it narrowed to.
            var narrowed = new Harness(Probe(Execution() + ";" + Variant("value", "string"),
                parameter: "{\"id\":\"" + ValueTypeParameter + "\",\"type\":\"enum\",\"role\":\"structural\",\"required\":true,"
                    + "\"set\":\"variable_value_type\",\"values\":[\"boolean\",\"string\"]}"), "string");
            narrowed.Load(narrowed.Plan("variant-known-member", new[] { "value" }));
            Check(narrowed.Slot("variant-known-member", "value").Kind == ValueKind.String,
                "a plan naming a member the narrowed parameter offers loads");
            RejectCode(() => narrowed.Resolve("handle"), "port-type",
                "a member the narrowed parameter does not offer is refused");
            // A narrowed parameter keeps its own index basis: the shared set's index of a member it does not offer is
            // out of range, never a second way to spell one of the members it does.
            var borrowed = JsonNode.Parse(narrowed.Plan("variant-shared-index", new[] { "value" }))!;
            borrowed["entrypoints"]![0]!["steps"]![0]!["layout"]!["constants"]!.AsArray()[0] =
                Array.IndexOf(RuntimeGraphContracts.EnumSets["variable_value_type"], "handle");
            RejectCode(() => narrowed.Load(borrowed.ToJsonString()), "invalid-enum",
                "a constant indexed in the shared set rather than the parameter's own list is refused");
            // A required structural parameter has to be named: the compiled spelling of "not authored" is no member
            // at all, and no member is not one the port could resolve to.
            var unnamed = JsonNode.Parse(narrowed.Plan("variant-unnamed", new[] { "value" }))!;
            unnamed["entrypoints"]![0]!["steps"]![0]!["layout"]!["constants"]!.AsArray()[0] = null;
            RejectCode(() => narrowed.Load(unnamed.ToJsonString()), "missing-constant",
                "a required structural parameter the plan does not name is refused");
            // A member's own rules are the port's, checked in the order the member's own type checks them: a
            // resource port needs its kind once it has the schema every reference carrier needs, and an entity
            // port carries no unit.
            RejectCode(() => new Harness(Probe(Execution() + ";" + Variant("value", "string", ", \"schema\":\"example.resource.x\"")), "resource"),
                "port-resource-kind", "a port that resolves to a resource without a resource kind is refused");
            RejectCode(() => new Harness(Probe(Execution() + ";" + Variant("value", "string", ", \"unit\":\"hp\"")), "entity"),
                "port-unit", "a port that resolves to an entity may not carry a unit");
        }

        // A collection carries one of the seven element kinds and nothing else.
        {
            foreach (var (type, extra) in new[]
            {
                ("boolean", ""), ("integer", ""), ("number", ""), ("string", ""), ("vector3", ""), ("entity", ""),
                ("handle", ", \"handleKind\":\"timer\", \"lifetime\":\"invocation\"")
            })
            {
                var h = new Harness(Probe(Execution() + ";" + Collection("value", type, extra)), null);
                h.Load(h.Plan("many-" + type, Array.Empty<string>()));
                Check(true, "a collection of " + type + " loads");
            }
            foreach (var (type, extra) in new[]
            {
                ("resource", ", \"schema\":\"example.resource.x\", \"resourceKind\":\"map\""),
                ("event", ", \"schema\":\"example.event.x\""),
                ("result", ", \"schema\":\"example.result.x\", \"fields\":" + Columns()),
                ("policy", ", \"schema\":\"forge.policy.recipient\"")
            })
            {
                var h = new Harness(Probe(Execution() + ";" + Collection("value", type, extra)), null);
                RejectCode(() => h.Load(h.Plan("many-" + type, Array.Empty<string>())),
                    "unsupported-input-port", "a collection of " + type + " is refused");
            }
        }

        // Reads are a registration fact, carried into the view a plan step's authority is judged by.
        {
            foreach (var reads in new[] { "[\"world\"]", "[\"kernel\"]", "[\"telemetry\"]", "[\"world\",\"kernel\",\"telemetry\"]" })
            { new Harness(Probe(NoInputs(), "query", reads, "condition", output: "number"), null); Check(true, "the read source " + reads + " registers on an evaluated capability"); }
            RejectCode(() => new Harness(Probe(NoInputs(), "query", "[]", "condition"), null), "read-declaration", "an empty read declaration is refused");
            RejectCode(() => new Harness(Probe(NoInputs(), "query", "[\"world\",\"world\"]", "condition"), null), "read-source", "a repeated read source is refused");
            RejectCode(() => new Harness(Probe(NoInputs(), "query", "[\"game\"]", "condition"), null), "read-source", "an unknown read source is refused");
            RejectCode(() => new Harness(Probe(NoInputs(), "query", "\"world\"", "condition"), null), "array-required", "a read declaration that is not an array is refused");
            RejectCode(() => new Harness(Probe(NoInputs(), "query", "[1]", "condition"), null), "string-required", "a read declaration with a non-string member is refused");
            RejectCode(() => new Harness(Probe(NoInputs(), "host", "[\"world\"]"), null), "read-declaration", "an executed capability may not declare a read");
            RejectCode(() => new Harness(Probe(NoInputs(), "pure", "[\"world\"]", "condition"), null), RuntimeAbiCodes.PureWorldRead,
                "a pure capability may not declare a read");
        }

        // The tier a capability and the step that claims it are judged by: a read is world access, so a read alone
        // makes a `query`; a capability that reads nothing may not claim the tier, and a `pure` one may not resolve a
        // port to a world type even when no port of its own declares one.
        {
            RejectCode(() => new Harness(Probe(NoInputs(), "query", null, "condition"), null), RuntimeAbiCodes.QueryAuthority,
                "a query capability with neither a world port nor a declared read is refused");
            RejectCode(() => new Harness(Probe(Execution() + ";" + Variant("value", "string"), "pure", null, "condition"), "entity"),
                RuntimeAbiCodes.PureWorldPort, "a pure capability whose typed port could resolve to a world type is refused");
            var reads = new Harness(Probe(NoInputs(), "query", "[\"kernel\"]", "condition", output: "number"), null);
            reads.Load(reads.Plan("query-reads", Array.Empty<string>(), "query"));
            Check(true, "a query step whose capability declares a read and no world port loads");
            var pure = new Harness(Probe(NoInputs(), "pure", null, "condition", output: "number"), null);
            pure.Load(pure.Plan("pure-plain", Array.Empty<string>(), "pure"));
            Check(true, "a pure step whose capability reads nothing loads");
            // The declared class is the world one, so the capability is a query before its member is even chosen:
            // only a member of a port the row already calls a world port can be resolved to one.
            var world = new Harness(Probe(Execution() + ";" + Variant("value", "entity"), "query", null, "condition", output: "number"), "entity");
            world.Load(world.Plan("query-world", Array.Empty<string>(), "query"));
            Check(world.Slot("query-world", "value").Kind == ValueKind.Entity, "a query step whose port resolves to a world type loads");
        }

        // A plan literal is a value the plan compiled: the carriers with no literal form are refused, each by the
        // code its own kind earns. An entity and a result row are refused as what they are — a live reference and a
        // row that is not a data port at all — the two dispatch identities by their own names, and a resource is
        // the one reference a plan may compile: it is resolved at load through its kind's owner, and this provider
        // owns no resource kind, so the reference is stale rather than accepted.
        {
            var scalar = new Harness(Probe(Execution() + ";" + Port("value", "string")), null);
            scalar.Load(scalar.Plan("literal-scalar", new[] { "value" }));
            Check(true, "a scalar literal loads");
            foreach (var (port, name, code) in new[]
            {
                (Port("value", "entity"), "entity", "literal-wrong-type"),
                (Port("value", "handle", ", \"handleKind\":\"timer\", \"lifetime\":\"invocation\""), "handle", RuntimeAbiCodes.HandleLiteral),
                (Port("value", "resource", ", \"schema\":\"example.resource.x\", \"resourceKind\":\"map\""), "resource", RuntimeAbiCodes.StaleResource),
                (Port("value", "event", ", \"schema\":\"example.event.x\""), "event", RuntimeAbiCodes.EventLiteral),
                (Port("value", "result", ", \"schema\":\"example.result.x\", \"fields\":" + Columns()), "result", "unsupported-input-port")
            })
            {
                var h = new Harness(Probe(Execution() + ";" + port), null);
                RejectCode(() => h.Load(h.Plan("literal-" + name, new[] { "value" })), code, "a plan literal for a " + name + " is refused");
            }
        }
        return checks;
    }

    private static string Port(string id, string type, string extra = "")
        => "{\"id\":\"" + id + "\",\"type\":\"" + type + "\"" + extra + "}";
    /// <summary>One port typed by the structural parameter, written the way the website writes one.</summary>
    private static string Variant(string id, string declared, string extra = "")
        => "{\"id\":\"" + id + "\",\"type\":\"" + declared + "\",\"optional\":true,"
            + "\"valueTypeParameter\":\"" + ValueTypeParameter + "\"" + extra + "}";
    private static string Collection(string id, string type, string extra = "")
        => "{\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"cardinality\":\"many\",\"optional\":true" + extra + "}";
    private static string Execution() => "{\"id\":\"in\",\"type\":\"execution\"}";
    private static string NoInputs() => "";
    private static string Columns()
        => "[{\"id\":\"target\",\"type\":\"entity\"},{\"id\":\"status\",\"type\":\"enum\",\"schema\":\"execution_outcome\"},"
            + "{\"id\":\"committed\",\"type\":\"enum\",\"schema\":\"commit_state\"},{\"id\":\"code\",\"type\":\"string\"}]";
    /// <summary>
    /// The probe capability: the one graph every case writes a plan against. It declares the structural enum a
    /// typed port defers to unless the case supplies a definition of its own, the ports every capability of its kind
    /// has to declare, and — because every action declares its recipient — the target input that recipient names.
    /// The recipient port sits between `in` and the case's own port, so a case's port is the third.
    /// </summary>
    private static string Probe(string inputs, string execution = "host", string? reads = null,
        string kind = "action", string? parameter = null, string output = "boolean")
    {
        var action = kind == "action";
        var ports = (action ? new[] { Execution(), Target() } : Array.Empty<string>())
            .Concat(inputs.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(part => part != Execution()));
        // An action carries the result row its recipient contract discharges into; an evaluated capability only
        // publishes the value it was asked for, because a `query`/`pure` step's outputs are the frames a later step
        // reads and a row is not a value.
        var outputs = "{\"id\":\"value\",\"type\":\"" + output + "\"}" + (action
            ? ",{\"id\":\"result\",\"type\":\"result\",\"schema\":\"example.result.probe\",\"fields\":" + Columns() + "}" : "");
        return "{\"id\":\"" + ProbeCapability + "\",\"owner\":\"" + Id + "\",\"kind\":\"" + kind + "\",\"label\":\"Probe\",\"version\":\"1.0.0\","
            + "\"parameters\":{},\"graph\":{\"domains\":[\"enemy\"],\"execution\":\"" + execution + "\","
            + "\"inputs\":[" + string.Join(",", ports) + "],"
            + "\"outputs\":[" + outputs + "],"
            + "\"parameters\":[" + (parameter ?? ParameterDefinition()) + "]"
            + (action ? ",\"recipients\":" + Recipients : "")
            + (reads == null ? "" : ",\"reads\":" + reads) + "}}";
    }
    /// <summary>An action's recipient input: a handle, which is the one target kind a value-only action can take.</summary>
    private static string Target() => "{\"id\":\"target\",\"type\":\"entity\"}";
    private const string Recipients = "{\"input\":\"target\",\"target\":\"entity\",\"cardinality\":\"one\",\"requires\":[],\"result\":\"result\"}";
    private static string ParameterDefinition()
        => "{\"id\":\"" + ValueTypeParameter + "\",\"type\":\"enum\",\"role\":\"structural\",\"required\":true,\"set\":\"variable_value_type\"}";

    /// <summary>
    /// One kernel with the probe provider registered on the case's own graph, and the member the case authored
    /// written into that graph's structural parameter. The plan is written from the same contract the registration
    /// resolved, so a case that tampers with a layout tampers with the plan, never with the registration.
    /// </summary>
    private sealed class Harness
    {
        internal readonly RuntimeKernel Kernel;
        /// <summary>The registered graph, resolved with the member the case named.</summary>
        internal readonly JsonElement Contract;
        private readonly string? _member;
        /// <summary>Whether the probe is evaluated on demand, which is what makes its binding an evaluator.</summary>
        private readonly bool _evaluated;

        internal Harness(string graph, string? member)
        {
            _member = member;
            var capability = RuntimeJson.Parse(graph);
            _evaluated = RuntimeJson.Text(capability.GetProperty("graph"), "execution") is "pure" or "query";
            Kernel = new RuntimeKernel(Fixture.Identity); Kernel.BeginWorld(1);
            Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
            Kernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
            Kernel.RegisterModule(Module(Registry(capability)), RuntimeLogLevel.Off);
            Contract = Resolve(member);
        }

        /// <summary>The contract this capability's ports resolve to under one authored member list: what a plan
        /// compiled against the same member would have been laid out from.</summary>
        internal JsonElement Resolve(string? member)
            => Kernel.ResolveGraphContract(ProbeCapability, "1.0.0", Values(member));

        internal void Load(string json)
        {
            try { Kernel.LoadPlan(json); }
            catch (RuntimeContractException error) { throw new RuntimeContractException(error.Code, error.Code + ": " + error.Message); }
        }

        /// <summary>The resolved frame slot of one step input, addressed by the port's own name.</summary>
        internal PortSlot Slot(string planId, string port)
        {
            var plan = Kernel.Resolved(planId) ?? throw new Exception("FAIL: " + planId + " was not loaded");
            var step = plan.Entries[0].Steps[0];
            var slot = -1;
            foreach (var candidate in RuntimeJson.Rows(step.Contract, "inputs"))
            { slot++; if (RuntimeJson.Text(candidate, "id") == port) break; }
            return step.Frames.Inputs[slot];
        }

        /// <summary>
        /// One plan against the registered probe capability. An entrypoint starts at a command and an evaluated step
        /// is never one, so a case that exercises an evaluated tier gets the two-step entry the compiler writes for
        /// one: the probe's own step first, then the fixture's action, which starts the entry and reads the
        /// observation through the input its own value parameter promotes to.
        /// </summary>
        internal string Plan(string planId, string[] literals, string stepKind = "action")
        {
            var manifest = RuntimeJson.Parse(Kernel.ExportManifest());
            var registry = manifest.GetProperty("registry");
            var triggerBinding = Fixture.Trigger(Id);
            var applyBinding = Id + ".binding.apply";
            var evaluated = stepKind is "pure" or "query";
            // The pin table is exactly the closure this entry uses — its trigger, its step, and the command an
            // evaluated case starts the entry at — ordinal sorted, and every reference to it is an index into it.
            var pins = (evaluated ? new[] { triggerBinding, applyBinding, ProbeBinding } : new[] { triggerBinding, ProbeBinding })
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var rows = pins.Select(id => (object)new
            {
                bindingId = id,
                capabilityId = registry.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("id").GetString() == id).GetProperty("capabilityId").GetString(),
                capabilityVersion = "1.0.0", providerId = Id, providerVersion = "1.0.0",
                handler = registry.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("id").GetString() == id).GetProperty("handler").GetString()
            }).ToArray();
            var permissions = manifest.GetProperty("bindingSupport").EnumerateArray()
                .Where(s => pins.Contains(s.GetProperty("bindingId").GetString()!))
                .SelectMany(s => s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var graph = registry.GetProperty("capabilities").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == ProbeCapability).GetProperty("graph");
            // One constant per declared parameter, in declaration order, as the compiler writes it: the member the
            // case registered the capability under. A case whose ports carry no variant still declares the graph's
            // own structural parameter, so its plan names a member too — the first one, which no port reads.
            var definitions = RuntimeJson.Rows(graph, "parameters");
            var constants = definitions.Length == 0
                ? Array.Empty<object?>()
                : new object?[] { EnumMember(definitions[0], _member ?? RuntimeGraphContracts.EnumMembers(definitions[0])[0]) };
            var inputs = RuntimeJson.Rows(Contract, "inputs");
            var wiring = new List<object>();
            for (var index = 0; index < inputs.Length; index++)
            {
                var name = RuntimeJson.Text(inputs[index], "id");
                if (RuntimeJson.Text(inputs[index], "type") == "execution") continue;

                if (literals.Contains(name, StringComparer.Ordinal))
                { wiring.Add(new { slot = index, value = Literal(_member ?? RuntimeJson.Text(inputs[index], "type")) }); continue; }
                // An optional port the case did not give a literal stays unwired, which is what lets an entity or a
                // handle member — neither of which has a literal form — be part of a plan that loads.
                if (RuntimeJson.Flag(inputs[index], "optional")) continue;
                var trigger = Array.IndexOf(TriggerPorts, name);
                wiring.Add(new { slot = index, fromEventSlot = trigger >= 0 ? trigger : 0 });
            }
            var steps = new List<object> { new
            {
                nodeId = "Step0", nodeKind = stepKind, binding = Array.IndexOf(pins, ProbeBinding),
                layout = new
                {
                    inputs = RuntimeGraphContracts.Layout(Contract, "inputs"), outputs = RuntimeGraphContracts.Layout(Contract, "outputs"),
                    constants, promoted = Array.Empty<int>()
                },
                inputs = wiring.ToArray(), successors = Array.Empty<int?>()
            } };
            var start = 0;
            if (evaluated)
            {
                // The command the entry starts at, with its `amount` parameter promoted so it can be handed the
                // observation: the promoted port is the one input an action has that an evaluated output can reach.
                var applyGraph = registry.GetProperty("capabilities").EnumerateArray()
                    .Single(c => c.GetProperty("id").GetString() == Fixture.ActionCapability(Id)).GetProperty("graph");
                var promoted = new HashSet<string>(new[] { "amount" }, StringComparer.Ordinal);
                var apply = RuntimeGraphContracts.Resolve(applyGraph, RuntimeJson.Parse("{\"amount\":1}"), promoted);
                var applyInputs = RuntimeJson.Rows(apply, "inputs");
                int PortIndex(string name) => Array.FindIndex(applyInputs, p => RuntimeJson.Text(p, "id") == name);
                start = steps.Count;
                steps.Add(new
                {
                    nodeId = "Step1", nodeKind = "action", binding = Array.IndexOf(pins, applyBinding),
                    layout = new
                    {
                        inputs = RuntimeGraphContracts.Layout(apply, "inputs"), outputs = RuntimeGraphContracts.Layout(apply, "outputs"),
                        // A promoted parameter is not authored, and the step reads it from a slot instead.
                        constants = new object?[] { null },
                        promoted = new[] { Array.FindIndex(RuntimeJson.Rows(applyGraph, "parameters"), p => RuntimeJson.Text(p, "id") == "amount") }
                    },
                    inputs = new object[] {
                        new { slot = PortIndex("target"), fromEventSlot = Array.IndexOf(TriggerPorts, "target") },
                        new { slot = PortIndex("amount"), fromStepSlot = new { step = 0, port = 0 } }
                    },
                    successors = new int?[] { null }
                });
            }
            var triggerContract = Kernel.ResolveGraphContract(Fixture.TriggerCapability(Id), "1.0.0", RuntimeJson.EmptyObject);
            return RuntimeJson.From(new
            {
                schemaVersion = 4, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" },
                runtime = Kernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
                permissions, dependencies = Array.Empty<string>(),
                limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
                bindings = rows, attachments = Fixture.Attachments,
                entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(pins, triggerBinding),
                    layout = new { inputs = RuntimeGraphContracts.Layout(triggerContract, "inputs"), outputs = RuntimeGraphContracts.Layout(triggerContract, "outputs"),
                        constants = Array.Empty<object>(), promoted = Array.Empty<int>() },
                    start, steps = steps.ToArray() } }
            }).GetRawText();
        }

        /// <summary>The index of one member in the parameter's own member list, which is the basis a compiled plan
        /// indexes a constant by: its inline `values` when it narrows, its named set otherwise. A member the
        /// parameter does not offer has no index at all, which is the authoring mistake the callers write a plan to
        /// make.</summary>
        private static int EnumMember(JsonElement parameter, string member)
        {
            var index = Array.IndexOf(RuntimeGraphContracts.EnumMembers(parameter), member);
            RuntimeJson.Require(index >= 0, "port-type", RuntimeJson.Text(parameter, "id") + "." + member);
            return index;
        }

        /// <summary>The authored structural parameter, written as the plan's own constant carries it.</summary>
        private static JsonElement Values(string? member)
            => RuntimeJson.Parse(member == null ? "{}" : "{\"" + ValueTypeParameter + "\":\"" + member + "\"}");

        /// <summary>The fixture seed with the probe capability and its binding added, written as JSON text.</summary>
        private string Registry(JsonElement capability)
        {
            var seed = JsonNode.Parse(Fixture.Module(Id, _ => CommandResult.Succeeded(RuntimeJson.EmptyObject)).RegistryJson)!;
            seed["capabilities"]!.AsArray().Add(JsonNode.Parse(capability.GetRawText()));
            seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = ProbeBinding, capabilityId = ProbeCapability, providerId = Id, handler = Fixture.Handler(Id),
                // The one registration fact a step kind is judged by: an evaluated capability is reached through an
                // evaluator, an executed one through a command handler.
                role = _evaluated ? "evaluate" : "execute",
                status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
            }).GetRawText()));
            return seed.ToJsonString();
        }

        private RuntimeModule Module(string seed) => Fixture.Module(Id, _ => CommandResult.Succeeded(RuntimeJson.EmptyObject)) with
        {
            RegistryJson = seed,
            Evaluators = _evaluated
                ? new Dictionary<string, EvaluatorHandler> { [Fixture.Handler(Id)] = _ => RuntimeJson.From(new { value = true }) }
                : new Dictionary<string, EvaluatorHandler>(),
            // One shape belongs to the fixture's handler, and the fixture's own trigger and action share it. A shape
            // that names no port describes any graph, which is what lets this provider add a capability whose ports
            // the handler does not read: these cases are about what loads, and none of them dispatches a command.
            Shapes = new Dictionary<string, HandlerShape> { [Fixture.Handler(Id)] = new HandlerShape() },
            BindingSupport = new[] { Fixture.Support(Id)[0], Fixture.Support(Id)[1], new BindingSupport(ProbeBinding, "implementation-only", NoPermissions) }
        };

        /// <summary>One literal of the member's own type: a value, never the string a case wrote down.</summary>
        private static object Literal(string type) => type switch
        {
            "boolean" => true, "integer" => 1L, "number" => 1d, "string" => "x", "vector3" => new[] { 0, 0, 0 },
            "handle" => new { worldEpoch = 1, lifeEpoch = 1, local = 0, provider = 0 },
            "resource" => new { id = "r1", revision = "1" }, _ => new { }
        };
    }
}
