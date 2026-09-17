using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMap;
using ForgeRuntime.Framework;
using LevelGeneration;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>The execute half of the door action rows: `forge.action.map.door_open` and
/// `forge.action.map.door_close`. Both act on the doors a plan named in
/// the one `gtfo.map_object` namespace, through the native entry points the door observation already reads — no
/// second door table, no plan object, and no handle.
///
/// What the native side can carry is narrow and every other request is refused by name instead of imitated:
///
/// - The open row asks the door's own interaction entry, or its own force entry when the plan chose `force`.
///   The interaction entry toggles, so the door's own status decides first: a door that already reads open is
///   left alone and reported as the state it is in, never toggled shut by a second open.
/// - The close row asks the same toggling entry only for a door that reads open. It carries no structural
///   parameter at all: the interaction entry is the one close entry the game has, and the catalog's two close
///   policies are gone with their ports.
///
/// A door's alarm is **not** a row here: the rulings deleted the alarm action, because a door's alarm is the
/// chained puzzle instance its lock component holds and `forge.action.map.scan_state` already writes that same
/// instance kind. A plan reads the puzzle reference from `forge.query.map.door_state`'s `puzzle` output and
/// feeds it to the scan row, so one native write keeps one card.
///
/// Every refusal is a code, never a silent success: a reference that is not a door, a reference the kernel no
/// longer answers for, an instance the provider's own table no longer maps back to that reference, a door whose
/// address changed under it, a door the game's own status refuses, and a bypass policy no native entry carries
/// are all different rows.</summary>
internal sealed class DoorActionCommands
{
    /// <summary>The one recipient kind these rows answer for. A reference of any other kind is refused by name
    /// before anything native is read, so the refusal says what it is instead of reporting a dead door.</summary>
    internal const string Kind = "gtfo.map_object";
    private const string Prefix = Kind + ":";
    /// <summary>The one category of that kind these rows act on. A terminal is a map object too, and a plan that
    /// wired one into `doors` is refused rather than read through the door reader.</summary>
    private const string DoorCategory = "door";

    /// <summary>This side is not the host, or the runtime is not at a point where a native write may be made.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    /// <summary>The reference does not name a door: another kind, another category, or no reference at all.</summary>
    internal const string KindCode = "door-unsupported-recipient";
    /// <summary>The kernel no longer answers for the reference, its instance resolver refused it, or the
    /// instance no longer reads as the address it was resolved through.</summary>
    internal const string StaleCode = "door-stale";
    /// <summary>The instance the address resolved to is not the one the kernel resolves the reference back to.</summary>
    internal const string MismatchCode = "door-identity-mismatch";
    /// <summary>More recipients than the result row budget allows; the command is refused before any write.</summary>
    internal const string TargetsCode = "too-many-targets";
    /// <summary>A request that named no door at all. Nothing was asked for, so nothing is reported done.</summary>
    internal const string NoTargetsCode = "door-no-targets";
    /// <summary>A target after a commit whose effect could not be established is not attempted.</summary>
    internal const string NotAttemptedCode = "not-attempted-after-unknown-commit";
    /// <summary>The native call threw after it was entered; whether the door's own write had already happened is
    /// not observable from here, so the row is an unknown commit.</summary>
    internal const string CommitExceptionCode = "native-commit-exception";
    /// <summary>A structural parameter whose member no native path carries — an unknown bypass policy.</summary>
    internal const string PolicyCode = "door-unknown-policy";

    /// <summary>The native instance behind one recipient reference, or null when this world cannot resolve it.
    /// It is a delegate so a registration hands over the session's own lookup and a test can hand over a table;
    /// the caller still verifies the answer against the kernel before anything is written.</summary>
    internal delegate LG_SecurityDoor? Resolver(EntityReference reference);

    /// <summary>One row of the open and close results, in the row order the catalog declares: the four fixed
    /// columns and the number of doors the request named. Both rows carry the same columns, because the open
    /// row's own `duration` column is gone with the port the native entry never took.</summary>
    private sealed record DoorRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code,
        [property: JsonPropertyName("target_count")] int TargetCount);

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _ready;
    private readonly Resolver _resolve;
    private readonly Action<string> _report;

    internal DoorActionCommands(RuntimeKernel kernel, Func<bool> ready, Resolver resolve, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Doors the native entry was invoked for.</summary>
    internal int Submitted { get; private set; }
    /// <summary>Doors that already read as the requested state, so the row committed without a write.</summary>
    internal int Settled { get; private set; }
    internal int Refused { get; private set; }
    internal int Unknown { get; private set; }

    /// <summary>Whether this side may write native door state at all. The interaction, force and puzzle
    /// entries are host-side native writes whose state the game replicates from the master, and every row here
    /// declares `execution: host`, so the gate is the command's own host fact plus the session's readiness and
    /// the game's own master flag: any one of them alone would be a claim this layer cannot back.</summary>
    internal bool Authoritative()
        => _ready() && SNet.IsMaster && _kernel.Lifecycle.StartupState == RuntimeStartupState.Ready
            && _kernel.Lifecycle.IsHost == true;

    /// <summary>Whether a reference names a door of the one map-object namespace. Only the address's own first
    /// segment is read here; the full grammar is the category's own parser's job.</summary>
    internal static bool IsDoor(EntityReference? reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var text = id.AsSpan(Prefix.Length);
        int slash = text.IndexOf('/');
        return slash > 0 && text[..slash].SequenceEqual(DoorCategory);
    }

    /// <summary>The address a door reference carries, read back with the door category's own grammar. A
    /// five-segment address of that category with its fixed key is this provider's; every other string — another
    /// category, another shape, a coordinate that is not a plain decimal, a key the category does not use — is
    /// not.</summary>
    internal static MapObjectReference? Address(EntityReference? reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return MapObjectDoorAddress.TryParse(id[Prefix.Length..]);
    }

    /// <summary>The native instance behind one door reference, as a registration hands it to the handler: the
    /// address the reference carries, resolved through the one door reader this assembly already owns. It is the
    /// same lookup the session's own category resolver makes for a door, so the action layer adds no second way
    /// to find a door; a reference of another category, of another shape, or one this world no longer holds
    /// answers null, and the caller verifies the answer against the kernel before anything is written.</summary>
    internal static LG_SecurityDoor? ResolveByAddress(EntityReference? reference)
        => Address(reference) is { } address ? DoorObservation.ByAddress(address) : null;

    /// <summary>One door behind one recipient reference, checked the same way for every row. The kernel is the
    /// authority on whether the reference is still for this world; the address text is read with the door
    /// category's own grammar; the instance the reference resolves to has to be the instance the kernel itself
    /// resolves that reference back to — a lookup that answers with a different door is refused rather than
    /// written to — and the instance still has to read as the address the reference was built from, so a door
    /// whose entrance zone changed under the same reference is no longer the door the plan named.</summary>
    internal LG_SecurityDoor? Resolve(EntityReference? reference, out string refusal)
    {
        refusal = "";
        if (reference == null || !IsDoor(reference)) { refusal = KindCode; return null; }
        if (!_kernel.IsEntityCurrent(reference)) { refusal = StaleCode; return null; }
        if (Address(reference) is not { } address) { refusal = KindCode; return null; }
        LG_SecurityDoor? door;
        try { door = _resolve(reference); }
        catch (Exception error)
        {
            _report("map.door-resolve-exception: " + error.GetType().Name);
            refusal = StaleCode;
            return null;
        }
        if (door == null) { refusal = StaleCode; return null; }
        if (_kernel.ResolveEntityInstance(Kind, door) is not { } verified || verified != reference)
        { refusal = MismatchCode; return null; }
        if (!DoorObservation.IsCurrentAddress(door, address)) { refusal = StaleCode; return null; }
        return door;
    }

    /// <summary>The same check in the form the loops use: the instance and the address it was resolved through,
    /// or the one code that refused it.</summary>
    private bool TryResolve(EntityReference target, out LG_SecurityDoor door, out MapObjectReference address,
        out string refusal)
    {
        door = null!;
        address = null!;
        if (Resolve(target, out refusal) is not { } resolved) return false;
        if (Address(target) is not { } parsed) { refusal = KindCode; return false; }
        door = resolved;
        address = parsed;
        return true;
    }

    /// <summary>The open row's handler. `bypass_policy` is checked once for the whole command, because a command
    /// that cannot be carried out as asked must not half-apply: a policy that is neither of the catalog's two
    /// members is refused before any door is read.</summary>
    internal CommandResult Open(JsonElement inputs, JsonElement parameters, bool isHost)
    {
        if (!isHost || !Authoritative()) return CommandResult.Rejected(AuthorityCode);
        var doors = Recipients(inputs);
        if (doors.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        if (doors.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (BypassPolicy(parameters) is not { } policy) return CommandResult.Rejected(PolicyCode);

        var rows = new List<DoorRow>(doors.Length);
        bool stop = false;
        foreach (var target in doors)
        {
            if (stop)
            {
                rows.Add(Row(target, "rejected", CommitStates.None, NotAttemptedCode, doors.Length));
                continue;
            }
            if (!TryResolve(target, out var door, out var address, out var refusal))
            {
                rows.Add(Row(target, "rejected", CommitStates.None, refusal, doors.Length));
                continue;
            }
            var (status, commit, code) = Run(() => DoorActions.Open(door, address, policy));
            if (commit == CommitStates.Unknown) stop = true;
            rows.Add(Row(target, status, commit, code, doors.Length));
        }
        return Aggregate(rows, "door-open-all-rejected", "door-open-all-unknown");
    }

    /// <summary>The close row's handler. The request carries no structural parameter at all: the door's own
    /// interaction entry is the one close entry the game has.</summary>
    internal CommandResult Close(JsonElement inputs, JsonElement parameters, bool isHost)
    {
        if (!isHost || !Authoritative()) return CommandResult.Rejected(AuthorityCode);
        var doors = Recipients(inputs);
        if (doors.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        if (doors.Length == 0) return CommandResult.Rejected(NoTargetsCode);

        var rows = new List<DoorRow>(doors.Length);
        bool stop = false;
        foreach (var target in doors)
        {
            if (stop)
            {
                rows.Add(Row(target, "rejected", CommitStates.None, NotAttemptedCode, doors.Length));
                continue;
            }
            if (!TryResolve(target, out var door, out var address, out var refusal))
            {
                rows.Add(Row(target, "rejected", CommitStates.None, refusal, doors.Length));
                continue;
            }
            var (status, commit, code) = Run(() => DoorActions.Close(door, address));
            if (commit == CommitStates.Unknown) stop = true;
            rows.Add(Row(target, status, commit, code, doors.Length));
        }
        return Aggregate(rows, "door-close-all-rejected", "door-close-all-unknown");
    }

    /// <summary>Runs one door's own native entry and turns the layer's decision into a result row. An entry
    /// that was invoked is a confirmed commit, a door that already reads as the requested state is a confirmed
    /// commit that wrote nothing, a refusal is a rejected row with the layer's code, and a call that threw after
    /// it was entered is an unknown commit — the write may or may not have landed, so the door is not retried
    /// and the rows after it are not attempted.</summary>
    private (string Status, string CommitState, string Code) Run(Func<MapActionOutcome> entry)
    {
        MapActionOutcome outcome;
        try { outcome = entry(); }
        catch (Exception error)
        {
            Unknown++;
            _report("map.door-commit-exception: " + error.GetType().Name);
            return ("failed", CommitStates.Unknown, CommitExceptionCode);
        }
        switch (outcome.Commit)
        {
            case MapActionCommit.Issued:
                Submitted++;
                return ("succeeded", CommitStates.Confirmed, outcome.Code);
            case MapActionCommit.AlreadyInState:
                Settled++;
                return ("succeeded", CommitStates.Confirmed, outcome.Code);
            default:
                Refused++;
                return ("rejected", CommitStates.None, outcome.Code);
        }
    }

    /// <summary>The command handler as the kernel calls it: the context's own host fact and two JSON bags, so
    /// the same body is reachable from a test without a dispatcher.</summary>
    internal CommandResult HandleOpen(CommandContext context)
        => Open(context.Inputs, context.Parameters, context.IsHost);

    internal CommandResult HandleClose(CommandContext context)
        => Close(context.Inputs, context.Parameters, context.IsHost);

    /// <summary>The command-level conclusion of a run of doors, by the rule the other native actions use: every
    /// row committed is a success, no committed row is a rejection or an unknown failure, and anything between is
    /// partial with the weaker commit state. `outputs` is the envelope the rows are written into.</summary>
    private static CommandResult Aggregate(IReadOnlyList<DoorRow> rows, string allRejected, string allUnknown)
    {
        var outputs = Envelope(rows);
        int committed = 0, unknown = 0;
        foreach (var row in rows)
        {
            if (row.CommitState == CommitStates.Confirmed) committed++;
            else if (row.CommitState == CommitStates.Unknown) unknown++;
        }
        if (committed == rows.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0) return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(rows, allRejected), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(rows, allUnknown), "", outputs);
    }

    private static string Single(IReadOnlyList<DoorRow> rows, string fallback)
    {
        if (rows.Count == 1) return rows[0].Code;
        var first = rows[0].Code;
        foreach (var row in rows) if (row.Code != first) return fallback;
        return first;
    }

    /// <summary>The result envelope: one `results` array in the plan's own order, with no dedupe, which is what
    /// the catalog's `forge.result.map.door_*` row sets declare.</summary>
    private static JsonElement Envelope(IReadOnlyList<DoorRow> rows) => RuntimeJson.From(new { results = rows });

    private static DoorRow Row(EntityReference target, string status, string commit, string code, int count)
        => new(target, status, commit, code, count);

    private static EntityReference[] Recipients(JsonElement inputs)
        => inputs.TryGetProperty("doors", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(RuntimeJson.Entity).ToArray()
            : Array.Empty<EntityReference>();

    private static string? Parameter(JsonElement parameters, string id)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(id, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>One structural policy of the catalog's own member set, or null when the parameter is missing or
    /// names a member no native path carries.</summary>
    internal static DoorBypassPolicy? BypassPolicy(JsonElement parameters) => Parameter(parameters, "bypass_policy") switch
    {
        "respect" => DoorBypassPolicy.Respect,
        "force" => DoorBypassPolicy.Force,
        _ => null
    };
}
