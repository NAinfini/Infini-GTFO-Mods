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

/// <summary>The execute half of the two terminal action rows: `forge.action.map.terminal_command` and
/// `forge.action.map.terminal_output`. Both act on the terminals a plan named in the one `gtfo.map_object`
/// namespace, through the native entry points the door and terminal observation already read — no second
/// terminal table, no plan object, no handle.
///
/// What the native side can carry is narrow and this half refuses everything else instead of imitating it:
///
/// - The command row runs one whitelisted command through the terminal's own interpreter and the terminal
///   manager's own command entry, which is the game's authoritative action and the path the game's own servers
///   replicate. The engine parses the text and this half only decides whether the parsed command is one of the
///   commands a request may run, so a line the terminal does not know can never become a command.
/// - The actor a request may name is not passed on. The native command payload is a terminal id, a command and
///   three strings — there is no player in it — so resolving an actor here would hand the game an argument it
///   has no slot for.
/// - The printed-line row adds a line to the terminal's own line buffer. The replicated terminal state carries
///   used and removed commands, not lines, so that write is local to this machine; the row reports it as the
///   local effect it is rather than as a replicated one.
/// - The command row can carry one argument string, because the entry point takes two parameter strings and the
///   row composes them into the input line the interpreter parses. A command that needs more is a command this
///   row does not offer.
///
/// Every refusal is a code, never a silent success: a terminal that is not this kind, a reference the kernel no
/// longer answers for, an instance that no longer reads as the address it was resolved through, a command the
/// terminal's own parser refused and a command outside the whitelist are five different rows.</summary>
internal sealed class TerminalObjectActions
{
    /// <summary>The one recipient kind both rows answer for. A reference of any other kind is refused by name
    /// before anything native is read, so the refusal says what it is instead of reporting a dead terminal.</summary>
    internal const string Kind = "gtfo.map_object";
    private const string Prefix = Kind + ":";
    /// <summary>The one category of that kind these rows act on. A door is a map object too, and a plan that
    /// wired one into `terminals` is refused rather than read through the terminal reader.</summary>
    private const string TerminalCategory = "terminal";

    /// <summary>This side is not the host, or the runtime is not at a point where a native write may be made.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    /// <summary>The reference does not name a terminal: another kind, another category, or no reference at all.</summary>
    internal const string KindCode = "terminal-unsupported-recipient";
    /// <summary>The kernel no longer answers for the reference, or its instance resolver refused it.</summary>
    internal const string StaleCode = "terminal-stale";
    /// <summary>The instance the address resolved to is not the one the kernel resolves it back to, or the two
    /// disagree about which terminal it is.</summary>
    internal const string MismatchCode = "terminal-identity-mismatch";
    /// <summary>More recipients than the result row budget allows; the command is refused before any write.</summary>
    internal const string TargetsCode = "too-many-targets";
    /// <summary>A target after a commit whose effect could not be established is not attempted.</summary>
    internal const string NotAttemptedCode = "not-attempted-after-unknown-commit";
    /// <summary>The native call threw after it was entered; whether it had already sent its own action is not
    /// observable from here, so the row is an unknown commit.</summary>
    internal const string CommitExceptionCode = "native-commit-exception";
    /// <summary>The severity a printed line asked for is not one of the game's own line kinds.</summary>
    internal const string SeverityCode = "terminal-unknown-severity";
    /// <summary>The printed line the game's own member accepted. It is a local line: the terminal's own line
    /// buffer is per machine and the replicated state carries no lines.</summary>
    internal const string LinePrintedCode = "terminal-line-printed-host-local";
    /// <summary>A lifetime on a printed line: the native member takes a time argument, but the evidence for
    /// this build does not establish what it expires, so a request that names one is refused rather than
    /// quietly printed forever.</summary>
    internal const string LifetimeCode = "terminal-lifetime-unsupported";
    /// <summary>A request that named no terminal at all. Nothing was asked for, so nothing is reported done.</summary>
    internal const string NoTargetsCode = "terminal-no-targets";
    /// <summary>The command visibility switch a request carried is not one of the row's two members.</summary>
    internal const string VisibilityCode = "terminal-unknown-visibility";
    /// <summary>Neither a command nor a slot named a command of the terminal's own enum. The request is refused
    /// by name instead of being served with a neighbouring command.</summary>
    internal const string CommandUnknownCode = "terminal-command-unknown";
    /// <summary>The command was hidden by the game's own synchronized setter.</summary>
    internal const string CommandHiddenCode = "terminal-command-hidden";
    /// <summary>The command was shown by the same setter.</summary>
    internal const string CommandShownCode = "terminal-command-shown";
    /// <summary>The command already reads as shown, so the request asked for the state it is in.</summary>
    internal const string AlreadyShownCode = "terminal-command-already-shown";
    /// <summary>The command already reads as hidden.</summary>
    internal const string AlreadyHiddenCode = "terminal-command-already-hidden";
    /// <summary>The synchronized setter ran but the terminal's own reading does not agree with the state it was
    /// asked for. The write was issued, so the commit is unknown rather than a clean refusal.</summary>
    internal const string VisibilityUnconfirmedCode = "terminal-visibility-unconfirmed";

    /// <summary>The native instance behind one recipient reference, or null when this world cannot resolve it.
    /// It is a delegate so a registration hands over the session's own lookup and a test can hand over a table;
    /// the caller still verifies the answer against the kernel before anything is written.</summary>
    internal delegate LG_ComputerTerminal? Resolver(EntityReference reference);

    /// <summary>One row of either result, in the row order the catalog declares: the four fixed columns, then
    /// the row's own fields, under the wire names the catalog spells (the serializer's own camel case would
    /// leave `committed` and `target_count` unreachable for a reader that looks the fields up by name). `Lifetime`
    /// is null for the command row and for a printed line that named none, and the column is left out of that
    /// row's JSON rather than written as a null or an invented duration.</summary>
    private sealed record TerminalRow(EntityReference Target, string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code,
        [property: JsonPropertyName("lifetime")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Lifetime,
        [property: JsonPropertyName("target_count")] int TargetCount);

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _ready;
    private readonly Resolver _resolve;
    private readonly Action<string> _report;

    internal TerminalObjectActions(RuntimeKernel kernel, Func<bool> ready, Resolver resolve, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    internal int Submitted { get; private set; }
    internal int Refused { get; private set; }
    internal int Unknown { get; private set; }

    /// <summary>Whether this side may write native terminal state at all. The command entry point is a static
    /// host entry that the game's own master check guards, and the line buffer is the host's own copy of the
    /// terminal, so both rows are host-only exactly as their catalog rows say.</summary>
    internal bool Authoritative()
        => _ready() && SNet.IsMaster && _kernel.Lifecycle.StartupState == RuntimeStartupState.Ready
            && _kernel.Lifecycle.IsHost == true;

    /// <summary>One terminal behind one recipient reference, checked the same way for both rows. The kernel is
    /// the authority on whether the reference is still for this world; the address text is read with the
    /// terminal category's own grammar, and the instance the address names has to be the instance the kernel
    /// itself resolves the reference back to — a lookup that answers with a different terminal is refused
    /// rather than written to.</summary>
    internal LG_ComputerTerminal? Resolve(EntityReference? reference, out string refusal)
    {
        refusal = "";
        if (reference == null || !IsTerminal(reference)) { refusal = KindCode; return null; }
        if (!_kernel.IsEntityCurrent(reference)) { refusal = StaleCode; return null; }
        if (Address(reference) is not { } address) { refusal = KindCode; return null; }
        LG_ComputerTerminal? terminal;
        try { terminal = _resolve(reference); }
        catch (Exception error)
        {
            _report("map.terminal-resolve-exception: " + error.GetType().Name);
            refusal = StaleCode;
            return null;
        }
        if (terminal == null) { refusal = StaleCode; return null; }
        if (_kernel.ResolveEntityInstance(Kind, terminal) is not { } verified || verified != reference)
        { refusal = MismatchCode; return null; }
        // The instance still has to read as the address the reference was built from: a terminal that moved to
        // another placement index in its zone, or into another zone, is no longer the terminal the plan named.
        if (TerminalObservation.Address(terminal) != address) { refusal = StaleCode; return null; }
        return terminal;
    }

    /// <summary>The same check, in the form the two loops use: the instance and the address it was resolved
    /// through, or the one code that refused it.</summary>
    private bool TryResolve(EntityReference target, out LG_ComputerTerminal terminal, out MapObjectReference address,
        out string refusal)
    {
        terminal = null!;
        address = null!;
        if (Resolve(target, out refusal) is not { } resolved) return false;
        if (Address(target) is not { } parsed) { refusal = KindCode; return false; }
        terminal = resolved;
        address = parsed;
        return true;
    }

    /// <summary>Runs one whitelisted command on one terminal. The native half composes the input line from the
    /// command and its argument string, asks the terminal's own interpreter to parse it, and refuses anything
    /// the interpreter did not recognise or the whitelist does not run.</summary>
    internal (string Status, string CommitState, string Code) Run(LG_ComputerTerminal terminal, MapObjectReference address,
        string? command, string? arguments)
    {
        MapActionOutcome outcome;
        try { outcome = TerminalActions.Command(terminal, address, command, arguments); }
        catch (Exception error)
        {
            // The call entered the terminal's own path; whether the manager's action had already sent its
            // packet is not observable from here, so the commit stays unknown and the terminal is not retried.
            Unknown++;
            _report("map.terminal-command-exception: " + error.GetType().Name);
            return ("failed", CommitStates.Unknown, CommitExceptionCode);
        }
        if (outcome.Commit == MapActionCommit.Issued)
        {
            Submitted++;
            return ("succeeded", CommitStates.Confirmed, outcome.Code);
        }
        Refused++;
        return ("rejected", CommitStates.None, outcome.Code);
    }

    /// <summary>Adds one line to the terminal's own buffer. The write is local to this machine by the game's
    /// own design, which the row's code states rather than the row claiming a replication the evidence for this
    /// build does not support.</summary>
    internal (string Status, string CommitState, string Code) Print(LG_ComputerTerminal terminal, MapObjectReference address,
        TerminalLineSeverity severity, string? text)
    {
        MapActionOutcome outcome;
        try { outcome = TerminalActions.Print(terminal, address, severity, text); }
        catch (Exception error)
        {
            Unknown++;
            _report("map.terminal-print-exception: " + error.GetType().Name);
            return ("failed", CommitStates.Unknown, CommitExceptionCode);
        }
        if (outcome.Commit == MapActionCommit.Issued)
        {
            Submitted++;
            return ("succeeded", CommitStates.Confirmed, LinePrintedCode);
        }
        Refused++;
        return ("rejected", CommitStates.None, outcome.Code);
    }

    /// <summary>Hides or shows one command on one terminal through the terminal's own synchronized setters —
    /// `TrySyncSetCommandHidden` and `TrySyncSetCommandShow`. Both are members of the terminal itself and write
    /// the terminal's replicated state (`pComputerTerminalState.RemovedCommands`, which the same component reads
    /// back through `CommandIsHidden`), which is why the row is host-only and why the effect every peer sees is
    /// the game's own replication and not a Forge broadcast: the writer and the reader are the same component.
    ///
    /// The state the request asked for is read first, so a command already in it is reported as the state it is
    /// in rather than re-written. The reading is repeated after the setter ran: a setter that returned without
    /// the terminal's own reading agreeing with it is an unknown commit, because whether the replicated state
    /// was written is not observable from here.</summary>
    internal (string Status, string CommitState, string Code) SetVisibility(LG_ComputerTerminal terminal,
        string? command, int? slot, bool wanted)
    {
        if (DoorTerminalDerivations.ResolveCommand(command, slot) is not { } resolved) return ("rejected", CommitStates.None, CommandUnknownCode);
        bool hidden;
        try { hidden = terminal.CommandIsHidden(resolved); }
        catch (Exception error)
        {
            _report("map.terminal-command-read-exception: " + error.GetType().Name);
            return ("rejected", CommitStates.None, StaleCode);
        }
        if (DoorTerminalDerivations.Visibility(hidden, wanted) == CommandVisibility.Already)
        {
            Refused++;
            return ("rejected", CommitStates.None, wanted ? AlreadyShownCode : AlreadyHiddenCode);
        }
        try
        {
            if (wanted) terminal.TrySyncSetCommandHidden(resolved);
            else terminal.TrySyncSetCommandShow(resolved);
        }
        catch (Exception error)
        {
            Unknown++;
            _report("map.terminal-command-visibility-exception: " + error.GetType().Name);
            return ("failed", CommitStates.Unknown, CommitExceptionCode);
        }
        bool applied;
        try { applied = terminal.CommandIsHidden(resolved) == wanted; }
        catch (Exception error)
        {
            Unknown++;
            _report("map.terminal-command-read-exception: " + error.GetType().Name);
            return ("failed", CommitStates.Unknown, CommitExceptionCode);
        }
        if (!applied)
        {
            Unknown++;
            return ("failed", CommitStates.Unknown, VisibilityUnconfirmedCode);
        }
        Submitted++;
        return ("succeeded", CommitStates.Confirmed, wanted ? CommandShownCode : CommandHiddenCode);
    }

    /// <summary>Whether a reference names a terminal of the one map-object namespace. Only the address's own
    /// first segment is read here; the full grammar is the category's own parser's job. The segment is compared
    /// as text as well, so a category a plan spelled with different case is refused rather than accepted and
    /// then failed by the parser with a different code.</summary>
    internal static bool IsTerminal(EntityReference reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        int slash = id.IndexOf('/', Prefix.Length);
        return slash > Prefix.Length && string.CompareOrdinal(id, Prefix.Length, TerminalCategory, 0, slash - Prefix.Length) == 0;
    }

    /// <summary>The address a terminal reference carries, read back with the terminal category's own grammar.
    /// A five-segment address of that category is this provider's; every other string — another category,
    /// another shape, a coordinate that is not a plain decimal, a key the category does not use — is not.</summary>
    internal static MapObjectReference? Address(EntityReference? reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return MapObjectTerminalAddress.TryParse(id[Prefix.Length..]);
    }

    /// <summary>One classification of the catalog's `severity` parameter onto the terminal's own line kinds.
    /// The parameter is the plan's structural choice, so it arrives in the node's constant bag; a value outside
    /// the catalog's three members — including an absent one, which is not a severity a line may be printed
    /// with — is refused rather than defaulted to the plain line kind.</summary>
    internal static TerminalLineSeverity? Severity(string? value) => value switch
    {
        "info" => TerminalLineSeverity.Info,
        "warning" => TerminalLineSeverity.Warning,
        "error" => TerminalLineSeverity.Error,
        _ => null
    };

    /// <summary>The terminal command handler. The whole request is checked before the first terminal, because a
    /// command that cannot be carried out as asked must not half-apply: the argument string has to be one the
    /// native entry can carry, and a request that names no terminal is refused rather than reported as done.
    ///
    /// The command row runs a command and nothing else: whether a command appears on the terminal at all is the
    /// visibility row's own handler, which is a different author node because it asks for a different thing.</summary>
    internal CommandResult ExecuteCommand(JsonElement inputs, JsonElement parameters)
    {
        if (!Authoritative()) return CommandResult.Rejected(AuthorityCode);
        var terminals = Recipients(inputs);
        if (terminals.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        if (terminals.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        string? command = Text(inputs, "command");
        string? arguments2 = Text(inputs, "arguments");
        if (command == null && arguments2 != null) return CommandResult.Rejected(TerminalActions.CommandEmpty);
        var rows = new List<TerminalRow>(terminals.Length);
        bool stop = false;
        foreach (var target in terminals)
        {
            if (stop)
            {
                rows.Add(Row(target, "rejected", CommitStates.None, NotAttemptedCode, null, terminals.Length));
                continue;
            }
            if (!TryResolve(target, out var terminal, out var address, out var refusal))
            {
                rows.Add(Row(target, "rejected", CommitStates.None, refusal, null, terminals.Length));
                continue;
            }
            var (status, commit, code) = Run(terminal, address, command, arguments2);
            if (commit == CommitStates.Unknown) stop = true;
            rows.Add(Row(target, status, commit, code, null, terminals.Length));
        }
        return Aggregate(rows);
    }

    /// <summary>The command-visibility handler: the same recipient collection and the same command or slot, and
    /// the row's one required switch decides which of the terminal's two synchronized setters runs. Every refusal
    /// is per row, exactly like the run handler, and an unknown commit stops the rest of the targets because the
    /// setter may already have written part of what was asked for.</summary>
    internal CommandResult ExecuteVisibility(JsonElement inputs, JsonElement parameters)
    {
        if (!Authoritative()) return CommandResult.Rejected(AuthorityCode);
        if (Visibility(parameters) is not { } wanted) return CommandResult.Rejected(VisibilityCode);
        var terminals = Recipients(inputs);
        if (terminals.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        if (terminals.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        string? command = Text(inputs, "command");
        int? slot = Slot(inputs);
        if (command == null && slot == null) return CommandResult.Rejected(CommandUnknownCode);

        var rows = new List<TerminalRow>(terminals.Length);
        bool stop = false;
        foreach (var target in terminals)
        {
            if (stop)
            {
                rows.Add(Row(target, "rejected", CommitStates.None, NotAttemptedCode, null, terminals.Length));
                continue;
            }
            if (!TryResolve(target, out var terminal, out _, out var refusal))
            {
                rows.Add(Row(target, "rejected", CommitStates.None, refusal, null, terminals.Length));
                continue;
            }
            var (status, commit, code) = SetVisibility(terminal, command, slot, wanted);
            if (commit == CommitStates.Unknown) stop = true;
            rows.Add(Row(target, status, commit, code, null, terminals.Length));
        }
        return Aggregate(rows);
    }

    /// <summary>The printed-line handler. `lifetime` is refused when it names one: the native member's time
    /// argument is documented by this build's evidence as taking the terminal's own zero, and what it would
    /// expire is not established, so a request that asks for an expiry is told it cannot have one instead of
    /// receiving a line that never expires under a code that says it does.</summary>
    internal CommandResult ExecuteOutput(JsonElement inputs, JsonElement parameters)
    {
        if (!Authoritative()) return CommandResult.Rejected(AuthorityCode);
        // The severity is the row's required structural parameter, so it is checked before the recipients: a
        // request whose parameter bag carries none is not a request this row can print.
        if (!parameters.TryGetProperty("severity", out var declared) || declared.ValueKind != JsonValueKind.String
            || Severity(declared.GetString()) is not { } severity) return CommandResult.Rejected(SeverityCode);
        var terminals = Recipients(inputs);
        if (terminals.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        if (terminals.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (Lifetime(inputs) is { } lifetime && lifetime != 0) return CommandResult.Rejected(LifetimeCode);
        string? text = Text(inputs, "text");
        if (text == null) return CommandResult.Rejected(TerminalActions.TextEmpty);

        var rows = new List<TerminalRow>(terminals.Length);
        bool stop = false;
        foreach (var target in terminals)
        {
            if (stop)
            {
                rows.Add(Row(target, "rejected", CommitStates.None, NotAttemptedCode, null, terminals.Length));
                continue;
            }
            if (!TryResolve(target, out var terminal, out var address, out var refusal))
            {
                rows.Add(Row(target, "rejected", CommitStates.None, refusal, null, terminals.Length));
                continue;
            }
            var (status, commit, code) = Print(terminal, address, severity, text);
            if (commit == CommitStates.Unknown) stop = true;
            rows.Add(Row(target, status, commit, code, null, terminals.Length));
        }
        return Aggregate(rows);
    }

    /// <summary>The command handler as the kernel calls it: the context's two JSON bags and nothing else, so
    /// the same body is reachable from a test without a dispatcher.</summary>
    internal CommandResult HandleCommand(CommandContext context)
        => ExecuteCommand(context.Inputs, context.Parameters);

    internal CommandResult HandleVisibility(CommandContext context)
        => ExecuteVisibility(context.Inputs, context.Parameters);

    internal CommandResult HandleOutput(CommandContext context)
        => ExecuteOutput(context.Inputs, context.Parameters);

    /// <summary>The visibility row's structural switch: `shown` asks for the command to appear on the terminal
    /// and `hidden` asks for it to disappear. It is required, so an absent member is not a mode and answers null,
    /// which the handler refuses by name; a value outside the two members answers null the same way.</summary>
    internal static bool? Visibility(JsonElement parameters)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("visible", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() switch { "shown" => true, "hidden" => false, _ => null }
            : null;

    /// <summary>The one-based unique-command slot a request named, or null when it named none.</summary>
    private static int? Slot(JsonElement inputs)
        => inputs.TryGetProperty("slot", out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int slot) ? slot : null;

    /// <summary>The command-level conclusion of a run of terminals, by the rule the other native actions use:
    /// every row committed is a success, no committed row is a rejection or an unknown failure, and anything
    /// between is partial with the weaker commit state. A row's own status is what the envelope carries; the
    /// envelope itself is `results`.</summary>
    private static CommandResult Aggregate(IReadOnlyList<TerminalRow> rows)
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
        if (unknown == 0) return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(rows, "terminal-all-rejected"), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(rows, "terminal-all-unknown"), "", outputs);
    }

    private static string Single(IReadOnlyList<TerminalRow> rows, string fallback)
    {
        if (rows.Count == 1) return rows[0].Code;
        var first = rows[0].Code;
        foreach (var row in rows) if (row.Code != first) return fallback;
        return first;
    }

    /// <summary>The result envelope. A row that named no lifetime leaves the column out, which is the only
    /// shape a plan can read as "none": a zero would be a lifetime the request never asked for.</summary>
    private static JsonElement Envelope(IReadOnlyList<TerminalRow> rows) => RuntimeJson.From(new { results = rows });

    private static TerminalRow Row(EntityReference target, string status, string commit, string code, int? lifetime, int count)
        => new(target, status, commit, code, lifetime, count);

    private static EntityReference[] Recipients(JsonElement inputs)
        => inputs.TryGetProperty("terminals", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(RuntimeJson.Entity).ToArray()
            : Array.Empty<EntityReference>();

    private static string? Text(JsonElement inputs, string port)
        => inputs.TryGetProperty(port, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? Lifetime(JsonElement inputs)
        => inputs.TryGetProperty("lifetime", out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int ticks) ? ticks : null;
}
