using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.InventoryActions;

/// <summary>One case's world: a kernel with a `gtfo.player` kind registered the way ForgeMap registers it — the
/// instance resolver maps a live `SNet_Player` to the reference the player domain allocated — one inventory
/// action adapter over it, and the game doubles the adapter submits through. There is no loader, no session and
/// no hook here, so a case builds a world and disposes the kernel before returning.
///
/// The command context is created through the SDK's own internal constructor, exactly as the player-heal and
/// enemy audits do, because the kernel's own dispatch would need a whole compiled plan to reach a handler.</summary>
internal sealed class InventoryActionsWorld : IDisposable
{
    internal const string PlayerKind = "gtfo.player";
    internal const string OtherKind = "fixture.other";
    internal const long WorldEpoch = 7;
    /// <summary>The item id the fixture's resource resolver answers with, and the one it refuses.</summary>
    internal const uint KnownItemId = 4242;
    internal const string KnownResource = "fixture.item.medkit";
    internal const string UnknownResource = "fixture.item.unknown";

    private readonly RuntimeModuleHandle _players, _other;
    internal RuntimeKernel Kernel { get; }
    internal readonly Dictionary<SNetwork.SNet_Player, EntityReference> References = new();
    internal readonly HashSet<EntityReference> LivePlayers = new();
    internal List<string> Reports { get; } = new();
    /// <summary>When clear, the kernel has no authoritative world, which is the phase the action's own host gate
    /// refuses; nothing else in these cases turns it off.</summary>
    internal bool Authoritative { get; set; } = true;

    internal InventoryActionsWorld()
    {
        Kernel = new RuntimeKernel(new("fixture.inventory.actions", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
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

    /// <summary>Turns the world non-authoritative from the next tick on, the way a machine that lost the master
    /// role sees it.</summary>
    internal void LoseAuthority()
    {
        Authoritative = false;
        Kernel.Advance(Kernel.CurrentTick + 1, false);
    }

    /// <summary>A player the fixture's own kind has registered, with a backpack the game's lookup answers for.
    /// `slots` is how many slots that backpack has; the pocket slot is index 7 in this build, so a case that wants
    /// a usable backpack asks for at least eight.</summary>
    internal (SNetwork.SNet_Player Player, Player.PlayerBackpack Backpack, EntityReference Reference) PlayerRef(
        int id, int slots = 10, bool withBackpack = true, bool registerKind = true)
    {
        var player = new SNetwork.SNet_Player { NickName = "player-" + id };
        var reference = new EntityReference(PlayerKind + ":" + id, WorldEpoch, 1);
        References[player] = reference;
        if (registerKind) LivePlayers.Add(reference);
        var backpack = new Player.PlayerBackpack
        {
            Owner = player,
            Slots = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Player.BackpackItem>(slots)
        };
        if (withBackpack) Player.PlayerBackpackManager.Backpacks[player] = backpack;
        return (player, backpack, reference);
    }

    /// <summary>A current reference of a kind this action does not answer for.</summary>
    internal EntityReference Other(string id)
    {
        var reference = new EntityReference(OtherKind + ":" + id, WorldEpoch, 1);
        LivePlayers.Add(reference);
        return reference;
    }

    /// <summary>Puts one item in a backpack slot, the way the game's own add body leaves it.</summary>
    internal static void Occupy(Player.PlayerBackpack backpack, int slot, uint itemId)
        => backpack.Slots![slot] = new Player.BackpackItem { ItemID = itemId };

    internal static void Clear(Player.PlayerBackpack backpack, int slot) => backpack.Slots![slot] = null!;

    /// <summary>The adapter over this world. The two injected halves are the two directions the framework does not
    /// have yet: the item resource resolver (the runtime resource registry) and the native-object lookup for a
    /// player reference (batch C's object-to-entity table read backwards). Both answer from the fixture's own
    /// tables, which is what the domains will own once those two land.</summary>
    internal InventoryActionAdapter Adapter(InventoryActionAdapter.ItemResolver? resolver = null)
        => new(Kernel, () => Authoritative, resolver ?? ((string resourceId, out uint itemId) =>
        {
            itemId = resourceId == KnownResource ? KnownItemId : 0u;
            return resourceId == KnownResource;
        }), PlayerOf, Reports.Add);

    /// <summary>The native player a reference names, or null. A case turns this off to model a session that
    /// cannot name a player at all, or makes it throw to model a native table mid-teardown.</summary>
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
    /// it: an origin event, the tick and the ids, then the node's own parameters and the request frame.</summary>
    internal CommandContext Context(string bindingId, object? parameters, params (string Port, object? Value)[] inputs)
    {
        var origin = new RuntimeEvent("test.inventory.trigger", bindingId, WorldEpoch, 0, "test.inventory.scope",
            RuntimeJson.EmptyObject);
        var ports = new Dictionary<string, object?>();
        foreach (var (port, value) in inputs) ports[port] = value;
        return (CommandContext)Activator.CreateInstance(typeof(CommandContext),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[]
            {
                origin, 0L, "test.inventory.command", "test.inventory.plan", "test.inventory.resource", "1",
                "test.inventory.node", RuntimeJson.From(parameters ?? new { }), RuntimeJson.From(ports), true
            }, null)!;
    }

    /// <summary>The item resource value one request carries on the `item` port. It is written as the two-field
    /// reference the frame's own resource slots hold, not as a bare string.</summary>
    internal static object Item(string resourceId) => new { resourceKind = "item", resourceId };

    internal static JsonElement[] Rows(CommandResult result)
        => result.Outputs.GetProperty("results").EnumerateArray().ToArray();

    internal static JsonElement Row(CommandResult result) => Rows(result).Single();

    public void Dispose()
    {
        _players.Dispose();
        _other.Dispose();
    }
}
