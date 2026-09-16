using System;
using System.Collections.Generic;
using System.Linq;
using ForgeMap;
using ForgeMap.Tests.Support;
using ForgeRuntime.Framework;
using LevelGeneration;
using SNetwork;
using Facts = ForgeMap.Native.DoorTerminalFacts;
using Actions = ForgeMap.Native.DoorTerminalActions;
using DoorObservation = ForgeMap.Native.DoorObservation;
using ZoneCoordinates = ForgeMap.Native.ZoneCoordinates;

namespace ForgeMapTests.DoorTerminalFacts;

/// <summary>One case's world: a kernel with the one `gtfo.map_object` kind registered exactly the way the Map
/// provider registers it — a namespace resolver plus the instance resolver that maps a live instance back to its
/// reference — the real four event rows and two action rows of this slice, and one production
/// `DoorTerminalFacts` and `DoorTerminalActions` over both.
///
/// The facts half, its pure derivation, the publisher, the action half, the two contracts and the terminal
/// action layer are the production files compiled into this assembly, so a case observes the production read,
/// the production event building and the production write call. Only the game's own members are doubles, and
/// they record what they were asked, which is what makes "the fact was published for this door with these ports"
/// and "the unlock asked the door's own interaction entry for `Unlock`" assertions rather than claims.
///
/// The module is registered from the real contract rows and bindings, so a handler's declared port shape is
/// resolved against the capability graph this slice really declares: a port renamed on one side fails the
/// fixture's own construction, not a later dispatch.</summary>
internal sealed class World : IDisposable
{
    internal const string Kind = "gtfo.map_object";
    internal const long WorldEpoch = 11;
    internal const int Dimension = 0;
    internal const int Layer = 0;
    internal const int Zone = 3;

    private readonly RuntimeModuleHandle _registration;
    private readonly Dictionary<LG_SecurityDoor, EntityReference> _doorReferences = new();
    private readonly Dictionary<LG_ComputerTerminal, EntityReference> _terminalReferences = new();
    private readonly Dictionary<LG_WeakDoor, EntityReference> _weakDoorReferences = new();
    private readonly Dictionary<EntityReference, object> _byReference = new();
    private readonly Dictionary<uint, LG_ComputerTerminal> _terminalsBySync = new();
    private readonly Dictionary<SNet_Player, EntityReference> _playerReferences = new();
    private readonly HashSet<EntityReference> _live = new();

    internal RuntimeKernel Kernel { get; }
    internal Facts Facts { get; }
    internal Actions Actions { get; }
    internal List<string> Reports { get; } = new();
    /// <summary>When clear, the kernel has no authoritative world, which is the phase both halves' host gate
    /// refuses. Nothing else in these cases turns it off.</summary>
    internal bool Authoritative { get; set; } = true;

    internal World()
    {
        DoorObservation.ByAddressTable.Clear();
        Kernel = new RuntimeKernel(new("fixture.door.terminal", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        // The control vocabulary is the kernel's own module, the way the host registers it: the one step of the
        // plan this fixture mounts below is the kernel's own `branch`.
        Kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        Kernel.BeginWorld(WorldEpoch);
        Actions = new Actions(Kernel, () => Authoritative, Lookup, Reports.Add);
        _registration = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
            {
                [DoorTerminalActionContract.LockHandlerName] = Actions.HandleLock,
                [DoorTerminalActionContract.UnlockHandlerName] = Actions.HandleUnlock
            },
            DoorTerminalEventContract.Supports().Concat(DoorTerminalActionContract.Supports()).ToArray())
        {
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>(StringComparer.Ordinal)
            {
                [Kind] = reference => _live.Contains(reference)
            },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>(StringComparer.Ordinal)
            {
                [Kind] = instance => instance switch
                {
                    LG_SecurityDoor door when _doorReferences.TryGetValue(door, out var reference) => reference,
                    LG_ComputerTerminal terminal when _terminalReferences.TryGetValue(terminal, out var reference) => reference,
                    _ => null
                }
            },
            Shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
            {
                [DoorTerminalActionContract.LockHandlerName] = DoorTerminalActionContract.LockShape,
                [DoorTerminalActionContract.UnlockHandlerName] = DoorTerminalActionContract.UnlockShape
            }
        }, RuntimeLogLevel.Off);
        Facts = new Facts(Kernel, _registration, () => Authoritative, Reports.Add,
            syncId => _terminalsBySync.TryGetValue(syncId, out var terminal) ? terminal : null,
            terminal => terminal.AddressNow,
            Facts.DoorOwner,
            // The weak door's own placement: the gate it was spawned on, the course node that gate links, and
            // the zone that node belongs to. It is the same path the production reader walks, and it answers both
            // the coordinates the address is built from and the reference the row's `zone` port carries.
            WeakPlacement,
            player => _playerReferences.TryGetValue(player, out var reference) ? reference : null);
    }

    /// <summary>The zone one weak door stands in, read the way the production reader reads it: the gate the door
    /// was spawned on links the course node, and the node names its zone. Both answers come from the one reading,
    /// so the address the door's own entity is built from and the reference the row's `zone` port carries can
    /// never name two different places.</summary>
    internal static (ZoneCoordinates Coordinates, EntityReference Reference)? WeakPlacement(LG_WeakDoor door)
        => WeakZone(door) is { } zone
            ? (zone, RuntimeZones.Reference(WorldEpoch, zone.Dimension, zone.Layer, zone.Zone)) : null;

    /// <summary>The zone the placement walk reaches, or null when the level cannot place the door. The walk
    /// casts each node the gate links to the one node kind that stands in a zone, exactly as the production
    /// reader does.</summary>
    private static ZoneCoordinates? WeakZone(LG_WeakDoor door)
    {
        if (door == null || door.WasCollected || door.Gate is not { } gate || gate.WasCollected) return null;
        var nodes = gate.m_nodes;
        if (nodes == null) return null;
        for (var index = 0; index != nodes.Count; index++)
        {
            var zone = nodes[index]?.TryCast<AIG_CourseNode>()?.m_zone;
            if (zone != null && !zone.WasCollected) return new ZoneCoordinates(zone.Dimension, zone.Layer, zone.LocalIndex);
        }
        return null;
    }

    /// <summary>The registry seed this fixture registers: the real event rows and bindings of this slice under
    /// the Map provider's own id, plus the two action rows, so the rows a case exercises are the rows the
    /// package declares.</summary>
    private static string Registry() => RuntimeJson.From(new
    {
        providers = new[]
        {
            new { id = ModuleDefinition.ProviderId, kind = "native", version = ModuleDefinition.Version,
                dependencies = Array.Empty<string>() }
        },
        capabilities = DoorTerminalEventContract.Rows().Concat(DoorTerminalActionContract.Rows()).ToArray(),
        bindings = DoorTerminalEventContract.Bindings().Concat(DoorTerminalActionContract.Bindings()).ToArray()
    }).GetRawText();

    /// <summary>Starts the runtime and settles one authoritative tick. The kernel answers no entity reference
    /// before its world has an authority, which is exactly the state both halves' gates refuse.</summary>
    internal void Start()
    {
        // The observation point these cases assert on sits in the publisher, before the kernel is reached, so the
        // module needs a subscriber for every row it publishes under or it builds no event at all.
        SubscriptionGateFixture.Open(Kernel, _registration);
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

    /// <summary>One security door with the address it reads as, its own lock component and its own sync
    /// component, all wired the way the game's own level generation wires them (`m_locks.m_door` and
    /// `m_sync` back to the door).</summary>
    internal LG_SecurityDoor Door(int zone = Zone, eDoorStatus status = eDoorStatus.Closed)
    {
        var address = MapObjectDoorAddress.Create(Dimension, Layer, zone)
            ?? throw new Exception("The door address did not build.");
        var door = new LG_SecurityDoor { LastStatus = status, AddressNow = address };
        var locks = new LG_SecurityDoor_Locks { m_door = door };
        door.m_locks = new iLG_Door_Locks { Target = locks };
        door.m_sync = new iLG_Door_Sync { Target = new LG_Door_Sync() };
        DoorObservation.ByAddressTable[address] = door;
        var reference = new EntityReference(Kind + ":" + address, WorldEpoch, 1);
        _doorReferences[door] = reference;
        _byReference[reference] = door;
        _live.Add(reference);
        return door;
    }

    /// <summary>The lock component of a door this fixture made.</summary>
    internal static LG_SecurityDoor_Locks Locks(LG_SecurityDoor door)
        => (LG_SecurityDoor_Locks)door.m_locks!.Target!;

    /// <summary>The sync component of a door this fixture made.</summary>
    internal static LG_Door_Sync Sync(LG_SecurityDoor door) => (LG_Door_Sync)door.m_sync!.Target!;

    /// <summary>One terminal with the own sync id and address the interaction facts read.</summary>
    internal LG_ComputerTerminal Terminal(uint syncId, int placement = 0, int zone = Zone)
    {
        var address = MapObjectTerminalAddress.Create(Dimension, Layer, zone, placement)
            ?? throw new Exception("The terminal address did not build.");
        var terminal = new LG_ComputerTerminal { SyncID = syncId, AddressNow = address };
        _terminalsBySync[syncId] = terminal;
        var reference = new EntityReference(Kind + ":" + address, WorldEpoch, 1);
        _terminalReferences[terminal] = reference;
        _byReference[reference] = terminal;
        _live.Add(reference);
        return terminal;
    }

    /// <summary>One weak lock spawned on a door, which is what the game's own weak doors do.</summary>
    internal static LG_WeakLock WeakLock(LG_SecurityDoor door, eWeakLockType type, eWeakLockStatus status)
        => new() { m_lockType = type, Status = status, m_holder = Holder.For(door) };

    /// <summary>One weak door in the given zone: the only door kind the game lets enemies break. It is spawned
    /// on a gate whose course node names the zone, which is the path the production reader walks, and its own
    /// game-object id is the serial its reference carries.</summary>
    internal LG_WeakDoor WeakDoor(int zone = Zone, float x = 1f, float y = 2f, float z = 3f)
    {
        var node = new LevelGeneration.AIG_CourseNode { m_zone = new LG_Zone { Dimension = Dimension, Layer = Layer, LocalIndex = zone } };
        var gate = new LG_Gate();
        gate.m_nodes.Add(node);
        var door = new LG_WeakDoor { Gate = gate };
        door.transform.position = new UnityEngine.Vector3(x, y, z);
        return door;
    }

    /// <summary>The reference one weak door is published under, which is the id the facts half built for it. A
    /// door this fixture made and never published has none: the reference exists because a fact carried it, not
    /// because the fixture invented one.</summary>
    internal EntityReference ReferenceOf(LG_WeakDoor door)
    {
        var reference = Facts.WeakDoorReference(door) ?? throw new Exception("The weak door has no reference.");
        _weakDoorReferences[door] = reference;
        return reference;
    }

    /// <summary>One player the replication callback may name, and the entity reference this world answers for
    /// it. The identity module is what maps the two in production; the fixture supplies the mapping.</summary>
    internal SNet_Player Player(string name)
    {
        var player = new SNet_Player();
        var reference = new EntityReference("gtfo.player:" + name, WorldEpoch, 1);
        _playerReferences[player] = reference;
        return player;
    }

    /// <summary>The reference one fixture player was registered under.</summary>
    internal EntityReference PlayerReference(SNet_Player player)
        => _playerReferences.TryGetValue(player, out var reference)
            ? reference : throw new Exception("The player was not made by this fixture.");

    /// <summary>Retires a door the way a level teardown would: the kernel's own resolver no longer knows it, so
    /// the reference stops being current.</summary>
    internal void Retire(LG_SecurityDoor door)
    {
        if (_doorReferences.Remove(door, out var reference)) { _byReference.Remove(reference); _live.Remove(reference); }
    }

    /// <summary>The reference of one map object this fixture made, spelled the way the entity id spells it.</summary>
    internal EntityReference ReferenceOf(LG_SecurityDoor door)
        => _doorReferences.TryGetValue(door, out var reference)
            ? reference : throw new Exception("The door was not made by this fixture.");

    internal EntityReference ReferenceOf(LG_ComputerTerminal terminal)
        => _terminalReferences.TryGetValue(terminal, out var reference)
            ? reference : throw new Exception("The terminal was not made by this fixture.");

    /// <summary>The address of a door this fixture made, which is the subject its facts are keyed by.</summary>
    internal static MapObjectReference AddressOf(LG_SecurityDoor door)
        => door.AddressNow ?? throw new Exception("The door has no address.");

    internal static MapObjectReference AddressOf(LG_ComputerTerminal terminal)
        => terminal.AddressNow ?? throw new Exception("The terminal has no address.");

    /// <summary>A current reference of the map-object kind that names no door: a foreign category's address. The
    /// action rows answer for doors only, so this is the "another category of my own kind" case.</summary>
    internal EntityReference TerminalReference(string address = "terminal/0/0/3/0")
    {
        var reference = new EntityReference(Kind + ":" + address, WorldEpoch, 1);
        _live.Add(reference);
        return reference;
    }

    /// <summary>A current reference of a kind neither half owns at all.</summary>
    internal EntityReference ForeignReference(string id = "fixture.other:1")
    {
        var reference = new EntityReference(id, WorldEpoch, 1);
        _live.Add(reference);
        return reference;
    }

    /// <summary>A reference of the right shape in a world that has ended.</summary>
    internal EntityReference StaleWorldReference(LG_SecurityDoor door)
        => ReferenceOf(door) with { WorldEpoch = WorldEpoch - 1 };

    public void Dispose() => _registration.Dispose();

    private object? Lookup(EntityReference reference)
        => _byReference.TryGetValue(reference, out var instance) ? instance : null;

    /// <summary>The weak lock's holder, which is the component the lock was spawned under. It is a component
    /// whose parent lookup answers the door, which is what the production `DoorOwner` walks.</summary>
    private sealed class Holder : UnityEngine.Component
    {
        private LG_SecurityDoor? door;

        internal static Holder For(LG_SecurityDoor door) => new() { door = door };

        public override T? GetComponentInParent<T>() where T : class => door as T;
    }
}
