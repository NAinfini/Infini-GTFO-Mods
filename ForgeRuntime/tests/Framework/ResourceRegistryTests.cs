using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

/// <summary>
/// The runtime resource table this batch adds: the kind a provider claims, the one entry point a `query` step
/// reads it through, the one compiled reference a plan may carry, and the handles a provider casts for itself.
/// Every case reads the production path (`RegisterModule`, `EnumerateResources`, `ResolveResources`,
/// `RuntimeQuerySession`, `RuntimePlan.Parse`, `RuntimeModuleHandle`), so a table that drifts from its rules fails
/// here rather than at a step that silently received nothing.
/// </summary>
internal static class ResourceRegistryTests
{
    private const string Id = "example.resources";
    private const string Other = "example.resources.other";
    private static readonly string[] NoPermissions = Array.Empty<string>();

    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL resource: " + name); }
        void RejectCode(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL resource: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL resource: " + name + " did not reject");
        }

        Registration(Check, RejectCode);
        Enumeration(Check);
        RegisteredQueryStep(Check, RejectCode);
        CompiledReference(Check);
        checks += HandleCasting();
        return checks;
    }

    /// <summary>One kind has one owner, exactly like a mount kind: the same provider may not register twice, a
    /// second provider may not take the kind over, and a name outside the shared vocabulary is refused by name.</summary>
    private static void Registration(Action<bool, string> check, Action<Action, string, string> rejectCode)
    {
        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        var first = kernel.RegisterModule(Module(Id, "map"), RuntimeLogLevel.Off);
        check(first.IsRegistered, "a provider may claim the resource kind it owns");
        rejectCode(() => kernel.RegisterModule(Module(Id, "map"), RuntimeLogLevel.Off), "provider-conflict",
            "the same provider cannot register twice");
        rejectCode(() => kernel.RegisterModule(Module(Other, "map"), RuntimeLogLevel.Off), RuntimeAbiCodes.ResourceKind,
            "another provider cannot take over an owned kind");
        rejectCode(() => kernel.RegisterModule(Module(Other, "not-a-kind"), RuntimeLogLevel.Off), RuntimeAbiCodes.ResourceKind,
            "a kind outside the shared vocabulary cannot be claimed");
        rejectCode(() => kernel.RegisterModule(Module(Other, "map") with { ResourceProviders =
            new Dictionary<string, RuntimeResourceProvider> { [""] = Ready() } }, RuntimeLogLevel.Off), RuntimeAbiCodes.ResourceKind,
            "an empty kind name cannot be claimed");
        var other = kernel.RegisterModule(Module(Other, "zone", resourceIds: new[] { new ResourceRef("zone", "zone/a") }), RuntimeLogLevel.Off);
        check(other.IsRegistered, "a different kind is still free after the first is claimed");
        kernel.StartRuntime(() => { });
        check(kernel.EnumerateResources("map").Status == "complete" && kernel.EnumerateResources("zone").Status == "complete",
            "both owners answer for their own kind");
        first.Dispose();
        check(kernel.EnumerateResources("map").Code == RuntimeAbiCodes.ResourceUnavailable,
            "an unregistered owner leaves its kind unanswered rather than empty");
        check(kernel.EnumerateResources("zone").Status == "complete", "unregistering one owner leaves the other's kind alone");
    }

    /// <summary>The provider's own table is the only source, and it is untrusted input like every other table: a
    /// listing of another kind, a listing that throws and a listing that answers nothing are refused by name.</summary>
    private static void Enumeration(Action<bool, string> check)
    {
        var kernel = Kernel(Id, "map");
        var resources = kernel.EnumerateResources("map");
        check(resources.Status == "complete" && resources.Code == "resources-enumerated" && resources.References.Count == 2
            && resources.References.Select(r => r.ResourceId).SequenceEqual(new[] { "map/a", "map/b" }),
            "a registered kind answers with its provider's own list");
        check(resources.Context.StartupState == RuntimeStartupState.Ready, "the answer carries the lifecycle it was read in");
        check(kernel.EnumerateResources("zone").Code == RuntimeAbiCodes.ResourceUnavailable, "a kind no provider owns is refused by name");
        check(kernel.EnumerateResources("not-a-kind").Code == RuntimeAbiCodes.ResourceUnavailable,
            "a name no provider owns is unanswered for the same reason");
        var resolved = kernel.ResolveResources("map", new[] { "map/a" });
        check(resolved.Status == "complete" && resolved.References.Single().ResourceId == "map/a", "a named id resolves through its owner");
        check(kernel.ResolveResources("map", new[] { "map/gone" }).Code == RuntimeAbiCodes.ResourceUnavailable,
            "an id the owner does not have is resource-unavailable, never a null reference");
        check(kernel.ResolveResourceRefs(new[] { new ResourceRef("map", "map/gone") }).Code == RuntimeAbiCodes.ResourceUnavailable,
            "a reference the owner no longer has is resource-unavailable");
        check(kernel.ResolveResourceRefs(new[] { new ResourceRef("map", "map/a") }).Status == "complete",
            "a reference the owner still has resolves");
        check(kernel.ResolveResources("map", new[] { "map/a", "map/gone" }).Code == RuntimeAbiCodes.ResourceUnavailable,
            "one missing id refuses the whole read instead of answering with part of it");
        // The provider's answers are input: another kind's reference, a throwing table and a table that answers
        // nothing are each refused instead of being handed to a step as a smaller world.
        var foreignKind = new ResourceRef("zone", "zone/a");
        foreach (var (owned, enumerateCode, resolveCode, name) in new (RuntimeResourceProvider, string, string, string)[]
        {
            (RuntimeResourceProvider.Of(() => new[] { foreignKind }, _ => foreignKind), RuntimeAbiCodes.ResourceKind,
                RuntimeAbiCodes.ResourceKind, "a listing of another kind"),
            (RuntimeResourceProvider.Of(() => throw new InvalidOperationException("native table is gone"),
                _ => throw new InvalidOperationException("gone")), "resource-provider-failed", "resource-provider-failed",
                "a listing that throws"),
            (RuntimeResourceProvider.Of(() => null!, _ => null), "resource-provider-failed", RuntimeAbiCodes.ResourceUnavailable,
                "a listing that answers nothing")
        })
        {
            var broken = new RuntimeKernel(Fixture.Identity); broken.BeginWorld(1);
            broken.RegisterModule(MountOwner(), RuntimeLogLevel.Off);
            broken.RegisterModule(Module(Id, "map", owned: owned), RuntimeLogLevel.Off);
            broken.StartRuntime(() => { });
            check(broken.EnumerateResources("map").Code == enumerateCode
                && broken.ResolveResources("map", new[] { "map/a" }).Code == resolveCode,
                name + " is refused instead of reaching a step");
        }
    }

    /// <summary>
    /// The same table read the way a step reads it: a `query` evaluator calls <see cref="RuntimeQuerySession"/>,
    /// and the read is charged to the one per-tick query budget the entity reads spend. A `pure` step is handed a
    /// session that refuses every read, which is the tier rule and not a provider's answer.
    /// </summary>
    private static void RegisteredQueryStep(Action<bool, string> check, Action<Action, string, string> rejectCode)
    {
        var world = new ResourcePlan("read");
        var tick = world.Dispatch("read-1");
        check(world.Applied == 1 && world.Enumerated == 2 && world.Resolved == "map/a",
            "a query step enumerates and resolves through the session [" + world.LastCode + "]");
        check(tick.Commands.Count == 1 && tick.Commands[0].Result.Status == "succeeded", "the action that read the resources committed");
        // The budget is the same one the entity table spends: one enumeration and one resolve per dispatch, and
        // the refusal is reported where the step that needed the read runs. A refused read never reaches the
        // provider's own table, which is what the provider's own call count shows.
        var budget = new ResourcePlan("budget");
        var refused = 0;
        for (var i = 0; i < RuntimeKernel.MaximumEntityQueriesPerTick / 2 + 2; i++)
        {
            budget.Dispatch("budget-" + i);
            if (budget.LastCode == RuntimeAbiCodes.QueryBudget) refused++;
        }
        check(refused == 2 && budget.SourceCalls == RuntimeKernel.MaximumEntityQueriesPerTick / 2,
            $"the read budget is metered across both reads: {refused} refusals, provider asked {budget.SourceCalls} times");
        // The session refuses the read and the step that needed it is rejected. The kernel reports the tier's own
        // rule — a pure step cannot read the world at all — rather than the code the session would have used for a
        // query step whose read the provider could not answer, and the handler never runs.
        var pure = new ResourcePlan("pure", pure: true);
        pure.Dispatch("pure-1");
        check(pure.Applied == 0 && pure.LastCode == RuntimeAbiCodes.PureWorldPort,
            "a pure step cannot read resources through the session: " + pure.LastCode);
        check(pure.SourceCalls == 0, "the refused pure read never reached the provider");
    }

    /// <summary>
    /// The one compiled resource reference a plan may carry, resolved once at load through the kind's owner. The
    /// provider's answer is the registry's static declaration, so a plan naming a resource its kind's owner does
    /// not have is refused with `stale-resource` before any step runs, and the same plan loads once it has it.
    /// </summary>
    private static void CompiledReference(Action<bool, string> check)
    {
        var loaded = LoadCompiled(PlanWorld(Id, "map"), "compiled-ok", Id);
        check(loaded.Loaded && loaded.Code == null, "a plan whose compiled reference resolves loads [" + loaded.Code + "]");
        var missing = LoadCompiled(PlanWorld(Id, "map", resourceIds: new[] { new ResourceRef("map", "map/b") }), "compiled-missing", Id);
        check(!missing.Loaded && missing.Code == RuntimeAbiCodes.StaleResource,
            "a compiled reference the kind's owner does not have is stale-resource at load [" + missing.Code + "]");
        var unowned = LoadCompiled(PlanWorld(Id, "zone", resourceIds: new[] { new ResourceRef("zone", "zone/a") }), "compiled-unowned", Id);
        check(!unowned.Loaded && unowned.Code == RuntimeAbiCodes.StaleResource,
            "a compiled reference no registered kind owns is stale-resource at load [" + unowned.Code + "]");
        var shapeless = LoadCompiled(PlanWorld(Id, "map"), "compiled-shape", Id, "{\"revision\":\"1\"}");
        check(!shapeless.Loaded && shapeless.Code == "missing-field", "a compiled reference without its id is refused [" + shapeless.Code + "]");
        var noWorld = LoadCompiled(PlanWorld(Id, "map", beginWorld: false), "compiled-no-world", Id);
        check(noWorld.Loaded, "the load-time resolution reads the registry's declaration and needs no world [" + noWorld.Code + "]");
    }

    /// <summary>One handle of every kind a provider casts, the native object it may stand for, and the life rules
    /// they all share.</summary>
    private static int HandleCasting()
    {
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL handle: " + name); }
        void RejectCode(Action action, string code, string name)
        {
            try { action(); }
            catch (RuntimeContractException error)
            {
                if (error.Code != code) throw new Exception($"FAIL handle: {name} rejected with {error.Code}, expected {code}");
                checks++; return;
            }
            throw new Exception("FAIL handle: " + name + " did not reject");
        }

        var kernel = new RuntimeKernel(Fixture.Identity); kernel.BeginWorld(1);
        var owner = kernel.RegisterModule(Module(Id, "map"), RuntimeLogLevel.Off);
        var foreign = kernel.RegisterModule(Module(Other, "zone", resourceIds: new[] { new ResourceRef("zone", "zone/a") }), RuntimeLogLevel.Off);
        var live = new HashSet<string>(StringComparer.Ordinal) { "example.dying.entity:1" };
        var native = new object();
        var dying = kernel.RegisterModule(Fixture.Module("example.dying") with
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
            { ["example.dying.entity"] = reference => live.Contains(reference.Id) },
            ObjectEntityResolvers = new[]
            {
                ObjectEntityResolver.Of(value => ReferenceEquals(value, native)
                    ? new EntityReference("example.dying.entity:1", 1, 1) : null)
            }
        }, RuntimeLogLevel.Off);
        kernel.StartRuntime(() => { });
        var casts = new (string Kind, Func<string, JsonElement> Cast)[]
        {
            ("effect", owner.CreateEffectHandle), ("charge", owner.CreateChargeHandle),
            ("pool", owner.CreatePoolHandle), ("request", owner.CreateRequestHandle)
        };
        foreach (var (kind, cast) in casts)
        {
            var handle = cast("invocation");
            Check(kernel.IsHandleLive(owner, handle), "a provider casts a live " + kind + " handle");
            // The consuming port of another lifetime is refused as `handle-lifetime` and not as `handle-kind`,
            // which is the one check saying the cast carried this kind's own name.
            RejectCode(() => kernel.CancelHandle(owner, handle, HandlePort(kind, "encounter"), kind), RuntimeAbiCodes.HandleLifetime,
                "a consuming port of another lifetime is refused: " + kind);
        }
        var bare = owner.CreateEffectHandle("session");
        Check(kernel.IsHandleLive(owner, bare), "a handle with no native object is live");
        RejectCode(() => owner.CreateEffectHandle("forever"), RuntimeAbiCodes.HandleLifetime, "a lifetime outside the shared table is refused");
        RejectCode(() => owner.CreateEffectHandle(null!), RuntimeAbiCodes.HandleLifetime, "a missing lifetime is refused");
        // The native object a handle stands for is the provider's own reading of it, and it goes with the handle.
        var held = owner.CreateEffectHandle("entity_life");
        owner.RegisterNative(held, native);
        Check(owner.TryNative(held, out var read) && ReferenceEquals(read, native), "a handle reads its native object back");
        RejectCode(() => owner.RegisterNative(held, new object()), "handle-native-conflict", "one handle holds one native object");
        Check(!foreign.TryNative(held, out _), "another module cannot read a handle it did not cast");
        Check(!kernel.IsHandleLive(foreign, held), "a handle is live only for the module that cast it");
        RejectCode(() => foreign.RegisterNative(held, native), "handle-owner", "another module cannot attach to a foreign handle");
        // The life rule: a handle whose native object's life has ended is released at the next advance, and every
        // later read of its value is stale-handle. No second expiry is invented for it.
        var living = dying.CreateEffectHandle("entity_life");
        dying.RegisterNative(living, native);
        Check(dying.TryNative(living, out _), "a handle to a current life reads back");
        live.Clear();
        kernel.Advance(1, true);
        Check(!dying.TryNative(living, out _), "a handle to an ended life is released");
        Check(!kernel.IsHandleLive(dying, living), "the released handle is no longer live");
        Check(kernel.IsHandleLive(owner, bare), "an unrelated handle survives its neighbour's life ending");
        // Cancellation: the kernel spends the handle, runs the hook the provider registered, and never runs it twice.
        var cancelled = 0;
        var cancellable = owner.CreatePoolHandle("session");
        owner.RegisterCancel(cancellable, () => cancelled++);
        Check(kernel.CancelHandle(owner, cancellable, HandlePort("pool", "session"), "cancel") == 1 && cancelled == 1,
            "cancelling a provider handle runs its hook once");
        Check(!kernel.IsHandleLive(owner, cancellable), "a cancelled handle is spent");
        RejectCode(() => kernel.CancelHandle(owner, cancellable, HandlePort("pool", "session"), "cancel"),
            RuntimeAbiCodes.StaleHandle, "a spent handle cannot be cancelled again");
        RejectCode(() => owner.RegisterCancel(cancellable, () => { }), RuntimeAbiCodes.StaleHandle, "a spent handle cannot register a hook");
        // A new world drops every handle: a value from the previous world names no slot of this one, so every use
        // of it is refused before any kind or lifetime is looked at.
        var acrossWorld = owner.CreateEffectHandle("session");
        Check(kernel.IsHandleLive(owner, acrossWorld), "a handle is live in the world that cast it");
        kernel.BeginWorld(2);
        Check(!kernel.IsHandleLive(owner, acrossWorld), "a new world drops every handle the previous one held");
        RejectCode(() => owner.RegisterNative(acrossWorld, native), "stale-world", "a handle from an ended world is refused by name");
        return checks;
    }

    /// <summary>The consuming port one handle is checked against: the same declaration a plan's step carries.</summary>
    private static JsonElement HandlePort(string kind, string lifetime)
        => RuntimeJson.Parse("{\"id\":\"task\",\"type\":\"handle\",\"schema\":\"forge.handle." + kind + "\",\"handleKind\":\"" + kind + "\",\"lifetime\":\"" + lifetime + "\"}");

    private static IReadOnlyList<ResourceRef> DefaultList() => new[] { Ref("map/a"), Ref("map/b") };
    private static ResourceRef Ref(string id) => new("map", id);
    private static RuntimeResourceProvider Ready(IReadOnlyList<ResourceRef>? resources = null, Func<string, ResourceRef?>? resolve = null)
    {
        var list = resources ?? DefaultList();
        return RuntimeResourceProvider.Of(() => list, resolve ?? (id => list.FirstOrDefault(r => r.ResourceId == id)));
    }
    private static RuntimeModule Module(string provider, string claim, RuntimeResourceProvider? owned = null,
        IReadOnlyList<ResourceRef>? resourceIds = null)
        => Fixture.Module(provider) with
        {
            ResourceProviders = new Dictionary<string, RuntimeResourceProvider> { [claim] = owned ?? Ready(resourceIds) }
        };
    private static RuntimeModule MountOwner() => Fixture.MountOwner();

    /// <summary>One live kernel with the fixture mount owner and one resource kind's owner already registered and
    /// the runtime started. When <paramref name="resourceIds"/> is given the owner answers with exactly those
    /// instances and nothing else, which is how a plan's compiled reference is made to go stale.</summary>
    private static RuntimeKernel Kernel(string provider, string claim, IReadOnlyList<ResourceRef>? resourceIds = null, bool beginWorld = true)
    {
        var kernel = new RuntimeKernel(Fixture.Identity);
        if (beginWorld) kernel.BeginWorld(1);
        kernel.RegisterModule(MountOwner(), RuntimeLogLevel.Off);
        kernel.RegisterModule(resourceIds == null
            ? Module(provider, claim)
            : Module(provider, claim, Ready(resourceIds, id => resourceIds.FirstOrDefault(r => r.ResourceId == id))), RuntimeLogLevel.Off);
        if (beginWorld) kernel.StartRuntime(() => { });
        return kernel;
    }

    /// <summary>
    /// A kernel whose provider owns a resource kind and whose action capability gains a `value` resource input, so
    /// a plan can carry the one compiled reference this batch makes legal. The runtime is started only after the
    /// module registers, because registration is frozen by then.
    /// </summary>
    private static RuntimeKernel PlanWorld(string provider, string claim, IReadOnlyList<ResourceRef>? resourceIds = null,
        bool beginWorld = true)
    {
        var kernel = new RuntimeKernel(Fixture.Identity);
        if (beginWorld) kernel.BeginWorld(1);
        kernel.RegisterModule(MountOwner(), RuntimeLogLevel.Off);
        var definition = resourceIds == null
            ? Module(provider, claim)
            : Module(provider, claim, Ready(resourceIds, id => resourceIds.FirstOrDefault(r => r.ResourceId == id)));
        var seed = JsonNode.Parse(definition.RegistryJson)!;
        var action = seed["capabilities"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == Fixture.ActionCapability(provider));
        var ports = action!["graph"]!["inputs"]!.AsArray().Select(p => JsonNode.Parse(p!.ToJsonString())!).ToList();
        ports.Add(JsonNode.Parse("{\"id\":\"value\",\"type\":\"resource\",\"schema\":\"forge.resource.map\",\"resourceKind\":\"map\"}")!);
        action["graph"]!["inputs"] = new JsonArray(ports.ToArray());
        kernel.RegisterModule(definition with { RegistryJson = seed.ToJsonString() }, RuntimeLogLevel.Off);
        if (beginWorld) kernel.StartRuntime(() => { });
        return kernel;
    }

    /// <summary>
    /// A plan with one step: the fixture action carrying a single compile-time resource reference on its own
    /// `value` port, and its recipient wired to the entity the fixture trigger carries. The plan is written from
    /// the registered contract, so it is laid out exactly as the compiler would lay one out and the load is decided
    /// by the reference alone.
    /// </summary>
    private static PlanLoadOutcome LoadCompiled(RuntimeKernel kernel, string planId, string provider, string? literal = null)
    {
        var manifest = RuntimeJson.Parse(kernel.ExportManifest());
        var bindingRows = manifest.GetProperty("registry").GetProperty("bindings").EnumerateArray().ToArray();
        var trigger = Fixture.Trigger(provider);
        var apply = provider + ".binding.apply";
        var pins = new[] { apply, trigger }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var rows = pins.Select(id =>
        {
            var row = bindingRows.Single(b => b.GetProperty("id").GetString() == id);
            return (object)new
            {
                bindingId = id, capabilityId = row.GetProperty("capabilityId").GetString(), capabilityVersion = "1.0.0",
                providerId = provider, providerVersion = "1.0.0", handler = row.GetProperty("handler").GetString()
            };
        }).ToArray();
        int Pin(string binding) => Array.IndexOf(pins, binding);
        var triggerGraph = kernel.ResolveGraphContract(Fixture.TriggerCapability(provider), "1.0.0", RuntimeJson.EmptyObject);
        var applyGraph = kernel.ResolveGraphContract(Fixture.ActionCapability(provider), "1.0.0", RuntimeJson.From(new { amount = 5 }));
        int Slot(JsonElement graph, string side, string id) => RuntimeJson.Rows(graph, side)
            .Select((p, index) => (p, index)).Single(x => RuntimeJson.Text(x.p, "id") == id).index;
        var plan = RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" },
            runtime = kernel.Identity, domain = "map", authority = "host", failurePolicy = "stop-entrypoint",
            permissions = Fixture.Permissions, dependencies = Array.Empty<string>(),
            limits = new { kernel.Limits.MaxEventsPerTick, kernel.Limits.MaxCommandsPerTick, kernel.Limits.MaxQueuedEvents, kernel.Limits.MaxCausalDepth },
            bindings = rows, attachments = Fixture.Attachments,
            entrypoints = new[] { new { nodeId = "Entry", binding = Pin(trigger), layout = Layout(triggerGraph), start = 0,
                steps = new object[] { new { nodeId = "S0_action", nodeKind = "action", binding = Pin(apply), layout = Layout(applyGraph, new object[] { 5 }),
                    inputs = new object[]
                    {
                        new { slot = Slot(applyGraph, "inputs", "target"), fromEventSlot = Slot(triggerGraph, "outputs", "target") },
                        new { slot = Slot(applyGraph, "inputs", "value"),
                            value = RuntimeJson.Parse(literal ?? "{\"id\":\"map/a\",\"revision\":\"1\"}") }
                    },
                    successors = new int?[] { null } } } } }
        }).GetRawText();
        return kernel.LoadPlans(new[] { PlanCandidate.Loaded(planId + ".plan.json", plan) })[0];
    }

    /// <summary>The compiled layout of one resolved contract: the descriptors a plan truly carries, written from
    /// the contract instead of restated, so a plan here is laid out exactly as the compiler's would be.</summary>
    private static object Layout(JsonElement graph, object[]? constants = null)
        => new { inputs = RuntimeGraphContracts.Layout(graph, "inputs"), outputs = RuntimeGraphContracts.Layout(graph, "outputs"),
            constants = constants ?? Array.Empty<object>(), promoted = Array.Empty<int>() };

    /// <summary>
    /// A plan whose evaluated step reads the resource table through the session and whose action reports what the
    /// read answered. The action is what forces the step, so a refusal is read off the same command receipt.
    /// </summary>
    private sealed class ResourcePlan
    {
        private const string Binding = Id + ".binding.resources";
        private const string TriggerBinding = Id + ".binding.trigger";
        private readonly RuntimeModuleHandle module;
        internal readonly RuntimeKernel Kernel;
        internal int Applied, Enumerated, SourceCalls;
        internal string? Resolved, LastCode;

        internal ResourcePlan(string planId, bool pure = false)
        {
            Kernel = new RuntimeKernel(Fixture.Identity);
            Kernel.BeginWorld(1);
            // The mount owner registers before the plan loads: a plan's mount kind has to be owned first.
            Kernel.RegisterModule(MountOwner(), RuntimeLogLevel.Off);
            var definition = Fixture.Module(Id, _ => { Applied++; return CommandResult.Succeeded(RuntimeJson.From(new { actual = 1 })); });
            var seed = JsonNode.Parse(definition.RegistryJson)!;
            // The action takes the count the evaluated step answered with, so the step is what forces the read.
            seed["capabilities"]!.AsArray()[1]!["graph"]!["inputs"] = JsonNode.Parse(RuntimeJson.From(new object[]
            {
                new { id = "in", type = "execution" }, new { id = "target", type = "entity" },
                new { id = "count", type = "number", optional = true }
            }).GetRawText())!;
            var capabilityGraph = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["domains"] = RuntimeJson.From(new[] { "map" }),
                ["execution"] = RuntimeJson.From(pure ? "pure" : "query"),
                ["inputs"] = RuntimeJson.From(Array.Empty<object>()),
                ["outputs"] = RuntimeJson.From(new object[] { new { id = "count", type = "number" } }),
                ["parameters"] = RuntimeJson.From(Array.Empty<object>())
            };
            // A `pure` capability declares no read and owns no world port; a `query` one declares the world read
            // that lets a step claim the tier. The pure case is refused where it reads, not at registration, which
            // is the rule under test.
            if (!pure) capabilityGraph["reads"] = RuntimeJson.From(new[] { "world" });
            seed["capabilities"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Id + ".resources", owner = Id, kind = "selector", label = "Resource reader", version = "1.0.0",
                parameters = new { }, graph = capabilityGraph
            }).GetRawText())!);
            seed["bindings"]!.AsArray().Add(JsonNode.Parse(RuntimeJson.From(new
            {
                id = Binding, capabilityId = Id + ".resources", providerId = Id, handler = Id + ".handler.resources",
                role = "evaluate", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
            }).GetRawText())!);
            module = Kernel.RegisterModule(definition with
            {
                RegistryJson = seed.ToJsonString(),
                ResourceProviders = new Dictionary<string, RuntimeResourceProvider> { ["map"] = Ready(null, Counting) },
                Evaluators = new Dictionary<string, EvaluatorHandler> { [Id + ".handler.resources"] = Read },
                Shapes = new Dictionary<string, HandlerShape>
                {
                    [Fixture.Handler(Id)] = new HandlerShape().Inputs("target", "count").Outputs("result"),
                    [Id + ".handler.resources"] = new HandlerShape().Outputs("count")
                },
                BindingSupport = new[] { Fixture.Support(Id)[0], Fixture.Support(Id)[1], new BindingSupport(Binding, "implementation-only", NoPermissions) },
                // The plan's action reads the entity the event is about, so this provider owns that namespace too.
                EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
                {
                    [Id] = reference => reference.Id == Id + ":1" && reference.WorldEpoch == 1 && reference.LifeEpoch == 1
                }
            }, RuntimeLogLevel.Off);
            Kernel.StartRuntime(() => { });
            Load(planId, pure);
        }

        /// <summary>The provider's own resolve, counted: a refused read must never reach it.</summary>
        private ResourceRef? Counting(string id)
        {
            SourceCalls++;
            return id is "map/a" or "map/b" ? Ref(id) : null;
        }

        private JsonElement Read(EvaluationContext context)
        {
            if (!context.Query.TryResources("map", out var resources, out var code)) throw new RuntimeContractException(code, code);
            Enumerated = resources.Count;
            if (!context.Query.TryResource("map", "map/a", out var reference, out code)) throw new RuntimeContractException(code, code);
            Resolved = reference!.ResourceId;
            return RuntimeJson.From(new { count = (double)resources.Count });
        }

        private void Load(string planId, bool pure)
        {
            var manifest = RuntimeJson.Parse(Kernel.ExportManifest());
            var registry = manifest.GetProperty("registry");
            var triggerGraph = Kernel.ResolveGraphContract(Fixture.TriggerCapability(Id), "1.0.0", RuntimeJson.EmptyObject);
            var selector = Kernel.ResolveGraphContract(Id + ".resources", "1.0.0", RuntimeJson.EmptyObject);
            var action = Kernel.ResolveGraphContract(Fixture.ActionCapability(Id), "1.0.0", RuntimeJson.From(new { amount = 5 }));
            var used = new[] { TriggerBinding, Binding, Id + ".binding.apply" }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            int Pin(string binding) => Array.IndexOf(used, binding);
            var pins = used.Select(id =>
            {
                var row = registry.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("id").GetString() == id);
                var capabilityId = row.GetProperty("capabilityId").GetString()!;
                return (object)new
                {
                    bindingId = id, capabilityId,
                    capabilityVersion = registry.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("id").GetString() == capabilityId).GetProperty("version").GetString(),
                    providerId = row.GetProperty("providerId").GetString(), providerVersion = "1.0.0", handler = row.GetProperty("handler").GetString()
                };
            }).ToArray();
            var steps = new object[]
            {
                new { nodeId = "S0_resources", nodeKind = pure ? "pure" : "query", binding = Pin(Binding), layout = Layout(selector),
                    inputs = Array.Empty<object>(), successors = Array.Empty<int?>() },
                new { nodeId = "S1_action", nodeKind = "action", binding = Pin(Id + ".binding.apply"), layout = Layout(action, new object[] { 5 }),
                    inputs = new object[]
                    {
                        // The trigger's own output order: 0 is `next`, 1 is the entity the event is about.
                        new { slot = 1, fromEventSlot = 1 },
                        new { slot = 2, fromStepSlot = new { step = 0, port = 0 } }
                    },
                    successors = new int?[] { null } }
            };
            Kernel.LoadPlan(RuntimeJson.From(new
            {
                schemaVersion = 1, kind = "forge-runtime-plan", planId, resource = new { id = "author.resource", revision = "revision-1" },
                runtime = Kernel.Identity, domain = "map", authority = "host", failurePolicy = "stop-entrypoint",
                permissions = Fixture.Permissions, dependencies = Array.Empty<string>(),
                limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
                bindings = pins, attachments = Fixture.Attachments,
                entrypoints = new[] { new { nodeId = "Entry", binding = Pin(TriggerBinding), layout = Layout(triggerGraph), start = 1, steps } }
            }).GetRawText());
        }

        internal TickResult Dispatch(string eventId)
        {
            var queued = Kernel.Publish(module, new RuntimeEvent(eventId, TriggerBinding, 1, 1, "shared-scope",
                RuntimeJson.From(new { target = new EntityReference(Id + ":1", 1, 1) })));
            if (queued.Status != "queued") throw new Exception("FAIL resource: " + eventId + " was not queued: " + queued.Status + "/" + queued.Code);
            var tick = Kernel.Advance(1, true);
            LastCode = tick.Commands.Count > 0 ? tick.Commands[^1].Result.Code : "no-command";
            return tick;
        }
    }
}
