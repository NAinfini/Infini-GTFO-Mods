using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

/// <summary>
/// The candidate sources a provider registers for its own entity kinds, and the one budgeted read a `query` step
/// uses to discover them. Discovery follows ownership: a kind is answerable only because the provider that already
/// resolves it also says which entities of it exist, one kind has exactly one such owner, and the read is charged to
/// the same per-tick query budget as a snapshot. A kind nobody exposes, a list of another kind and a spent budget
/// are all refusals with a code — never a short list that looks like a small world.
/// </summary>
internal static class RuntimeCandidateSourceTests
{
    private const string Id = "example.candidates";
    private const string Other = "example.candidates.other";
    private static readonly string[] NoPermissions = Array.Empty<string>();

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

        // The kernel's own entry point, before any step is involved: a registered kind answers with its provider's
        // own list, and that list is all the kernel knows — nothing here scans a world.
        {
            var kernel = new RuntimeKernel(Fixture.Identity);
            kernel.BeginWorld(1);
            var handle = kernel.RegisterModule(Module(Id, candidates: true), RuntimeLogLevel.Off);
            kernel.StartRuntime(() => { });
            var one = kernel.EnumerateEntityCandidates(Id);
            Check(one.Status == "complete" && one.Code == "entities-enumerated" && one.Requested == 3 && one.Distinct == 3
                && one.Items.Select(item => item.Reference).SequenceEqual(new[] { Entity(1), Entity(2), Entity(3) }),
                "a registered kind answers with its provider's candidates");
            Check(one.Items.All(item => item.Snapshot == null), "an enumeration is a reference list, not an observation");
            Check(one.Context.StartupState == RuntimeStartupState.Ready, "the answer carries the lifecycle it was read in");
            Check(kernel.EnumerateEntityCandidates("example.nowhere").Code == "entity-candidates-unavailable",
                "a kind no provider exposes is refused by name");
            RejectCode(() => kernel.EnumerateEntityCandidates("not a kind"), "entity-kind",
                "a malformed kind name is refused like every other registration name");
            handle.Dispose();
        }

        // A query step reads the same list through its session, and the read spends the kernel's per-tick query
        // budget: the 65th enumeration of one tick is refused as `query-budget` with no candidates at all.
        {
            var plan = new EnumerationPlan("enumerate");
            var tick = plan.Dispatch("enumerate");
            Check(plan.Applied.Count == 1 && plan.Candidates == 3,
                "a query step enumerates its provider's kind [" + string.Join("|", plan.Applied) + "]"
                + " [" + plan.LastStep?.Result.Status + ":" + plan.LastStep?.Result.Code + "]");
            Check(tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "succeeded", "the action that reads the enumeration committed");
        }
        {
            var plan = new EnumerationPlan("budget");
            var enumerated = 0; var refused = 0;
            for (var i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick + 1; i++)
            {
                plan.Dispatch("budget-" + i);
                if (plan.Applied.Count > enumerated) enumerated = plan.Applied.Count;
                if (plan.LastStep is { Result.Code: RuntimeAbiCodes.QueryBudget }) refused++;
            }
            Check(refused == 1 && enumerated == RuntimeKernel.MaximumEntityQueriesPerTick,
                $"enumeration is charged to the tick's read budget: {enumerated} enumerated, {refused} refused as query-budget");
            Check(plan.SourceCalls == RuntimeKernel.MaximumEntityQueriesPerTick,
                "a refused enumeration never reached the provider's own table");
        }

        // Discovery follows ownership: only the provider that resolves a kind may say which entities of it exist,
        // and the kind goes with its owner rather than staying behind to refuse the next provider's claim.
        {
            var kernel = new RuntimeKernel(Fixture.Identity);
            kernel.BeginWorld(1);
            var first = kernel.RegisterModule(Module(Id), RuntimeLogLevel.Off);
            Check(first.IsRegistered, "a provider that owns a kind's resolver may expose its candidates");
            // Another provider may not answer for a kind whose resolver belongs to someone else: discovery follows
            // the same ownership the resolver table already records.
            RejectCode(() => kernel.RegisterModule(Module(Other) with
            {
                EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>>
                { [Id] = () => new[] { Entity(1) } }
            }, RuntimeLogLevel.Off), "entity-candidate-source-owner", "a provider cannot answer for a kind it does not resolve");
            first.Dispose();
            var replacement = kernel.RegisterModule(Module(Id, candidates: true), RuntimeLogLevel.Off);
            kernel.StartRuntime(() => { });
            Check(replacement.IsRegistered, "the same kind can be claimed again after its provider unregistered");
            Check(kernel.EnumerateEntityCandidates(Id).Status == "complete", "the replacement source answers after the swap");
            replacement.Dispose();
            Check(kernel.EnumerateEntityCandidates(Id).Code == "entity-candidates-unavailable",
                "an unregistered provider leaves no candidate source behind");
            Check(Module(Id).EntityCandidates == null && Module(Id, candidates: true).EntityCandidates!.Count == 1,
                "a provider that exposes nothing registers no source at all");
        }

        // A provider's own table is untrusted input: a source that answers with another kind's references, throws,
        // or returns nothing is refused by name instead of reaching a selector as a short answer.
        foreach (var (answer, code, description) in new (Func<IReadOnlyList<EntityReference>>, string, string)[]
        {
            (() => new[] { new EntityReference(Other + ":1", 1, 1) }, "entity-candidate-kind", "a candidate of another kind"),
            (() => throw new InvalidOperationException("native table is gone"), "entity-candidates-failed", "a source that throws"),
            (() => null!, "entity-candidates-failed", "a source that answers nothing")
        })
        {
            var kernel = new RuntimeKernel(Fixture.Identity);
            kernel.BeginWorld(1);
            kernel.RegisterModule(Module(Id, candidates: true, answer: answer), RuntimeLogLevel.Off);
            kernel.StartRuntime(() => { });
            Check(kernel.EnumerateEntityCandidates(Id).Code == code,
                description + " is refused instead of being handed to a selector");
        }
        return checks;
    }

    private static EntityReference Entity(int index) => new(Id + ":" + index, 1, 1);

    /// <summary>One provider's own table: the kind it resolves, and what it answers when asked which of its
    /// entities exist right now.</summary>
    private static RuntimeModule Module(string provider, bool candidates = false,
        Func<IReadOnlyList<EntityReference>>? answer = null)
    {
        var kind = provider;
        var definition = Fixture.Module(provider);
        var module = definition with
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
            { [kind] = reference => reference.Id.StartsWith(kind + ":", StringComparison.Ordinal) }
        };
        return candidates
            ? module with { EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>>
                { [kind] = answer ?? (() => new[] { Entity(1), Entity(2), Entity(3) }) } }
            : module;
    }

    /// <summary>
    /// A plan whose query step enumerates the fixture kind and whose action reads the size of what came back. The
    /// action is what forces the query step, and the step is where a refusal is reported, so both the positive case
    /// and the budget case are read off the same command receipt.
    /// </summary>
    private sealed class EnumerationPlan
    {
        private readonly string planId;
        private readonly RuntimeModuleHandle module;
        internal readonly RuntimeKernel Kernel;
        internal readonly List<string> Applied = new();
        internal int SourceCalls, Candidates;
        internal CommandReceipt? LastStep;

        internal EnumerationPlan(string planId)
        {
            this.planId = planId;
            Kernel = new RuntimeKernel(Fixture.Identity);
            Kernel.BeginWorld(1);
            Kernel.RegisterModule(Fixture.MountOwner(), RuntimeLogLevel.Off);
            var definition = Fixture.Module(Id, _ => { Applied.Add(Id + ".applied"); return CommandResult.Succeeded(RuntimeJson.From(new { actual = 1 })); });
            var seed = JsonNode.Parse(definition.RegistryJson)!;
            // The action keeps its own recipient and gains the candidate set the enumeration answered with: an
            // enumeration is a world read, so its own capability answers entities and is a `query`.
            seed["capabilities"]!.AsArray()[1]!["graph"]!["inputs"] = JsonNode.Parse(RuntimeJson.From(new object[]
            {
                new { id = "in", type = "execution" }, new { id = "target", type = "entity" },
                new { id = "candidates", type = "entity", cardinality = "many" }
            }).GetRawText())!;
            seed["capabilities"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Id + ".enumerate", owner = Id, kind = "selector", label = "Candidate enumerator", version = "1.0.0",
                parameters = new { },
                graph = new
                {
                    domains = new[] { "enemy" }, execution = "query", inputs = Array.Empty<object>(),
                    outputs = new object[] { new { id = "targets", type = "entity", cardinality = "many" } }, parameters = Array.Empty<object>()
                }
            }).GetRawText())!);
            seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Binding, capabilityId = Id + ".enumerate", providerId = Id, handler = Id + ".handler.enumerate",
                role = "evaluate", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
            }).GetRawText())!);
            // The provider's own table is the one `Module` writes: the resolver and the candidate source for its
            // kind, kept apart from the plan's registry seed so neither has to restate the other.
            var provider = Module(Id, candidates: true, answer: Answer);
            module = Kernel.RegisterModule(definition with
            {
                RegistryJson = seed.ToJsonString(),
                EntityResolvers = provider.EntityResolvers, EntityCandidates = provider.EntityCandidates,
                Evaluators = new Dictionary<string, EvaluatorHandler> { [Id + ".handler.enumerate"] = Enumerate },
                Shapes = new Dictionary<string, HandlerShape>
                {
                    [Fixture.Handler(Id)] = new HandlerShape().Inputs("target").Outputs("result"),
                    [Id + ".handler.enumerate"] = new HandlerShape().Outputs("targets")
                },
                BindingSupport = new[] { Fixture.Support(Id)[0], Fixture.Support(Id)[1], new BindingSupport(Binding, "implementation-only", NoPermissions) }
            }, RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { });
            Load();
        }

        private const string Binding = Id + ".binding.enumerate";
        private const string TriggerBinding = Id + ".binding.trigger";

        private JsonElement Enumerate(EvaluationContext context)
        {
            if (!context.Query.TryCandidates(Id, out var candidates, out var code)) throw new RuntimeContractException(code, code);
            Candidates = candidates.Count;
            return RuntimeJson.From(new { targets = candidates });
        }

        /// <summary>The provider's own table. Counting its calls is what shows a refused read never reaches it.</summary>
        private IReadOnlyList<EntityReference> Answer()
        {
            SourceCalls++;
            return new[] { Entity(1), Entity(2), Entity(3) };
        }

        private void Load()
        {
            var manifest = RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry");
            var capabilities = manifest.GetProperty("capabilities").EnumerateArray()
                .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("version").GetString()!, StringComparer.Ordinal);
            var trigger = Kernel.ResolveGraphContract(Fixture.TriggerCapability(Id), "1.0.0", RuntimeJson.EmptyObject);
            var action = Kernel.ResolveGraphContract(Fixture.ActionCapability(Id), "1.0.0", RuntimeJson.From(new { amount = 5 }));
            var selector = Kernel.ResolveGraphContract(Id + ".enumerate", "1.0.0", RuntimeJson.EmptyObject);
            var used = new[] { TriggerBinding, Binding, Id + ".binding.apply" }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
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
            var steps = new object[]
            {
                new { nodeId = "S0_enumerate", nodeKind = "query", binding = Pin(Binding),
                    layout = Layout(selector, Array.Empty<object>()), inputs = Array.Empty<object>(), successors = Array.Empty<int?>() },
                new { nodeId = "S1_action", nodeKind = "action", binding = Pin(Id + ".binding.apply"),
                    layout = Layout(action, new object[] { 5 }),
                    inputs = new object[]
                    {
                        new { slot = Input(action, "target"), fromEventSlot = Output(trigger, "target") },
                        new { slot = Input(action, "candidates"), fromStepSlot = new { step = 0, port = 0 } }
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
        /// a refused enumeration is reported on the action that was waiting for it, not as a missing command.</summary>
        internal TickResult Dispatch(string eventId)
        {
            var queued = Kernel.Publish(module, new RuntimeEvent(eventId, TriggerBinding, 1, 1, "shared-scope",
                RuntimeJson.From(new { target = Entity(1) })));
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
