using System.Reflection;
using System.Text.Json;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>The suite's world: one started kernel with the node family's own rows registered — the capability
/// rows, the binding rows, the support rows, the handler shapes and the evaluator table, all read from the
/// production contracts rather than restated — a tracked enemy whose receiver, model and course node are whatever
/// the case needs, and the module-side observation the facts are published from.
///
/// The registration is the production one minus the rows `EnemyModule.Registry()` owns for the rest of the
/// package: `EnemyModule`'s own core belongs to a dozen sibling slices at once and is not compiled here, so the
/// shim registers the node family's declaration while this scene owns the contexts a case drives a row with.
///
/// The command and evaluation contexts are built by reflection only because their constructors are internal to
/// the Framework assembly; every value handed to them is built with the SDK's own public helpers.</summary>
internal sealed class Scene : IDisposable
{
    private static readonly ConstructorInfo CommandConstructor = typeof(CommandContext)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 10);

    private static readonly ConstructorInfo EvaluationConstructor = typeof(EvaluationContext)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 6);

    private static readonly ConstructorInfo SessionConstructor = typeof(RuntimeQuerySession)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 3);

    /// <summary>The kernel hands a step's own effect handle to its handler, and its setter is internal to the
    /// Framework assembly; a fixture has no plan, so it casts the same handle the kernel would and writes it the
    /// same way.</summary>
    private static readonly PropertyInfo EffectHandleProperty = typeof(CommandContext)
        .GetProperty("EffectHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("CommandContext's effect handle was not found.");

    /// <summary>The kernel's own runtime-effect context, built by the fixture for the same reason: its
    /// constructor is internal. Only the handle and the reason are read back by the callback under test.</summary>
    private static readonly ConstructorInfo EffectContextConstructor = typeof(RuntimeEffectContext)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(c => c.GetParameters().Length == 11);

    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly EnemyModule Module;
    internal readonly NavMarkerLayer Layer = new();
    internal readonly List<string> Messages = new();
    internal bool Allowed = true;

    /// <summary>Whether one node binding has a subscriber, which is the gate every observation publishes
    /// through: a session with no plan loaded has none, so a case proves the closed gate by asking this.</summary>
    internal bool Subscribed(string binding) => Kernel.HasSubscribers(binding);

    internal Scene(bool start = true)
    {
        // The marker layer is native state, so every case starts with an empty one; the effect-volume manager is
        // the same, and a volume that outlived its own case would be read by the next one's count.
        GuiManager.NavMarkerLayer = Layer;
        EffectVolumeManager.Reset();
        FogSphereAllocator.Allocations = 0;
        FogSphereAllocator.LastAllocation = null;
        SNetwork.SNet.IsMaster = true;
        Kernel.BeginWorld(1);
        // The trigger contract's provider registers before any domain package, which is what lets the enemy
        // module bind the generic entity-spawn row it does not own.
        // The control contract's provider registers beside the trigger one: the runtime's own control rows are
        // what a subscription plan branches on, and the host registers them before any domain package. The mount
        // owner registers with them, because a plan whose attachment kind nothing owns is refused at load.
        Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerModule(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(LevelMount(), RuntimeLogLevel.Off);
        Module = new EnemyModule(Kernel, () => Allowed, Messages.Add);
        // The value rows read this module's own tracked lives, so the resolver is attached once the
        // registration that declares them is live — the same order the provider's own constructor uses. The
        // agent lookup is the scene's own here: the accepted path of the `target` row has to be assertable, and
        // the game's own id table is a double in this suite.
        Module.AttachNodeFamily(Kernel, AgentOf);
        // The player kind's own instances, registered before the world starts: the kernel freezes registration
        // at startup, so a case cannot stand a provider up afterwards.
        Kernel.RegisterModule(PlayerModule(), RuntimeLogLevel.Off);
        if (start) Start();
    }

    /// <summary>Every player instance this suite's world knows: the target row resolves the propagated agent
    /// through the kind's own registered provider, exactly as it does in production, and a reference no entry
    /// answers for is the unresolvable case.</summary>
    internal readonly Dictionary<ushort, (EntityReference Reference, Agents.Agent Agent)> Players = new();

    private const ushort PlayerGlobalId = 3;

    private RuntimeModule PlayerModule()
    {
        var agent = new Agents.Agent();
        var reference = new EntityReference("gtfo.player:" + PlayerGlobalId, Kernel.WorldEpoch, 1);
        Players[PlayerGlobalId] = (reference, agent);
        Agents.AgentManager.Agents[PlayerGlobalId] = agent;
        return new RuntimeModule(RuntimeKernel.ApiVersion, PlayerDeclaration,
            new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
                { ["gtfo.player"] = candidate => candidate == reference },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
                { ["gtfo.player"] = instance => ReferenceEquals(instance, agent) ? reference : null }
        };
    }

    /// <summary>The one player reference the world knows, and the agent behind it.</summary>
    internal EntityReference PlayerReference(out Agents.Agent agent)
    {
        var entry = Players[PlayerGlobalId];
        agent = entry.Agent;
        return entry.Reference;
    }

    /// <summary>The agent behind a reference, as the provider's own id lookup answers it: the reference's own
    /// global id into the table this scene registered, and nothing else.</summary>
    private dynamic? AgentOf(EntityReference reference)
    {
        foreach (var entry in Players.Values)
            if (entry.Reference == reference) return entry.Agent;
        return null;
    }

    /// <summary>One player instance, registered for `gtfo.player` the way the Map provider registers its own
    /// kind. This is the registration the scene itself performs for every case, so a case asks for the one
    /// player the world has rather than standing a provider up mid-world.</summary>
    private const string PlayerDeclaration = """
    {
      "providers": [ { "id": "forge.test.player", "kind": "native", "version": "1.0.0", "dependencies": [] } ],
      "capabilities": [],
      "bindings": []
    }
    """;

    /// <summary>The trigger contract's own module, registered the way the host registers it: it owns
    /// `forge.contract.trigger` and with it the generic spawn row this provider binds without declaring. The row
    /// is the kernel's, so the suite registers the kernel's module instead of restating the row's text — a row
    /// this file copied would be a second declaration of a capability the runtime refuses to see twice.</summary>
    private static RuntimeModule TriggerModule() => TriggerContracts.Module();

    internal static EnemyAgent NewEnemy(ushort id = 7, long pointer = 10)
    {
        var enemy = new EnemyAgent
        {
            GlobalID = id, Pointer = new(pointer),
            Model = new UnityEngine.GameObject { name = "enemy" },
            AI = new EnemyAI { m_behaviour = new EnemyBehaviour() },
            // Every live enemy in this build carries its replication half, so the removal row's own path is the
            // one a case drives unless it deliberately takes the replicator away.
            Sync = new EnemySync { Replicator = new SNetwork.Replicator() }
        };
        enemy.Damage = new Dam_EnemyDamageBase { Owner = enemy, Pointer = new(pointer + 100) };
        return enemy;
    }

    internal EntityReference Track(EnemyAgent enemy) => Module.Track(enemy);

    /// <summary>One live group with the members the group row answers from, already at the state, type and
    /// frustration a case wants to read back.</summary>
    internal static EnemyGroup NewGroup(EGS state = EGS.PatrolMove, EnemyGroupType type = EnemyGroupType.Patrolling,
        float frustration = 0f, long pointer = 900)
        => new()
        {
            Pointer = new(pointer),
            Data = new pEnemyGroupData { currentState = state },
            GroupType = type,
            PatrolFrustration = frustration
        };

    internal void Retire(EntityReference reference) => Module.Retire(reference);

    /// <summary>Stands the world forward, which is what retires every reference minted in the old one.</summary>
    internal void NextWorld(long epoch) => Kernel.BeginWorld(epoch);

    /// <summary>The one level identity this scene's subscription plans are mounted on. No mount kind belongs to
    /// the kernel, so the scene stands the owner up: a plan whose kind nothing owns is refused at load.</summary>
    internal const string LevelReference = "31:A:0";

    private static RuntimeModule LevelMount() => new(RuntimeKernel.ApiVersion, """
    {"providers":[{"id":"test.node.mounts","kind":"extension","version":"1.0.0","dependencies":[]}],
    "capabilities":[],"bindings":[]}
    """, new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
    {
        AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
        {
            // A level names no event subject, so the kind is judged from the mount target alone.
            ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                category == null && reference == LevelReference)
        }
    };

    /// <summary>Loads the smallest plan that makes one binding observable: a branch on the row's own binding as
    /// the entry, whose condition is a constant. A fact is published only while a plan subscribes to its binding,
    /// so an observation case needs a real subscriber — and the subscriber is a loaded plan rather than a flag,
    /// because that is the same gate production passes. The row's own capability, version, handler and required
    /// permissions are read back from the module's exported manifest, so a row this suite cannot name is a
    /// failure here rather than a subscription to nothing.</summary>
    internal void Subscribe(string binding, bool condition = true)
    {
        var root = RuntimeJson.Parse(Kernel.ExportManifest());
        var manifest = root.GetProperty("registry");
        JsonElement Row(string list, string id) => manifest.GetProperty(list).EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == id);
        var row = Row("bindings", binding);
        var capabilityId = row.GetProperty("capabilityId").GetString()!;
        var contract = Kernel.ResolveGraphContract(capabilityId,
            Row("capabilities", capabilityId).GetProperty("version").GetString()!, RuntimeJson.EmptyObject);
        var branchRow = Row("bindings", BranchBinding);
        var branchId = branchRow.GetProperty("capabilityId").GetString()!;
        var branchContract = Kernel.ResolveGraphContract(branchId, "1.0.0", RuntimeJson.EmptyObject);
        var support = root.GetProperty("bindingSupport").EnumerateArray().ToDictionary(
            item => item.GetProperty("bindingId").GetString()!,
            item => item.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!).ToArray());
        var pins = new[] { binding, BranchBinding }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        object Pin(string id)
        {
            var pin = Row("bindings", id);
            var pinCapability = pin.GetProperty("capabilityId").GetString()!;
            var providerId = pin.GetProperty("providerId").GetString()!;
            return new
            {
                bindingId = id, capabilityId = pinCapability,
                capabilityVersion = Row("capabilities", pinCapability).GetProperty("version").GetString()!,
                providerId, providerVersion = Row("providers", providerId).GetProperty("version").GetString()!,
                handler = pin.GetProperty("handler").GetString()!
            };
        }
        object Frame(JsonElement source) => new
        {
            inputs = LayoutFrame.Invoke(null, new object[] { source, "inputs" })!,
            outputs = LayoutFrame.Invoke(null, new object[] { source, "outputs" })!,
            constants = Array.Empty<object>(), promoted = Array.Empty<int>()
        };
        var json = RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId = "test.node." + binding,
            resource = new { id = "test.node", revision = "1" }, runtime = Kernel.Identity,
            domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
            permissions = pins.SelectMany(pin => support[pin]).Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            dependencies = Array.Empty<string>(),
            limits = new
            {
                Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick,
                Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth
            },
            bindings = pins.Select(Pin).ToArray(),
            attachments = new[] { new { kind = "level", reference = LevelReference } },
            entrypoints = new[]
            {
                new
                {
                    nodeId = "Entry", binding = Array.IndexOf(pins, binding), start = 0, layout = Frame(contract),
                    steps = new object[]
                    {
                        new
                        {
                            nodeId = "Step0", nodeKind = "control",
                            binding = Array.IndexOf(pins, BranchBinding), layout = Frame(branchContract),
                            inputs = new object[] { new { slot = 1, value = condition } },
                            successors = new int?[] { null, null }
                        }
                    }
                }
            }
        }).GetRawText();
        var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/" + binding + ".plan.json", json) })[0];
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
    }

    /// <summary>The one control binding the subscription plans branch on; its contract belongs to the runtime's
    /// own control provider, which is registered before any domain package.</summary>
    private const string BranchBinding = "forge.contract.control.binding.branch";

    private static readonly MethodInfo LayoutFrame = typeof(RuntimeKernel).Assembly
        .GetType("ForgeRuntime.Framework.RuntimeGraphContracts", true)!
        .GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>One tick of the kernel's own clock, which is where a publication turns into a dispatch.</summary>
    internal TickResult Tick() => Kernel.Advance(Kernel.CurrentTick + 1, true);

    /// <summary>Starts the runtime. A scene that subscribed a row before starting is the production order: plans
    /// load while the runtime is still arming, and the first tick is what they observe.</summary>
    internal void Start() => Kernel.StartRuntime(static () => { });

    /// <summary>One command dispatch over the given row, with the inputs a plan would hand it. A case whose row
    /// has its clock in the step's own `effect` block asks for the kernel's handle — the same value the kernel
    /// casts for a step — and the ending of that effect is driven through <see cref="EndEffect"/>.</summary>
    internal CommandResult Dispatch(string capabilityId, object inputs, object? parameters = null, bool isHost = true,
        bool namesEffect = false)
    {
        var origin = new RuntimeEvent("test.node:" + Kernel.WorldEpoch, "test.binding." + capabilityId,
            Kernel.WorldEpoch, Math.Max(0, Kernel.CurrentTick), "gtfo.world:" + Kernel.WorldEpoch, RuntimeJson.EmptyObject);
        var context = (CommandContext)CommandConstructor.Invoke(new object?[]
        {
            origin, Math.Max(0, Kernel.CurrentTick), "test.command", "test.plan", "test.resource", "1", "Step",
            parameters == null ? RuntimeJson.EmptyObject : RuntimeJson.From(parameters), RuntimeJson.From(inputs), isHost
        });
        LastHandle = null;
        if (namesEffect)
        {
            LastHandle = Module.MintHandle();
            EffectHandleProperty.SetValue(context, LastHandle.Value);
        }
        return capabilityId switch
        {
            EnemyNodeEffectContract.KillCapability => Module.Kill(context),
            EnemyNodeEffectContract.RemoveCapability => Module.Remove(context),
            EnemyNodeEffectContract.MarkCapability => Module.Mark(context),
            EnemyNodeEffectContract.TargetCapability => Module.Target(context),
            EnemyVolumeContract.VolumeCapability => Module.EffectVolume(context),
            _ => throw new InvalidOperationException("Not a node action: " + capabilityId)
        };
    }

    /// <summary>The kernel's effect handle the last dispatch carried, as a plan's step would have published it.</summary>
    internal JsonElement? LastHandle { get; private set; }

    /// <summary>One effect ending, driven through the production restore callback with a context the kernel builds:
    /// this is what the kernel calls when an effect ends by its duration, by a cancellation, by a released plan or
    /// by the world.</summary>
    internal void EndEffect(JsonElement handle, string reason)
        => Module.RestoreVolume((RuntimeEffectContext)EffectContextConstructor.Invoke(new object?[]
        {
            handle, reason, 1, Kernel.CurrentTick, Kernel.WorldEpoch, "test.plan", "Step",
            EnemyVolumeContract.VolumeBinding, EnemyVolumeContract.VolumeCapability, "test.command",
            Array.Empty<EntityReference>()
        })!);

    /// <summary>One evaluation of a value row, over the kernel's own query session: the row reads the world the
    /// way a real step does, so the snapshot, the zone and the budget are the production ones.</summary>
    internal JsonElement Evaluate(string capabilityId, object inputs)
    {
        var handler = HandlerFor(capabilityId);
        var session = (RuntimeQuerySession)SessionConstructor.Invoke(new object?[] { Kernel, "Step", true })!;
        var context = (EvaluationContext)EvaluationConstructor.Invoke(new object?[]
        {
            "Step", RuntimeJson.EmptyObject, RuntimeJson.From(inputs), session, null!, null!
        })!;
        return handler(context);
    }

    private static EvaluatorHandler HandlerFor(string capabilityId)
    {
        var evaluators = EnemyModule.NodeEvaluators();
        var handler = capabilityId switch
        {
            EnemyNodeValueContract.AliveCapability => EnemyNodeValueContract.AliveHandler,
            EnemyNodeValueContract.TypeCapability => EnemyNodeValueContract.TypeHandler,
            EnemyNodeValueContract.SleepingCapability => EnemyNodeValueContract.SleepingHandler,
            EnemyNodeValueContract.WhereCapability => EnemyNodeValueContract.WhereHandler,
            EnemyNodeValueContract.TaggedCapability => EnemyNodeValueContract.TaggedHandler,
            EnemyNodeValueContract.GroupCapability => EnemyNodeValueContract.GroupHandler,
            _ => throw new InvalidOperationException("Not a node value row: " + capabilityId)
        };
        return evaluators[handler];
    }

    /// <summary>The exception a case expects a row to refuse with, or null when it answered. A value row reports
    /// a missing or unreadable fact as a refusal with a code rather than as a substituted value, so the code is
    /// what a case asserts on.</summary>
    internal static string? Refusal(Func<JsonElement> answer)
    {
        try { answer(); return null; }
        catch (RuntimeContractException error) { return error.Code; }
    }

    public void Dispose()
    {
        Module.DetachNodeFamily();
        Module.ForgetInstance();
        Kernel.StopRuntime();
        GuiManager.NavMarkerLayer = null;
    }
}

internal static class T
{
    internal sealed record CheckRow(string Id, bool Passed, string Detail);
    internal static readonly List<CheckRow> Rows = new();
    internal static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
    internal static void Case(string id, Action test)
    {
        try { test(); Rows.Add(new(id, true, "passed")); }
        catch (Exception e) { Rows.Add(new(id, false, e.ToString())); Console.Error.WriteLine("FAIL " + id + ": " + e.Message); }
        finally { SNetwork.SNet.IsMaster = true; }
    }
    internal static JsonElement ResultRow(CommandResult result, int index)
        => result.Outputs.GetProperty("results").EnumerateArray().ElementAt(index);
    internal static string Code(CommandResult result, int index) => ResultRow(result, index).GetProperty("code").GetString()!;
    internal static string Status(CommandResult result, int index) => ResultRow(result, index).GetProperty("status").GetString()!;
    internal static string Committed(CommandResult result, int index) => ResultRow(result, index).GetProperty("committed").GetString()!;
}
