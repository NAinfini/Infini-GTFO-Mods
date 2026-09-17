using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;
using SNetwork;

namespace ForgeMap.Tests.DoorActions;

/// <summary>One case's world: a kernel with the one `gtfo.map_object` namespace registered the way the Map
/// provider registers it — a namespace resolver plus the instance resolver that maps a live native instance back
/// to its reference — and the three door action rows declared from `DoorActionContract`, with the native
/// handler object over the same table. There is no loader, no session and no hook: the handler is a plain
/// object, so a case builds one and disposes the kernel it was built over before returning.
///
/// The declaration is the contract's own rows, bindings and shapes, so registering it proves the rows resolve
/// against the real registry: a capability whose recipient contract, ports or parameters do not satisfy the
/// framework is refused here, not in a comment.</summary>
internal sealed class DoorActionWorld : IDisposable
{
    /// <summary>The one kind these rows answer for, spelled as the production handler spells it: the fixture
    /// keeps the literal so a reworded kind fails a test.</summary>
    internal const string Kind = "gtfo.map_object";
    internal const string OtherKind = "gtfo.player";
    internal const long WorldEpoch = 11;

    private readonly RuntimeModuleHandle _map;
    private readonly RuntimeModuleHandle _other;
    internal RuntimeKernel Kernel { get; }
    /// <summary>Every door instance this world resolves, by the reference the provider assigned it.</summary>
    internal readonly Dictionary<EntityReference, LG_SecurityDoor> Doors = new();
    /// <summary>The references this world's own namespace resolver still answers for.</summary>
    internal readonly HashSet<EntityReference> Live = new();
    internal readonly List<EntityReference> LookedUp = new();
    internal readonly List<string> Reports = new();
    /// <summary>When clear, the session half reports itself unready, which is one of the gates a write needs.</summary>
    internal bool Ready { get; set; } = true;
    /// <summary>The game's own master flag, as the action gate reads it.</summary>
    internal bool Master
    {
        get => SNet.IsMaster;
        set => SNet.IsMaster = value;
    }
    /// <summary>When set, the door lookup throws instead of answering, which is what a namespace whose own
    /// table is mid-teardown looks like from here.</summary>
    internal bool LookupThrows { get; set; }
    /// <summary>When set, the lookup answers with this instance instead of the one the reference names, so the
    /// provider's own table and the reference can disagree.</summary>
    internal LG_SecurityDoor? Substitute { get; set; }

    internal DoorActionWorld()
    {
        Kernel = new RuntimeKernel(new("fixture.door.actions", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(WorldEpoch);
        var commands = new DoorActionCommands(Kernel, () => Ready, Resolve, Reports.Add);
        Commands = commands;
        _map = Kernel.RegisterModule(Declaration(commands), RuntimeLogLevel.Off);
        var other = OtherKind;
        _other = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry("fixture.players"),
            new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>> { [other] = reference => OtherLive.Contains(reference) }),
            RuntimeLogLevel.Off);
    }

    /// <summary>The handler object under test, built over this world's own table.</summary>
    internal DoorActionCommands Commands { get; }
    internal readonly HashSet<EntityReference> OtherLive = new();

    /// <summary>The Map provider's own declaration, from the contract's rows, bindings, supports, shapes and
    /// handler names: the one place a case can show that the rows under test are the rows the runtime accepts.</summary>
    private RuntimeModule Declaration(DoorActionCommands commands) => new(
        RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = ModuleDefinition.ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = DoorActionContract.Rows(),
            bindings = DoorActionContract.Bindings()
        }).GetRawText(),
        new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [DoorActionContract.OpenHandlerName] = commands.HandleOpen,
            [DoorActionContract.CloseHandlerName] = commands.HandleClose,
            [DoorActionContract.AlarmHandlerName] = commands.HandleAlarm
        },
        DoorActionContract.Supports())
    {
        EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
        {
            [Kind] = reference => Live.Contains(reference)
        },
        EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
        {
            [Kind] = instance => instance is LG_SecurityDoor door
                ? Doors.FirstOrDefault(pair => ReferenceEquals(pair.Value, door)).Key
                : null
        },
        Shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [DoorActionContract.OpenHandlerName] = DoorActionContract.OpenShape,
            [DoorActionContract.CloseHandlerName] = DoorActionContract.CloseShape,
            [DoorActionContract.AlarmHandlerName] = DoorActionContract.AlarmShape
        }
    };

    private static string Registry(string provider) => RuntimeJson.From(new
    {
        providers = new[] { new { id = provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText();

    /// <summary>Starts the runtime and settles one authoritative tick. The kernel answers no entity reference
    /// before its world has an authority, which is one of the states the action's own gate refuses.</summary>
    internal void Start(bool isHost = true)
    {
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, isHost);
    }

    /// <summary>The provider's own lookup, as the session hands it over: the production address lookup, with the
    /// two knobs a case needs to make it fail. A reference this world does not hold answers null.</summary>
    private LG_SecurityDoor? Resolve(EntityReference reference)
    {
        LookedUp.Add(reference);
        if (LookupThrows) throw new InvalidOperationException("fixture door lookup failure");
        if (Substitute != null) return Substitute;
        return DoorActionCommands.ResolveByAddress(reference);
    }

    /// <summary>A door this world's own namespace registered, with the address it reads as, so the handler's
    /// reference, its own instance table and the address check all agree exactly as they do in the game.</summary>
    internal LG_SecurityDoor Door(int zone, eDoorStatus status = eDoorStatus.Closed, bool withLocks = true)
    {
        var address = MapObjectDoorAddress.Create(0, 0, zone)!;
        var door = new LG_SecurityDoor { LastStatus = status, AddressNow = address };
        if (withLocks)
        {
            var locks = new LG_SecurityDoor_Locks { m_door = door };
            door.m_locks = new iLG_Door_Locks { Target = locks };
        }
        var reference = new EntityReference(Kind + ":" + address, WorldEpoch, 1);
        Doors[reference] = door;
        Live.Add(reference);
        DoorObservation.ByAddressTable[address] = door;
        return door;
    }

    /// <summary>The reference one door was registered under.</summary>
    internal EntityReference ReferenceOf(LG_SecurityDoor door)
        => Doors.First(pair => ReferenceEquals(pair.Value, door)).Key;

    /// <summary>The lock component of one door, as the production layer reaches it.</summary>
    internal static LG_SecurityDoor_Locks Locks(LG_SecurityDoor door) => door.m_locks!.Target as LG_SecurityDoor_Locks
        ?? throw new InvalidOperationException("fixture door has no lock component");

    /// <summary>A reference this world never registered, of the door kind and of the right shape: the kernel's
    /// own namespace resolver refuses it, which is the stale case.</summary>
    internal EntityReference Unknown(int zone)
        => new(Kind + ":door/0/0/" + zone + "/security", WorldEpoch, 1);

    /// <summary>A reference of the door kind minted in another world.</summary>
    internal EntityReference OtherWorld(int zone)
        => new(Kind + ":door/0/0/" + zone + "/security", WorldEpoch - 1, 1);

    /// <summary>A current reference of a kind this action does not answer for.</summary>
    internal EntityReference Player(string id)
    {
        var reference = new EntityReference(OtherKind + ":" + id, WorldEpoch, 1);
        OtherLive.Add(reference);
        return reference;
    }

    /// <summary>A reference of the map-object kind but the terminal category: the same namespace, a category
    /// these rows do not act on.</summary>
    internal EntityReference Terminal(int zone)
    {
        var reference = new EntityReference(Kind + ":terminal/0/0/" + zone + "/0", WorldEpoch, 1);
        Live.Add(reference);
        return reference;
    }

    internal void Retire(EntityReference reference) => Live.Remove(reference);

    /// <summary>Runs one handler the way the kernel does: the command context's own constructor is internal to
    /// the SDK, so it is reached through its non-public signature. `isHost` is the kernel's own per-command host
    /// fact, not this world's authority.</summary>
    internal CommandResult Run(CommandHandler handler, object? inputs, object? parameters = null, bool isHost = true)
    {
        var origin = new RuntimeEvent("test.door.event", ModuleDefinition.ProviderId + ".binding.test", WorldEpoch, 0,
            "test.door.scope", RuntimeJson.EmptyObject);
        var context = (CommandContext)Activator.CreateInstance(typeof(CommandContext),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object?[]
            {
                origin, 0L, "test.door.command", "test.door.plan", "test.door.resource", "1", "test.door.node",
                RuntimeJson.From(parameters ?? new { }), RuntimeJson.From(inputs ?? new { }), isHost
            }, null)!;
        return handler(context);
    }

    /// <summary>The one result row of a command whose rows a case expects to be single.</summary>
    internal static JsonElement Row(CommandResult result)
        => result.Outputs.GetProperty("results").EnumerateArray().Single();

    internal static JsonElement[] Rows(CommandResult result)
        => result.Outputs.GetProperty("results").EnumerateArray().ToArray();

    internal static string Field(CommandResult result, string name) => Row(result).GetProperty(name).GetString() ?? "";

    /// <summary>The exported manifest, which is what the website and the release manifest read.</summary>
    internal JsonElement Manifest() => RuntimeJson.Parse(Kernel.ExportManifest());

    public void Dispose()
    {
        _map.Dispose();
        _other.Dispose();
        SNet.IsMaster = true;
    }
}
