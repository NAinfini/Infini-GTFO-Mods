using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeMap;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>One door state transition as this provider reads it out of the door's own `eDoorStatus`. The enum is
/// the vocabulary of the facts the door and terminal rows publish, and it exists so the derivation is written
/// once: the door's own lock/state callback fires for every status write, and each fact row is one transition
/// out of that stream.
///
/// The mapping is the whole derivation and is deliberately not a re-spelling of the status enum:
///
/// - `Destroyed` (11) is the door broken, which is `e-door-broken`'s second stage on a security door.
/// - `Unlocked` (9) is the door's lock released, which is `e-door-unlock` and the value row's `open`/`locked`
///   question. Which lock released it is the lock component's own answer, not this enum's.
/// - `Open` (10) and `Opening` (16) are the door open, which `e-door-open` and the `v-door` value are read from.
/// - Every other status is a state the door holds rather than a transition this provider reports, so it maps to
///   no event at all instead of to the nearest one.
///
/// The two members a scan used to be classified by are gone: `e-door-scan` is driven by the lock component's own
/// chained-puzzle callbacks and by the puzzle instance's own solved reading, which is written in
/// <see cref="DoorScanStage"/> and <see cref="DoorTerminalDerivations.ScanPhase"/>. A status is not the scan's
/// authority — the callback carries no status at all — so keeping a status-shaped scan classification here would
/// have been a second, weaker answer to the same question.</summary>
internal enum DoorTerminalDoorEvent
{
    None,
    Approach,
    Broken,
    Unlocked,
    Opened
}

/// <summary>The two things a door's chained-puzzle lock can report, as the callback that carried the report. It
/// is deliberately not the phase: the callback names which of the lock's two notifications ran, while the phase
/// the row publishes is decided from the puzzle instance's own reading (see
/// <see cref="DoorTerminalDerivations.ScanPhase"/>), because a "solved" notification that fires while the puzzle
/// still reads unsolved is an activation.</summary>
internal enum DoorScanStage
{
    /// <summary>`LG_SecurityDoor_Locks.OnPlayerActivateChainedPuzzle` ran: the puzzle on the door was activated.</summary>
    Activated,
    /// <summary>`LG_SecurityDoor_Locks.OnChainedPuzzleSolved` ran: the puzzle on the door reported solved.</summary>
    Solved
}

/// <summary>The door and terminal interaction derivations, and nothing else: every method here is a pure reading
/// of values the native side produced. The native callbacks in `Native/DoorTerminalFacts.cs` do the reading and
/// hand the values in, which is what makes the whole derivation reachable from a test without a game — and what
/// keeps a callback and a row from describing the same transition two different ways.
///
/// The status numbers are `LevelGeneration.eDoorStatus` as this build declares it (`Modules-ASM.dll`,
/// `eDoorStatus`: `None`=0, `Closed`=1, `Closed_BrokenCantOpen`=2, `Closed_LockedWithKeyItem`=3,
/// `Closed_LockedWithChainedPuzzle_Alarm`=4, `Closed_LockedWithChainedPuzzle`=5,
/// `Closed_LockedWithPowerGenerator`=6, `Closed_LockedWithNoKey`=7, `ChainedPuzzleActivated`=8, `Unlocked`=9,
/// `Open`=10, `Destroyed`=11, `GluedMax`=12, `TryOpenStuckInGlue`=13, `TryOpenStuckBroken`=14,
/// `Closed_LockedWithBulkheadDC`=15, `Opening`=16). The status names come from `MapObjectDoorStatus.Name`, which
/// is this package's single spelling of the same enum.</summary>
internal static class DoorTerminalDerivations
{
    /// <summary>The five unique-command slots the map editor offers per terminal. The native command enum has
    /// exactly five `UniqueCommand*` members, which is the whole reason the count is five: the slots are the
    /// game's own, not a Forge table.</summary>
    internal const int UniqueCommandSlots = 5;

    /// <summary>The `TERM_Command` members this derivation names, as the byte values the native enum declares
    /// them with. They are written as the enum's own indices because the values are what a hook receives.</summary>
    internal const int CommandReadLog = 29;
    internal const int CommandUniqueSlot1 = 38;
    internal const int CommandUniqueSlot5 = 42;

    /// <summary>The one command a terminal's own auto-complete offers that is not a member of `TERM_Command`:
    /// the entry the player may hide, which the docs and the terminal's autocomplete list spell this way.</summary>
    internal const string UsedCommandName = "used";

    /// <summary>The transition one status is, or `None` for a status that is a held state rather than a
    /// transition. This is the only place a status is classified.</summary>
    internal static DoorTerminalDoorEvent EventOf(int status) => status switch
    {
        9 => DoorTerminalDoorEvent.Unlocked,
        10 or 16 => DoorTerminalDoorEvent.Opened,
        11 => DoorTerminalDoorEvent.Broken,
        _ => DoorTerminalDoorEvent.None
    };

    /// <summary>The `interaction_phase` member index a scan callback carries. The puzzle's own solved reading is
    /// the authority when the lock could produce one: a callback that ran while the puzzle reads solved is the
    /// completion and one that ran while it does not is the activation, whatever the callback was named. The
    /// indices are the framework's own enum order (`interaction_phase`: requested, started, completed, cancelled,
    /// failed), which is what `MapObjectPhases` spells in the same order.
    ///
    /// The one asymmetry is deliberate: `IsSolved` has no "unsolved because it did not start" reading, so a
    /// callback that named the completion and found the puzzle unsolved is reported as the activation this
    /// reading really saw, rather than as a completion nothing confirmed.</summary>
    internal static int ScanPhase(DoorScanStage stage, bool? puzzleSolved) => puzzleSolved switch
    {
        true => MapObjectPhases.Completed,
        false => MapObjectPhases.Started,
        null => stage == DoorScanStage.Solved ? MapObjectPhases.Completed : MapObjectPhases.Started
    };

    /// <summary>The `door_lock_cause` member name for a lock release. A weak lock that was destroyed by melee is
    /// `smashed`; a successful hack is `hacked`; the door's own unlock interaction is `unlocked`. That is the
    /// distinction `eWeakLockType` (`Melee` / `Hackable`) and `LG_SecurityDoor_Locks.SyncHackSuccess` give
    /// natively.</summary>
    internal static string LockCause(bool smashed, bool hacked)
        => smashed ? "smashed" : hacked ? "hacked" : "unlocked";

    /// <summary>Whether a native command read a terminal log. `TERM_Command.ReadLog` is what the terminal's own
    /// interpreter produces for a typed read, so a log read is one command value rather than a member the
    /// terminal would have to be watched for.</summary>
    internal static bool IsLogRead(int command) => command == CommandReadLog;

    /// <summary>The custom-command slot a native command is, one-based, or 0 for a command that is not one of
    /// the five slots. This is the slot `e-term-cmd` asks a plan to be able to tell apart, and it is the game's
    /// own member order rather than a Forge table.</summary>
    internal static int UniqueSlot(int command)
        => command >= CommandUniqueSlot1 && command <= CommandUniqueSlot5
            ? command - CommandUniqueSlot1 + 1
            : 0;

    /// <summary>The log's own name out of the interpreter's first parameter string. The interpreter produces the
    /// two parameter strings and the command entry carries them; a read that named no log publishes no log name
    /// instead of an empty one, which is why this answers null rather than "".</summary>
    internal static string? LogName(string? param1)
        => string.IsNullOrWhiteSpace(param1) ? null : param1;

    /// <summary>The raw input line a plan may want back: the line the terminal's interpreter parsed. Blank is
    /// absence, the same rule the log name follows.</summary>
    internal static string? InputLine(string? line)
        => string.IsNullOrWhiteSpace(line) ? null : line;

    // ---- the weak door -----------------------------------------------------------------------------

    /// <summary>One weak door's runtime entity: it is a `gtfo.map_object` of the `door` category, exactly like
    /// the entrance gate the door actions open, close and lock. A weak door is not a zone's entrance — several of
    /// them stand in one zone — so its address carries its own serial instead of the entrance token, which is
    /// what makes it the one identity an author can wire both the door actions and this fact to. The address is
    /// built by the category's own factory, so a reference of this half can never spell an address the door
    /// reader would refuse.</summary>
    internal static EntityReference? WeakDoorReference(int? dimension, int? layer, int? zone, int? serial, long world)
        => MapObjectDoorAddress.CreateWeak(dimension, layer, zone, serial) is { } address
            ? new EntityReference(MapObjectModule.EntityKind + ":" + address, world, 1)
            : null;

    /// <summary>The serial one weak door carries inside its zone. The game assigns one instance id per object
    /// inside one level and this is it, which is the same per-object identity `LevelObjectObservation` keys its
    /// containers and level items by. A missing or destroyed object has none, and a door with no serial is
    /// published under no reference rather than under a shared one.</summary>
    internal static int? WeakDoorSerial(LG_WeakDoor door)
    {
        if (door == null || door.WasCollected) return null;
        var gameObject = door.gameObject;
        if (gameObject == null || gameObject.WasCollected) return null;
        int serial = gameObject.GetInstanceID();
        return serial < 0 ? null : serial;
    }

    /// <summary>One object's own world position, as the three finite numbers a `vector3` port carries, or null
    /// when the transform or a coordinate does not read. A snapshot carries three real numbers, so this answers
    /// no position rather than a substituted origin.</summary>
    internal static double[]? Position(UnityEngine.Transform? transform)
    {
        if (transform == null || transform.WasCollected) return null;
        var position = transform.position;
        return float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
            ? new[] { (double)position.x, (double)position.y, (double)position.z } : null;
    }

    // ---- the terminal command row ------------------------------------------------------------------

    /// <summary>Whether one command is the command an author named by slot, and which slot. Both spellings the
    /// row accepts meet here: a `unique1`..`unique5` name and the game's own one-based slot number are the same
    /// command, and a request that named neither a slot nor a command resolves to nothing rather than to the
    /// first command of the terminal.</summary>
    internal static int CommandSlot(string? commandText, int? slot)
    {
        if (slot is { } declared) return declared is >= 1 and <= UniqueCommandSlots ? declared : 0;
        if (commandText == null) return 0;
        var text = commandText.Trim();
        const string prefix = "unique";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        int suffix = text.Length > prefix.Length && int.TryParse(text[prefix.Length..], NumberStyles.None,
            CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        return suffix is >= 1 and <= UniqueCommandSlots ? suffix : 0;
    }

    /// <summary>The command one name or slot names, or null when nothing names a command the terminal's own enum
    /// has. A slot is the game's own `UniqueCommand1`..`UniqueCommand5`; a name is a member of `TERM_Command`
    /// matched without case, plus the one entry the terminal's auto-complete offers that is not an enum member.
    /// An unknown name yields null, which the caller reports by name instead of defaulting to a nearby command.
    /// </summary>
    internal static TERM_Command? ResolveCommand(string? commandText, int? slot)
    {
        int number = CommandSlot(commandText, slot);
        if (number > 0) return (TERM_Command)(byte)(CommandUniqueSlot1 + number - 1);
        if (string.IsNullOrWhiteSpace(commandText)) return null;
        var text = commandText.Trim();
        if (string.Equals(text, UsedCommandName, StringComparison.OrdinalIgnoreCase)) return TERM_Command.UsedCommand;
        return Enum.TryParse<TERM_Command>(text, ignoreCase: true, out var command)
            && Enum.IsDefined(typeof(TERM_Command), command) ? command : null;
    }

    /// <summary>One classification of a command against the state a request asked for: `already` means the
    /// command is already in it, `change` means the terminal has to be asked, and `command` is what the caller
    /// reports when the request named none. `hidden` is the terminal's own `CommandIsHidden`, which is the same
    /// reading `pComputerTerminalState.RemovedCommands` holds and the one the synced setter writes.</summary>
    internal static CommandVisibility Visibility(bool hidden, bool wanted) => hidden == wanted
        ? CommandVisibility.Already
        : CommandVisibility.Change;
}

/// <summary>What a command's visibility request turned out to be: the terminal is already in the asked-for
/// state, or it has to be told. Two members and no third, because "the command does not exist" is decided
/// before this question is asked.</summary>
internal enum CommandVisibility
{
    Already,
    Change
}

/// <summary>The per-world state a fact stream needs and nothing else: which state of which object was published
/// last, so a native callback that fires twice for one state publishes one fact. The key is the fact kind and
/// the object's own address, so two facts about one door cannot collide, and the state key is the value the fact
/// carries — a repeated sync produces the same key and publishes nothing while reporting the transition it was
/// published with.
///
/// The table holds no native reference: a world change invalidates every address it could name, so holding the
/// keys would only hold dead text, and the owner clears it on a world transition.</summary>
internal sealed class DoorTerminalFactLedger
{
    private readonly Dictionary<string, (string StateKey, long Transition)> _published = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Binding, string EventId, string StateKey, string Outputs)> _last = new(StringComparer.Ordinal);
    private long _count;

    /// <summary>Whether one state is new. A state that was already published under this key answers false and
    /// reports the transition it was published with; a new one consumes exactly one transition.</summary>
    internal bool Observe(string key, string stateKey, out long transition)
    {
        if (_published.TryGetValue(key, out var last) && string.Equals(last.StateKey, stateKey, StringComparison.Ordinal))
        {
            transition = last.Transition;
            return false;
        }
        transition = ++_count;
        _published[key] = (stateKey, transition);
        return true;
    }

    /// <summary>Remembers what one queued state carried, keyed the way <see cref="Observe"/> keys its own table.
    /// It answers "what did this fact publish when it did" without an event sink, which is what a focused case
    /// needs and nothing more; the payload text is the JSON the kernel was handed.</summary>
    internal void Remember(string key, string binding, string eventId, string stateKey, string outputs)
        => _last[key] = (binding, eventId, stateKey, outputs);

    /// <summary>The last queued state of one key, or null when nothing was queued for it.</summary>
    internal (string Binding, string EventId, string StateKey, string Outputs)? Last(string key)
        => _last.TryGetValue(key, out var last) ? last : null;

    /// <summary>Drops every key. Called on a world transition, where no address survives.</summary>
    internal void Clear()
    {
        _published.Clear();
        _last.Clear();
        _count = 0;
    }

    /// <summary>How many states this ledger currently remembers, for a test to assert a world transition really
    /// released them.</summary>
    internal int Count => _published.Count;
}
