using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

/// <summary>
/// The one read that tells "the kernel proved this reference is gone" apart from "the kernel could not observe
/// it", and the one contract that reads through it. A live reference is `Current`; a world that ended or a life the
/// owning resolver no longer knows is `Absent` — an answer, not a refusal, because the row declaring this read is
/// defined as `false` there; anything else is `Unknown`, recorded as a refusal so the step is rejected rather than
/// handed a `false` that looks observed. The read is charged to the same per-tick query budget as a snapshot, and the
/// handler below deliberately ignores the third state: the kernel must reject the step anyway.
/// <para>
/// The row that declares the read is this fixture's own: `forge.condition.predicate.exists` was retired with the
/// rest of the targeting vocabulary, and the kernel-side rule it exercised — the tri-state presence read every
/// `query` row reaches through the step's own session — is what these cases pin, not any one package's handler.
/// </para>
/// </summary>
internal static class QueryPresenceTests
{
    private const string Id = "example.presence";
    private static readonly string[] NoPermissions = Array.Empty<string>();

    /// <summary>The compiled layout of one node: its two port lists and the constants its parameters resolved to.
    /// Nothing here is promoted, so the constant array is the whole parameter frame.</summary>
    private static object Layout(JsonElement contract, object[] constants)
        => new { inputs = RuntimeGraphContracts.Layout(contract, "inputs"), outputs = RuntimeGraphContracts.Layout(contract, "outputs"), constants, promoted = Array.Empty<int>() };

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }

        // A live reference: the step evaluates, the handler's frame reaches the action, and the session answered
        // `Current` rather than a refusal.
        {
            var plan = new PresencePlan("presence-current");
            var tick = plan.Dispatch("current");
            Check(plan.Presence == EntityPresence.Current && plan.Applied.SequenceEqual(new[] { true }),
                "a live reference reads as Current [" + string.Join("|", plan.Applied) + "]");
            Check(tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "succeeded"
                && plan.LastStep is { Result.Status: "succeeded" },
                "the action behind a Current read committed [" + plan.LastStep?.Result.Status + ":" + plan.LastStep?.Result.Code + "]");
        }

        // A life the resolver no longer knows is `Absent`: the handler answers `false`, the step is not refused,
        // and the action still runs. A stale identity is an answer here, which is the whole point of the read.
        // The subject resolves three times before the read — once while the event is published, once for the
        // action's recipient and once for the step's own input — and answers gone on the fourth, which is the
        // resolution the read itself makes. That is the case the read exists for: the world moved between the
        // step's input and the step's read, and the step is answered `false` instead of being refused.
        {
            var plan = new PresencePlan("presence-absent") { GoneOnCall = 4 };
            var tick = plan.Dispatch("absent");
            Check(plan.Presence == EntityPresence.Absent && plan.Applied.SequenceEqual(new[] { false }),
                "a life the resolver dropped reads as Absent, not as a refusal [" + plan.Presence + " on call " + plan.Calls + "]");
            Check(tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "succeeded" && plan.Calls == 4,
                "an Absent read does not reject the step [" + plan.LastStep?.Result.Status + ":" + plan.LastStep?.Result.Code + "]");
        }

        // An observation the kernel could not make stays a refusal even though the handler answered a frame:
        // the session's first refusal outranks the handler's own result and the step is rejected by name.
        foreach (var (description, code, breakObserver) in new (string, string, Action<PresencePlan>)[]
        {
            ("an observer that has no answer", "entity-observation-unavailable", plan => plan.OnObserve = _ => null),
            ("an observer that throws", "entity-observer-failed", plan => plan.OnObserve = _ => throw new InvalidOperationException("native read failed"))
        })
        {
            var plan = new PresencePlan("presence-unknown-" + code);
            breakObserver(plan);
            var tick = plan.Dispatch("unknown-" + code);
            Check(plan.Presence == EntityPresence.Unknown && plan.Applied.Count == 0
                && tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "rejected",
                description + " never reaches the action [" + plan.Presence + "]");
            Check(plan.LastStep is { Result.Status: "rejected", Result.Code: var refused } && refused == code,
                description + " rejects the step with its own code [" + plan.LastStep?.Result.Status + ":" + plan.LastStep?.Result.Code + "]");
        }

        // The fixture's own `presence` row, dispatched through the kernel: the same three answers, this time from
        // the registered handler. `Absent` is the whole point — the step succeeds with `false` — and `Unknown` still
        // rejects the step with the session's own code, never with a `false` the author would read as observed.
        {
            var plan = new PresencePlan("presence-registered-current");
            var tick = plan.Dispatch("registered-current");
            Check(plan.Applied.SequenceEqual(new[] { true }) && tick.Commands.Count == 1
                && tick.Commands[0].Result.Status == "succeeded",
                "the registered presence row answers true for a live reference [" + plan.LastStep?.Result.Status + ":" + plan.LastStep?.Result.Code + "]");
        }
        {
            var plan = new PresencePlan("presence-registered-absent") { GoneOnCall = 4 };
            var tick = plan.Dispatch("registered-absent");
            Check(plan.Applied.SequenceEqual(new[] { false }) && tick.Commands.Count == 1
                && tick.Commands[0].Result.Status == "succeeded" && plan.Calls == 4,
                "the registered presence row answers false for a life the resolver dropped [" + plan.LastStep?.Result.Status + ":" + plan.LastStep?.Result.Code + "]");
        }
        foreach (var (description, code, breakObserver) in new (string, string, Action<PresencePlan>)[]
        {
            ("an observer that has no answer", "entity-observation-unavailable", plan => plan.OnObserve = _ => null),
            ("an observer that throws", "entity-observer-failed", plan => plan.OnObserve = _ => throw new InvalidOperationException("native read failed"))
        })
        {
            var plan = new PresencePlan("presence-registered-" + code);
            breakObserver(plan);
            var tick = plan.Dispatch("registered-" + code);
            Check(plan.Applied.Count == 0 && tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "rejected"
                && plan.LastStep is { Result.Status: "rejected", Result.Code: var refused } && refused == code,
                description + " rejects the registered presence row with its own code [" + plan.LastStep?.Result.Status + ":" + plan.LastStep?.Result.Code + "]");
        }
        {
            // The registered row reads once per dispatch, so the tick's budget is spent at one read per event and
            // the refusal is the kernel's own `query-budget`, not a `false` the author would read as "gone".
            var plan = new PresencePlan("presence-registered-budget");
            var succeeded = 0; var refused = 0;
            for (var i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick + 1; i++)
            {
                plan.Dispatch("registered-budget-" + i);
                if (plan.LastStep is { Result.Status: "succeeded" }) succeeded++;
                if (plan.LastStep is { Result.Code: RuntimeAbiCodes.QueryBudget }) refused++;
            }
            Check(succeeded == RuntimeKernel.MaximumEntityQueriesPerTick && refused == 1,
                $"the registered presence row spends one query per dispatch: {succeeded} dispatches, {refused} refused");
        }

        // The budget is the kernel's one per-tick query budget, shared with every other read this session offers:
        // a plan that reads a presence and a snapshot per dispatch gets half as many dispatches before the tick is
        // spent, and the refusal is `query-budget` rather than a `false` the author would read as "gone".
        {
            var plan = new PresencePlan("presence-budget-shared", snapshotToo: true);
            var succeeded = 0; var refused = 0;
            for (var i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick / 2 + 1; i++)
            {
                plan.Dispatch("shared-" + i);
                if (plan.LastStep is { Result.Status: "succeeded" }) succeeded++;
                if (plan.LastStep is { Result.Code: RuntimeAbiCodes.QueryBudget }) refused++;
            }
            Check(succeeded == RuntimeKernel.MaximumEntityQueriesPerTick / 2 && refused == 1,
                $"a presence and a snapshot share one query budget: {succeeded} dispatches, {refused} refused");
        }
        {
            var plan = new PresencePlan("presence-budget-alone");
            var succeeded = 0; var refused = 0;
            for (var i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick + 1; i++)
            {
                plan.Dispatch("alone-" + i);
                if (plan.LastStep is { Result.Status: "succeeded" }) succeeded++;
                if (plan.LastStep is { Result.Code: RuntimeAbiCodes.QueryBudget }) refused++;
            }
            Check(succeeded == RuntimeKernel.MaximumEntityQueriesPerTick && refused == 1,
                $"the presence read alone spends one query per call: {succeeded} dispatches, {refused} refused");
        }
        return checks;
    }

    /// <summary>
    /// A plan whose `query` step answers a presence and whose action reads the boolean that came back. The action is
    /// what forces the query step and the step is where a refusal is reported, so the positive cases and the refusal
    /// cases are read off the same command receipt.
    /// </summary>
    private sealed class PresencePlan
    {
        private const string Binding = Id + ".binding.presence";
        private const string TriggerBinding = Id + ".binding.trigger";
        private readonly string planId;
        private readonly bool snapshotToo;
        private readonly RuntimeModuleHandle module;
        private readonly Dictionary<string, RuntimeEntitySnapshot> entities = new(StringComparer.Ordinal);
        internal readonly RuntimeKernel Kernel;
        internal readonly List<bool> Applied = new();
        internal CommandReceipt? LastStep;
        internal EntityPresence Presence;
        /// <summary>The resolution that answers "this life is gone", counted from the publish of the event. Zero
        /// never answers gone: the fixture's world only changes when a test says which resolution sees it change.</summary>
        internal int GoneOnCall;
        internal int Calls;
        internal Func<EntityReference, RuntimeEntitySnapshot?>? OnObserve;

        internal PresencePlan(string planId, bool snapshotToo = false)
        {
            this.planId = planId; this.snapshotToo = snapshotToo;            Kernel = new RuntimeKernel(Fixture.Identity);
            Kernel.BeginWorld(1);
            Kernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
            var subject = new EntityReference(Id + ":1", 1, 1);
            entities[subject.Id] = Snapshot(subject);
            var definition = Fixture.Module(Id, context =>
            {
                Applied.Add(context.Inputs.GetProperty("present").GetBoolean());
                return CommandResult.Succeeded(RuntimeJson.From(new { actual = 1 }));
            });
            var seed = JsonNode.Parse(definition.RegistryJson)!;
            // The action keeps its own recipient and gains the boolean the query step answered: a world read is a
            // `query`, so the step that makes it is one and the action consumes its frame.
            seed["capabilities"]!.AsArray()[1]!["graph"]!["inputs"] = JsonNode.Parse(RuntimeJson.From(new object[]
            {
                new { id = "in", type = "execution" }, new { id = "target", type = "entity" }, new { id = "present", type = "boolean" }
            }).GetRawText())!;
            // The read is the fixture's own capability row, because the row is what declares the port the kernel
            // answers through; a package's handler never decides the tri-state, the session it is handed does.
            seed["capabilities"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Id + ".presence", owner = Id, kind = "condition", label = "Presence reader", version = "1.0.0",
                parameters = new { },
                graph = new
                {
                    domains = new[] { "enemy" }, execution = "query",
                    inputs = new object[] { new { id = "subject", type = "entity" } },
                    outputs = new object[] { new { id = "value", type = "boolean" } }, parameters = Array.Empty<object>()
                }
            }).GetRawText())!);
            seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Binding, capabilityId = Id + ".presence", providerId = Id, handler = Id + ".handler.presence",
                role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
            }).GetRawText())!);
            module = Kernel.RegisterModule(definition with
            {
                RegistryJson = seed.ToJsonString(),
                EntityResolvers = new Dictionary<string, Func<EntityReference, bool>> { [Id] = Resolve },
                EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>> { [Id] = Observe },
                Evaluators = new Dictionary<string, EvaluatorHandler> { [Id + ".handler.presence"] = Read },
                Shapes = new Dictionary<string, HandlerShape>
                {
                    // The action's own handler reads the recipient and the boolean the step answered.
                    [Fixture.Handler(Id)] = Fixture.Shape(Id, inputs: new[] { "target", "present" }),
                    [Id + ".handler.presence"] = new HandlerShape().Inputs("subject").Outputs("value")
                },
                BindingSupport = new[] { Fixture.Support(Id)[0], Fixture.Support(Id)[1], new BindingSupport(Binding, "implementation-only", NoPermissions) }
            }, RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { });
            Load();
        }

        /// <summary>The action handler records what the step answered; the command itself commits nothing.</summary>
        private bool Resolve(EntityReference reference)
        {
            Calls++;
            if (Calls == GoneOnCall) return false;
            return entities.TryGetValue(reference.Id, out var entity) && entity.Ref == reference;
        }
        private RuntimeEntitySnapshot? Observe(EntityReference reference)
            => OnObserve is null ? entities.GetValueOrDefault(reference.Id) : OnObserve(reference);

        /// <summary>The `query` step: one presence read, and — unless this plan reads nothing else — one snapshot
        /// of the same reference, so the tick's single query budget is spent at the rate the test asserts. The third
        /// state is deliberately flattened to `false`: the kernel must refuse the step anyway.</summary>
        private JsonElement Read(EvaluationContext context)
        {
            var subject = RuntimeJson.Entity(context.Inputs.GetProperty("subject"));
            Presence = context.Query.TryPresence(subject, out _);
            if (snapshotToo)
            {
                if (!context.Query.TrySnapshot(subject, out _, out var code))
                    throw new RuntimeContractException(code, "Snapshot failed: " + code);
            }
            return RuntimeJson.From(new { value = Presence == EntityPresence.Current });
        }

        private static RuntimeEntitySnapshot Snapshot(EntityReference reference)
            => new(reference, "enemy", null, "alive", Array.Empty<string>(), new[] { "example.receiver" }, new[] { 0d, 0d, 0d });

        private void Load()
        {
            var manifest = RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry");
            var capabilities = manifest.GetProperty("capabilities").EnumerateArray()
                .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("version").GetString()!, StringComparer.Ordinal);
            var providers = manifest.GetProperty("providers").EnumerateArray()
                .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("version").GetString()!, StringComparer.Ordinal);
            var trigger = Kernel.ResolveGraphContract(Fixture.TriggerCapability(Id), "1.0.0", RuntimeJson.EmptyObject);
            var action = Kernel.ResolveGraphContract(Fixture.ActionCapability(Id), "1.0.0", RuntimeJson.From(new { amount = 5 }));
            // The query step is the fixture's own registered presence row: one entity input, one boolean output.
            var presence = Kernel.ResolveGraphContract(Id + ".presence", capabilities[Id + ".presence"], RuntimeJson.EmptyObject);
            var used = new[] { TriggerBinding, Binding, Id + ".binding.apply" }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            int Pin(string binding) => Array.IndexOf(used, binding);
            var pins = used.Select(id =>
            {
                var row = manifest.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("id").GetString() == id);
                var capabilityId = row.GetProperty("capabilityId").GetString()!;
                var providerId = row.GetProperty("providerId").GetString()!;
                // The pinned provider revision is the one the registry published, so a row owned by another
                // provider than this fixture's is pinned at its own version rather than at the fixture's.
                return (object)new
                {
                    bindingId = id, capabilityId, capabilityVersion = capabilities[capabilityId],
                    providerId, providerVersion = providers[providerId],
                    handler = row.GetProperty("handler").GetString()
                };
            }).ToArray();
            var steps = new object[]
            {
                new { nodeId = "S0_presence", nodeKind = "query", binding = Pin(Binding),
                    layout = Layout(presence, Array.Empty<object>()),
                    inputs = new object[] { new { slot = Input(presence, "subject"), fromEventSlot = Output(trigger, "target") } },
                    successors = Array.Empty<int?>() },
                new { nodeId = "S1_action", nodeKind = "action", binding = Pin(Id + ".binding.apply"),
                    layout = Layout(action, new object[] { 5 }),
                    inputs = new object[]
                    {
                        new { slot = Input(action, "target"), fromEventSlot = Output(trigger, "target") },
                        new { slot = Input(action, "present"), fromStepSlot = new { step = 0, port = 0 } }
                    },
                    successors = new int?[] { null } }
            };
            Kernel.LoadPlan(RuntimeJson.From(new
            {
                schemaVersion = 4, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" },
                runtime = Kernel.Identity, domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
                permissions = Fixture.Permissions, dependencies = Array.Empty<string>(),
                limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
                bindings = pins, attachments = Fixture.Attachments,
                entrypoints = new[] { new { nodeId = "Entry", binding = Pin(TriggerBinding),
                    layout = Layout(trigger, Array.Empty<object>()), start = 1, steps } }
            }).GetRawText());
        }

        /// <summary>Publishes one event and advances the tick it is dispatched in, keeping the step's own receipt:
        /// a refused read is reported on the action that was waiting for it, not as a missing command.</summary>
        internal TickResult Dispatch(string eventId)
        {
            var queued = Kernel.Publish(module, new RuntimeEvent(eventId, TriggerBinding, 1, 1, "shared-scope",
                RuntimeJson.From(new { target = new EntityReference(Id + ":1", 1, 1) })));
            if (queued.Status != "queued") throw new Exception("FAIL: " + eventId + " was not queued: " + queued.Status + "/" + queued.Code);
            var tick = Kernel.Advance(1, true);
            LastStep = tick.Commands.Count > 0 ? tick.Commands[^1] : null;
            return tick;
        }

        private static int Input(JsonElement contract, string id) => Port(contract, "inputs", id);
        private static int Output(JsonElement contract, string id) => Port(contract, "outputs", id);
        private static int Port(JsonElement contract, string side, string id) => RuntimeJson.Rows(contract, side)
            .Select((p, index) => (p, index)).Single(x => RuntimeJson.Text(x.p, "id") == id).index;
    }
}
