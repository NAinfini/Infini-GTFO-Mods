using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;
using SNetwork;

namespace ForgeMap.Tests.AgentModifierFacts;

/// <summary>One player life with the three pieces a case reads it through: the game double, the account key the
/// fixture spawned it with, and the `gtfo.player:<n>` reference the identity module recorded.</summary>
internal sealed record PlayerFixture(SNet_Player Player, PlayerAgent Agent, EntityReference Reference);

/// <summary>One case's world: a kernel holding the two registrations the real startup performs — the combat
/// contract module that owns `forge.action.combat.attribute_apply`/`attribute_remove`, and the Map provider that
/// binds them to this package's handlers — plus the player identity and the attribute-modifier adapter on that
/// registration. There is no loader, no session and no hook: the handlers are driven directly with a real
/// `CommandContext`, so a shape that does not resolve against its own capability fails here exactly as it would
/// at startup.
///
/// The registration is built the way the session builds it: the claim rows and the capability rows come from
/// `AgentModifierContract`, the handler table holds the adapter's static entry points, and the adapter itself is
/// created after `RegisterModule` returned, because it mints its handles on the registration handle.</summary>
internal sealed class ModifierWorld : IDisposable
{
    private static readonly ConstructorInfo ContextConstructor = typeof(CommandContext)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[]
        {
            typeof(RuntimeEvent), typeof(long), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(JsonElement), typeof(JsonElement), typeof(bool)
        }, null) ?? throw new InvalidOperationException("CommandContext's own constructor was not found.");

    private static long _world;
    private readonly RuntimeModuleHandle _contract;
    private readonly RuntimeModuleHandle _registration;
    internal RuntimeKernel Kernel { get; }
    internal PlayerIdentityModule Identity { get; private set; } = null!;
    internal AgentModifierAdapter Adapter { get; private set; } = null!;
    internal List<string> Reports { get; } = new();
    /// <summary>When clear, the identity half refuses every read and every commit, which is the state a faulted
    /// or non-authoritative session is in.</summary>
    internal bool CanObserve { get; set; } = true;

    internal ModifierWorld()
    {
        PlayerManager.Reset();
        PlayerAgent.ResetStatics();
        AgentModifierManager.Reset();
        SNet.IsMaster = true;
        Kernel = new RuntimeKernel(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
        // A world of its own per case: a reference carries the epoch it was recorded in, and a case that reused an
        // earlier epoch would keep answering for an earlier world's lives.
        Kernel.BeginWorld(++_world);
        _contract = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, ContractRegistry(),
            new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>()), RuntimeLogLevel.Off);
        _registration = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, MapRegistry(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
            {
                [AgentModifierContract.ApplyHandlerName] = AgentModifierAdapter.ApplyHandler,
                [AgentModifierContract.RemoveHandlerName] = AgentModifierAdapter.RemoveHandler
            }, AgentModifierContract.Support(),
            new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = reference => PlayerIdentityModule.Current is { } half && half.IsCurrent(reference)
            })
        {
            Shapes = AgentModifierContract.Shapes(),
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = instance => PlayerIdentityModule.Current?.ResolveInstance(instance)
            },
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = reference => PlayerIdentityModule.Current?.Observe(reference)
            },
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = () => PlayerIdentityModule.Current is { } half && half.IsRegistered
                    ? half.CurrentPlayers() : throw new RuntimeContractException("player-module-unavailable", "no identity")
            }
        }, RuntimeLogLevel.Off);
        Identity = new PlayerIdentityModule(_registration, Kernel, () => CanObserve, Reports.Add, Reports.Add);
        Adapter = new AgentModifierAdapter(_registration, Reports.Add);
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, true);
    }

    /// <summary>The contract module's own declaration: the canonical combat rows the framework already ships,
    /// plus the two capability rows this slice publishes for the integration to splice into that same array. A
    /// contract module only declares shapes and owns both ids, so the rows belong in this one declaration — which
    /// is why the fixture composes them from the canonical module's own text instead of restating heal and damage.
    /// The rows are the exact JSON text `AgentModifierContract` publishes for the integration.</summary>
    private static string ContractRegistry()
    {
        var canonical = RuntimeJson.Parse(CombatContracts.Module().RegistryJson);
        var capabilities = canonical.GetProperty("capabilities").EnumerateArray()
            .Append(AgentModifierContract.ApplyCapability)
            .Append(AgentModifierContract.RemoveCapability)
            .ToArray();
        return RuntimeJson.From(new
        {
            providers = canonical.GetProperty("providers").EnumerateArray().ToArray(),
            capabilities,
            bindings = Array.Empty<object>()
        }).GetRawText();
    }

    /// <summary>The Map provider's declaration: this provider's id, the two binding rows the native half answers
    /// and no capability of its own — the capabilities are the combat contract's.</summary>
    private static string MapRegistry() => RuntimeJson.From(new
    {
        providers = new[]
        {
            new { id = ModuleDefinition.ProviderId, kind = "native", version = "0.1.0", dependencies = Array.Empty<string>() }
        },
        capabilities = Array.Empty<object>(),
        bindings = AgentModifierContract.Bindings()
    }).GetRawText();

    /// <summary>One command context over a row of this slice, as a dispatch would hand it to the handler: the
    /// constructor is assembly-internal, so the fixture builds the same object through it. The parameters arrive
    /// as member names — what the kernel hands over after it resolved the compiled enum index — and the inputs are
    /// the frame a plan's wiring produces, with an input a case does not name simply absent.</summary>
    internal CommandContext Context(string capabilityId, object? inputs, object? parameters, bool isHost = true)
    {
        var origin = new RuntimeEvent("test.event", "test.binding", Kernel.WorldEpoch, Kernel.CurrentTick, "test.scope", RuntimeJson.EmptyObject);
        return (CommandContext)ContextConstructor.Invoke(new object?[]
        {
            origin, Kernel.CurrentTick, "test.command", "test.plan", "author.resource", "revision-1", "A_action",
            parameters == null ? RuntimeJson.EmptyObject : RuntimeJson.From(parameters),
            inputs == null ? RuntimeJson.EmptyObject : RuntimeJson.From(inputs),
            isHost
        })!;
    }

    /// <summary>One apply request: the operation is the row's own structural parameter and the attribute is the
    /// member name the kernel resolves the enum port to.</summary>
    internal CommandResult Apply(string operation, object? inputs)
        => Adapter.Apply(Context(AgentModifierContract.ApplyCapabilityId, inputs, new { operation }));

    /// <summary>One remove request over the handles a case collected.</summary>
    internal CommandResult Remove(object? inputs)
        => Adapter.Remove(Context(AgentModifierContract.RemoveCapabilityId, inputs, null));

    internal TickResult Advance(long tick) => Kernel.Advance(tick, true);

    /// <summary>One spawned player with a link and a readable damage base, read back through the identity module's
    /// own reconcile, which is the only path that allocates a life.</summary>
    internal PlayerFixture Spawn(ulong lookup)
    {
        int slot = PlayerManager.PlayerAgentsInLevel.Count;
        var player = new SNet_Player { Lookup = lookup, SlotIndex = slot };
        var agent = new PlayerAgent { Owner = player, PlayerSlotIndex = slot };
        agent.Locomotion = new PlayerLocomotion { m_currentStateEnum = PlayerLocomotion.PLOC_State.Stand };
        agent.Damage = new Dam_PlayerDamageBase { Owner = agent, IsSetup = true, Health = 50f, HealthMax = 100f };
        player.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
        PlayerManager.PlayerAgentsInLevel.Add(agent);
        Identity.Reconcile();
        return new PlayerFixture(player, agent, Kernel.ResolveEntityInstance(PlayerIdentityModule.EntityKind, player)!);
    }

    /// <summary>Removes a life the way the game's despawn does, then reconciles: the reference it was recorded
    /// under stops resolving.</summary>
    internal void Despawn(PlayerFixture fixture)
    {
        PlayerManager.PlayerAgentsInLevel.Remove(fixture.Agent);
        fixture.Player.PlayerAgent = null;
        Identity.Reconcile();
    }

    public void Dispose()
    {
        Adapter.Dispose();
        Identity.Dispose();
        _registration.Dispose();
        _contract.Dispose();
    }
}
