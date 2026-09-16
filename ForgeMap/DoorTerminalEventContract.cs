using System;
using ForgeRuntime.Framework;

namespace ForgeMap;
/// <summary>The door and terminal interaction rows this provider publishes but the catalog does not carry yet.
/// Every row here is a **new** canonical id: the node-list rows that already have a landing place keep it
/// (`e-door-open` → `forge.trigger.interaction.door_state`, `e-door-unlock` → `forge.trigger.interaction.lock_state`,
/// `e-term-cmd`/`e-term-alarm` → `forge.trigger.interaction.terminal_command`), and only the facts no catalog row
/// names are declared here, so nothing is described twice.
///
/// Each row's ports are the facts the native side really carries, and nothing else:
///
/// - `door_approach` is the door's own approach callback. The native evidence for this build is
///   `LevelGeneration.LG_SecurityDoor_Locks.OnApproached` (an `Action`, `LevelGeneration.LG_SecurityDoor_Locks`,
///   `Modules-ASM.dll`) plus `pDoorState.hasBeenApproached` (`[FieldOffset(21)]`, the replicated state that makes
///   the fact a host-visible one). The callback carries no actor, and the port is declared optional for exactly
///   that reason: an absent port is how this framework says "not observable", where a null would claim the actor
///   was observed and happened to be nobody.
/// - `door_scan` is the door's own chained-puzzle lock. The two callbacks the lock component owns —
///   `LG_SecurityDoor_Locks.OnPlayerActivateChainedPuzzle` and `OnChainedPuzzleSolved` — are the game's own
///   notifications that the puzzle on the door was activated and that it was solved, which is exactly the
///   checklist's "扫描开始 / 完成". The phase is not taken from the callback's name alone: the callback runs a
///   check of the puzzle instance's own `IsSolved` / `IsActive` readings, so a solved callback that fires while
///   the puzzle still reads unsolved is reported as the activation it really is. The door status the phase was
///   derived from travels beside the phase, because a plan that wants the native value should be able to read it
///   rather than trust this provider's reading of it.
/// - `lock_broken` is the weak lock's own replicated status: `LevelGeneration.LG_WeakLock.OnStateChange` with
///   `eWeakLockStatus.Unlocked`, or the door-lock component's own `SyncHackSuccess`. The lock kind
///   (`eWeakLockType.Melee` / `Hackable`) is the native member that tells a smashed lock from a hacked one.
///   The subject is the door the lock was spawned on, which is the only identity this provider can address: a
///   weak lock has no stable address of its own.
/// - `door_broken` is the weak door itself — the only door kind the game lets enemies break (`LG_WeakDoor`,
///   `LevelGeneration.LG_WeakDoor`). A weak door is no zone's entrance and carries no map address, so the row
///   carries the facts an author filters and reacts on instead: the door's own runtime entity, the zone it stands
///   in as a `zone` resource, and its world position. The two stages are the two sync callbacks the game's own
///   replication layer raises on the door — `LG_Door_Sync.OnDoorGotDamage` while it is being hit and
///   `LG_Door_Sync.OnDoorGotDestroyed` when it breaks. Both carry the `SNet_Player` that caused the damage, which
///   is the `attacker` port; it is optional because an environmental or enemy hit names no player.
/// - `terminal_log` is one log a terminal read, taken from the command entry that already carries the accepted
///   command and the two parameter strings the terminal's own interpreter produced
///   (`LG_ComputerTerminalCommandInterpreter.TryGetCommand`, 0x1292CB0). `TERM_Command.ReadLog` (29) is what makes
///   a read a read; the log's own name is the interpreter's first parameter, so the port is absent when the
///   command carried none instead of being published as an empty name.
///
/// Every row is `host` execution and publishes only on the authoritative peer, which is the rule the package's
/// existing map-object rows already follow.</summary>
public static class DoorTerminalEventContract
{
    public const string DoorApproachCapability = "forge.trigger.interaction.door_approach";
    public const string DoorScanCapability = "forge.trigger.interaction.door_scan";
    public const string LockBrokenCapability = "forge.trigger.interaction.lock_broken";
    public const string DoorBrokenCapability = "forge.trigger.interaction.door_broken";
    public const string TerminalLogCapability = "forge.trigger.interaction.terminal_log";

    /// <summary>The five fact kinds these rows carry, in the order the module declares them. A binding or a
    /// fact key names one of these, never the capability again, so the two cannot drift apart.</summary>
    public const string DoorApproachFact = "door_approach";
    public const string DoorScanFact = "door_scan";
    public const string LockBrokenFact = "lock_broken";
    public const string DoorBrokenFact = "door_broken";
    public const string TerminalLogFact = "terminal_log";

    /// <summary>The two stages of `forge.trigger.interaction.door_broken`, in checklist order. A weak door is
    /// attacked first and broken second; the two are one author node's two phases rather than two rows, because
    /// the door, the zone and the position are the same three facts in both.</summary>
    public const string AttackedPhase = "attacked";
    public const string BrokenPhase = "broken";

    /// <summary>The domain list every row here names. A door or a terminal is a map object a room, a tool and a
    /// consumable all act on, which is the same set the package's other map-object rows carry.</summary>
    internal static readonly string[] Domains = { "map", "room", "tool", "consumable" };

    /// <summary>The provider-side binding id of one fact: this provider's own namespace plus the fact's own
    /// name, so the counterpart of a row is readable from either side. It is derived here and never spelled out
    /// twice, because a binding id that disagrees with the capability it serves fails registration.</summary>
    public static string Binding(string fact) => ModuleDefinition.ProviderId + ".binding.interaction." + fact;

    public static string Capability(string fact) => fact switch
    {
        DoorApproachFact => DoorApproachCapability,
        DoorScanFact => DoorScanCapability,
        LockBrokenFact => LockBrokenCapability,
        DoorBrokenFact => DoorBrokenCapability,
        TerminalLogFact => TerminalLogCapability,
        _ => throw new RuntimeContractException("door-terminal-event-fact", "Unknown door or terminal event fact.")
    };

    /// <summary>One trigger row: the catalog's own shape — id, owner, kind, label, version, description, and a
    /// graph block with the domains, the host execution and the ports that row really publishes.</summary>
    internal static object Row(string capability, string label, string description, object[] outputs)
        => new
        {
            id = capability, owner = ModuleDefinition.ProviderId, kind = "trigger", label, version = "1.0.0",
            parameters = new { description },
            graph = new { domains = Domains, execution = "host", inputs = Array.Empty<object>(), outputs,
                parameters = Array.Empty<object>() }
        };

    internal static object Port(string id, string type) => new { id, type };
    internal static object Optional(string id, string type) => new { id, type, optional = true };
    internal static object EnumPort(string id, string schema) => new { id, type = "enum", schema };
    internal static object ZonePort(string id) => new { id, type = "resource", resourceKind = "zone", schema = "forge.resource.zone" };
    internal static object OptionalZonePort(string id) => new { id, type = "resource", resourceKind = "zone", schema = "forge.resource.zone", optional = true };
    internal static object PositionPort(string id) => new { id, type = "vector3", unit = "m" };

    /// <summary>The approach row: the door that was approached and, when the callback or the instigator field
    /// names one, the actor. Both an approach and its actor are facts the door's own replicated state carries,
    /// so the row is the door's and not a generic proximity event's.</summary>
    public static object ApproachRow() => Row(DoorApproachCapability, "玩家靠近门", "有人靠近门。", new object[]
    {
        Port("next", "execution"),
        Port("door", "entity"),
        Optional("actor", "entity")
    });

    /// <summary>The scan row: the door whose chained-puzzle lock changed, the phase the status is, and the door
    /// status the phase was derived from. `status` is published as well as `phase`, because the derivation is
    /// this provider's reading of the native status and a plan that wants the native value should be able to
    /// read it rather than trust the derivation.</summary>
    public static object ScanRow() => Row(DoorScanCapability, "门的扫描开始或完成", "门的扫描开始或者完成。", new object[]
    {
        Port("next", "execution"),
        Port("door", "entity"),
        EnumPort("phase", "interaction_phase"),
        Port("status", "string")
    });

    /// <summary>The lock row: the door whose lock was broken, what broke it, and the lock kind when the native
    /// member names one. `cause` is the door's own interaction vocabulary (`eDoorInteractionType.Unlock` for a
    /// hacking success, `DoDamage` for a weak lock that was smashed); `lock_kind` is `eWeakLockType`, and it is
    /// optional because a security door's lock component is not a weak lock and has no such member.</summary>
    public static object LockBrokenRow() => Row(LockBrokenCapability, "门锁被砸开或破解", "门锁被砸开或被破解。", new object[]
    {
        Port("next", "execution"),
        Port("door", "entity"),
        EnumPort("cause", "door_lock_cause"),
        Optional("lock_kind", "string")
    });

    /// <summary>The weak-door row: the two stages of a door enemies can break. The door is this provider's own
    /// `gtfo.weak_door` runtime entity — a weak door is no zone's entrance, so it has no map address and the
    /// entity is the identity an author wires on. The zone is a `zone` resource for the same reason the light and
    /// fog rows name zones: it is what an author filters on. The position is the door's own world position, and
    /// the attacker is the player the replication callback named, when it named one.</summary>
    public static object DoorBrokenRow() => Row(DoorBrokenCapability, "门被攻击或被打破", "门被攻击，或者被打坏。", new object[]
    {
        Port("next", "execution"),
        Port("door", "entity"),
        EnumPort("phase", "door_phase"),
        OptionalZonePort("zone"),
        PositionPort("position"),
        Optional("attacker", "entity")
    });

    /// <summary>The terminal-log row: the terminal that read a log, the log's own name as the terminal's
    /// interpreter named it, and the raw input line the player typed. The line is carried because the name is
    /// the interpreter's reading of it, and a plan that wants what was really typed should not have to
    /// reconstruct it from a parse.</summary>
    public static object TerminalLogRow() => Row(TerminalLogCapability, "读取终端日志", "某个终端日志被读取。", new object[]
    {
        Port("next", "execution"),
        Port("terminal", "entity"),
        Port("log", "string"),
        Optional("line", "string")
    });

    /// <summary>The five rows in the order this module declares them, paired with the fact each one carries.</summary>
    internal static readonly (string Fact, object Row)[] RowTable =
    {
        (DoorApproachFact, ApproachRow()),
        (DoorScanFact, ScanRow()),
        (LockBrokenFact, LockBrokenRow()),
        (DoorBrokenFact, DoorBrokenRow()),
        (TerminalLogFact, TerminalLogRow())
    };

    /// <summary>Every row, in the table's order.</summary>
    public static object[] Rows()
    {
        var rows = new object[RowTable.Length];
        for (int index = 0; index < RowTable.Length; index++) rows[index] = RowTable[index].Row;
        return rows;
    }

    /// <summary>One observe binding row: this provider's own id, the canonical capability, the fact kind the
    /// native callback names, and `observe` as the role — none of these rows has a handler, because every one of
    /// them publishes state the game already replicated instead of acting on it.</summary>
    public static object BindingRow(string fact) => new
    {
        id = Binding(fact), capabilityId = Capability(fact), providerId = ModuleDefinition.ProviderId,
        handler = ModuleDefinition.ProviderId + ".observe." + fact, role = "observe", status = "implemented",
        dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    /// <summary>The five binding rows in the same order as <see cref="Rows"/>.</summary>
    public static object[] Bindings()
    {
        var bindings = new object[RowTable.Length];
        for (int index = 0; index < RowTable.Length; index++) bindings[index] = BindingRow(RowTable[index].Fact);
        return bindings;
    }

    /// <summary>One binding's registration support: reading a door or a terminal at all is what the map-object
    /// namespace's own read permission is, and no row here needs another binding to work.</summary>
    public static BindingSupport Support(string fact)
        => new(Binding(fact), "implementation-only", new[] { MapObjectContract.MapObjectReadPermission });

    /// <summary>The five registration rows in the same order as <see cref="Bindings"/>.</summary>
    public static BindingSupport[] Supports()
    {
        var support = new BindingSupport[RowTable.Length];
        for (int index = 0; index < RowTable.Length; index++) support[index] = Support(RowTable[index].Fact);
        return support;
    }

    /// <summary>The `door_lock_cause` vocabulary this row's enum port carries, in member order, because the port
    /// publishes the member index and not the name. Three members, one per native interaction this provider can
    /// observe release a lock: the door's own unlock interaction, a successful hack, and a weak lock that was
    /// destroyed. The set is declared here and offered to the catalog; if the website already carries a
    /// `door_lock_cause` set, the integration batch aligns the two spellings instead of keeping a second one.</summary>
    public static readonly string[] LockCauses = { "unlocked", "hacked", "smashed" };

    /// <summary>The member index of one lock cause in <see cref="LockCauses"/>, or -1 for a cause outside the
    /// set, which is a programming error rather than a value a native read can produce.</summary>
    public static int LockCauseIndex(string cause)
    {
        for (int index = 0; index < LockCauses.Length; index++)
            if (string.Equals(LockCauses[index], cause, StringComparison.Ordinal)) return index;
        return -1;
    }

    /// <summary>The `door_phase` vocabulary the weak-door row's enum port carries, in member order. It is its own
    /// set because `interaction_phase` is the door's own interaction vocabulary (`requested`..`failed`) and this
    /// row's two stages are not members of it: a door under attack is not a requested interaction, and a door that
    /// broke is not a failed one. The set is declared here and registered with the framework and the catalog by
    /// the integration batch, exactly as the lock-cause set above is.</summary>
    public static readonly string[] DoorPhases = { AttackedPhase, BrokenPhase };

    /// <summary>The member index of one weak-door phase in <see cref="DoorPhases"/>, or -1 for a value outside
    /// the set.</summary>
    public static int DoorPhaseIndex(string phase)
    {
        for (int index = 0; index < DoorPhases.Length; index++)
            if (string.Equals(DoorPhases[index], phase, StringComparison.Ordinal)) return index;
        return -1;
    }
}
