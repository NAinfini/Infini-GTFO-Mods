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

    internal readonly RuntimeKernel Kernel = new(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
    internal readonly EnemyModule Module;
    internal readonly NavMarkerLayer Layer = new();
    internal readonly List<string> Messages = new();
    internal bool Allowed = true;

    /// <summary>Whether one node binding has a subscriber, which is the gate every observation publishes
    /// through: a session with no plan loaded has none, so a case proves the closed gate by asking this.</summary>
    internal bool Subscribed(string binding) => Kernel.HasSubscribers(binding);

    internal Scene(bool start = true)
    {
        // The marker layer is native state, so every case starts with an empty one.
        GuiManager.NavMarkerLayer = Layer;
        SNetwork.SNet.IsMaster = true;
        Kernel.BeginWorld(1);
        // The trigger contract's provider registers before any domain package, which is what lets the enemy
        // module bind the generic entity-spawn row it does not own.
        Kernel.RegisterModule(TriggerModule(), RuntimeLogLevel.Off);
        Module = new EnemyModule(Kernel, () => Allowed, Messages.Add);
        // The value rows read this module's own tracked lives, so the resolver is attached once the
        // registration that declares them is live — the same order the provider's own constructor uses. The
        // agent lookup is the scene's own here: the accepted path of the `target` row has to be assertable, and
        // the game's own id table is a double in this suite.
        Module.AttachNodeFamily(Kernel, AgentOf);
        // The player kind's own instances, registered before the world starts: the kernel freezes registration
        // at startup, so a case cannot stand a provider up afterwards.
        Kernel.RegisterModule(PlayerModule(), RuntimeLogLevel.Off);
        if (start) Kernel.StartRuntime(static () => { });
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
            AI = new EnemyAI { m_behaviour = new EnemyBehaviour() }
        };
        enemy.Damage = new Dam_EnemyDamageBase { Owner = enemy, Pointer = new(pointer + 100) };
        return enemy;
    }

    internal EntityReference Track(EnemyAgent enemy) => Module.Track(enemy);

    internal void Retire(EntityReference reference) => Module.Retire(reference);

    /// <summary>Stands the world forward, which is what retires every reference minted in the old one.</summary>
    internal void NextWorld(long epoch) => Kernel.BeginWorld(epoch);

    /// <summary>One command dispatch over the given row, with the inputs a plan would hand it.</summary>
    internal CommandResult Dispatch(string capabilityId, object inputs, object? parameters = null, bool isHost = true)
    {
        var origin = new RuntimeEvent("test.node:" + Kernel.WorldEpoch, "test.binding." + capabilityId,
            Kernel.WorldEpoch, Math.Max(0, Kernel.CurrentTick), "gtfo.world:" + Kernel.WorldEpoch, RuntimeJson.EmptyObject);
        var context = (CommandContext)CommandConstructor.Invoke(new object?[]
        {
            origin, Math.Max(0, Kernel.CurrentTick), "test.command", "test.plan", "test.resource", "1", "Step",
            parameters == null ? RuntimeJson.EmptyObject : RuntimeJson.From(parameters), RuntimeJson.From(inputs), isHost
        });
        return capabilityId switch
        {
            EnemyNodeEffectContract.KillCapability => Module.Kill(context),
            EnemyNodeEffectContract.MarkCapability => Module.Mark(context),
            EnemyNodeEffectContract.TargetCapability => Module.Target(context),
            _ => throw new InvalidOperationException("Not a node action: " + capabilityId)
        };
    }

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
            EnemyNodeValueContract.HealthCapability => EnemyNodeValueContract.HealthHandler,
            EnemyNodeValueContract.AliveCapability => EnemyNodeValueContract.AliveHandler,
            EnemyNodeValueContract.TypeCapability => EnemyNodeValueContract.TypeHandler,
            EnemyNodeValueContract.SleepingCapability => EnemyNodeValueContract.SleepingHandler,
            EnemyNodeValueContract.WhereCapability => EnemyNodeValueContract.WhereHandler,
            EnemyNodeValueContract.TaggedCapability => EnemyNodeValueContract.TaggedHandler,
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
