using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

namespace ForgeRuntime.GameBindings.Tests;

/// <summary>
/// The `presentation` tier as the host bridge reaches it: which side a dispatched command says it is, and what the
/// host's walk produces for a presentation step. The recipient half is the kernel entry point the network layer
/// hands a request to, so it is driven here directly.
/// </summary>
internal static class PresentationBridgeTests
{
    private const string Id = "test.presentation";
    private const string Action = Id + ".binding.apply";
    private const string Trigger = Id + ".binding.trigger";
    private const string Target = Id + ":1";
    /// <summary>The dense port-type table the wire's slot indexes address, in the order the contract declares it.</summary>
    private static readonly string[] WirePortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }

        // IsHost is the advance's own fact, in both directions, and it is what a handler that writes game state
        // reads before writing: the host advance says true and the replica advance says false.
        {
            var h = new Scene();
            h.Publish("bridge-1");
            h.Kernel.Advance(1, true);
            Check(h.SawHost == true, "a host advance dispatches IsHost true");
            var replica = new Scene();
            replica.Publish("bridge-2");
            replica.Kernel.Advance(1, false);
            Check(replica.SawHost == null, "a replica advance dispatches no host-tier command at all");
        }
        // The host's walk of a presentation step: no handler runs on this machine, the intent names the recipients
        // the provider answered with, and the step's own receipt reports a result that committed nothing.
        {
            var h = new Scene(presentation: true);
            h.Publish("bridge-3");
            var tick = h.Kernel.Advance(1, true);
            Check(h.Applied == 0, "the host's presentation step does not invoke a handler here");
            var receipt = tick.Commands.Single();
            Check(receipt.Result.CommitState == CommitStates.None && receipt.Result.Facts.Count == 0,
                "the presentation step's receipt commits nothing [" + receipt.Result.Status + "/" + receipt.Result.CommitState + "]");
            var sent = tick.Presentations.Single();
            Check(sent.Recipients.SequenceEqual(new[] { "2" }), "the intent carries the provider's own recipient set");
            Check(sent.Inputs.GetProperty("target").GetProperty("id").GetString() == Target, "the intent carries the resolved inputs");
        }
        return checks;
    }

    /// <summary>One provider and one plan: a trigger into one action, and whether that action's capability is the
    /// presentation tier is the only variable a case here turns on.</summary>
    private sealed class Scene
    {
        internal readonly RuntimeKernel Kernel = new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        internal readonly RuntimeModuleHandle Module;
        internal int Applied;
        /// <summary>What the last dispatched command reported as <see cref="CommandContext.IsHost"/>, or null when
        /// no command reached this machine at all.</summary>
        internal bool? SawHost;
        internal readonly string Plan;

        internal Scene(bool presentation = false)
        {
            Kernel.BeginWorld(1);
            Kernel.RegisterModule(Mounts(), RuntimeLogLevel.Off);
            var module = ModuleDefinition();
            var seed = JsonNode.Parse(module.RegistryJson)!;
            if (presentation) seed["capabilities"]!.AsArray()[1]!["graph"]!["execution"] = "presentation";
            Module = Kernel.RegisterModule(module with
            {
                RegistryJson = seed.ToJsonString(),
                Handlers = new Dictionary<string, CommandHandler> { [Id + ".handler.apply"] = Apply },
                PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>
                {
                    [Id] = _ => new[] { "2" }
                }
            }, RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { });
            Plan = PlanJson(Kernel);
            // A plan the loader refuses would leave every case here publishing into no subscription at all, which
            // would read as "the handler never ran"; the fixture refuses it by name instead.
            var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/presentation.plan.json", Plan) })[0];
            if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
        }

        /// <summary>The one handler the provider registers, so a case can see which commands were dispatched to
        /// this machine and which side each one reported.</summary>
        private CommandResult Apply(CommandContext context)
        {
            Applied++;
            SawHost = context.IsHost;
            return CommandResult.Succeeded(RuntimeJson.From(new { applied = 1 }));
        }

        internal void Publish(string eventId) => Kernel.Publish(Module, new RuntimeEvent(eventId, Trigger, 1, 1, "scope.bridge",
            RuntimeJson.From(new { target = new EntityReference(Target, 1, 1) })));

        private static RuntimeModule Mounts() => new(RuntimeKernel.ApiVersion, """
            {"providers":[{"id":"test.presentation.mounts","kind":"extension","version":"1.0.0","dependencies":[]}],
            "capabilities":[],"bindings":[]}
            """, new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
            {
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == "31:A:0")
            }
        };

        private static RuntimeModule ModuleDefinition()
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
                    new { id = Id + ".apply", owner = Id, kind = "action", label = "Apply", version = "1.0.0", parameters = new { },
                        graph = new { domains = new[] { "enemy" }, execution = "host",
                            inputs = new object[] { new { id = "in", type = "execution" }, new { id = "target", type = "entity" } },
                            outputs = new object[] { new { id = "next", type = "execution" },
                                new { id = "result", type = "result", schema = "test.presentation.result", fields = new object[] {
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
                new Dictionary<string, Func<EntityReference, bool>> { [Id] = reference => reference.Id == Target && reference.WorldEpoch == 1 && reference.LifeEpoch == 1 })
            {
                Shapes = new Dictionary<string, HandlerShape> { [Id + ".handler.apply"] = new HandlerShape().Inputs("target").Outputs("result") }
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
                schemaVersion = 1, kind = "forge-runtime-plan", planId = "test.presentation.plan",
                resource = new { id = "test.presentation.resource", revision = "1" }, runtime = kernel.Identity,
                domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
                permissions = Array.Empty<string>(), dependencies = Array.Empty<string>(),
                limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth },
                bindings = pins.Select(Pin).ToArray(),
                attachments = new object[] { new { kind = "level", reference = "31:A:0" } },
                entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(pins, Trigger), start = 0,
                    layout = Frame(Id + ".trigger"),
                    steps = new object[] { new { nodeId = "Apply", nodeKind = "action", binding = Array.IndexOf(pins, Action),
                        layout = Frame(Id + ".apply"),
                        inputs = new object[] { new { slot = Slot(Id + ".apply", "inputs", "target"), fromEventSlot = Slot(Id + ".trigger", "outputs", "target") } },
                        successors = new int?[] { null } } } } }
            }).GetRawText();
        }
    }
}



