using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;
using LevelGeneration;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>
/// The door and terminal interaction rows this slice adds, read from the native callbacks and published through
/// the one Map registration. This half holds the reading — which native member names which fact — while
/// `DoorTerminalDerivations` holds the pure derivation and `DoorTerminalPublisher` the event building. No rule
/// is written twice, and a test drives the very same methods the Harmony patches call.
///
/// The instance is owned by the Map plugin session, which owns the registration and the world epoch. `Current`
/// is how a native callback reaches it, because a Harmony patch is static and the session is not; a callback on
/// a peer with no session, or after the session faulted or the world ended, publishes nothing.
/// </summary>
internal sealed class DoorTerminalFacts
{
    /// <summary>The live half, or null before the session created it and after it was released or faulted.</summary>
    internal static DoorTerminalFacts? Current { get; set; }

    /// <summary>The terminal one sync id names, or null when this world has no such terminal. It is a delegate
    /// for the same reason the action half's resolver is: the session hands over its own lookup, a test hands
    /// over a table, and neither has to build a level for the mapping to be exercised.</summary>
    internal delegate LG_ComputerTerminal? TerminalLookup(uint syncId);

    /// <summary>The door a weak lock belongs to, or null when nothing addressable owns the lock.</summary>
    internal delegate LG_SecurityDoor? WeakLockOwner(LG_WeakLock lockInstance);

    /// <summary>The address a terminal reads as right now, or null when it has none. It is a delegate for the
    /// same reason: the session hands over the observation half's own reader, a test hands over the terminal's
    /// own coordinates, and the mapping from a command to a fact is exercised without a level.</summary>
    internal delegate MapObjectReference? TerminalAddress(LG_ComputerTerminal terminal);

    /// <summary>The zone one weak door stands in, read once for both answers the row needs: the level's own three
    /// coordinates, which are the door's address, and the entity reference the row's `zone` port carries. A weak
    /// door is not a zone's entrance gate, so this is its own walk of the level: the gate the door was spawned
    /// on, the course node that gate links, and the zone that node belongs to. One delegate answers both, because
    /// they are one reading: an address built from one zone and a `zone` port filled from another would describe
    /// two different places.</summary>
    internal delegate (ZoneCoordinates Coordinates, EntityReference Reference)? WeakDoorPlacement(LG_WeakDoor door);

    /// <summary>The player entity the replication callback's `SNet_Player` names, or null when this process
    /// tracks no life for it. The callback carries the acting player, and the identity module is what turns that
    /// player into the entity a port may carry.</summary>
    internal delegate EntityReference? PlayerEntity(SNet_Player player);

    private readonly DoorTerminalPublisher _publisher;
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;
    private readonly TerminalLookup _terminals;
    private readonly TerminalAddress _address;
    private readonly WeakLockOwner _locks;
    private readonly WeakDoorPlacement _weakPlacement;
    private readonly PlayerEntity _players;
    private readonly long _world;
    private bool _faulted;

    internal DoorTerminalFacts(RuntimeKernel kernel, RuntimeModuleHandle registration, Func<bool> ready,
        Action<string> report, TerminalLookup terminals, TerminalAddress address, WeakLockOwner locks,
        WeakDoorPlacement weakPlacement, PlayerEntity players)
    {
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _weakPlacement = weakPlacement ?? throw new ArgumentNullException(nameof(weakPlacement));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _world = kernel.WorldEpoch;
        _publisher = new DoorTerminalPublisher(kernel, registration, () => ready() && SNet.IsMaster, report);
    }

    /// <summary>The facts handed to the kernel with status `queued`.</summary>
    internal long Published => _publisher.Published;

    /// <summary>How many state keys the ledger holds right now, for a test to assert a world transition
    /// released them.</summary>
    internal int Tracked => _publisher.Tracked;

    /// <summary>The last state one fact queued for one address, or null when nothing was queued. It exists so a
    /// case can assert the ports a fact carried without an event sink.</summary>
    internal (string Binding, string EventId, string StateKey, string Outputs)? Last(string fact, MapObjectReference address)
        => _publisher.Last(fact, address);

    /// <summary>The same answer for a fact whose subject is a runtime entity rather than a map address, which is
    /// how the two weak-door stages are keyed.</summary>
    internal (string Binding, string EventId, string StateKey, string Outputs)? LastWeak(string fact, EntityReference subject)
        => _publisher.Last(fact, subject);
    /// <summary>Whether this half stopped producing after a callback failed.</summary>
    internal bool Faulted => _faulted;

    /// <summary>The weak doors this world published a reference for, by their own runtime id. A weak door has no
    /// map address, so the reference the fact carried is the only name it has, and this table is what answers
    /// "is that reference still one of this world's doors". It holds no native reference: a world transition
    /// invalidates every id it could name, so the table is dropped rather than kept.</summary>
    private readonly Dictionary<string, EntityReference> _weakDoors = new(StringComparer.Ordinal);

    /// <summary>Drops the per-world table. The session calls this on a world transition, where no address the
    /// ledger holds can name anything any more and no weak-door id can name a live door.</summary>
    internal void BeginWorld()
    {
        _publisher.BeginWorld();
        _weakDoors.Clear();
    }

    /// <summary>Whether one entity reference still names a weak door of this world. The runtime resolver the
    /// session registers for the weak-door namespace is this method, so a reference from a previous world and a
    /// reference with a door's serial that never existed are both refused by the kernel before a plan can use
    /// them.</summary>
    internal bool IsCurrentWeakDoor(EntityReference reference)
        => reference.WorldEpoch == _world && _weakDoors.ContainsKey(reference.Id);

    /// <summary>One door the game's own approach callback reported. The callback carries no player and the
    /// door's replicated state carries none either, so the fact has no actor.</summary>
    internal void DoorApproached(object door)
    {
        if (door is not LG_SecurityDoor instance || !Addressable(instance, out var address)) return;
        _publisher.Approach(address);
    }

    /// <summary>One chained-puzzle transition on a door. The callback names which of the lock's two
    /// notifications ran, and the puzzle instance the lock holds is asked for its own reading afterwards:
    /// `ChainedPuzzleToSolve.IsSolved` is the puzzle's own answer to "is it solved", so a solved notification
    /// that fires while the puzzle still reads unsolved is published as the activation it really is. The door's
    /// status is read from the door itself rather than taken from the callback, so the phase and the status come
    /// from one reading of one instance.</summary>
    internal void ScanTransition(object locks, DoorScanStage stage)
    {
        if (locks is not LG_SecurityDoor_Locks instance) return;
        var door = DoorObservation.Door(instance);
        if (door == null || !Addressable(door, out var address)) return;
        _publisher.Scan(address, DoorTerminalDerivations.ScanPhase(stage, PuzzleSolved(instance)),
            (int)door.LastStatus);
    }

    /// <summary>Whether the puzzle the lock holds reads solved right now, or null when the lock holds no puzzle
    /// yet or the instance is gone. Null is absence, not "unsolved": a door whose puzzle is not readable falls
    /// back to the callback's own stage instead of being reported as an activation.</summary>
    private static bool? PuzzleSolved(LG_SecurityDoor_Locks locks)
    {
        var puzzle = locks.ChainedPuzzleToSolve;
        if (puzzle == null || puzzle.WasCollected) return null;
        return puzzle.IsSolved;
    }

    /// <summary>One terminal command the terminal manager's own entry carried. The command value is the
    /// accepted command, the input line is what the player typed, and the interpreter's first parameter names
    /// the log when the command is a read. One entry therefore serves `e-term-cmd` (through the command and its
    /// unique slot), `e-term-alarm` (through `DisableAlarm`) and `e-term-log` (through `ReadLog` and its
    /// parameter), which is what the native side really offers: there is one command entry and no separate event
    /// for any of the three.</summary>
    internal void TerminalCommandEntry(uint terminalId, int command, string? input, string? param1)
    {
        var terminal = _terminals(terminalId);
        if (terminal == null) return;
        if (_address(terminal) is not { } address) return;
        _publisher.TerminalCommand(address, command, input, DoorTerminalDerivations.UniqueSlot(command));
        if (DoorTerminalDerivations.LogName(param1) is { } log) _publisher.TerminalLog(address, log, input);
    }

    /// <summary>One weak lock's replicated state. A lock is broken when its own status reads `Unlocked`, and
    /// what broke it is the lock's own type: a melee lock is smashed through its damage entry, a hackable one
    /// through the hack interaction. The fact is published against the door the lock belongs to, because a weak
    /// lock has no address of its own in this provider's grammar.</summary>
    internal void WeakLockChanged(LG_WeakLock lockInstance)
    {
        if (lockInstance == null || lockInstance.WasCollected) return;
        if (lockInstance.Status != eWeakLockStatus.Unlocked) return;
        if (_locks(lockInstance) is not { } door || !Addressable(door, out var address)) return;
        bool smashed = lockInstance.m_lockType == eWeakLockType.Melee;
        _publisher.LockBroken(address, DoorTerminalDerivations.LockCause(smashed, !smashed),
            lockInstance.m_lockType.ToString().ToLowerInvariant());
    }

    /// <summary>One weak door taking a hit. The callback is the game's own replication notification
    /// (`LG_Door_Sync.OnDoorGotDamage`), so it fires on the host that computed the damage and on every client
    /// that applies it; the publisher's authority gate is what keeps the fact host-only. The door's own identity
    /// is built here rather than taken from the callback, because the callback carries the door instance and no
    /// name for it, and the zone and position are read from the door itself in the same reading.</summary>
    internal void WeakDoorAttacked(LG_WeakDoor door, SNet_Player? attacker)
        => WeakDoorStage(door, DoorTerminalEventContract.AttackedPhase, attacker);

    /// <summary>One weak door breaking. The same two readings as the attack stage, so the two stages of one door
    /// always name one door, one zone and one position.</summary>
    internal void WeakDoorBroken(LG_WeakDoor door, SNet_Player? attacker)
        => WeakDoorStage(door, DoorTerminalEventContract.BrokenPhase, attacker);

    /// <summary>One stage of one weak door: the identity it is published under, the zone it stands in, its world
    /// position and the player the callback named. A door this level cannot place publishes nothing, because the
    /// zone is what an author filters on and a fact without it would be one nobody could react to; the door is
    /// still identified by its own instance id, so the two stages of one door share one subject.</summary>
    private void WeakDoorStage(LG_WeakDoor door, string phase, SNet_Player? attacker)
    {
        if (door == null || door.WasCollected) return;
        if (WeakDoorReference(door) is not { } reference) return;
        if (_weakPlacement(door) is not { } placement) return;
        _weakDoors[reference.Id] = reference;
        _publisher.WeakDoor(reference, phase,
            new ResourceRef(RuntimeZones.ResourceKind, RuntimeZones.Id(placement.Coordinates.Dimension,
                placement.Coordinates.Layer, placement.Coordinates.Zone)),
            DoorTerminalDerivations.Position(door.transform),
            attacker == null ? null : _players(attacker));
    }

    /// <summary>The runtime entity one weak door is named by: a `gtfo.map_object` door address of the zone the
    /// door stands in and the door's own serial, which is the same identity the door actions name a door by. A
    /// door the level cannot place yields no reference instead of a reference no reader could resolve again.</summary>
    internal EntityReference? WeakDoorReference(LG_WeakDoor door)
    {
        if (door == null || door.WasCollected) return null;
        if (DoorTerminalDerivations.WeakDoorSerial(door) is not { } serial) return null;
        if (_weakPlacement(door) is not { } placement) return null;
        return DoorTerminalDerivations.WeakDoorReference(placement.Coordinates.Dimension, placement.Coordinates.Layer,
            placement.Coordinates.Zone, serial, _world);
    }

    /// <summary>Whether a door can be addressed at all. The address is the one the observation half publishes —
    /// the zone this door is the entrance gate of — so a fact and a state always name one door, and a weak, node
    /// or decorative door that has no address publishes no fact.</summary>
    private static bool Addressable(LG_SecurityDoor door, out MapObjectReference address)
    {
        address = null!;
        if (door.WasCollected) return false;
        if (DoorObservation.Decide(door).Address is not { } resolved) return false;
        address = resolved;
        return true;
    }

    /// <summary>The door a weak lock belongs to, read from the lock's own holder: the real implementation of the
    /// <see cref="WeakLockOwner"/> delegate. A holder that is not a component under a door yields no fact rather
    /// than a lock with an invented owner.</summary>
    internal static LG_SecurityDoor? DoorOwner(LG_WeakLock lockInstance)
    {
        var holder = lockInstance.m_holder;
        if (holder == null) return null;
        var component = holder.TryCast<UnityEngine.Component>();
        return component == null ? null : component.GetComponentInParent<LG_SecurityDoor>();
    }

    /// <summary>Runs one fact callback under this half's own guard: a callback that fails disables the producer
    /// once instead of leaving a broken one running for the rest of the session. It is the rule the package's
    /// other native callbacks follow, kept here because this half owns its own failure and the session does not
    /// know about it.</summary>
    internal void Guard(Action<DoorTerminalFacts> callback)
    {
        if (!_ready() || _faulted) return;
        try { callback(this); }
        catch (Exception error)
        {
            _faulted = true;
            Current = null;
            _report("Door and terminal interaction observation disabled until restart after native callback failure: "
                + error.GetType().Name + ": " + error.Message);
        }
    }
}
