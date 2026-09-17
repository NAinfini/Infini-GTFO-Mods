using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeRuntime.Tests.Perf;

/// <summary>
/// The one provider and the plan shape every benchmark in this project runs against. It is deliberately the
/// smallest world the kernel accepts — one host trigger, one host action, one query row, one presentation row and
/// the kernel's own `sequence` control — because a benchmark that needed a second runtime would be measuring the
/// harness rather than the kernel.
///
/// Nothing here is registered in a shipped package: this project only adds files under `tests/Perf`.
/// </summary>
internal static class PerfWorld
{
    internal const string Provider = "forge.perf";
    internal const string TriggerBinding = Provider + ".binding.trigger";
    internal const string Action = Provider + ".binding.apply";
    internal const string Query = Provider + ".binding.query";
    internal const string Present = Provider + ".binding.present";
    internal const string ActionHandler = Provider + ".handler.apply";
    internal const string QueryHandler = Provider + ".handler.query";
    internal const string PresentHandler = Provider + ".handler.present";
    internal const string MountProvider = "forge.perf.mounts";
    internal const string MountReference = "perf-level";
    internal const string TriggerCapability = Provider + ".trigger";
    internal const string ActionCapability = Provider + ".apply";
    internal const string QueryCapability = Provider + ".query";
    internal const string PresentCapability = Provider + ".present";
    internal const string SequenceCapability = "forge.control.flow.sequence";
    internal const string ParallelCapability = "forge.control.flow.parallel_all";
    internal const string ResultSchema = Provider + ".result";
    internal const string PresentResultSchema = Provider + ".present.result";

    /// <summary>The namespace an entity reference is routed by: a candidate has to be of the kind its source is
    /// registered under, so the reference prefix and the candidate-source key are the same string.</summary>
    internal const string EntityKind = Provider;
    internal const string Subject = Provider + ":1";

    internal static readonly EntityReference SubjectRef = new(Subject, 1, 1);

    private static readonly object[] ResultFields =
    {
        new { id = "target", type = "entity" },
        new { id = "status", type = "enum", schema = "execution_outcome" },
        new { id = "committed", type = "enum", schema = "commit_state" },
        new { id = "code", type = "string" }
    };

    /// <summary>One step of a plan: the node identity, the addressable port frame, and where the walk goes next.</summary>
    internal sealed record StepDesc(string NodeId, string NodeKind, string BindingId, JsonElement Layout,
        IReadOnlyList<object> Inputs, int?[] Successors, string? Control = null, JsonElement? Parameters = null);

    /// <summary>
    /// A world with the capability set a benchmark asked for and one loaded plan. <see cref="QueryEvaluations"/>
    /// counts `evaluate` handler calls, which is what proves a memoized read is one evaluation rather than N.
    /// </summary>
    internal sealed class World : IDisposable
    {
        internal readonly RuntimeKernel Kernel;
        internal readonly RuntimeModuleHandle Module;
        internal long QueryEvaluations;
        internal long HandlerCalls;
        internal int RecipientCount = 8;

        internal World(bool query = false, bool presentation = false, RuntimeLimits? limits = null,
            Func<World, Trigger>? plan = null, int candidates = 100)
        {
            Kernel = new RuntimeKernel(Harness.Identity, limits);
            Kernel.BeginWorld(1);
            Kernel.RegisterModule(Mounts(), RuntimeLogLevel.Off);
            // The kernel's own `forge.control.*` rows are a module like any other, so a plan that uses one needs it
            // registered. The host does this for itself; the benchmark stands the same registration up.
            Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
            Module = Kernel.RegisterModule(Definition(query, presentation, candidates), RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { });
            if (plan == null) return;
            var entry = plan(this);
            var json = PlanJson(Kernel, "perf.plan", entry);
            var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded("perf/plan.plan.json", json) })[0];
            if (!outcome.Loaded)
                throw new RuntimeContractException(outcome.Code ?? "invalid", "plan rejected: " + outcome.Code + " " + outcome.Detail);
        }

        internal DispatchResult Publish(string eventId, long tick = 1)
            => Module.Publish(new RuntimeEvent(eventId, TriggerBinding, 1, tick, "perf-scope",
                RuntimeJson.From(new { target = SubjectRef })));

        internal TickResult Advance(long tick) => Kernel.Advance(tick, true);

        public void Dispose() => Module.Dispose();

        /// <summary>
        /// One entrypoint: the trigger the plan hangs on (carried by the entry row, never by the step array), the
        /// step the walk starts at, and the steps themselves.
        /// </summary>
        internal sealed record Trigger(JsonElement Layout, int Start, IReadOnlyList<StepDesc> Steps);

        /// <summary>The entry row of a plan: one trigger, and the steps its `next` exit starts.</summary>
        internal static Trigger Entry(RuntimeKernel kernel, params StepDesc[] steps) => Entry(kernel, 0, steps);

        /// <summary>The same entry row starting at a named step. `start` is never a read-only step: the walk begins
        /// at an action or a control, and a query's output is read by one of those.</summary>
        internal static Trigger Entry(RuntimeKernel kernel, int start, params StepDesc[] steps)
            => new(Layout(Contract(kernel, TriggerCapability)), start, steps);

        /// <summary>One action step: its entity input comes from the trigger's payload, or from another step's
        /// output when one is named; its second input reads a `query` step's result.</summary>
        internal static StepDesc ActionStep(RuntimeKernel kernel, string nodeId, int? fromStep, int? fromPort, int? next,
            bool targetFromStep = false)
        {
            var layout = Layout(Contract(kernel, ActionCapability));
            var wires = new List<object>();
            if (targetFromStep)
                wires.Add(new { slot = Slot(kernel, ActionCapability, "inputs", "target"), fromStepSlot = new { step = fromStep!.Value, port = 0 } });
            else
                wires.Add(new { slot = Slot(kernel, ActionCapability, "inputs", "target"), fromEventSlot = Slot(kernel, TriggerCapability, "outputs", "target") });
            if (fromStep != null)
                wires.Add(new { slot = Slot(kernel, ActionCapability, "inputs", "candidates"), fromStepSlot = new { step = fromStep.Value, port = fromPort ?? 0 } });
            return new StepDesc(nodeId, "action", Action, layout, wires, new int?[] { next });
        }

        /// <summary>
        /// One `forge.control.flow.parallel_all`: every branch is entered inside this one activation, which is what
        /// puts several reads of one `query` result in the same memo. Its exits are the branches in declaration order
        /// and then the single `next` the step continues from, so the successor array is `branches + 1` long.
        /// </summary>
        internal static StepDesc ParallelStep(RuntimeKernel kernel, string nodeId, int branches, int?[] successors) => new(
            nodeId, "control", ParallelCapability,
            Layout(Contract(kernel, ParallelCapability, new { branch_count = branches }), branches),
            Array.Empty<object>(), successors);

        /// <summary>One `query` step reading the provider's own candidate source. Its output is read on demand and
        /// never walked, so it has no successors.</summary>
        internal static StepDesc QueryStep(RuntimeKernel kernel, string nodeId) => new(
            nodeId, "query", Query, Layout(Contract(kernel, QueryCapability)), Array.Empty<object>(), Array.Empty<int?>());

        /// <summary>One `presentation` step: the host decides, the addressed sessions write.</summary>
        internal static StepDesc PresentStep(RuntimeKernel kernel, string nodeId, int? next)
        {
            var layout = Layout(Contract(kernel, PresentCapability));
            return new StepDesc(nodeId, "action", PerfWorld.Present, layout,
                new object[] { new { slot = Slot(kernel, PresentCapability, "inputs", "target"), fromEventSlot = Slot(kernel, TriggerCapability, "outputs", "target") } },
                new int?[] { next });
        }

        /// <summary>The kernel's own resolved contract for one capability, at the parameter set the plan uses.</summary>
        private static JsonElement Contract(RuntimeKernel kernel, string capabilityId, object? parameters = null)
            => kernel.ResolveGraphContract(capabilityId, "1.0.0", parameters == null ? RuntimeJson.EmptyObject : RuntimeJson.From(parameters));

        /// <summary>Positional constant frame: one slot per declared parameter, in declaration order.</summary>
        private static readonly object?[] NoConstants = Array.Empty<object?>();

        /// <summary>Dense slot frame of one side of a resolved contract, written the way the website writes it. The
        /// constant frame is positional, so a capability with one structural parameter states that one value.</summary>
        internal static JsonElement Layout(JsonElement resolved, params object?[] constants)
        {
            var frame = constants.Length == 0 ? NoConstants : constants;
            return RuntimeJson.From(new
            {
                inputs = Slots(resolved, "inputs"),
                outputs = Slots(resolved, "outputs"),
                constants = frame,
                promoted = Array.Empty<int>()
            });
        }

        private static object[] Slots(JsonElement resolved, string side)
        {
            var rows = resolved.GetProperty(side).EnumerateArray().ToArray();
            var slots = new object[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                var port = rows[i];
                var type = port.GetProperty("type").GetString()!;
                slots[i] = new
                {
                    index = i,
                    type = Array.IndexOf(RuntimeGraphContracts.PortTypes, type),
                    cardinality = port.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0,
                    valueSet = -1, lifetime = -1,
                    optional = port.TryGetProperty("optional", out var o) && o.GetBoolean(),
                    nullable = port.TryGetProperty("nullable", out var n) && n.GetBoolean()
                };
            }
            return slots;
        }

        /// <summary>The slot one named port of one declared side occupies in the resolved contract.</summary>
        internal static int Slot(RuntimeKernel kernel, string capabilityId, string side, string port)
        {
            var resolved = Contract(kernel, capabilityId);
            return resolved.GetProperty(side).EnumerateArray()
                .Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == port).i;
        }

        private static RuntimeModule Mounts() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = MountProvider, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(),
            bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
            {
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == MountReference)
            }
        };

        private RuntimeModule Definition(bool query, bool presentation, int candidates)
        {
            var capabilities = new List<object>
            {
                new
                {
                    id = Provider + ".trigger", owner = Provider, kind = "trigger", label = "Perf trigger",
                    version = "1.0.0", parameters = new { },
                    graph = new
                    {
                        domains = new[] { "map" }, execution = "host", inputs = Array.Empty<object>(),
                        outputs = new object[] { new { id = "next", type = "execution" }, new { id = "target", type = "entity" } },
                        parameters = Array.Empty<object>()
                    }
                },
                new
                {
                    id = Provider + ".apply", owner = Provider, kind = "action", label = "Perf action",
                    version = "1.0.0", parameters = new { },
                    graph = new
                    {
                        domains = new[] { "map" }, execution = "host",
                        inputs = new object[]
                        {
                            new { id = "in", type = "execution" },
                            new { id = "target", type = "entity" },
                            new { id = "candidates", type = "entity", cardinality = "many", optional = true }
                        },
                        outputs = new object[]
                        {
                            new { id = "next", type = "execution" },
                            new { id = "result", type = "result", schema = ResultSchema, fields = ResultFields }
                        },
                        parameters = Array.Empty<object>(),
                        recipients = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" }
                    }
                }
            };
            var bindings = new List<object>
            {
                new { id = TriggerBinding, capabilityId = TriggerCapability, providerId = Provider, handler = Provider + ".handler.trigger",
                    role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
                new { id = Action, capabilityId = ActionCapability, providerId = Provider, handler = ActionHandler,
                    role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
            };
            var handlers = new Dictionary<string, CommandHandler> { [ActionHandler] = Apply };
            var evaluators = new Dictionary<string, EvaluatorHandler>();
            var shapes = new Dictionary<string, HandlerShape> { [ActionHandler] = new HandlerShape().Inputs("target", "candidates") };
            var support = new List<BindingSupport>
            {
                new(TriggerBinding, "implementation-only", Array.Empty<string>()),
                new(Action, "implementation-only", Array.Empty<string>())
            };
            if (query)
            {
                capabilities.Add(new
                {
                    id = QueryCapability, owner = Provider, kind = "selector", label = "Perf candidates",
                    version = "1.0.0", parameters = new { },
                    graph = new
                    {
                        domains = new[] { "map" }, execution = "query", inputs = Array.Empty<object>(),
                        outputs = new object[] { new { id = "targets", type = "entity", cardinality = "many" } },
                        parameters = Array.Empty<object>(),
                        reads = new[] { "world" }
                    }
                });
                bindings.Add(new { id = Query, capabilityId = QueryCapability, providerId = Provider, handler = QueryHandler,
                    role = "evaluate", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() });
                evaluators[QueryHandler] = Evaluate;
                shapes[QueryHandler] = new HandlerShape().Outputs("targets");
                support.Add(new BindingSupport(Query, "implementation-only", Array.Empty<string>()));
            }
            if (presentation)
            {
                capabilities.Add(new
                {
                    id = PresentCapability, owner = Provider, kind = "action", label = "Perf presentation",
                    version = "1.0.0", parameters = new { },
                    graph = new
                    {
                        domains = new[] { "map" }, execution = "presentation",
                        inputs = new object[] { new { id = "in", type = "execution" }, new { id = "target", type = "entity" } },
                        outputs = new object[]
                        {
                            new { id = "next", type = "execution" },
                            new { id = "result", type = "result", schema = PresentResultSchema, fields = ResultFields }
                        },
                        parameters = Array.Empty<object>(),
                        recipients = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" }
                    }
                });
                bindings.Add(new { id = PerfWorld.Present, capabilityId = PresentCapability, providerId = Provider, handler = PresentHandler,
                    role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() });
                handlers[PresentHandler] = Present;
                shapes[PresentHandler] = new HandlerShape().Inputs("target");
                support.Add(new BindingSupport(PerfWorld.Present, "implementation-only", Array.Empty<string>()));
            }
            var json = RuntimeJson.From(new { providers = new[] { new { id = PerfWorld.Provider, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } }, capabilities, bindings });
            return new RuntimeModule(RuntimeKernel.ApiVersion, json.GetRawText(), handlers, support)
            {
                Evaluators = evaluators,
                Shapes = shapes,
                // The trigger payload carries a live reference, so the provider that owns the kind has to answer for
                // it: the kernel never validates an entity no resolver claims.
                // The provider answers for its whole kind, not only for the event's own subject: the candidate source
                // hands the kernel `forge.perf:0..n-1`, and a resolver that accepted only `Subject` would make every
                // selector result a `stale-entity` refusal, which is a harness that measures its own rejection.
                EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
                {
                    [EntityKind] = reference => reference.WorldEpoch == 1 && reference.LifeEpoch == 1
                        && reference.Id.Length > EntityKind.Length + 1
                        && reference.Id.StartsWith(EntityKind + ":", StringComparison.Ordinal)
                },
                EntityCandidates = query
                    ? new Dictionary<string, Func<IReadOnlyList<EntityReference>>>
                    {
                        [EntityKind] = () => CandidateSet(candidates)
                    }
                    : null,
                PresentationSessions = presentation
                    ? new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>
                    {
                        [PerfWorld.Provider] = _ => Enumerable.Range(1, RecipientCount).Select(i => i.ToString()).ToArray()
                    }
                    : null
            };
        }

        private static EntityReference[] CandidateSet(int count)
        {
            var set = new EntityReference[count];
            for (var i = 0; i < count; i++) set[i] = new EntityReference(EntityKind + ":" + i, 1, 1);
            return set;
        }

        private CommandResult Apply(CommandContext context)
        {
            HandlerCalls++;
            return CommandResult.Succeeded(RuntimeJson.From(new { actual = 1 }));
        }

        private JsonElement Evaluate(EvaluationContext context)
        {
            QueryEvaluations++;
            if (!context.Query.TryCandidates(EntityKind, out var candidates, out var code))
                throw new RuntimeContractException(code, code);
            return RuntimeJson.From(new { targets = candidates });
        }

        private CommandResult Present(CommandContext context)
            => CommandResult.Partial(RuntimeJson.From(new { presented = true }), CommitStates.None, Array.Empty<RuntimeFact>());

        /// <summary>
        /// Builds one plan in the wire shape the loader accepts. Every layout is the kernel's own resolved contract,
        /// so a benchmark cannot state a port frame the runtime would not.
        /// </summary>
        private static string PlanJson(RuntimeKernel kernel, string planId, Trigger entry)
        {
            var manifest = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry");
            var steps = entry.Steps;
            // The trigger reaches the plan through the entry row's own binding lock, so it is one of the pins.
            var pinIds = steps.Select(BindingOf).Append(TriggerBinding).Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            object Pin(string bindingId)
            {
                var row = manifest.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("id").GetString() == bindingId);
                var capabilityId = row.GetProperty("capabilityId").GetString()!;
                return new
                {
                    bindingId, capabilityId,
                    capabilityVersion = manifest.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("id").GetString() == capabilityId).GetProperty("version").GetString()!,
                    providerId = row.GetProperty("providerId").GetString()!,
                    providerVersion = manifest.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == row.GetProperty("providerId").GetString()).GetProperty("version").GetString()!,
                    handler = row.GetProperty("handler").GetString()!
                };
            }
            // A step names its binding by position in the pin table, so a control row states its capability and the
            // table resolves the binding that capability is registered under.
            string BindingOf(StepDesc step) => step.NodeKind == "control"
                ? manifest.GetProperty("bindings").EnumerateArray()
                    .Single(b => b.GetProperty("capabilityId").GetString() == step.BindingId).GetProperty("id").GetString()!
                : step.BindingId;
            // The loader reads a step row as exactly these five keys; `control` and `parameters` are not part of a
            // row and are carried by the capability the binding resolves to.
            var rows = steps.Select(step => (object)new
            {
                nodeId = step.NodeId,
                nodeKind = step.NodeKind,
                binding = Array.IndexOf(pinIds, BindingOf(step)),
                layout = step.Layout,
                inputs = step.Inputs.ToArray(),
                successors = step.Successors
            }).ToArray();
            return RuntimeJson.From(new
            {
                schemaVersion = 1, kind = "forge-runtime-plan", planId,
                resource = new { id = "forge.perf.resource", revision = "1" }, runtime = kernel.Identity,
                domain = "map", authority = "host", failurePolicy = "stop-entrypoint",
                permissions = Array.Empty<string>(), dependencies = Array.Empty<string>(),
                limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth },
                bindings = pinIds.Select(Pin).ToArray(),
                attachments = new object[] { new { kind = "level", reference = MountReference } },
                entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(pinIds, TriggerBinding), start = entry.Start, layout = entry.Layout, steps = rows } }
            }).GetRawText();
        }
    }
}
