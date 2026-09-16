using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

/// <summary>
/// The `presentation` tier: the host decides when a presentation step runs and which players run it, and the
/// recipient performs the write. The cases here are the dispatch half — the step is walked and its inputs are
/// resolved by the host, no handler runs there, the intent names a non-empty recipient set, and the entry's own
/// receipt reports a result that committed nothing. The recipient half is the same entry point a remote request
/// reaches, so the cases drive it directly and check what it refuses.
/// </summary>
internal static class PresentationDispatchTests
{
    private const string Id = "example.present";
    private const string Action = Id + ".binding.apply";
    private const string Trigger = Id + ".binding.trigger";
    private const string PlanId = "presentation.plan";
    private const string Target = Id + ":1";
    /// <summary>The dense port-type table the wire's slot indexes address, in the declaration order of the contract.</summary>
    private static readonly string[] WirePortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };

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

        // The host's half: the step is dispatched, not invoked, and what it hands out is the resolved input frame
        // plus the recipient set the provider answered with.
        {
            var h = new Scene();
            h.Publish("present-1");
            var tick = h.Kernel.Advance(1, true);
            Check(h.Applied.Count == 0, "a presentation step is not invoked on the host");
            Check(!h.ReceiverRan, "the host's own presentation handler never runs during the advance");
            Check(tick.CommandsExecuted == 0, "no command of the entry was counted as executed on the host");
            var receipt = tick.Commands.Single();
            Check(receipt.Result.Status == CommandStatuses.Partial && receipt.Result.CommitState == CommitStates.None
                && receipt.Result.Facts.Count == 0, "the presentation step's own receipt commits nothing and carries no fact");
            Check(tick.Events.Single().Status == "processed", "the entry continues past its presentation step");
            var sent = tick.Presentations.Single();
            Check(sent.Recipients.SequenceEqual(new[] { "2", "3" }), "the recipients are the sessions the provider resolved [" + string.Join(",", sent.Recipients) + "]");
            Check(h.Addressed!.Select(reference => reference.Id).SequenceEqual(new[] { Target }),
                "the provider is handed the step's own recipient references [" + string.Join(",", h.Addressed!.Select(r => r.Id)) + "]");
            Check(sent.PlanId == PlanId && sent.StepIndex == 0 && sent.BindingId == Action && sent.NodeId == "Present",
                "the intent names the plan, the step and the binding it presents ["
                + sent.PlanId + "|" + sent.StepIndex + "|" + sent.BindingId + "|" + sent.NodeId + "]");
            Check(sent.Inputs.GetProperty("target").GetProperty("id").GetString() == Target,
                "the intent carries the resolved input frame, not the raw event");
            Check(tick.Presentations.Count == 1, "the advance reports the intent it decided");
            // The intents belong to the advance that decided them: a later advance reports its own, and an advance
            // that decides nothing reports none rather than repeating the previous decision.
            var quiet = h.Kernel.Advance(2, true);
            Check(quiet.Presentations.Count == 0, "the next advance reports no intent of its own");
        }
        // The recipient's half through the same entry point the network layer calls: the frame is re-validated
        // against the step's own contract, the handler sees IsHost false, and nothing is committed.
        {
            var h = new Scene();
            h.Publish("present-2");
            var sent = h.Kernel.Advance(1, true).Presentations.Single();
            var result = h.Kernel.ExecutePresentationCommand(sent.PlanId, sent.StepIndex, sent.CommandId, "41", "scope.p",
                sent.SimulationTick, sent.Inputs);
            Check(result.Status == CommandStatuses.Partial && result.CommitState == CommitStates.None,
                "the recipient's result commits nothing [" + result.Status + "/" + result.CommitState + "]");
            Check(result.Code == "partial" && result.Facts.Count == 0,
                "the result names its own partial status and publishes no fact [" + result.Code + "/" + result.Facts.Count + "]");
            Check(h.ReceiverRan && !h.ReceiverWasHost, "the recipient's handler ran and saw IsHost false");
            var repeat = h.Kernel.ExecutePresentationCommand(sent.PlanId, sent.StepIndex, sent.CommandId, "41", "scope.p",
                sent.SimulationTick, sent.Inputs);
            Check(repeat.Code == "partial" && repeat.CommitState == CommitStates.None,
                "a redelivered presentation command presents again rather than being refused");
        }
        // The recipient's refusals: a committing result, a wrong plan, a step that is not a presentation, and a
        // frame that does not satisfy the step's own inputs.
        {
            var h = new Scene { ReceiverCommits = true };
            h.Publish("present-3");
            var sent = h.Kernel.Advance(1, true).Presentations.Single();
            var committing = h.Kernel.ExecutePresentationCommand(sent.PlanId, sent.StepIndex, sent.CommandId, "42", "scope.p",
                sent.SimulationTick, sent.Inputs);
            Check(committing.Status == CommandStatuses.Rejected && committing.Code == RuntimeAbiCodes.PresentationCommit,
                "a presentation handler that reports a commit is refused [" + committing.Status + ":" + committing.Code + "]");
            var missing = h.Kernel.ExecutePresentationCommand("presentation.absent", sent.StepIndex, sent.CommandId, "42", "scope.p",
                sent.SimulationTick, sent.Inputs);
            Check(missing.Code == "plan-unloaded", "a plan this kernel has not loaded is refused by name");
            var unknownStep = h.Kernel.ExecutePresentationCommand(sent.PlanId, 97, sent.CommandId, "42", "scope.p",
                sent.SimulationTick, sent.Inputs);
            Check(unknownStep.Code == "presentation-step", "a step index this plan has no step for is refused by name");
            var shortFrame = h.Kernel.ExecutePresentationCommand(sent.PlanId, sent.StepIndex, sent.CommandId, "42", "scope.p",
                sent.SimulationTick, RuntimeJson.EmptyObject);
            Check(shortFrame.Status == CommandStatuses.Rejected && shortFrame.Code == "missing-input",
                "a frame that leaves a required input unwired is refused by the step's own contract [" + shortFrame.Code + "]");
        }
        // A step whose recipients this process cannot name is refused rather than widened to everyone.
        {
            var h = new Scene();
            h.Recipients.Clear();
            h.Publish("present-4");
            var tick = h.Kernel.Advance(1, true);
            Check(tick.Presentations.Count == 0, "an unnamed recipient set produces no intent");
            Check(tick.Events.Single().Code == RuntimeAbiCodes.PresentationRecipient,
                "an unnamed recipient set rejects the step [" + tick.Events.Single().Code + "]");
            Check(h.Applied.Count == 0 && !h.ReceiverRan, "nothing presented and no handler ran");
        }
        // A presentation handler is a real handler: the command context it is handed says which side dispatched it.
        {
            var host = new Scene();
            host.Publish("present-5");
            host.Kernel.Advance(1, true);
            Check(host.HostSawIsHost == false, "the host's own walk never invokes the presentation handler");
            var replica = new Scene();
            replica.Publish("present-6");
            replica.Kernel.Advance(1, false);
            Check(replica.Applied.Count == 0 && replica.SawHost == null, "a replica advance neither executes nor claims the host");
        }
        // The registry's own rule: a module answers for its own presentation recipients, and the kernel refuses a
        // second registration of the same provider rather than replacing who presents.
        {
            var h = new Scene();
            RejectCode(() => h.Kernel.RegisterModule(Scene.ModuleDefinition(_ => new[] { "2" }), RuntimeLogLevel.Off),
                "registration-closed", "a second registration of the same provider is refused");
        }
        return checks;
    }

    /// <summary>
    /// One provider whose action is the `presentation` tier, one trigger, and a plan whose entry walks the trigger
    /// into that action. The provider owns the recipient set as well, because a presentation step's recipients are
    /// the registration's own answer about the running process, and it records what its handlers saw so a case can
    /// assert which side dispatched a command.
    /// </summary>
    private sealed class Scene
    {
        internal readonly RuntimeKernel Kernel = new(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
        internal readonly RuntimeModuleHandle Module;
        internal readonly List<string> Applied = new();
        /// <summary>The players the provider says its presentation steps address; unsorted on purpose, so a case
        /// proves the routing list is normalized rather than passed through. A case may empty it, which the
        /// provider answers as "cannot name them" — the refusal the kernel turns into `presentation-recipient`.</summary>
        internal readonly List<string> Recipients = new() { "3", "2" };
        /// <summary>The recipient references the provider was last asked about, so a case proves the kernel hands
        /// the step's own addressed entities over instead of a process-wide list.</summary>
        internal IReadOnlyList<EntityReference>? Addressed;
        /// <summary>Scripts the recipient's handler into reporting a committing result, which the tier must refuse.</summary>
        internal bool ReceiverCommits;
        internal bool ReceiverRan;
        internal bool ReceiverWasHost;
        internal bool HostSawIsHost;
        /// <summary>What the last dispatched command reported, or null when none reached the handler.</summary>
        internal bool? SawHost;

        internal Scene()
        {
            Kernel.BeginWorld(1);
            Kernel.RegisterModule(Mounts(), RuntimeLogLevel.Off);
            Module = Kernel.RegisterModule(ModuleDefinition(addressed =>
            {
                Addressed = addressed;
                return Recipients.Count == 0 ? null : Recipients;
            }) with
            {
                Handlers = new Dictionary<string, CommandHandler> { [Id + ".handler.apply"] = Dispatch }
            }, RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { });
            var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded("presentation.plan.json", PlanJson(Kernel)) })[0];
            if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
        }

        /// <summary>
        /// The one handler both halves run. A command that arrives with <see cref="CommandContext.IsHost"/> true is
        /// the host's own walk reaching a host-tier step; the presentation tier never invokes it there, so a call
        /// with false is the recipient presenting.
        /// </summary>
        private CommandResult Dispatch(CommandContext context)
        {
            SawHost = context.IsHost;
            if (!context.IsHost) { ReceiverRan = true; ReceiverWasHost = context.IsHost; }
            if (context.IsHost) HostSawIsHost = true;
            Applied.Add(context.NodeId);
            if (ReceiverCommits) return CommandResult.Succeeded(RuntimeJson.EmptyObject);
            // The three-argument overload: `partial` plus an explicit commit state. The two-argument one reports a
            // confirmed commit, which is exactly what the presentation tier refuses.
            return CommandResult.Partial(RuntimeJson.From(new { presented = true }), CommitStates.None, Array.Empty<RuntimeFact>());
        }

        internal DispatchResult Publish(string eventId, long tick = 1) => Kernel.Publish(Module,
            new RuntimeEvent(eventId, Trigger, 1, tick, "scope.present",
                RuntimeJson.From(new { target = new EntityReference(Target, 1, 1) })));

        private static RuntimeModule Mounts() => new(RuntimeKernel.ApiVersion, """
            {"providers":[{"id":"example.present.mounts","kind":"extension","version":"1.0.0","dependencies":[]}],
            "capabilities":[],"bindings":[]}
            """, new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
            {
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == "fixture-level")
            }
        };

        internal static RuntimeModule ModuleDefinition(Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?> sessions)
        {
            var json = RuntimeJson.From(new
            {
                providers = new[] { new { id = Id, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
                capabilities = new object[]
                {
                    new { id = Id + ".trigger", owner = Id, kind = "trigger", label = "Observed", version = "1.0.0", parameters = new { },
                        graph = new { domains = new[] { "enemy" }, execution = "host", inputs = Array.Empty<object>(),
                            outputs = new object[] { new { id = "next", type = "execution" }, new { id = "target", type = "entity" } },
                            parameters = Array.Empty<object>() } },
                    new { id = Id + ".apply", owner = Id, kind = "action", label = "Present", version = "1.0.0", parameters = new { },
                        graph = new { domains = new[] { "enemy" }, execution = "presentation",
                            inputs = new object[] { new { id = "in", type = "execution" }, new { id = "target", type = "entity" } },
                            outputs = new object[] { new { id = "next", type = "execution" },
                                new { id = "result", type = "result", schema = "example.present.result", fields = new object[] {
                                    new { id = "target", type = "entity" }, new { id = "status", type = "enum", schema = "execution_outcome" },
                                    new { id = "committed", type = "enum", schema = "commit_state" }, new { id = "code", type = "string" } } } },
                            parameters = Array.Empty<object>(),
                            recipients = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" } } }
                },
                bindings = new object[]
                {
                    new { id = Trigger, capabilityId = Id + ".trigger", providerId = Id, handler = Id + ".handler.trigger",
                        role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
                    new { id = Action, capabilityId = Id + ".apply", providerId = Id, handler = Id + ".handler.apply",
                        role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
                }
            });
            return new RuntimeModule(RuntimeKernel.ApiVersion, json.GetRawText(),
                new Dictionary<string, CommandHandler>(), new[] {
                    new BindingSupport(Trigger, "implementation-only", Array.Empty<string>()),
                    new BindingSupport(Action, "implementation-only", Array.Empty<string>()) },
                new Dictionary<string, Func<EntityReference, bool>>
                {
                    [Id] = reference => reference.Id == Target && reference.WorldEpoch == 1 && reference.LifeEpoch == 1
                })
            {
                Shapes = new Dictionary<string, HandlerShape> { [Id + ".handler.apply"] = new HandlerShape().Inputs("target").Outputs("result") },
                PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>
                {
                    [Id] = sessions
                }
            };
        }

        /// <summary>The plan a case runs: the trigger into the one action, with every port addressed by the position
        /// the resolved contract gives it.</summary>
        private static string PlanJson(RuntimeKernel kernel)
        {
            var manifest = RuntimeJson.Parse(kernel.ExportManifest()).GetProperty("registry");
            var pins = new[] { Trigger, Action }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            object Pin(string id)
            {
                var row = manifest.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("id").GetString() == id);
                var capabilityId = row.GetProperty("capabilityId").GetString()!;
                var providerId = row.GetProperty("providerId").GetString()!;
                return new { bindingId = id, capabilityId,
                    capabilityVersion = manifest.GetProperty("capabilities").EnumerateArray()
                        .Single(c => c.GetProperty("id").GetString() == capabilityId).GetProperty("version").GetString()!,
                    providerId, providerVersion = manifest.GetProperty("providers").EnumerateArray()
                        .Single(p => p.GetProperty("id").GetString() == providerId).GetProperty("version").GetString()!,
                    handler = row.GetProperty("handler").GetString()! };
            }
            object Frame(string capabilityId)
            {
                var graph = manifest.GetProperty("capabilities").EnumerateArray()
                    .Single(c => c.GetProperty("id").GetString() == capabilityId).GetProperty("graph");
                object[] Slots(string side) => graph.GetProperty(side).EnumerateArray().Select((port, index) => (object)new
                {
                    index, type = Array.IndexOf(WirePortTypes, port.GetProperty("type").GetString()), cardinality = 0,
                    valueSet = -1, lifetime = -1, optional = false, nullable = false
                }).ToArray();
                return new { inputs = Slots("inputs"), outputs = Slots("outputs"), constants = Array.Empty<object>(), promoted = Array.Empty<int>() };
            }
            int Slot(string capabilityId, string side, string port) => manifest.GetProperty("capabilities").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == capabilityId).GetProperty("graph").GetProperty(side)
                .EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == port).i;
            return RuntimeJson.From(new
            {
                schemaVersion = 4, kind = "forge-runtime-plan", planId = PlanId,
                resource = new { id = "example.present.resource", revision = "1" }, runtime = kernel.Identity,
                domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
                permissions = Array.Empty<string>(), dependencies = Array.Empty<string>(),
                limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth },
                bindings = pins.Select(Pin).ToArray(),
                attachments = new object[] { new { kind = "level", reference = "fixture-level" } },
                entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(pins, Trigger), start = 0,
                    layout = Frame(Id + ".trigger"),
                    steps = new object[] { new { nodeId = "Present", nodeKind = "action", binding = Array.IndexOf(pins, Action),
                        layout = Frame(Id + ".apply"),
                        inputs = new object[] { new { slot = Slot(Id + ".apply", "inputs", "target"), fromEventSlot = Slot(Id + ".trigger", "outputs", "target") } },
                        successors = new int?[] { null } } } } }
            }).GetRawText();
        }
    }
}
