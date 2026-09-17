using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;
using SNetwork;

namespace ForgeMap.Tests.PlayerActionFacts;

/// <summary>One player life with the three pieces a case reads it through: the game double, the account key the
/// fixture spawned it with, and the `gtfo.player:<n>` reference the identity module recorded.</summary>
internal sealed record PlayerFixture(SNet_Player Player, PlayerAgent Agent, EntityReference Reference);

/// <summary>One case's world: a kernel whose one registration is the Map provider declaration this slice adds —
/// the catalog rows, the binding rows, the support rows, the handler shapes and the identity surface the action
/// layer resolves recipients through. There is no loader, no session and no hook here: the handlers are plain
/// static entry points, so a case drives them directly and reads what they did through the game doubles.
///
/// The registration is built the way `MapPluginSession.Definition()` builds the real one, so a handler shape that
/// does not resolve against its own declared capability fails here exactly as it would at startup.</summary>
internal sealed class PlayerActionWorld : IDisposable
{
    private static long _world;
    private readonly RuntimeModuleHandle _registration;
    internal RuntimeKernel Kernel { get; }
    internal PlayerIdentityModule Identity { get; private set; } = null!;
    internal List<string> Reports { get; } = new();
    /// <summary>When clear, the identity half refuses every readback, which is the state a faulted or
    /// non-authoritative session is in.</summary>
    internal bool CanObserve { get; set; } = true;

    internal PlayerActionWorld()
    {
        // The game's statics are process-wide, so a case starts from the fixture's own zero.
        PlayerManager.Reset();
        PlayerAgent.ResetStatics();
        SNet.IsMaster = true;
        Kernel = new RuntimeKernel(new("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        // A world of its own per case: a reference carries the epoch it was recorded in, and a case that reused
        // an earlier epoch would keep answering for an earlier world's lives.
        Kernel.BeginWorld(++_world);
        _registration = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
            PlayerActions.Handlers(), PlayerActionContract.Support(),
            new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [PlayerIdentityModule.EntityKind] = reference => PlayerIdentityModule.Current is { } half && half.IsCurrent(reference)
            })
        {
            Shapes = PlayerActions.Shapes(),
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
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, true);
    }

    /// <summary>The provider declaration the real Map registration composes: this provider's id, the two catalog
    /// rows this slice implements, and their binding rows. The shapes and handlers are the native half's, exactly
    /// as the session's definition supplies them.</summary>
    private static string Registry() => RuntimeJson.From(new
    {
        providers = new[] { new { id = ModuleDefinition.ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = PlayerActionContract.Rows(),
        bindings = PlayerActionContract.Bindings()
    }).GetRawText();

    /// <summary>One spawned player with a link, a locomotion state and a set-up infection receiver, read back
    /// through the identity module's own reconcile, which is the only path that allocates a life.</summary>
    internal PlayerFixture Spawn(ulong lookup, float infection = 0f,
        PlayerLocomotion.PLOC_State state = PlayerLocomotion.PLOC_State.Stand)
    {
        int slot = PlayerManager.PlayerAgentsInLevel.Count;
        var player = new SNet_Player { Lookup = lookup, SlotIndex = slot };
        var agent = new PlayerAgent { Owner = player, PlayerSlotIndex = slot };
        agent.Locomotion = new PlayerLocomotion { m_currentStateEnum = state };
        agent.Damage = new Dam_PlayerDamageBase { Owner = agent, IsSetup = true, Infection = infection, Health = 50f, HealthMax = 100f };
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

    /// <summary>One request frame as the dispatcher would hand it to a handler: the ports a case names, and no
    /// other. An input a case does not name is absent, which is what a plan that wired nothing into it
    /// produces.</summary>
    internal static JsonElement Frame(params (string Port, object? Value)[] inputs)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (port, value) in inputs) fields[port] = value;
        return RuntimeJson.From(fields);
    }

    internal static JsonElement Parameters(object parameters) => RuntimeJson.From(parameters);

    public void Dispose()
    {
        Identity.Dispose();
        _registration.Dispose();
    }
}
