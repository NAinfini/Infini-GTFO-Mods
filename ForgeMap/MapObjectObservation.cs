using System;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The map-object categories this provider addresses. A category is a field of an address and of an
/// observation, never a second entity namespace: one `gtfo.map_object` namespace holds every map object and the
/// category travels with the object. The spelling is the shared `MapObjectKind` value's own name in lower case,
/// so an address, the shared `MapObjectAddress` record and a plan's `map-object` attachment category all name
/// one kind the same way.</summary>
public static class MapObjectCategories
{
    public static readonly string Door = Name(MapObjectKind.Door);
    public static readonly string Terminal = Name(MapObjectKind.Terminal);

    private static string Name(MapObjectKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary>Whether a key segment is one a category writes. Each category owns its key grammar, because the
/// two keys are different kinds of name: a door's is the fixed role token `security`, a terminal's is a
/// zero-based placement index.</summary>
public delegate bool MapObjectKeyIsValid(string key);

/// <summary>
/// One map object's address, built only from keys the authoring side can derive before the level exists — this
/// is the format the plan `attachments[]` `map-object` reference and the website use, so both sides agree on
/// one spelling.
///
///   &lt;category&gt;/&lt;dimension&gt;/&lt;layer&gt;/&lt;zone&gt;/&lt;key&gt;
///
/// `category` is the lower-case name of a `MapObjectKind` value. `dimension`, `layer` and `zone` are the
/// coordinates of the zone the object belongs to, as native values: `dimension` is `LG_Zone.m_dimensionIndex`,
/// `layer` is `LG_Zone.m_layer.m_type`'s own ordinal (main/secondary/third), and `zone` is
/// `LG_Zone.LocalIndex`. Every coordinate is a decimal written without a leading zero; a coordinate that did
/// not read yields no address at all, so an address that exists always names a real zone.
///
/// `key` names the object inside that zone. A door carries the closed vocabulary token `security`: the zone's
/// entrance gate, which is at most one per zone because a zone has exactly one entrance. A terminal carries
/// the zero-based index of its placement in the zone's own terminal list, so several terminals in one zone are
/// distinct without any id the level builder hands out.
///
/// None of these keys is a runtime identifier: the door mapper id and the terminal sync id are assigned while
/// the level is being built, which is why neither is part of an address any more.
///
/// Only the category's own address factory builds one, and a factory refuses to build an address from keys
/// that do not read, so an address that exists always names an object.
///
/// An address never carries a native instance: reading an instance's address re-reads these keys from that
/// instance, so an id built for one address can never answer for another, and a level change invalidates an
/// earlier id through the kernel's world epoch instead of through a table this provider would clear.
/// </summary>
public sealed record MapObjectReference(string Category, string Dimension, string Layer, string Zone,
    string Key)
{
    public override string ToString() => Category + "/" + Dimension + "/" + Layer + "/" + Zone + "/" + Key;

    /// <summary>Reads an address of one expected category back, with the key grammar that category's own
    /// address factory writes. A five-segment address of that category is this provider's; every other string —
    /// another category, another shape, a coordinate that is not a plain decimal, a key the category does not
    /// use — is not, so a plan attachment or an entity id that something else built is refused rather than
    /// reinterpreted.</summary>
    public static MapObjectReference? TryParse(string? text, string expectedCategory, MapObjectKeyIsValid keyIsValid)
    {
        if (text == null) return null;
        var parts = text.Split('/');
        if (parts.Length != 5 || parts[0] != expectedCategory || !keyIsValid(parts[4])) return null;
        for (var i = 1; i != 4; i++) if (!IsCoordinate(parts[i])) return null;
        return new MapObjectReference(parts[0], parts[1], parts[2], parts[3], parts[4]);
    }

    /// <summary>A decimal coordinate written without a leading zero. The parser stops at the first non-digit,
    /// so a leading zero, a sign, a blank coordinate and a trailing non-digit all leave the text unparsed.
    /// The categories share it, because every coordinate segment of every address is written the same way.</summary>
    internal static bool IsCoordinate(string text)
    {
        int value = 0, index = 0;
        for (; index != text.Length; index++)
        {
            char digit = text[index];
            if (digit is < '0' or > '9') break;
            if (index == 0 && digit == '0') { index++; break; }
            value = checked(value * 10 + (digit - '0'));
        }
        if (index != text.Length || text.Length == 0) return false;
        return value >= 0;
    }
}

/// <summary>The address of one door: the coordinates of the zone it guards, and the fixed key its own zone
/// role gives it. A door the game's own level generation did not make the entrance gate of a zone has no
/// address at all — a weak door, a node door and a decorative door carry no zone identity, and a door that
/// only moves a player between layers is layer data rather than a zone entrance.</summary>
public static class MapObjectDoorAddress
{
    public static readonly string Category = MapObjectCategories.Door;

    /// <summary>The one door key there is: a zone's entrance gate. A zone is entered through exactly one gate,
    /// so the entrance does not need a number, and a zone with no entrance gate has no `security` door.</summary>
    public const string Security = "security";

    /// <summary>The prefix of the key a weak door carries. A weak door is a door of the same `door` category —
    /// it is opened, closed and locked through the same door actions, so it is the same kind of object and an
    /// author wires one identity — but it is not a zone's entrance gate, so it cannot carry the `security` token:
    /// several weak doors stand in one zone and the entrance key names exactly one object. Its own per-instance
    /// serial follows the prefix, which is the one ordinal the level already assigns per object.</summary>
    public const string WeakPrefix = "weak";

    /// <summary>Reads a door address: this category, three coordinates and either the fixed `security` key or a
    /// `weak<serial>` key.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, IsDoorKey);

    /// <summary>Whether a key is one this category writes: the entrance token, or a weak door's serial.</summary>
    public static bool IsDoorKey(string key) => key == Security || ParseWeakSerial(key) != null;

    /// <summary>Reads a door address of the weak kind alone, or null when the text names an entrance gate, no
    /// door at all, or a key this category does not write. The door observation's own reader asks this, because a
    /// weak door is not the entrance of the zone it stands in and resolving its reference through the entrance
    /// table would answer with a door it is not.</summary>
    public static MapObjectReference? TryParseWeak(string? text)
        => TryParse(text) is { } address && ParseWeakSerial(address.Key) != null ? address : null;

    public static MapObjectReference? Create(int? dimension, int? layer, int? zone)
        => dimension == null || layer == null || zone == null
            ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                Security);

    /// <summary>The address of one weak door: the coordinates of the zone it stands in and its own serial inside
    /// that zone. The three coordinates are the same text an entrance address writes, so the zone an author
    /// filters on and the zone a weak door reports are one spelling.</summary>
    public static MapObjectReference? CreateWeak(int? dimension, int? layer, int? zone, int? serial)
        => dimension == null || layer == null || zone == null || serial == null || serial < 0
            ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                WeakKey(serial.Value));

    /// <summary>The serial one weak door's key carries, or null when the key is not that shape. A key with a
    /// leading zero, a sign, no digits or a second `weak` segment is refused rather than reinterpreted.</summary>
    public static int? ParseWeakSerial(string key)
        => key != null && key.StartsWith(WeakPrefix, StringComparison.Ordinal)
            && MapObjectReference.IsCoordinate(key[WeakPrefix.Length..])
            && int.TryParse(key[WeakPrefix.Length..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int serial)
            ? serial : null;

    private static string WeakKey(int serial) => WeakPrefix + serial.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Coordinate(int? value)
        => value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The address of one terminal: the coordinates of the zone it stands in, and its own zero-based index
/// in that zone's terminal list. The index is the placement's position, which is the one ordinal the authoring
/// side already fixes by ordering the zone's placements.</summary>
public static class MapObjectTerminalAddress
{
    public static readonly string Category = MapObjectCategories.Terminal;

    /// <summary>Reads a terminal address: this category, three coordinates and a placement index that is a
    /// coordinate like the others.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, MapObjectReference.IsCoordinate);

    public static MapObjectReference? Create(int? dimension, int? layer, int? zone, int? placementIndex)
        => dimension == null || layer == null || zone == null || placementIndex == null
            ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                Coordinate(placementIndex));

    private static string Coordinate(int? value)
        => value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The address of one power generator: the coordinates of the zone it stands in and the level's own
/// serial for it — the same five segments a door and a terminal are addressed by (ruling 130.1). It is declared
/// here, with the other two categories' addresses, because this is where the grammar (`MapObjectReference`)
/// lives.
///
/// A generator is a level-generated object and carries no authoring-time ordinal of its own — the level assigns
/// each one its serial while it builds the map — so unlike a door's fixed `security` token or a terminal's
/// placement index this key is a *runtime* value. The address is buildable and reversible at run time, which is
/// what the website's host list needs in order to offer the generator as a host; it is not a text an author can
/// write before the level exists.
///
/// The category carries a second key form, exactly as the door category carries its `weak<serial>` one:
/// `group<serial>` names the generator *group* — `LevelGeneration.LG_PowerGeneratorCluster` — whose members the
/// group fact reports. A group is not an authoring-time object either, and a key form of its own rather than a
/// second category is what keeps one host (`generator`) and one address namespace while a group and a generator
/// that happen to carry the same serial stay two addresses.</summary>
public static class MapObjectGeneratorAddress
{
    /// <summary>The category every generator address carries. It is a map-object category: a generator is one
    /// more kind of object the level places, addressed like a door and a terminal.</summary>
    public static readonly string Category = "generator";

    /// <summary>The prefix of the key a generator group carries.</summary>
    public const string GroupPrefix = "group";

    /// <summary>Reads a generator address: this category, three zone coordinates and either the level's own
    /// serial for one generator or a `group&lt;serial&gt;` key for one group, each written as a decimal
    /// coordinate. Any other text — another category, another segment count, a coordinate with a leading zero —
    /// is not one of this category's addresses.</summary>
    public static MapObjectReference? TryParse(string? text)
        => MapObjectReference.TryParse(text, Category, IsGeneratorKey);

    /// <summary>Whether a key is one this category writes: one generator's serial, or a group's own key.</summary>
    public static bool IsGeneratorKey(string key)
        => MapObjectReference.IsCoordinate(key) || ParseGroupSerial(key) != null;

    /// <summary>The serial one group's key carries, or null when the key is not that shape. A key with a leading
    /// zero, a sign, no digits or a second `group` segment is refused rather than reinterpreted.</summary>
    public static int? ParseGroupSerial(string? key)
        => key != null && key.StartsWith(GroupPrefix, StringComparison.Ordinal)
            && MapObjectReference.IsCoordinate(key[GroupPrefix.Length..])
            && int.TryParse(key[GroupPrefix.Length..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int serial)
            ? serial : null;

    /// <summary>Whether one key names a group rather than a single generator.</summary>
    public static bool IsGroupKey(string key) => ParseGroupSerial(key) != null;

    /// <summary>The address of one generator: the coordinates of the zone it stands in and the level's own
    /// serial for it. A coordinate or a serial that did not read yields no address at all.</summary>
    public static MapObjectReference? Create(int? dimension, int? layer, int? zone, int? serial)
        => dimension == null || layer == null || zone == null || serial == null || serial < 0
            ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                Coordinate(serial));

    /// <summary>The address of one generator group: the coordinates of the zone its members stand in and its own
    /// serial, carried under the group key form so a group and a generator of one zone never share a text.</summary>
    public static MapObjectReference? CreateGroup(int? dimension, int? layer, int? zone, int? serial)
        => dimension == null || layer == null || zone == null || serial == null || serial < 0
            ? null
            : new MapObjectReference(Category, Coordinate(dimension), Coordinate(layer), Coordinate(zone),
                GroupPrefix + Coordinate(serial));

    private static string Coordinate(int? value)
        => value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The native `eDoorStatus` as this provider publishes it: the reader never synthesizes a state, so
/// every value of the enum has a name and an unknown value is refused instead of reported as closed.
/// `interaction_phase` classifies only the statuses that are an interaction transition; a status that is not
/// one carries null rather than a phase the door was never in.</summary>
public static class MapObjectDoorStatus
{
    /// <summary>Whether the native status is a locked state. A door whose own status says it is locked is
    /// locked whatever component put it there.</summary>
    public static bool IsLocked(int status) => status is 3 or 4 or 5 or 6 or 7 or 15;

    /// <summary>Whether the door's own status is the glue state it is held in
    /// (`eDoorStatus.GluedMax`, dump.cs:688678). Glue is an obstruction the door itself reports, not a lock: a
    /// plan that wants to know whether the way is blocked reads this rather than inferring it from `closed`.</summary>
    public static bool IsGlued(int status) => status == 12;

    /// <summary>Whether the door's own status is one of the two states where an attempt to open it stuck — in
    /// glue (`TryOpenStuckInGlue`) or against something broken (`TryOpenStuckBroken`), dump.cs:688679-688680. Both
    /// are the door failing to move rather than a state it settled in.</summary>
    public static bool IsStuck(int status) => status is 13 or 14;

    public static string Name(int status) => status switch
    {
        0 => "none",
        1 => "closed",
        2 => "closed_broken_cant_open",
        3 => "closed_locked_with_key_item",
        4 => "closed_locked_with_chained_puzzle_alarm",
        5 => "closed_locked_with_chained_puzzle",
        6 => "closed_locked_with_power_generator",
        7 => "closed_locked_with_no_key",
        8 => "chained_puzzle_activated",
        9 => "unlocked",
        10 => "open",
        11 => "destroyed",
        12 => "glued_max",
        13 => "try_open_stuck_in_glue",
        14 => "try_open_stuck_broken",
        15 => "closed_locked_with_bulkhead_dc",
        16 => "opening",
        _ => throw new RuntimeContractException("map-object-door-status", "Unknown door status value.")
    };

    /// <summary>The `interaction_phase` member the status itself is, as the wire index the port carries, or
    /// null for a status that is not an interaction transition. Locked statuses are the state a lock holds the
    /// door in, not an interaction this callback performed, so they carry no phase either.</summary>
    public static int? Phase(int status) => status switch
    {
        8 => MapObjectPhases.Requested,
        16 => MapObjectPhases.Started,
        2 or 9 or 10 or 11 or 12 or 13 or 14 => MapObjectPhases.Completed,
        _ => null
    };
}

/// <summary>The framework's `interaction_phase` members in the order the enum set declares them: an enum port
/// carries the member index, never the name, so this is the vocabulary the `phase` output publishes.</summary>
public static class MapObjectPhases
{
    public const int Requested = 0, Started = 1, Completed = 2, Cancelled = 3, Failed = 4;

    public static string Name(int phase) => phase switch
    {
        Requested => "requested",
        Started => "started",
        Completed => "completed",
        Cancelled => "cancelled",
        Failed => "failed",
        _ => throw new RuntimeContractException("map-object-phase", "Unknown interaction phase index.")
    };
}

/// <summary>The framework's `execution_outcome` members in the order the enum set declares them, for the same
/// reason: the `outcome` output is a member index.</summary>
public static class MapObjectOutcomes
{
    public const int Succeeded = 0, Partial = 1, Rejected = 2, Failed = 3, Cancelled = 4, Expired = 5;

    public static string Name(int outcome) => outcome switch
    {
        Succeeded => "succeeded",
        Partial => "partial",
        Rejected => "rejected",
        Failed => "failed",
        Cancelled => "cancelled",
        Expired => "expired",
        _ => throw new RuntimeContractException("map-object-outcome", "Unknown execution outcome index.")
    };
}

/// <summary>The native `TERM_State` as this provider publishes it, with the two facts the catalog asks a
/// terminal for: whether a player is at the terminal, and what the terminal's own state means as an outcome.</summary>
public static class MapObjectTerminalState
{
    public static string Name(int state) => state switch
    {
        0 => "sleeping",
        1 => "awake",
        2 => "player_interacting",
        3 => "data_mining",
        4 => "hacked",
        5 => "code_puzzle",
        6 => "input_test",
        7 => "reactor_error",
        8 => "ask_to_play_log_audio",
        9 => "do_play_audio_file",
        10 => "audio_loop_error",
        11 => "ping",
        12 => "password_protected",
        13 => "enter_password",
        _ => throw new RuntimeContractException("map-object-terminal-state", "Unknown terminal state value.")
    };

    /// <summary>True exactly for the one state in which a player sits at the terminal; every other state,
    /// including the awake and idle states, is "no session".</summary>
    public static bool SessionActive(int state) => state == 2;

    /// <summary>The `execution_outcome` member a terminal state is, as the wire index the port carries, or null
    /// for a state that is not an outcome. Only the states a completed command leaves behind are an outcome: the
    /// two error states are a failed command, the mined and hacked states are a succeeded one, and every other
    /// state — the session, and the states that ask the player for more input — is a prompt, not a result.</summary>
    public static int? Outcome(int state) => state switch
    {
        3 or 4 => MapObjectOutcomes.Succeeded,
        7 or 10 => MapObjectOutcomes.Failed,
        _ => null
    };
}

/// <summary>The native command enum's own name in this provider's vocabulary: `TERM_Command` is already the
/// accepted command, so `ViewSecurityLog` is published as `view_security_log` rather than as a number or a
/// second spelling a plan would have to match.</summary>
public static class MapObjectTerminalCommand
{
    public static string Name(int command) => command switch
    {
        0 => "none",
        1 => "help",
        2 => "commands",
        3 => "cls",
        4 => "exit",
        5 => "open",
        6 => "close",
        7 => "activate",
        8 => "deactivate",
        9 => "empty_line",
        10 => "invalid_command",
        11 => "download_data",
        12 => "view_security_log",
        13 => "override",
        14 => "disable_alarm",
        15 => "locate",
        16 => "activate_beacon",
        17 => "find",
        18 => "show_list",
        19 => "query",
        20 => "ping",
        21 => "reactor_startup",
        22 => "reactor_verify",
        23 => "reactor_shutdown",
        24 => "warden_objective_special_command",
        25 => "terminal_uplink_connect",
        26 => "terminal_uplink_verify",
        27 => "terminal_uplink_confirm",
        28 => "list_logs",
        29 => "read_log",
        30 => "start",
        31 => "try_unlocking_terminal",
        32 => "warden_objective_gather_command",
        33 => "terminal_corrupted_uplink_connect",
        34 => "terminal_corrupted_uplink_verify",
        35 => "timed_connection_send",
        36 => "timed_connection_verify",
        37 => "used_command",
        38 => "unique_command_1",
        39 => "unique_command_2",
        40 => "unique_command_3",
        41 => "unique_command_4",
        42 => "unique_command_5",
        43 => "info",
        _ => throw new RuntimeContractException("map-object-terminal-command", "Unknown terminal command value.")
    };
}

/// <summary>A map object re-read in one place, from the native members and nothing else. The module publishes
/// only values a read produced, and a read whose address stopped matching belongs to no publication at all.
/// Door, terminal, generator and group members are separate sections rather than one state string, so a hook never
/// computes a fact the reader would compute again; a zone is the authored record itself, because a trigger zone
/// is a map object the package declares rather than one the level generated. Every section after the first two is
/// optional, which is what keeps a reader that has no zone from restating the record.</summary>
public sealed record MapObjectObservation(bool AddressMatches, MapObjectDoorSnapshot? Door,
    MapObjectTerminalSnapshot? Terminal, TriggerZone? Zone = null, MapObjectGeneratorSnapshot? Generator = null,
    MapObjectGeneratorGroupSnapshot? Group = null);

/// <summary>One door as its own native members report it: its status, whether it is locked, and the key its
/// own lock component currently requires. The key is read from the gate key item the lock holds — the
/// component type the door lock was set up with — never inferred from the status name.</summary>
public sealed record MapObjectDoorSnapshot(string State, int Status, int? Phase, bool Locked, string Key)
{
    /// <summary>The state identity a publication is deduplicated by: the status and the lock reading
    /// together, so a repeated sync of one state never publishes twice while a lock change under an unchanged
    /// status still does.</summary>
    public string StateKey => State + "|" + (Locked ? "locked" : "unlocked") + "|" + Key;
}

/// <summary>One terminal as its own native members report it: its state name, whether a player is at it, and
/// the outcome its state is. Nothing here is synthesized from the callback's parameters.</summary>
public sealed record MapObjectTerminalSnapshot(string State, int Status, bool SessionActive, int? Outcome)
{
    public string StateKey => State + "|" + (SessionActive ? "session" : "idle");
}

/// <summary>One generator as its own native members report it: whether it holds a cell, and the counts of the
/// group it stands in. The powered reading is the generator's own replicated status — the game has exactly two,
/// `Powered` and `UnPowered` — and the counts are the derivation the group fact publishes: how many members of
/// the owning group read `Powered` against how many the group holds. A generator that stands under no group
/// reads `(0, 0)`, which the module publishes as "one of one" rather than as a completed group.</summary>
public sealed record MapObjectGeneratorSnapshot(bool Powered, int Connected, int Total)
{
    /// <summary>The state identity a publication is deduplicated by: the direction the cell change went, so a
    /// repeated native sync of one state never publishes twice while a second insert/remove still does.</summary>
    public string StateKey => Powered ? "cell:powered" : "cell:unpowered";
}

/// <summary>One generator group as its own members report it: how many of them read `Powered` and how many the
/// group holds. The group's own native state carries a fog step and no count, so the counts are the derivation
/// and the module publishes the group fact only when every member is powered.</summary>
public sealed record MapObjectGeneratorGroupSnapshot(int Connected, int Total)
{
    public string StateKey => "group:" + Connected.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "/" + Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The game-bound half of one map-object category. It owns the native reads and the instance identity: an
/// address is reversible from a native instance alone, so this provider keeps no world registry of objects
/// and a reference is re-read against its instance every time it is resolved. A source is one category's
/// reader, not an entity namespace of its own — the provider registers one map-object namespace and the
/// category travels inside the address.
/// </summary>
public interface IMapObjectSource
{
    string Category { get; }

    /// <summary>The current address of a native instance, or null when the instance is not this category's or
    /// cannot be addressed yet — never a guessed address.</summary>
    MapObjectReference? TryAddress(object instance);

    /// <summary>Reads a written address back with this category's own key grammar, or null when the text is
    /// not one of this category's addresses. The category owns this, because the two keys are different kinds
    /// of name: an address of another category, or one whose key this category does not use, is not its.</summary>
    MapObjectReference? Parse(string text);

    /// <summary>The current observation of an instance, or null when the instance cannot be read in full.</summary>
    MapObjectObservation? Read(object instance);

    /// <summary>The object's own position, or null when it cannot be read. A snapshot carries exactly three
    /// finite coordinates, so a position that did not read yields no observation rather than a zero one.</summary>
    double[]? Position(object instance);

    /// <summary>Whether the native members this address is built from still read the same way.</summary>
    bool IsCurrentAddress(object instance, MapObjectReference address);

    /// <summary>Reports an unreadable instance once per cause.</summary>
    void Report(string message);
}

/// <summary>The subject of a published fact: the addressed map object, the native instance behind it and the
/// source that read both. Keeping the source and the instance on the subject is what keeps a fact from ever
/// being published through another category's reader or from another object's members.</summary>
public sealed record MapObjectSubject(EntityReference Reference, MapObjectReference Address,
    IMapObjectSource Source, object Instance)
{
    public string Category => Address.Category;
}
