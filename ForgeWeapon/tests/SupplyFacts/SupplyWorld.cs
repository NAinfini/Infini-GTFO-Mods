using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.SupplyFacts;

/// <summary>One case's world: a kernel with a `gtfo.player` kind registered the way ForgeMap registers it, one
/// supply adapter over it, and the game doubles the adapter submits through. There is no loader, no session and no
/// hook here, so a case builds a world and disposes the kernel before returning.
///
/// The command context is created through the SDK's own internal constructor, exactly as the inventory-action
/// cases do, because the kernel's own dispatch would need a whole compiled plan to reach a handler.</summary>
internal sealed class SupplyWorld : IDisposable
{
    internal const string PlayerKind = "gtfo.player";
    internal const string OtherKind = "fixture.other";
    internal const long WorldEpoch = 11;

    private readonly RuntimeModuleHandle _players, _other;
    internal RuntimeKernel Kernel { get; }
    internal readonly Dictionary<SNetwork.SNet_Player, EntityReference> References = new();
    internal readonly HashSet<EntityReference> LivePlayers = new();
    internal List<string> Reports { get; } = new();
    /// <summary>When clear, the kernel has no authoritative world, which is the phase the adapter's own host gate
    /// refuses.</summary>
    internal bool Authoritative { get; set; } = true;

    internal SupplyWorld()
    {
        SNetwork.SNet.Reset();
        Player.PlayerBackpackManager.Reset();
        GameData.ItemDataBlock.Reset();
        Kernel = new RuntimeKernel(new("fixture.supply.facts", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(WorldEpoch);
        var players = PlayerKind;
        _players = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry("fixture.players"),
            new Dictionary<string, CommandHandler>(),
            Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>> { [players] = reference => LivePlayers.Contains(reference) })
        {
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
            {
                [players] = instance => instance is SNetwork.SNet_Player player
                    && References.TryGetValue(player, out var reference) ? reference : null
            }
        }, RuntimeLogLevel.Off);
        var other = OtherKind;
        _other = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry("fixture.other"),
            new Dictionary<string, CommandHandler>(),
            Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>> { [other] = reference => LivePlayers.Contains(reference) }),
            RuntimeLogLevel.Off);
    }

    private static string Registry(string provider) => RuntimeJson.From(new
    {
        providers = new[] { new { id = provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText();

    /// <summary>Starts the runtime and settles one authoritative tick, which is what gives the kernel a world for
    /// the references to belong to.</summary>
    internal void Start()
    {
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, Authoritative);
    }

    /// <summary>A player the fixture's own kind has registered, with the backpack and storage the game's lookup
    /// answers for.</summary>
    internal (SNetwork.SNet_Player Player, Player.PlayerBackpack Backpack, Player.PlayerAmmoStorage Storage, EntityReference Reference)
        PlayerRef(int id, bool withBackpack = true, bool registerKind = true)
    {
        var player = new SNetwork.SNet_Player { NickName = "player-" + id };
        var reference = new EntityReference(PlayerKind + ":" + id, WorldEpoch, 1);
        References[player] = reference;
        if (registerKind) LivePlayers.Add(reference);
        var storage = new Player.PlayerAmmoStorage();
        var backpack = new Player.PlayerBackpack { Owner = player, AmmoStorage = storage };
        if (withBackpack) Player.PlayerBackpackManager.Backpacks[player] = backpack;
        return (player, backpack, storage, reference);
    }

    /// <summary>One pool as a case writes it.</summary>
    internal static void Pool(Player.PlayerAmmoStorage storage, Player.AmmoType type, int bullets, int cap)
        => storage.Pools[type] = new Player.Pool { Bullets = bullets, Cap = cap };

    /// <summary>A current reference of a kind this action does not answer for.</summary>
    internal EntityReference Other(string id)
    {
        var reference = new EntityReference(OtherKind + ":" + id, WorldEpoch, 1);
        LivePlayers.Add(reference);
        return reference;
    }

    /// <summary>The adapter over this world. The injected lookup is the one direction the framework does not have
    /// yet: the native object behind a player reference.</summary>
    internal WeaponSupplyAdapter Adapter() => new(Kernel, () => Authoritative, PlayerOf, Reports.Add);

    internal bool CanNamePlayers { get; set; } = true;
    internal bool LookupThrows { get; set; }

    internal object? PlayerOf(EntityReference reference)
    {
        if (LookupThrows) throw new InvalidOperationException("fixture player lookup failure");
        if (!CanNamePlayers) return null;
        foreach (var pair in References)
            if (pair.Value == reference) return pair.Key;
        return null;
    }

    /// <summary>One command context, built through the SDK's own internal constructor exactly as the kernel builds
    /// it.</summary>
    internal CommandContext Context(object? parameters, params (string Port, object? Value)[] inputs)
    {
        var origin = new RuntimeEvent("test.supply.trigger", WeaponSupplyContract.AmmoAddBinding, WorldEpoch, 0,
            "test.supply.scope", RuntimeJson.EmptyObject);
        var ports = new Dictionary<string, object?>();
        foreach (var (port, value) in inputs) ports[port] = value;
        return (CommandContext)Activator.CreateInstance(typeof(CommandContext),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[]
            {
                origin, 0L, "test.supply.command", "test.supply.plan", "test.supply.resource", "1",
                "test.supply.node", RuntimeJson.From(parameters ?? new { }), RuntimeJson.From(ports), true
            }, null)!;
    }

    internal static JsonElement Row(CommandResult result) => result.Outputs.GetProperty("results").EnumerateArray().Single();

    public void Dispose()
    {
        _players.Dispose();
        _other.Dispose();
    }
}
