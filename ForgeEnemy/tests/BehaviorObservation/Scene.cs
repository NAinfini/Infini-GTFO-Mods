using System.Reflection;
using System.Text.Json;
using Agents;
using Enemies;
using ForgeEnemy.Native;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;

internal sealed class Scene : IDisposable
{
    private static readonly MethodInfo LayoutFrame = typeof(RuntimeKernel).Assembly
        .GetType("ForgeRuntime.Framework.RuntimeGraphContracts", true)!
        .GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic)!;

    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly EnemyModule Module;
    internal readonly EnemyAgent Enemy;
    internal readonly EntityReference Ref;
    internal readonly List<string> Reported = new();
    internal bool Allowed = true;
    private bool _playerInstancesClaimed;
    private long _tick;

    /// <summary>The kernel freezes module registration and plan loading at startup, so a scene stays in its
    /// registering state until the test has declared its subscriptions and only then calls <see cref="Start"/>.</summary>
    internal Scene()
    {
        Kernel.BeginWorld(1);
        Kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        Kernel.RegisterModule(LevelMount(), RuntimeLogLevel.Off);
        Module = new(Kernel, RuntimeLogLevel.Off, () => Allowed, Reported.Add);
        Enemy = NewEnemy(); Ref = Module.TrackSpawn(Enemy);
    }

    /// <summary>The one level identity this scene's plans are mounted on. No mount kind belongs to the kernel
    /// any more, so the scene stands the owner up: a plan whose kind nothing owns is refused at load.</summary>
    internal const string LevelReference = "31:A:0";
    private static RuntimeModule LevelMount() => new(RuntimeKernel.ApiVersion, """
    {"providers":[{"id":"test.behavior.mounts","kind":"extension","version":"1.0.0","dependencies":[]}],
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

    internal void Start() => Kernel.StartRuntime(() => { });

    internal static EnemyAgent NewEnemy(ushort id = 7, long pointer = 10)
    {
        var actor = new EnemyAgent { GlobalID = id, Pointer = new(pointer) };
        actor.Damage = new() { Owner = actor, Pointer = new(pointer + 100) };
        actor.AI = new() { m_enemyAgent = actor, Pointer = new(pointer + 1) };
        actor.AI.m_behaviour = new() { m_ai = actor.AI, Pointer = new(pointer + 2) };
        actor.AI.m_detection = new() { m_ai = actor.AI, Pointer = new(pointer + 3) };
        actor.AI.m_locomotion = new() { m_agent = actor, CurrentStateEnum = ES_StateEnum.PathMove };
        actor.Locomotion = actor.AI.m_locomotion;
        actor.Abilities = new() { m_agent = actor, ActiveAbility = AgentAbility.Melee, CanTriggerAbilities = true };
        actor.m_hasValidTarget = true;
        return actor;
    }

    /// <summary>A second provider's instance resolver, standing in for the other kind of agent an AI target can
    /// be. With <paramref name="claims"/> false no registered kind claims that target. The kind is registered
    /// once per scene: a scene that subscribed two rows already owns it, and a second provider claiming the same
    /// kind is refused by the kernel rather than silently replaced.</summary>
    internal void OwnInstances(bool claims)
    {
        if (!claims || _playerInstancesClaimed) return;
        _playerInstancesClaimed = true;
        Kernel.RegisterModule(new RuntimeModule(
            RuntimeKernel.ApiVersion, """
            {"providers":[{"id":"test.enemy.player","kind":"extension","version":"1.0.0","dependencies":[]}],
            "capabilities":[],"bindings":[]}
            """, new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>> { ["gtfo.player"] = _ => true })
        {
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
                { ["gtfo.player"] = _ => Ref }
        }, RuntimeLogLevel.Off);
    }

    /// <summary>Subscribes one behaviour trigger with the smallest legal plan: a level attachment and one branch
    /// control step as the entry's start. The pin table is exactly the closure the runtime demands (the trigger
    /// plus that step, ordinal sorted), and the kernel's own event validation and admission decide whether a fact
    /// was published.</summary>
    internal void Subscribe(string binding, bool targetClaimed = true)
    {
        var root = RuntimeJson.Parse(Kernel.ExportManifest());
        var manifest = root.GetProperty("registry");
        JsonElement Row(string list, string id) => manifest.GetProperty(list).EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == id);
        var row = Row("bindings", binding); var capabilityId = row.GetProperty("capabilityId").GetString()!;
        var capability = Row("capabilities", capabilityId);
        var version = capability.GetProperty("version").GetString()!;
        var contract = Kernel.ResolveGraphContract(capabilityId, version, RuntimeJson.EmptyObject);
        var branchRow = Row("bindings", BranchBinding);
        var branchCapability = branchRow.GetProperty("capabilityId").GetString()!;
        var branchContract = Kernel.ResolveGraphContract(branchCapability, "1.0.0", RuntimeJson.EmptyObject);
        var pins = new[] { binding, BranchBinding }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var support = root.GetProperty("bindingSupport").EnumerateArray().ToDictionary(
            r => r.GetProperty("bindingId").GetString()!,
            r => r.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!).ToArray());
        object Pin(string id)
        {
            var pin = Row("bindings", id); var capId = pin.GetProperty("capabilityId").GetString()!;
            var providerId = pin.GetProperty("providerId").GetString()!;
            return new { bindingId = id, capabilityId = capId,
                capabilityVersion = Row("capabilities", capId).GetProperty("version").GetString()!,
                providerId, providerVersion = Row("providers", providerId).GetProperty("version").GetString()!,
                handler = pin.GetProperty("handler").GetString()! };
        }
        object Frame(JsonElement source) => new
        {
            inputs = LayoutFrame.Invoke(null, new object[] { source, "inputs" })!,
            outputs = LayoutFrame.Invoke(null, new object[] { source, "outputs" })!,
            constants = Array.Empty<object>(), promoted = Array.Empty<int>()
        };
        var json = RuntimeJson.From(new
        {
            schemaVersion = 1, kind = "forge-runtime-plan", planId = "test.behavior." + binding,
            resource = new { id = "test.behavior", revision = "1" }, runtime = Kernel.Identity,
            domain = "enemy", authority = "host", failurePolicy = "stop-entrypoint",
            permissions = pins.SelectMany(p => support[p]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            dependencies = Array.Empty<string>(),
            limits = new { Kernel.Limits.MaxEventsPerTick, Kernel.Limits.MaxCommandsPerTick, Kernel.Limits.MaxQueuedEvents, Kernel.Limits.MaxCausalDepth },
            bindings = pins.Select(Pin).ToArray(),
            attachments = new[] { new { kind = "level", reference = LevelReference } },
            entrypoints = new[] { new { nodeId = "Entry", binding = Array.IndexOf(pins, binding), start = 0, layout = Frame(contract),
                steps = new object[] { new { nodeId = "Step0", nodeKind = "control", binding = Array.IndexOf(pins, BranchBinding),
                    layout = Frame(branchContract),
                    inputs = new object[] { new { slot = PortIndex(branchContract, "condition"), value = true } },
                    successors = new int?[] { null, null } } } } }
        }).GetRawText();
        var outcome = Kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/" + binding + ".plan.json", json) })[0];
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
        OwnInstances(targetClaimed);
    }

    /// <summary>The wire slot of one declared input port: a plan names an input by its position in the resolved
    /// contract's own input list, never by name.</summary>
    private static int PortIndex(JsonElement contract, string port)
    {
        int index = 0;
        foreach (var candidate in contract.GetProperty("inputs").EnumerateArray())
        {
            if (candidate.GetProperty("id").GetString() == port) return index;
            index++;
        }
        throw new InvalidOperationException("Contract declares no input port " + port + ".");
    }

    internal const string BranchBinding = "forge.contract.control.binding.branch";

    /// <summary>One native frame: the pump reads once and the dispatch it queued is drained by the tick after.</summary>
    internal TickResult Frame()
    {
        var result = Tick();
        Module.ObserveEnemyBehavior(Enemy.AI);
        return Tick();
    }

    internal TickResult Tick() => Kernel.Advance(++_tick, true);

    /// <summary>Facts the kernel admitted for a subscribed plan and dispatched in this tick. A rejected or
    /// cancelled event has a different receipt status, so a refused publish never counts as a fact.</summary>
    internal static int Dispatched(TickResult result) => result.Events.Count(x => x.Status == "processed");

    internal EnemyBehaviorObserver.Snapshot? Read() => Module.ObserveBehavior(Ref);

    public void Dispose()
    {
        Module.Dispose();
        if (Kernel.StartupState != RuntimeStartupState.Stopped) Kernel.StopRuntime();
    }
}
