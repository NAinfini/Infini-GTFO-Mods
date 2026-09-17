using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;
using ForgeTrigger.Targeting;

// The query rows of this batch, dispatched: the production Trigger provider answers a plan's `query` step with the
// capability, binding, shape and evaluator it registers, and the case files shared with the R3 consumer suite cover
// the selection rules themselves. Two questions are answered here and nowhere else — that the production module
// really publishes and serves these rows, and that a read the world cannot answer is refused by name rather than
// taken for "outside".
if (args.Length != 1) throw new ArgumentException("Usage: ForgeTrigger.QueryFacts <result.json>");
var checks = new List<string>();
var assertions = 0;
void Check(bool value, string name)
{
    assertions++;
    if (!value) checks.Add(name);
    Console.WriteLine((value ? "ok   " : "FAIL ") + name);
}

// --- the batch's own selection cases -------------------------------------------------------------------------
try { ObservedSpaceTests.Run(Check); }
catch (Exception error) { Check(false, "space selection cases: " + error.GetType().Name + ": " + error.Message); }

using var scene = new ZoneScene();
var kernel = scene.Kernel;

// --- the production module serves every row the batch declares -----------------------------------------------
try
{
    var manifest = RuntimeJson.Parse(kernel.ExportManifest());
    var registry = manifest.GetProperty("registry");
    string[] Ids(string list) => registry.GetProperty(list).EnumerateArray()
        .Select(row => row.GetProperty("id").GetString()!).ToArray();
    var capabilities = Ids("capabilities");
    // The kernel only accepts a registration whose rows resolve, so a served row is proof that the bindings,
    // shapes, evaluators and candidate sources line up; the three query rows are named here so a rename cannot
    // pass as "some row is served".
    Check(capabilities.Contains("forge.selector.target.shape_overlap"), "q-radius: shape_overlap is published by the production module");
    Check(capabilities.Contains("forge.selector.target.nearest"), "q-nearest: nearest is published by the production module");
    Check(capabilities.Contains("forge.selector.target.zone_members"), "q-zone: zone_members is published by the production module");
    Check(registry.GetProperty("bindings").EnumerateArray().Any(row =>
            row.GetProperty("capabilityId").GetString() == "forge.selector.target.zone_members"
            && row.GetProperty("role").GetString() == "observe"
            && row.GetProperty("status").GetString() == "implemented"),
        "q-zone: the row's binding is published as an implemented observe binding");
    Check(kernel.ResolveGraphContract("forge.selector.target.zone_members", "1.0.0", RuntimeJson.EmptyObject)
            .GetProperty("inputs").EnumerateArray().Select(port => port.GetProperty("id").GetString())
            .SequenceEqual(new[] { "candidates", "zone" }),
        "q-zone: the published row declares the candidate set and the zone resource, in that order");
    Check(kernel.ResolveGraphContract("forge.selector.target.zone_members", "1.0.0", RuntimeJson.EmptyObject)
            .GetProperty("outputs").EnumerateArray().Select(port => port.GetProperty("id").GetString())
            .SequenceEqual(new[] { "targets" }),
        "q-zone: the published row answers the entity collection the consumer reads");
}
catch (Exception error) { Check(false, "published rows: " + error.GetType().Name + ": " + error.Message); }

// --- the zone row through a real dispatch --------------------------------------------------------------------
try
{
    var outcome = scene.LoadAndDispatch();
    Check(outcome.Loaded, "q-zone: the production plan loads against the registered rows (" + outcome.Code + ": " + outcome.Detail + ")");
    Check(scene.ResultCode == "succeeded",
        "q-zone: the query step succeeds and the action receives its answer (code " + scene.ResultCode + ", detail " + scene.ResultDetail + ")");
    Check(scene.RecordedTargets().SequenceEqual(new[] { "test.zone:a" }),
        "q-zone: only the candidate standing in the named zone reaches the consumer (got "
        + string.Join("|", scene.RecordedTargets()) + ")");
}
catch (Exception error) { Check(false, "q-zone dispatch: " + error.GetType().Name + ": " + error.Message); }

// --- a zone read that cannot be answered is refused, never "outside" -----------------------------------------
using (var unplaced = new ZoneScene(unplaced: true))
{
    try
    {
        unplaced.LoadAndDispatch();
        Check(unplaced.ResultCode == "rejected",
            "q-zone: a candidate no provider can place rejects the step instead of dropping it");
    }
    catch (Exception error) { Check(false, "q-zone refusal: " + error.GetType().Name + ": " + error.Message); }
}

// --- the typed comparison row, through a real dispatch --------------------------------------------------------
using (var compare = new CompareScene())
{
    try
    {
        var inside = compare.LoadAndDispatch(6);
        Check(inside.Loaded, "g-compare: the production plan loads against the registered row (" + inside.Code + ": " + inside.Detail + ")");
        Check(compare.Ran("S2_then") && !compare.Ran("S3_otherwise"),
            "g-compare: 6 against 5.5 with tolerance 0.5 takes the `then` branch (ran " + compare.Branches() + ")");
        var outside = compare.LoadAndDispatch(4);
        Check(outside.Loaded && compare.Ran("S3_otherwise") && !compare.Ran("S2_then"),
            "g-compare: 4 against 5.5 with tolerance 0.5 takes the `otherwise` branch (ran " + compare.Branches() + ")");
        var near = compare.LoadAndDispatch(5.2);
        Check(near.Loaded && compare.Ran("S2_then") && !compare.Ran("S3_otherwise"),
            "g-compare: an operand inside the tolerance is not below it (ran " + compare.Branches() + ")");
        Check(compare.Failed == null, "g-compare: every activation dispatched without a refusal (" + compare.Failed + ")");

    }
    catch (Exception error) { Check(false, "g-compare dispatch: " + error.GetType().Name + ": " + error.Message); }
}

var failures = checks.ToArray();
File.WriteAllText(Path.GetFullPath(args[0]), JsonSerializer.Serialize(new
{
    status = failures.Length == 0 ? "passed" : "failed", scope = "query-facts", assertions,
    gameVerified = false, failures
}, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine($"{(failures.Length == 0 ? "PASS" : "FAIL")} {assertions - failures.Length}/{assertions} query facts; no GTFO execution.");
return failures.Length == 0 ? 0 : 1;

/// <summary>
/// One kernel holding the production Trigger provider — every query row this batch declares, served by its own
/// module — plus the builtin contract modules the plan's mount and those rows name. All cases share it, because
/// registration is frozen at startup and one process serves one provider.
/// </summary>
internal sealed class ZoneScene : IDisposable
{
    private const string Provider = "test.zone.fixture";
    private const string MountProvider = "test.zone.mounts";
    private const string TriggerCapability = Provider + ".trigger.pulse";
    private const string TriggerBinding = Provider + ".binding.pulse";
    private const string ActionCapability = Provider + ".action.record";
    private const string ActionBinding = Provider + ".binding.record";
    private const string ActionHandler = Provider + ".handler.record";
    private const string LevelReference = "31:A:0";
    private const string PlanId = "test.query.zone";
    private static readonly MethodInfo LayoutFrame = typeof(RuntimeKernel).Assembly
        .GetType("ForgeRuntime.Framework.RuntimeGraphContracts", true)!
        .GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>Every zone read this scene's provider answered, so a case can check that the row reads each
    /// candidate exactly once.</summary>
    private int zoneReads;
    private readonly bool unplaced;
    private readonly RuntimeModuleHandle fixture;
    private readonly RuntimeModuleHandle mounts;
    private readonly Dictionary<string, EntityReference> standing = new(StringComparer.Ordinal);
    internal RuntimeKernel Kernel { get; } =
        new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "query-facts"));
    internal string? ResultCode { get; private set; }
    internal string? ResultDetail { get; private set; }
    internal JsonElement Recorded { get; private set; }

    /// <summary>One scene over two candidates: one in the named zone and one in another. With
    /// <paramref name="unplaced"/> the provider cannot place the second one, which is the read a zone filter must
    /// refuse rather than treat as "outside".</summary>
    internal ZoneScene(bool unplaced = false)
    {
        this.unplaced = unplaced;
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        // No control module: this suite's plan uses no control step, and the flow vocabulary is another batch's
        // to change — registering it would make this suite fail for a reason that is not this batch's.
        // The production module: the capability rows, binding rows, shapes, evaluators and the candidate sources
        // every query row of this batch reads.
        Kernel.RegisterModule(ForgeTrigger.ModuleDefinition.Create(), RuntimeLogLevel.Off);
        // The plan mounts the whole level, and a mount kind nothing owns is refused at load: the own little
        // provider that answers that one kind stands here, exactly as every other suite stands its mount up.
        mounts = Kernel.RegisterModule(Mounts(), RuntimeLogLevel.Off);
        fixture = Kernel.RegisterModule(Fixture(), RuntimeLogLevel.Off);
        Kernel.StartRuntime(() => { });
        Kernel.BeginWorld(1);
    }

    

    internal PlanLoadOutcome LoadAndDispatch()
    {
        var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded(PlanId + ".json", Build()) })[0];
        if (!outcome.Loaded) return outcome;
        // The trigger publishes the candidate set it is about, which is the set the plan's query step filters.
        var candidates = RuntimeJson.From(CandidateIds().Select(id => new EntityReference(id, 1, 1)).ToArray());
        var queued = fixture.Publish(new RuntimeEvent("query.zone", TriggerBinding, 1, 1, "zone-scope",
            RuntimeJson.From(new { candidates })));
        ResultDetail = queued.Status + "/" + queued.Code;
        try
        {
            var result = Kernel.Advance(1, true);
            ResultCode = result.Commands.Count > 0 ? result.Commands[^1].Result.Status : "no-command";
            if (result.Commands.Count > 0) ResultDetail = result.Commands[^1].Result.Code + "|" + result.Commands[^1].Result.Detail;
        }
        catch (Exception error) { ResultDetail = "advance threw " + error.GetType().Name + ": " + error.Message; }
        return outcome;
    }

    /// <summary>The entities the recording action received, in the order the query answered them.</summary>
    internal string[] RecordedTargets() => Recorded.GetProperty("targets").EnumerateArray()
        .Select(item => item.GetProperty("id").GetString()!).ToArray();

    /// <summary>The candidate set the fixture publishes, in the id order the kernel will see.</summary>
    internal string[] CandidateIds() => standing.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();

    /// <summary>The one mount kind every plan here declares: a level names no event subject, so its kind is judged
    /// from the mount target alone.</summary>
    private static RuntimeModule Mounts() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = MountProvider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                category == null && reference == "31:A:0")
        }
    };

    private RuntimeModule Fixture()
    {
        // Two candidates of this provider's own kind, each standing in a zone of the level: `a` in the zone the
        // plan names and `b` in another one.
        standing["test.zone:a"] = RuntimeZones.Reference(1, 0, 0, 1);
        standing["test.zone:b"] = RuntimeZones.Reference(1, 0, 0, 2);
        var capabilities = new JsonArray
        {
            JsonNode.Parse(RuntimeJson.From(new
            {
                id = TriggerCapability, owner = Provider, kind = "trigger", label = "Fixture pulse", version = "1.0.0",
                parameters = new { }, graph = new
                {
                    // The trigger carries the candidate set, which is how the plan hands the row its `candidates`
                    // input: an entity collection is dispatched, never compiled into a plan's own frame.
                    domains = new[] { "logic" }, execution = "host", inputs = Array.Empty<object>(),
                    outputs = new object[]
                    {
                        new { id = "next", type = "execution" },
                        new { id = "candidates", type = "entity", cardinality = "many" }
                    },
                    parameters = Array.Empty<object>()
                }
            }).GetRawText())!,
            JsonNode.Parse(RuntimeJson.From(new
            {
                id = ActionCapability, owner = Provider, kind = "action", label = "Record", version = "1.0.0",
                parameters = new { }, graph = new
                {
                    domains = new[] { "logic" }, execution = "host",
                    inputs = new object[] { new { id = "in", type = "execution" }, new { id = "targets", type = "entity", cardinality = "many" } },
                    outputs = new object[]
                    {
                        new { id = "next", type = "execution" },
                        new { id = "result", type = "result", schema = "test.query.zone.result", fields = new object[]
                        {
                            new { id = "target", type = "entity" },
                            new { id = "status", type = "enum", schema = "execution_outcome" },
                            new { id = "committed", type = "enum", schema = "commit_state" },
                            new { id = "code", type = "string" }
                        } }
                    },
                    parameters = Array.Empty<object>(),
                    recipients = new { input = "targets", target = "entity", cardinality = "many", requires = Array.Empty<string>(), result = "result" }
                }
            }).GetRawText())!
        };
        var registry = RuntimeJson.From(new
        {
            providers = new[] { new { id = Provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities,
            bindings = new object[]
            {
                new { id = TriggerBinding, capabilityId = TriggerCapability, providerId = Provider, handler = Provider + ".handler.pulse",
                    role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
                new { id = ActionBinding, capabilityId = ActionCapability, providerId = Provider, handler = ActionHandler,
                    role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
            }
        });
        return new RuntimeModule(RuntimeKernel.ApiVersion, registry.GetRawText(),
            new Dictionary<string, CommandHandler> { [ActionHandler] = Record },
            new[]
            {
                new BindingSupport(TriggerBinding, "implementation-only", Array.Empty<string>()),
                new BindingSupport(ActionBinding, "implementation-only", Array.Empty<string>())
            },
            // A kind is only answerable because the provider that owns it also resolves it: the entity read, the
            // candidate read and the zone read all route through this one table. A reference this world does not
            // stand behind — a life that has been replaced, one of another world — is false rather than an error,
            // which is what makes it a refusal and not a broken provider.
            new Dictionary<string, Func<EntityReference, bool>>
            {
                ["test.zone"] = reference => standing.ContainsKey(reference.Id)
                    && reference.WorldEpoch == 1 && reference.LifeEpoch == 1,
                // A zone reference is resolvable exactly when this level has that zone: the resource owner and the
                // entity resolver answer from the one table, so a plan cannot name a place nothing stands in.
                [RuntimeZones.EntityKind] = reference =>
                    standing.Values.Any(zone => zone.Id == reference.Id) && reference.WorldEpoch == 1
            })
        {
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
            {
                ["test.zone"] = reference => standing.ContainsKey(reference.Id)
                    ? new RuntimeEntitySnapshot(reference, "test.zone", null, "alive", Array.Empty<string>(),
                        Array.Empty<string>(), new double[] { 0, 0, 0 }) : null
            },
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>>
            {
                ["test.zone"] = () => CandidateIds().Select(id => new EntityReference(id, 1, 1)).ToArray()
            },
            // The one read the zone row makes of a provider: where a candidate of its kind stands. Null is an
            // entity this provider cannot place, which the row must refuse rather than filter away.
            EntityZones = new Dictionary<string, Func<EntityReference, EntityReference?>>
            {
                ["test.zone"] = reference =>
                {
                    zoneReads++;
                    return unplaced && reference.Id == "test.zone:b" ? null : standing.GetValueOrDefault(reference.Id);
                },
                // The plan's own zone reference is an entity of the `gtfo.zone` kind: it is read back through the
                // same responder so a zone this level does not have is `stale-entity` rather than a filter that
                // compares every candidate against a place nothing stands in.
                [RuntimeZones.EntityKind] = reference =>
                    standing.Values.Any(zone => zone.Id == reference.Id) ? standing.Values.First(zone => zone.Id == reference.Id) : null
            },
            // A plan's zone input is a compiled resource reference, which the loader resolves through the kind's
            // owner before the plan loads. The fixture stands that owner up for the two zones it uses, so the plan
            // is loadable in the kernel it was built for; production has the same owner in ForgeMap.
            ResourceProviders = new Dictionary<string, RuntimeResourceProvider>
            {
                [RuntimeZones.ResourceKind] = RuntimeResourceProvider.Of(
                    () => standing.Values.Distinct().Select(zone => new ResourceRef(RuntimeZones.ResourceKind, zone.Id)).ToArray(),
                    id => standing.Values.Any(zone => zone.Id == id) ? new ResourceRef(RuntimeZones.ResourceKind, id) : null)
            },
            // The registry resolves a shape per supplied handler and refuses one no binding used, so the fixture
            // declares the action's shape and nothing else: a trigger's payload arrives as a whole frame.
            Shapes = new Dictionary<string, HandlerShape> { [ActionHandler] = new HandlerShape().Inputs("targets") }
        };
    }

    private CommandResult Record(CommandContext context)
    {
        Recorded = context.Inputs;
        return CommandResult.Succeeded(RuntimeJson.EmptyObject);
    }

    /// <summary>The one plan: the fixture's trigger, the production zone query reading the fixture's own candidate
    /// kind and one named zone, and the action that records what arrived. Pins, layouts and versions come from the
    /// kernel's own export, so the plan follows the registered contracts instead of a fixture that could drift.</summary>
    private string Build()
    {
        var manifest = RuntimeJson.Parse(Kernel.ExportManifest());
        var registry = manifest.GetProperty("registry");
        JsonElement Row(string list, string id) => registry.GetProperty(list).EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == id);
        var ids = new[] { TriggerBinding, ActionBinding, "forge.module.trigger.binding.zone_members" }
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var pins = ids.Select(id =>
        {
            var binding = Row("bindings", id);
            var capabilityId = binding.GetProperty("capabilityId").GetString()!;
            return (object)new
            {
                bindingId = id, capabilityId,
                capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
                providerId = binding.GetProperty("providerId").GetString()!,
                providerVersion = Row("providers", binding.GetProperty("providerId").GetString()!).GetProperty("version").GetString()!,
                handler = binding.GetProperty("handler").GetString()!
            };
        }).ToArray();
        var permissions = manifest.GetProperty("bindingSupport").EnumerateArray()
            .Where(support => ids.Contains(support.GetProperty("bindingId").GetString()!))
            .SelectMany(support => support.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var action = Kernel.ResolveGraphContract(ActionCapability, "1.0.0", RuntimeJson.EmptyObject);
        var trigger = Kernel.ResolveGraphContract(TriggerCapability, "1.0.0", RuntimeJson.EmptyObject);
        var zone = Kernel.ResolveGraphContract("forge.selector.target.zone_members", "1.0.0", RuntimeJson.EmptyObject);
        object Layout(JsonElement contract, object[] constants) => new
        {
            inputs = LayoutFrame.Invoke(null, new object[] { contract, "inputs" })!,
            outputs = LayoutFrame.Invoke(null, new object[] { contract, "outputs" })!,
            constants, promoted = Array.Empty<int>()
        };
        int Port(JsonElement contract, string side, string id) => contract.GetProperty(side).EnumerateArray()
            .Select((port, index) => (port, index)).Single(x => x.port.GetProperty("id").GetString() == id).index;
        // The row's one parameter, in declaration order: `empty` = emit-empty is member 0 of the `empty_policy` set.
        var constants = new object[] { 0 };
        return RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId = PlanId, resource = new { id = PlanId, revision = "1" },
            runtime = Kernel.Identity, domain = "logic", authority = "host", failurePolicy = "stop-entrypoint",
            permissions, dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
            bindings = pins, attachments = new object[] { new { kind = "level", reference = LevelReference } },
            // The candidate set is the fixture's own kind, which is what the row's `candidates` port reads; the
            // zone is the compiled resource constant the resource port carries, in the one text every zone reader
            // spells.
            entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(ids, TriggerBinding),
                layout = Layout(trigger, Array.Empty<object>()), start = 1, steps = new object[]
                {
                    new { nodeId = "Zone", nodeKind = "query", binding = Array.IndexOf(ids, "forge.module.trigger.binding.zone_members"),
                        layout = Layout(zone, constants),
                        inputs = new object[]
                        {
                            new { slot = Port(zone, "inputs", "candidates"), fromEventSlot = 1 },
                            // The row's `zone` input is a compiled resource constant, which a plan writes in the
                            // document's own `{id, revision}` form: the id is the zone's coordinates, and the
                            // loader resolves it through the kind's owner before the plan is dispatched.
                            new { slot = Port(zone, "inputs", "zone"), value = (object)new { id = "gtfo.zone:0:0:1", revision = "1" } }
                        },
                        successors = Array.Empty<int?>() },
                    new { nodeId = "Record", nodeKind = "action", binding = Array.IndexOf(ids, ActionBinding),
                        layout = Layout(action, Array.Empty<object>()),
                        inputs = new object[] { new { slot = Port(action, "inputs", "targets"), fromStepSlot = new { step = 0, port = 0 } } },
                        successors = new int?[] { null } }
                } } }
        }).GetRawText();
    }

    public void Dispose()
    {
        fixture.Dispose();
        mounts.Dispose();
        if (Kernel.StartupState != RuntimeStartupState.Stopped) Kernel.StopRuntime();
    }
}

/// <summary>
/// One kernel holding the production Trigger provider beside the kernel's own control contract, dispatching a plan
/// whose `forge.condition.predicate.compare` step decides which branch of a `flow.branch` runs. This is the row's
/// end-to-end case: the plan loads against the registered parameterized shape, the kernel resolves the compiled
/// `value_type` and `operator` constants, and the boolean the handler answered is what routes the flow.
/// </summary>
internal sealed class CompareScene : IDisposable
{
    private const string Provider = "test.compare.fixture";
    private const string MountProvider = "test.compare.mounts";
    private const string TriggerCapability = Provider + ".trigger.pulse";
    private const string TriggerBinding = Provider + ".binding.pulse";
    private const string ActionCapability = Provider + ".action.record";
    private const string ActionBinding = Provider + ".binding.record";
    private const string ActionHandler = Provider + ".handler.record";
    private const string CompareBinding = "forge.module.trigger.binding.compare";
    private const string BranchBinding = "forge.contract.control.binding.branch";
    private const string LevelReference = "31:A:0";
    private const string PlanId = "test.query.compare";
    /// <summary>The one entity a compare scene's event is about: a real reference of the fixture's own kind, so
    /// the recipient port of the two branch actions is a set the kernel resolves rather than an invented one.</summary>
    private static readonly EntityReference Target = new("test.compare:1", 1, 1);
    private static readonly MethodInfo LayoutFrame = typeof(RuntimeKernel).Assembly
        .GetType("ForgeRuntime.Framework.RuntimeGraphContracts", true)!
        .GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>The refusal a dispatch reported, if any, so a case can name why it failed.</summary>
    internal string? Failed { get; private set; }

    private readonly List<string> ran = new();
    private readonly RuntimeModuleHandle fixture;
    private readonly RuntimeModuleHandle mounts;
    internal RuntimeKernel Kernel { get; } =
        new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "query-facts"));

    internal CompareScene()
    {
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(ForgeTrigger.ModuleDefinition.Create(), RuntimeLogLevel.Off);
        mounts = Kernel.RegisterModule(Mounts(), RuntimeLogLevel.Off);
        fixture = Kernel.RegisterModule(Fixture(), RuntimeLogLevel.Off);
        Kernel.StartRuntime(() => { });
        Kernel.BeginWorld(1);
    }

    /// <summary>Replaces the plan with one whose comparison constant is the given amount and dispatches one event
    /// carrying it, so the same scene answers both the inside-tolerance and the outside-tolerance question.</summary>
    internal (bool Loaded, string Code, string Detail) LoadAndDispatch(double amount)
    {
        ran.Clear();
        // The scene dispatches the same plan three times with one constant changed, so the previous copy is
        // unloaded first: the kernel refuses two loaded plans that share an id.
        Kernel.UnloadPlan(PlanId);
        var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded(PlanId + ".json", Build(amount)) })[0];
        if (!outcome.Loaded) return (false, outcome.Code ?? "-", outcome.Detail ?? "");
        // The dispatch identity carries the constant this activation compiled: the ledger drops an event it has
        // already accepted, and two activations of the same plan with different amounts are two events.
        var eventId = "query.compare." + amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var queued = fixture.Publish(new RuntimeEvent(eventId, TriggerBinding, 1, 1, "compare-scope",
            RuntimeJson.From(new { targets = new[] { Target } })));

        try
        {
            var result = Kernel.Advance(1, true);

            Failed = result.Events.FirstOrDefault(entry => entry.Status == "rejected")?.Code;
            if (Failed == null && result.Commands.Any(c => c.Result.Status == "rejected"))
                Failed = result.Commands.First(c => c.Result.Status == "rejected").Result.Code;
        }
        catch (Exception error) { Failed = error.GetType().Name + ": " + error.Message; }
        return (true, queued.Code, queued.Status);
    }

    internal bool Ran(string branch) => ran.Contains(branch);
    internal string Branches() => string.Join("|", ran);

    private static RuntimeModule Mounts() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = MountProvider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) => category == null && reference == LevelReference)
        }
    };

    private RuntimeModule Fixture()
    {
        var capabilities = new JsonArray
        {
            JsonNode.Parse(RuntimeJson.From(new
            {
                id = TriggerCapability, owner = Provider, kind = "trigger", label = "Fixture pulse", version = "1.0.0",
                parameters = new { }, graph = new
                {
                    domains = new[] { "logic" }, execution = "host", inputs = Array.Empty<object>(),
                    outputs = new object[]
                    {
                        new { id = "next", type = "execution" },
                        new { id = "targets", type = "entity", cardinality = "many" }
                    },
                    parameters = Array.Empty<object>()
                }
            }).GetRawText())!,
            JsonNode.Parse(RuntimeJson.From(new
            {
                id = ActionCapability, owner = Provider, kind = "action", label = "Record branch", version = "1.0.0",
                parameters = new { }, graph = new
                {
                    domains = new[] { "logic" }, execution = "host",
                    inputs = new object[]
                    {
                        new { id = "in", type = "execution" },
                        new { id = "targets", type = "entity", cardinality = "many" }
                    },
                    outputs = new object[]
                    {
                        new { id = "next", type = "execution" },
                        new { id = "result", type = "result", schema = "test.compare.fixture.result", fields = new object[]
                        {
                            new { id = "target", type = "entity" },
                            new { id = "status", type = "enum", schema = "execution_outcome" },
                            new { id = "committed", type = "enum", schema = "commit_state" },
                            new { id = "code", type = "string" }
                        } }
                    },
                    parameters = Array.Empty<object>(),
                    recipients = new { input = "targets", target = "entity", cardinality = "many", requires = Array.Empty<string>(), result = "result" }
                }
            }).GetRawText())!
        };
        var registry = RuntimeJson.From(new
        {
            providers = new[] { new { id = Provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities,
            bindings = new object[]
            {
                new { id = TriggerBinding, capabilityId = TriggerCapability, providerId = Provider, handler = Provider + ".handler.pulse",
                    role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() },
                new { id = ActionBinding, capabilityId = ActionCapability, providerId = Provider, handler = ActionHandler,
                    role = "execute", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() }
            }
        });
        return new RuntimeModule(RuntimeKernel.ApiVersion, registry.GetRawText(),
            new Dictionary<string, CommandHandler> { [ActionHandler] = Record },
            new[]
            {
                new BindingSupport(TriggerBinding, "implementation-only", Array.Empty<string>()),
                new BindingSupport(ActionBinding, "implementation-only", Array.Empty<string>())
            })
        {
            Shapes = new Dictionary<string, HandlerShape> { [ActionHandler] = new HandlerShape().Inputs("targets") },
            // The one entity kind this fixture's event is about. The kernel resolves every reference an event
            // carries, so the recipient set is answered here rather than left to a kind nobody owns.
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                ["test.compare"] = reference => reference == Target
            }
        };
    }

    private CommandResult Record(CommandContext context)
    {
        ran.Add(context.NodeId);
        return CommandResult.Succeeded(RuntimeJson.EmptyObject);
    }

    /// <summary>The plan: the trigger, the production comparison reading the event's own `amount` against one
    /// compiled threshold, and a branch that routes the answer to one of the two recording actions. Pins, layouts
    /// and constants all come from the kernel's own export, so the plan follows the registered contracts instead
    /// of a fixture that could drift.</summary>
    private string Build(double amount)
    {
        var manifest = RuntimeJson.Parse(Kernel.ExportManifest());
        var registry = manifest.GetProperty("registry");
        JsonElement Row(string list, string id) => registry.GetProperty(list).EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == id);
        var ids = new[] { TriggerBinding, ActionBinding, CompareBinding, BranchBinding }
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var pins = ids.Select(id =>
        {
            var binding = Row("bindings", id);
            var capabilityId = binding.GetProperty("capabilityId").GetString()!;
            return (object)new
            {
                bindingId = id, capabilityId,
                capabilityVersion = Row("capabilities", capabilityId).GetProperty("version").GetString()!,
                providerId = binding.GetProperty("providerId").GetString()!,
                providerVersion = Row("providers", binding.GetProperty("providerId").GetString()!).GetProperty("version").GetString()!,
                handler = binding.GetProperty("handler").GetString()!
            };
        }).ToArray();
        var permissions = manifest.GetProperty("bindingSupport").EnumerateArray()
            .Where(support => ids.Contains(support.GetProperty("bindingId").GetString()!))
            .SelectMany(support => support.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var action = Kernel.ResolveGraphContract(ActionCapability, "1.0.0", RuntimeJson.EmptyObject);
        var trigger = Kernel.ResolveGraphContract(TriggerCapability, "1.0.0", RuntimeJson.EmptyObject);
        var branch = Kernel.ResolveGraphContract("forge.control.flow.branch", "1.0.0", RuntimeJson.EmptyObject);
        // The comparison's one structural parameter: `value_type` = number is member 0 of the row's inline list.
        // `operator` is an input port, not a parameter, so it travels in the step's own inputs. A case that
        // changes the member list has to change this plan, which is what keeps the member order under test.
        var compare = Kernel.ResolveGraphContract("forge.condition.predicate.compare", "1.0.0",
            RuntimeJson.From(new { value_type = 0 }));
        object Layout(JsonElement contract, object[] constants) => new
        {
            inputs = LayoutFrame.Invoke(null, new object[] { contract, "inputs" })!,
            outputs = LayoutFrame.Invoke(null, new object[] { contract, "outputs" })!,
            constants, promoted = Array.Empty<int>()
        };
        int Port(JsonElement contract, string side, string id) => contract.GetProperty(side).EnumerateArray()
            .Select((port, index) => (port, index)).Single(x => x.port.GetProperty("id").GetString() == id).index;
        return RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId = PlanId, resource = new { id = PlanId, revision = "1" },
            runtime = Kernel.Identity, domain = "logic", authority = "host", failurePolicy = "stop-entrypoint",
            permissions, dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
            bindings = pins, attachments = new object[] { new { kind = "level", reference = LevelReference } },
            entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(ids, TriggerBinding),
                layout = Layout(trigger, Array.Empty<object>()), start = 1, steps = new object[]
                {
                    new { nodeId = "S0_compare", nodeKind = "query", binding = Array.IndexOf(ids, CompareBinding),
                        layout = Layout(compare, new object[] { 0 }),
                        inputs = new object[]
                        {
                            // Both operands are compiled numbers: the parameterized ports carry values, not world
                            // references, which is exactly what a `query` row with a structural value type has to
                            // accept. `operator` is a port of its own, and its wire value is the compiled member
                            // index — `gte` is member 5 of the `compare_operator` set.
                            new { slot = Port(compare, "inputs", "left"), value = amount },
                            new { slot = Port(compare, "inputs", "right"), value = 5.5d },
                            new { slot = Port(compare, "inputs", "operator"), value = 5 },
                            new { slot = Port(compare, "inputs", "tolerance"), value = 0.5d }
                        },
                        successors = Array.Empty<int?>() },
                    new { nodeId = "S1_branch", nodeKind = "control", binding = Array.IndexOf(ids, BranchBinding),
                        layout = Layout(branch, Array.Empty<object>()),
                        inputs = new object[] { new { slot = Port(branch, "inputs", "condition"), fromStepSlot = new { step = 0, port = 0 } } },
                        successors = new int?[] { 2, 3 } },
                    new { nodeId = "S2_then", nodeKind = "action", binding = Array.IndexOf(ids, ActionBinding),
                        layout = Layout(action, Array.Empty<object>()),
                        // The recipient port is required, so the fixture's own trigger carries the candidate set
                        // every action of this plan is addressed to; the branch decides which one runs.
                        inputs = new object[] { new { slot = Port(action, "inputs", "targets"), fromEventSlot = Port(trigger, "outputs", "targets") } },
                        successors = new int?[] { null } },
                    new { nodeId = "S3_otherwise", nodeKind = "action", binding = Array.IndexOf(ids, ActionBinding),
                        layout = Layout(action, Array.Empty<object>()),
                        inputs = new object[] { new { slot = Port(action, "inputs", "targets"), fromEventSlot = Port(trigger, "outputs", "targets") } },
                        successors = new int?[] { null } }
                } } }
        }).GetRawText();
    }

    public void Dispose()
    {
        fixture.Dispose();
        mounts.Dispose();
        if (Kernel.StartupState != RuntimeStartupState.Stopped) Kernel.StopRuntime();
    }
}









