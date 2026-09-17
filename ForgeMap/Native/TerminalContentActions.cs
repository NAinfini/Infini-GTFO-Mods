using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;
using GameData;
using LevelGeneration;
using Localization;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>
/// The native half of the terminal-content row (`forge.action.map.terminal_content`): one action that adds or
/// removes a command on a terminal the level already built, and the two members the command half writes.
///
/// The command is added to the terminal's own interpreter, not to a table of this package's: the interpreter is
/// the same object a player's typed line is parsed by, so a command this row adds is typed, completed and answered
/// exactly like a vanilla one, and it is carried to the other machines by the terminal's own state. Removing a
/// command uses the interpreter's own hide entry, which is the game's own way of withdrawing a command from the
/// list without rebuilding the command table.
///
/// A terminal that already knows the command is not written twice: the row reports that the state it was asked
/// for already holds. That check is also what makes a second event harmless.
///
/// The log half writes the terminal's own local log table — `AddLocalLog` takes the `TerminalLogFileData` entry,
/// `RemoveLocalLog` takes it back out, and `SetLogVisible` only takes one off the list a player reads. Those three
/// are **not** replicated: the terminal's synced state (`pComputerTerminalState`) carries the used, removed and
/// rule arrays but no log array, so a log this row writes is held by the machine that wrote it and the other
/// machines rebuild their own table from level generation. The row is a host write, which is what the command half
/// needs; a log that every player has to read therefore needs the same step run on each machine, and that gap is
/// reported rather than papered over.</summary>
internal sealed class TerminalContentActions
{
    internal const string AuthorityCode = "authority-or-phase";
    internal const string NoTargetsCode = "terminal-content-no-targets";
    internal const string TargetsCode = "too-many-targets";
    internal const string MismatchCode = "terminal-identity-mismatch";
    internal const string KindCode = "terminal-content-kind-unknown";
    internal const string OperationCode = "terminal-content-operation-unknown";
    internal const string NumberCode = "terminal-content-number-required";
    internal const string NameCode = "terminal-content-name-required";
    internal const string RuleCode = "terminal-content-rule-unknown";
    internal const string AddedCode = "terminal-content-command-added";
    internal const string RemovedCode = "terminal-content-command-removed";
    internal const string PresentCode = "terminal-content-command-present";
    internal const string AbsentCode = "terminal-content-command-absent";
    internal const string LogAddedCode = "terminal-content-log-added";
    internal const string LogRemovedCode = "terminal-content-log-removed";
    internal const string LogShownCode = "terminal-content-log-shown";
    internal const string LogHiddenCode = "terminal-content-log-hidden";
    internal const string LogPresentCode = "terminal-content-log-present";
    internal const string LogAbsentCode = "terminal-content-log-absent";
    internal const string BodyCode = "terminal-content-log-body-required";
    internal const string CommitExceptionCode = "native-commit-exception";
    internal const string AllRejectedCode = "terminal-content-all-rejected";
    internal const string AllUnknownCode = "terminal-content-all-unknown";

    private const string EntityKind = MapObjectModule.EntityKind;

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;

    internal TerminalContentActions(RuntimeKernel kernel, Func<bool> ready, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    internal CommandResult Handle(CommandContext context)
        => Apply(context.Inputs, context.Parameters, context.IsHost);

    /// <summary>The request is read once for the whole command, before any terminal is touched: a request the
    /// native path cannot carry in full must not half-apply.</summary>
    internal CommandResult Apply(JsonElement inputs, JsonElement parameters, bool isHost)
    {
        if (!isHost || !Authoritative()) return CommandResult.Rejected(AuthorityCode);
        if (!TryReadRequest(inputs, parameters, out var request, out var code)) return CommandResult.Rejected(code);
        var terminals = Recipients(inputs, "terminals");
        if (terminals.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (terminals.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);

        var rows = new List<TerminalContentRow>(terminals.Length);
        foreach (var target in terminals)
        {
            if (!Resolve(target, out var terminal, out var resolveCode))
            {
                rows.Add(Refused(target, resolveCode, terminals.Length));
                continue;
            }
            if (request.Kind == "log")
            {
                rows.Add(Log(target, terminal, request, terminals.Length));
                continue;
            }
            var interpreter = terminal.m_command;
            if (interpreter == null || interpreter.WasCollected)
            {
                rows.Add(Refused(target, TerminalActions.InterpreterUnavailable, terminals.Length));
                continue;
            }
            rows.Add(request.Operation == "add"
                ? Add(target, interpreter, request, terminals.Length)
                : Remove(target, terminal, request, terminals.Length));
        }
        return Aggregate(rows, AllRejectedCode, AllUnknownCode);
    }

    /// <summary>Whether this side owns the terminal's content: the row is a host write whose state the terminal's
    /// own replication carries, so the gate is the command's host fact plus the session's readiness and the game's
    /// master flag.</summary>
    internal bool Authoritative()
        => _ready() && SNet.IsMaster && _kernel.Lifecycle.StartupState == RuntimeStartupState.Ready
            && _kernel.Lifecycle.IsHost == true;

    private TerminalContentRow Add(EntityReference target, LG_ComputerTerminalCommandInterpreter interpreter,
        TerminalRequest request, int count)
    {
        bool known;
        try { known = interpreter.TryGetCommand(request.Name, out _, out _, out _); }
        catch (Exception error)
        {
            _report("map.terminal-content-read-exception: " + error.GetType().Name);
            return Refused(target, TerminalActions.InterpreterUnavailable, count);
        }
        if (known) return Issued(target, PresentCode, count);
        try
        {
            interpreter.AddCommand((TERM_Command)request.Number, request.Name, new LocalizedText(request.Text),
                Rule(request.Rule), new Il2CppSystem.Collections.Generic.List<WardenObjectiveEventData>());
        }
        catch (Exception error)
        {
            _report("map.terminal-content-commit-exception: " + error.GetType().Name);
            return new TerminalContentRow(target, CommandStatuses.Failed, CommitStates.Unknown, CommitExceptionCode, count);
        }
        return Issued(target, AddedCode, count);
    }

    /// <summary>Removal is the terminal's own synchronized hide entry, which is the member the game itself uses to
    /// withdraw a command: it writes `pComputerTerminalState.RemovedCommands`, the array the terminal reads back
    /// through `CommandIsHidden`, so the effect every peer sees is the game's own replication of the terminal's
    /// state and not a Forge broadcast. The readback is that same `CommandIsHidden`.</summary>
    private TerminalContentRow Remove(EntityReference target, LG_ComputerTerminal terminal, TerminalRequest request,
        int count)
    {
        var command = (TERM_Command)request.Number;
        bool hidden;
        try { hidden = terminal.CommandIsHidden(command); }
        catch (Exception error)
        {
            _report("map.terminal-content-read-exception: " + error.GetType().Name);
            return Refused(target, TerminalActions.InterpreterUnavailable, count);
        }
        if (hidden) return Issued(target, AbsentCode, count);
        try { terminal.TrySyncSetCommandHidden(command); }
        catch (Exception error)
        {
            _report("map.terminal-content-commit-exception: " + error.GetType().Name);
            return new TerminalContentRow(target, CommandStatuses.Failed, CommitStates.Unknown, CommitExceptionCode, count);
        }
        return Issued(target, RemovedCode, count);
    }

    /// <summary>One terminal request, already checked against the row's own vocabulary. `text` is the command's
    /// help line or the log file's body, and `sound` is the log's own audio file id — `TerminalLogFileData.
    /// AttachedAudioFile` — with a negative value meaning the request named none.</summary>
    private readonly record struct TerminalRequest(string Kind, string Operation, int Number, string Name, string Rule,
        string Text, int Sound);

    /// <summary>The request half's own reading. A command needs its code and its name — the interpreter looks a
    /// command up by name and stores it under its enum member — while a log needs its name and, when it is being
    /// added, the body a player reads. The help text and the body are the row's own `text` input; `sound` is only
    /// read for a log, because a command's interpreter carries no audio file.</summary>
    private static bool TryReadRequest(JsonElement inputs, JsonElement parameters, out TerminalRequest request,
        out string code)
    {
        request = default;
        code = "";
        var kind = Member(parameters, "kind");
        if (kind == null || Array.IndexOf(TerminalContentContract.Kinds, kind) < 0) { code = KindCode; return false; }
        var operation = Member(parameters, "operation");
        if (operation == null || Array.IndexOf(TerminalContentContract.Operations, operation) < 0)
        {
            code = OperationCode;
            return false;
        }
        var name = Member(parameters, "name");
        if (string.IsNullOrEmpty(name) || name!.Length > NameLimit) { code = NameCode; return false; }
        var text = Member(inputs, "text") ?? "";
        if (text.Length > TextLimit) { code = BodyCode; return false; }
        if (kind == "log")
        {
            if (operation == "add" && text.Length == 0) { code = BodyCode; return false; }
            var sound = Integer(parameters, "sound", out var audio) ? audio : -1;
            request = new TerminalRequest(kind, operation, 0, name, "normal", text, sound);
            return true;
        }
        var rule = Member(parameters, "rule") ?? "normal";
        if (Array.IndexOf(TerminalContentContract.Rules, rule) < 0) { code = RuleCode; return false; }
        if (operation == "show" || operation == "hide") { code = OperationCode; return false; }
        if (!Integer(parameters, "slot", out var slot) || slot < 0) { code = NumberCode; return false; }
        request = new TerminalRequest(kind, operation, slot, name, rule, text, -1);
        return true;
    }

    /// <summary>The interpreter's own three rules.</summary>
    private static TERM_CommandRule Rule(string rule) => rule switch
    {
        "only_once" => TERM_CommandRule.OnlyOnce,
        "only_once_delete" => TERM_CommandRule.OnlyOnceDelete,
        _ => TERM_CommandRule.Normal
    };

    /// <summary>The longest terminal command name this row accepts, matching the buffer a typed line is read at.
    /// The interpreter's own help argument carries the row's `text` input, which is bounded the same way.</summary>
    internal const int NameLimit = 64;

    /// <summary>The longest `text` this row accepts, for a command's help line or a log file's body. A log is a
    /// page of text rather than a line, so the bound is the payload slot one command request carries.</summary>
    internal const int TextLimit = 4096;

    /// <summary>One log request's own write. `add` builds the entry the terminal holds (`TerminalLogFileData` with
    /// this row's name and body), `remove` takes the entry out of the table, and `show`/`hide` only move it on and
    /// off the list a player reads. The readback is the same member the game itself reads — `GetLocalLogs` for the
    /// entry, `IsLogVisible` for the list — so the row reports the state that holds afterwards and not the one it
    /// asked for.</summary>
    private TerminalContentRow Log(EntityReference target, LG_ComputerTerminal terminal, TerminalRequest request,
        int count)
    {
        try
        {
            switch (request.Operation)
            {
                case "add":
                {
                    if (Holds(terminal, request.Name)) return Issued(target, LogPresentCode, count);
                    var data = new TerminalLogFileData
                    {
                        FileName = request.Name,
                        FileContent = new LocalizedText(request.Text)
                    };
                    if (request.Sound >= 0) data.AttachedAudioFile = (uint)request.Sound;
                    terminal.AddLocalLog(data, true);
                    return Issued(target, LogAddedCode, count);
                }
                case "remove":
                    if (!Holds(terminal, request.Name)) return Issued(target, LogAbsentCode, count);
                    return terminal.RemoveLocalLog(request.Name)
                        ? Issued(target, LogRemovedCode, count)
                        : Issued(target, LogAbsentCode, count);
                case "show":
                case "hide":
                {
                    var visible = request.Operation == "show";
                    if (terminal.IsLogVisible(request.Name) == visible)
                        return Issued(target, LogPresentCode, count);
                    terminal.SetLogVisible(request.Name, visible);
                    return Issued(target, visible ? LogShownCode : LogHiddenCode, count);
                }
                default:
                    return Refused(target, OperationCode, count);
            }
        }
        catch (Exception error)
        {
            _report("map.terminal-content-log-exception: " + error.GetType().Name);
            return new TerminalContentRow(target, CommandStatuses.Failed, CommitStates.Unknown, CommitExceptionCode,
                count);
        }
    }

    /// <summary>Whether the terminal already holds a log file of this name.</summary>
    private static bool Holds(LG_ComputerTerminal terminal, string name)
    {
        var logs = terminal.GetLocalLogs();
        return logs != null && !logs.WasCollected && logs.ContainsKey(name);
    }

    /// <summary>One terminal behind one recipient reference, checked the way the terminal family checks its own:
    /// the kernel still has to answer for the reference, the address has to read with the terminal category's
    /// grammar, and the instance the address resolves to has to still read as that address.</summary>
    private bool Resolve(EntityReference? reference, out LG_ComputerTerminal terminal, out string refusal)
    {
        terminal = null!;
        if (reference == null || TerminalObjectActions.Address(reference) is not { } parsed)
        {
            refusal = TerminalActions.Unavailable;
            return false;
        }
        if (!_kernel.IsEntityCurrent(reference))
        {
            refusal = TerminalActions.Stale;
            return false;
        }
        LG_ComputerTerminal? resolved;
        try { resolved = TerminalObservation.ByAddress(parsed); }
        catch (Exception error)
        {
            _report("map.terminal-content-resolve-exception: " + error.GetType().Name);
            refusal = TerminalActions.Stale;
            return false;
        }
        if (resolved == null) { refusal = TerminalActions.Stale; return false; }
        if (_kernel.ResolveEntityInstance(EntityKind, resolved) is not { } verified || verified != reference)
        {
            refusal = MismatchCode;
            return false;
        }
        if (!TerminalObservation.IsCurrentAddress(resolved, parsed))
        {
            refusal = TerminalActions.Stale;
            return false;
        }
        terminal = resolved;
        refusal = "";
        return true;
    }

    private readonly record struct TerminalContentRow(EntityReference Target, string Status, string CommitState,
        string Code, int TargetCount);

    private static TerminalContentRow Issued(EntityReference target, string code, int count)
        => new(target, CommandStatuses.Succeeded, CommitStates.Confirmed, code, count);

    private static TerminalContentRow Refused(EntityReference target, string code, int count)
        => new(target, CommandStatuses.Rejected, CommitStates.None, code, count);

    /// <summary>The command-level conclusion, in the shape the terminal family states: every row confirmed is a
    /// success, none confirmed is a rejection, and anything in between is partial.</summary>
    private static CommandResult Aggregate(IReadOnlyList<TerminalContentRow> rows, string allRejected, string allUnknown)
    {
        var outputs = RuntimeJson.From(new { results = rows });
        int committed = 0, unknown = 0;
        foreach (var row in rows)
        {
            if (row.CommitState == CommitStates.Confirmed) committed++;
            else if (row.CommitState == CommitStates.Unknown) unknown++;
        }
        if (committed == rows.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(rows, allRejected), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(rows, allUnknown), "", outputs);
    }

    private static string Single(IReadOnlyList<TerminalContentRow> rows, string fallback)
    {
        var first = rows[0].Code;
        foreach (var row in rows) if (row.Code != first) return fallback;
        return first;
    }

    private static EntityReference[] Recipients(JsonElement inputs, string port)
        => inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(port, out var value)
           && value.ValueKind == JsonValueKind.Array
            ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(value.EnumerateArray(), RuntimeJson.Entity))
            : Array.Empty<EntityReference>();

    private static string? Member(JsonElement parameters, string id)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(id, out var member)
           && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    private static bool Integer(JsonElement parameters, string id, out int value)
    {
        value = 0;
        return parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(id, out var member)
            && member.ValueKind == JsonValueKind.Number && member.TryGetInt32(out value);
    }
}
