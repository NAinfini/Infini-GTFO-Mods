using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

/// <summary>
/// The actor roles one dispatched trigger event delivers to a step's evaluation context, and the one table that says
/// which event slot each role is read from. Four roles are read from one payload port each — `source`, `owner` and
/// `instigator` from the port of the same name, `event-target` from the payload's `target` — and nothing is inferred
/// from a neighbouring slot: a port the event names but answers null, or never names at all, is that role's absence.
/// `self` is not a payload port at all: it is the entity the plan's own mount accepted for this dispatch, and only
/// where the mount settled on exactly one. A mount that accepted none leaves it absent — the one role the contract's
/// own selector refuses by name — and one that accepted several different entities refuses it as `actor-ambiguous`
/// rather than choosing between them.
/// </summary>
internal static class RuntimeContextTests
{
    private const string Id = "example.context";
    private const string TriggerBinding = Id + ".binding.trigger";
    private const string ActionBinding = Id + ".binding.apply";
    /// <summary>The subject-matched mount kind whose matcher is asked about the entities the event carries: the
    /// plan's own mount, and the only source of a subject. It accepts the one entity its reference names, so a case
    /// scripts which entities of one event its mounts settle on.</summary>
    private const string SubjectKind = "enemy-type";
    private static readonly string[] NoPermissions = Array.Empty<string>();
    private static object SubjectMount(string reference) => new { kind = SubjectKind, reference };
    private static readonly object LevelMount = new { kind = "level", reference = Fixture.MountReference };

    /// <summary>The compiled layout of one node: its two port lists and the constants its parameters resolved to.
    /// Nothing here is promoted, so the constant array is the whole parameter frame.</summary>
    private static object Layout(JsonElement contract, object[] constants)
        => new { inputs = RuntimeGraphContracts.Layout(contract, "inputs"), outputs = RuntimeGraphContracts.Layout(contract, "outputs"), constants, promoted = Array.Empty<int>() };

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

        var victim = new EntityReference(Id + ":victim", 1, 1);
        var actor = new EntityReference(Id + ":actor", 1, 1);
        RuntimeEvent Event(object payload) => new("inspect", TriggerBinding, 1, 1, "scope", RuntimeJson.From(payload));

        // Every role is answered from its own payload port, and only from it: the payload ports a trigger might name
        // for `self` or `event_target` are not slots, so a value parked there answers nothing at all.
        {
            var carried = RuntimeActorRoles.FromTriggerEvent(Event(new
            {
                target = victim, self = actor, event_target = actor, source = actor, owner = actor, instigator = actor
            }), Array.Empty<EntityReference>());
            Check(carried.Actors.Count == 4 && carried.Get("source") == actor && carried.Get("owner") == actor
                && carried.Get("instigator") == actor && carried.Get("event-target") == victim && carried.Get("self") == null,
                "four roles come from their own ports, and the payload's `self`/`event_target` ports are not slots");
        }

        // One slot's value never answers a neighbouring role: with only `source` present, `self` — which no payload
        // port can answer — and `event-target` are both absent rather than read from `source`.
        {
            var sourceOnly = RuntimeActorRoles.FromTriggerEvent(Event(new { source = actor }), Array.Empty<EntityReference>());
            Check(sourceOnly.Get("source") == actor && sourceOnly.Get("self") == null && sourceOnly.Get("event-target") == null
                && sourceOnly.Get("owner") == null && sourceOnly.Get("instigator") == null && sourceOnly.Actors.Count == 1,
                "a role is never borrowed from the slot next to it");
        }

        // The two spellings of `event-target`: the payload port is `target`, and a port merely spelled `event_target`
        // is an ordinary port of the trigger that the role never reads.
        {
            var decoyOnly = RuntimeActorRoles.FromTriggerEvent(Event(new { event_target = victim }), Array.Empty<EntityReference>());
            Check(decoyOnly.Get("event-target") == null && decoyOnly.Actors.Count == 0,
                "the payload's `event_target` spelling is not the event-target slot");
            var targetOnly = RuntimeActorRoles.FromTriggerEvent(Event(new { target = victim }), Array.Empty<EntityReference>());
            Check(targetOnly.Get("event-target") == victim, "event-target reads the payload's `target` port");
        }

        // A port the event names and answers null is that role's absence, and it says nothing about the other roles.
        {
            var explicitNull = RuntimeActorRoles.FromTriggerEvent(Event(new { target = victim, source = (EntityReference?)null, owner = (EntityReference?)null }), Array.Empty<EntityReference>());
            Check(explicitNull.Get("source") == null && explicitNull.Get("owner") == null && explicitNull.Get("event-target") == victim
                && explicitNull.Get("instigator") == null,
                "an explicit null is an absence of that role, not of the others");
        }

        // `self` is the entity the plan's own mount accepted, deduplicated: the same entity accepted twice is one
        // subject, while two different entities leave the role unreadable rather than answered with a choice.
        {
            var one = RuntimeActorRoles.FromTriggerEvent(Event(new { target = victim }), new[] { actor, actor });
            Check(one.Get("self") == actor, "the same entity accepted on two ports is one subject");
            var two = RuntimeActorRoles.FromTriggerEvent(Event(new { target = victim, source = actor }), new[] { actor, victim });
            RejectCode(() => two.Get("self"), "actor-ambiguous", "a mount that accepted two entities refuses self");
            RejectCode(() => two.Require("self"), "actor-ambiguous", "requiring an ambiguous self refuses by name");
            Check(two.Get("source") == actor && two.Get("event-target") == victim,
                "an unreadable self does not refuse the roles that do have a slot");
            var none = RuntimeActorRoles.FromTriggerEvent(Event(new { target = victim }), Array.Empty<EntityReference>());
            Check(none.Get("self") == null, "a mount that accepted nothing leaves self absent");
        }

        // The spelling of the five roles and of their two port spellings lives in the table alone, and a name outside
        // it is refused rather than ignored.
        Check(RuntimeActorRoles.Roles.SequenceEqual(new[] { "self", "source", "owner", "instigator", "event-target" })
            && RuntimeActorRoles.PortIds.SequenceEqual(new[] { "self", "source", "owner", "instigator", "event_target" }),
            "the role table spells the five roles and their ports once");
        Check(RuntimeActorRoles.IsRole("event-target") && !RuntimeActorRoles.IsRole("event_target") && !RuntimeActorRoles.IsRole("caster"),
            "only the five names are roles; the payload's port spelling is not one of them");
        Check(RuntimeActorRoles.RoleForPort("event_target") == "event-target" && RuntimeActorRoles.RoleForPort("target") == null
            && RuntimeActorRoles.PortFor("event-target") == "event_target",
            "the table relates the two spellings of one role and knows no port outside it");
        RejectCode(() => RuntimeActorRoles.PortFor("caster"), "actor-role", "a port is asked for by a role the table knows");
        RejectCode(() => new RuntimeActorContext(new Dictionary<string, EntityReference> { ["caster"] = actor }),
            "actor-role", "an unknown role name is refused");
        var single = new RuntimeActorContext(new Dictionary<string, EntityReference> { ["source"] = actor });
        Check(single.Require("source") == actor, "a carried role reads itself");
        RejectCode(() => single.Require("self"), "actor-missing", "requiring an absent role refuses by name");

        // The same rules end to end: the plan's own mount decides `self`, and a step that reads it is refused by the
        // one code the dispatch's own outcome names — absent, ambiguous, or the subject the mount settled on.
        var harness = new Harness();
        {
            // The plan's recipient rides one payload port and the mount's subject another, so the case shows the two
            // are independent: `self` is what the mount accepted, not what the event is delivered to.
            var context = harness.Advance("subject", "self", new Dictionary<string, object?>
                { ["other"] = harness.Entity("recipient"), ["target"] = harness.Entity("victim"), ["source"] = harness.Entity("cause") },
                new[] { SubjectMount("victim") });
            Check(context != null && context.Actors.Get("self") == harness.Entity("victim")
                && context.Actors.Get("source") == harness.Entity("cause") && context.Actors.Get("event-target") == harness.Entity("victim"),
                "the entity the plan's own mount accepted is self, next to the roles the payload carries ["
                + harness.LastStep?.Result.Status + ":" + harness.LastStep?.Result.Code + "]");
        }
        {
            var context = harness.Advance("level-only", "self", new Dictionary<string, object?>
                { ["other"] = harness.Entity("recipient"), ["target"] = harness.Entity("victim") },
                new[] { LevelMount });
            Check(context == null && harness.LastStep is { Result.Status: "rejected", Result.Code: "actor-missing" },
                "a mount that provides no subject leaves self absent, and reading it refuses the step ["
                + harness.LastStep?.Result.Status + ":" + harness.LastStep?.Result.Code + "]");
        }
        {
            var context = harness.Advance("ambiguous", "self", new Dictionary<string, object?>
                { ["other"] = harness.Entity("recipient"), ["target"] = harness.Entity("victim"), ["source"] = harness.Entity("actor") },
                new[] { SubjectMount("actor"), SubjectMount("victim") });
            Check(context == null && harness.LastStep is { Result.Status: "rejected", Result.Code: "actor-ambiguous" },
                "two different accepted entities refuse a step that needs self by that name, not as an absence ["
                + harness.LastStep?.Result.Status + ":" + harness.LastStep?.Result.Code + "]");
        }
        {
            var context = harness.Advance("same-entity", "self", new Dictionary<string, object?>
                { ["other"] = harness.Entity("recipient"), ["target"] = harness.Entity("victim"), ["source"] = harness.Entity("victim") },
                new[] { SubjectMount("victim") });
            Check(context != null && context.Actors.Get("self") == harness.Entity("victim"),
                "the same entity on two ports is one subject, so the step still runs");
        }
        {
            var context = harness.Advance("source-only", "source", new Dictionary<string, object?>
                { ["other"] = harness.Entity("recipient"), ["source"] = harness.Entity("cause") },
                new[] { LevelMount });
            Check(context != null && context.Actors.Get("source") == harness.Entity("cause") && context.Actors.Get("self") == null
                && context.Actors.Get("event-target") == null,
                "with only `source` carried, self and event-target are both absent at dispatch too");
        }
        {
            var context = harness.Advance("decoy-port", "event-target", new Dictionary<string, object?>
                { ["other"] = harness.Entity("recipient"), ["event_target"] = harness.Entity("victim") }, new[] { LevelMount });
            Check(context != null && context.Actors.Get("event-target") == null,
                "a payload that names only `event_target` answers no event-target role");
        }
        {
            var context = harness.Advance("explicit-null", "event-target", new Dictionary<string, object?>
                { ["other"] = harness.Entity("recipient"), ["target"] = null, ["source"] = harness.Entity("cause") },
                new[] { LevelMount });
            Check(context != null && context.Actors.Get("event-target") == null && context.Actors.Get("source") == harness.Entity("cause"),
                "a target port answered with null is an absence, never a fallback behind it");
        }
        return checks;
    }

    /// <summary>One step as the plan builder writes it: the binding by name, everything else already wire-shaped.</summary>
    private sealed record StepRow(string NodeId, string NodeKind, string BindingId, object Layout, object[] Inputs, int?[] Successors);

    /// <summary>
    /// The five selectors of the contract as capabilities of this fixture, each writing an output port named after
    /// its role, plus the two actions a plan consumes a role through: one takes the role value, the other declares
    /// `event_target` as a required context-role input. The trigger's payload declares one nullable port per role
    /// spellings — including the two the table does not read — because whether an event carries a role is exactly
    /// what the cases vary.
    /// </summary>
    private sealed class Harness
    {
        private const string RequiredRoleCapability = Id + ".required";
        private const string RequiredRoleBinding = Id + ".binding.required";
        private readonly Dictionary<string, EvaluationContext> observed = new(StringComparer.Ordinal);
        internal readonly List<string> Applied = new();
        internal CommandReceipt? LastStep;

        /// <summary>
        /// A kernel with one plan is the unit under test, so every case boots its own: a trigger binding is shared
        /// by every plan loaded into one kernel, and a case that observed a second plan's action would be reading
        /// another case's work. The module is the same provider every time.
        /// </summary>
        private (RuntimeKernel Kernel, RuntimeModuleHandle Module) Boot()
        {
            var kernel = new RuntimeKernel(Fixture.Identity);
            kernel.BeginWorld(1);
            kernel.RegisterModule(MountOwner(), RuntimeLogLevel.Off);
            var definition = Fixture.Module(Id) with
            {
                Handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
                {
                    [Fixture.Handler(Id)] = _ => Commit(Id + ".applied"),
                    [Id + ".handler.required"] = _ => Commit(Id + ".required.applied")
                }
            };
            var seed = JsonNode.Parse(definition.RegistryJson)!;
            // The payload ports every case varies: the recipient the action's plan wires (`other`), the contract's own
            // `target` plus one optional nullable port per role spelling — including `self` and `event_target`, which
            // no role is read from — so a case names exactly the ports its event carries.
            var ports = new List<object> { new { id = "next", type = "execution" }, new { id = "other", type = "entity" } };
            foreach (var port in RuntimeActorRoles.PortIds.Append("target"))
                ports.Add(new { id = port, type = "entity", optional = true, nullable = true });
            seed["capabilities"]!.AsArray()[0]!["graph"]!["outputs"] = JsonNode.Parse(RuntimeJson.From(ports.ToArray()).GetRawText())!;
            // The action reads its recipient plus one nullable role value: a nullable observation may only feed a
            // nullable input, and four of the five role selectors are nullable by contract.
            seed["capabilities"]!.AsArray()[1]!["graph"]!["inputs"] = JsonNode.Parse(RuntimeJson.From(new object[]
            {
                new { id = "in", type = "execution" }, new { id = "target", type = "entity" },
                new { id = "role", type = "entity", nullable = true }
            }).GetRawText())!;
            // The required-role action declares a context role's own name as an ordinary required input, so the
            // rule under test is the wiring's, not the role table's.
            seed["capabilities"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = RequiredRoleCapability, owner = Id, kind = "action", label = "Role-named input action", version = "1.0.0",
                parameters = new { },
                graph = new
                {
                    domains = new[] { "enemy" }, execution = "host",
                    inputs = new object[] { new { id = "in", type = "execution" }, new { id = "owner", type = "entity" } },
                    outputs = new object[] { new { id = "next", type = "execution" },
                        new { id = "result", type = "result", schema = "example.result.apply", fields = Fixture.ResultFields } },
                    parameters = Array.Empty<object>(),
                    recipients = new { input = "owner", target = "entity", cardinality = "one", requires = new[] { "health.current" }, result = "result" }
                }
            }).GetRawText())!);
            seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = RequiredRoleBinding, capabilityId = RequiredRoleCapability, providerId = Id, handler = Id + ".handler.required",
                role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
            }).GetRawText())!);
            var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal);
            var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
            {
                [Fixture.Handler(Id)] = new HandlerShape().Inputs("target").Outputs("result"),
                [Id + ".handler.required"] = new HandlerShape().Inputs("owner").Outputs("result")
            };
            var support = new List<BindingSupport>
            { Fixture.Support(Id)[0], Fixture.Support(Id)[1], new BindingSupport(RequiredRoleBinding, "implementation-only", Fixture.Permissions) };
            foreach (var role in RuntimeActorRoles.Roles)
            {
                var port = RuntimeActorRoles.PortFor(role);
                seed["capabilities"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
                {
                    id = Id + "." + port, owner = Id, kind = "selector", label = role + " selector", version = "1.0.0",
                    parameters = new { },
                    graph = new
                    {
                        domains = new[] { "enemy" }, execution = "query", inputs = Array.Empty<object>(),
                        outputs = new object[] { new { id = port, type = "entity", nullable = role != "self" } },
                        parameters = Array.Empty<object>()
                    }
                }).GetRawText())!);
                seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
                {
                    id = Binding(role), capabilityId = Id + "." + port, providerId = Id, handler = Id + ".handler." + port,
                    role = "evaluate", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
                }).GetRawText())!);
                evaluators[Id + ".handler." + port] = context =>
                {
                    observed[context.NodeId] = context;
                    return RuntimeJson.From(new Dictionary<string, object?> { [port] = context.Actors.Get(role) });
                };
                shapes[Id + ".handler." + port] = new HandlerShape().Outputs(port);
                support.Add(new BindingSupport(Binding(role), "implementation-only", NoPermissions));
            }
            var module = kernel.RegisterModule(definition with
            {
                RegistryJson = seed.ToJsonString(), Evaluators = evaluators, Shapes = shapes, BindingSupport = support.ToArray(),
                EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [Id] = reference => Current(kernel, reference) },
                EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                { [Id] = reference => Current(kernel, reference) ? Snapshot(Name(reference)) : null }
            }, RuntimeLogLevel.Off);
            // Two factions are enough for the delivery check: one hostile pair and one ally pair.
            kernel.SetFactionRelations(new RuntimeFactionRelations(new[]
            { new RuntimeFactionRelation("blue", "red", "hostile"), new RuntimeFactionRelation("blue", "blue", "ally") }));
            kernel.StartRuntime(() => { });
            return (kernel, module);
        }

        /// <summary>The provider that owns the fixture's two mount kinds: a subject kind whose matcher accepts every
        /// entity of this harness, and the level kind that judges the mount target alone and so provides no subject.
        /// A plan claiming both is claimed by either, and its subjects are the ones the subject kind accepted.</summary>
        private static RuntimeModule MountOwner() => new(RuntimeKernel.ApiVersion,
            RuntimeJson.From(new
            {
                providers = new[] { new { id = "example.mounts", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
                capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
            }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
            {
                [SubjectKind] = AttachmentMatcherRegistration.BySubject((category, reference, subject) =>
                    category == null && subject.Id == Id + ":" + reference),
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                    category == null && reference == Fixture.MountReference)
            }
        };

        private static string Binding(string role) => Id + ".binding." + RuntimeActorRoles.PortFor(role);
        private static string Name(EntityReference reference) => reference.Id[(reference.Id.IndexOf(':') + 1)..];
        private static bool Current(RuntimeKernel kernel, EntityReference reference)
            => reference.Id.StartsWith(Id + ":", StringComparison.Ordinal) && reference.WorldEpoch == kernel.WorldEpoch;
        private static bool Current(EntityReference reference)
            => reference.Id.StartsWith(Id + ":", StringComparison.Ordinal) && reference.WorldEpoch == 1 && reference.LifeEpoch == 1;

        internal EntityReference Entity(string name) => new(Id + ":" + name, 1, 1);
        internal RuntimeEntitySnapshot Snapshot(string name)
            => new(Entity(name), "player", name is "subject" or "holder" ? "blue" : "red", "alive",
                Array.Empty<string>(), Array.Empty<string>(), new[] { 0d, 0d, 0d });

        private CommandResult Commit(string name) { Applied.Add(name); return CommandResult.Succeeded(RuntimeJson.From(new { actual = 1 })); }

        /// <summary>
        /// Loads one plan whose step 0 is the given role's selector and step 1 is the action that reads it, publishes
        /// one event with the payload the case names and advances one tick. The context the role step was evaluated
        /// with comes back, or null when that step was refused before its handler ran.
        /// </summary>
        internal EvaluationContext? Advance(string planId, string role, Dictionary<string, object?> payload, object[] attachments)
        {
            var (kernel, module) = Boot();
            var trigger = kernel.ResolveGraphContract(Fixture.TriggerCapability(Id), "1.0.0", RuntimeJson.EmptyObject);
            var action = kernel.ResolveGraphContract(Fixture.ActionCapability(Id), "1.0.0", RuntimeJson.From(new { amount = 5 }));
            var selector = kernel.ResolveGraphContract(Id + "." + RuntimeActorRoles.PortFor(role), "1.0.0", RuntimeJson.EmptyObject);
            var steps = new[]
            {
                new StepRow("S0_role", "query", Binding(role), Layout(selector, Array.Empty<object>()), Array.Empty<object>(), Array.Empty<int?>()),
                new StepRow("S1_action", "action", ActionBinding, Layout(action, new object[] { 5 }), new object[]
                {
                    Wired(Port(action, "inputs", "target"), "event", Port(trigger, "outputs", "other")),
                    Wired(Port(action, "inputs", "role"), "step", 0)
                }, new int?[] { null })
            };
            kernel.LoadPlan(Plan(kernel, planId, trigger, steps, start: 1, attachments));
            // Only the ports the case names are in the payload; a port left out is one the event never mentions,
            // which is a different event than one that answers null for it.
            var frame = new Dictionary<string, object?>();
            foreach (var port in payload) frame[port.Key] = port.Value;
            observed.Remove("S0_role");
            LastStep = null;
            var queued = kernel.Publish(module, new RuntimeEvent(planId + "-event", TriggerBinding, 1, 1, "shared-scope", RuntimeJson.From(frame)));
            if (queued.Status != "queued") throw new Exception("FAIL: " + planId + " was not queued: " + queued.Status + "/" + queued.Code);
            var tick = kernel.Advance(1, true);
            LastStep = tick.Commands.Count > 0 ? tick.Commands[^1] : null;
            if (tick.Commands.Count == 0) throw new Exception("FAIL: " + planId + " executed no step at all");
            return observed.TryGetValue("S0_role", out var context) ? context : null;
        }

        private static int Port(JsonElement contract, string side, string id) => RuntimeJson.Rows(contract, side)
            .Select((p, index) => (p, index)).Single(x => RuntimeJson.Text(x.p, "id") == id).index;
        private static object Wired(int slot, string from, int source) => from == "event"
            ? new { slot, fromEventSlot = source }
            : new { slot, fromStepSlot = new { step = source, port = 0 } };

        private static string Plan(RuntimeKernel kernel, string planId, JsonElement trigger, StepRow[] steps, int start, object[] attachments)
        {
            var manifest = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry");
            var capabilities = manifest.GetProperty("capabilities").EnumerateArray()
                .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("version").GetString()!, StringComparer.Ordinal);
            // The plan's pin table is exactly its own closure, ordinal sorted, the way the compiler writes it.
            var used = steps.Select(step => step.BindingId).Append(TriggerBinding).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            int Pin(string binding) => Array.IndexOf(used, binding);
            var pins = used.Select(id =>
            {
                var row = manifest.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("id").GetString() == id);
                var capabilityId = row.GetProperty("capabilityId").GetString()!;
                return (object)new
                {
                    bindingId = id, capabilityId, capabilityVersion = capabilities[capabilityId],
                    providerId = row.GetProperty("providerId").GetString(), providerVersion = "1.0.0",
                    handler = row.GetProperty("handler").GetString()
                };
            }).ToArray();
            var rows = steps.Select(step => (object)new
            {
                nodeId = step.NodeId, nodeKind = step.NodeKind, binding = Pin(step.BindingId),
                layout = step.Layout, inputs = step.Inputs, successors = step.Successors
            }).ToArray();
            return RuntimeJson.From(new
            {
                schemaVersion = 1, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" },
                runtime = kernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
                permissions = Fixture.Permissions, dependencies = Array.Empty<string>(),
                limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth },
                bindings = pins, attachments,
                entrypoints = new[] { new { nodeId = "Entry", binding = Pin(TriggerBinding),
                    layout = Layout(trigger, Array.Empty<object>()), start, steps = rows } }
            }).GetRawText();
        }
    }
}
