using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The native half of the one map-object state row: the interaction switch
/// (`forge.action.map.interaction_state`).
///
/// The interaction body writes the one member this build exposes for a map object's own availability: a
/// terminal's `CurrentStateName` (`TERM_State.Sleeping` is dormant and deliberately drops whoever is at the
/// terminal; `TERM_State.Awake` is the state a player may walk up to). A door is refused by name instead of
/// being written: `LG_SecurityDoor` exposes its interaction as the read `get_InteractionAllowed()` with no
/// setter and no per-part switch, and `Interact_Base.SetActive` — the one other candidate — is evidenced as the
/// interactable leaving the world, which is teardown rather than an interaction switch this row may claim.
///
/// Both rows are host-authoritative world state, so both refuse on a peer that is not the host or a session that
/// is not ready, and neither is retried after a call that threw after it was entered.</summary>
internal sealed class MapStateActions
{
    /// <summary>This machine cannot write yet: the session is not ready, or the runtime is not at a point where a
    /// step may run.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    /// <summary>The reference names no map object this provider addresses.</summary>
    internal const string KindCode = "map-state-unsupported-recipient";
    /// <summary>The reference is stale: the kernel no longer answers for it.</summary>
    internal const string StaleCode = "map-state-stale";
    /// <summary>A door's own interaction has no write in this build.</summary>
    internal const string DoorInteractionCode = "door-interaction-unsupported";
    /// <summary>The request named more recipients than one command may report.</summary>
    internal const string TargetsCode = "too-many-targets";
    /// <summary>The request addressed no recipient at all.</summary>
    internal const string NoTargetsCode = "map-state-no-targets";
    /// <summary>The request's `operation` is missing or names a setting this row does not carry.</summary>
    internal const string OperationCode = "map-state-unknown-operation";
    /// <summary>The row after one whose commit is unknown: it is not attempted.</summary>
    internal const string NotAttemptedCode = "not-attempted-after-unknown-commit";
    /// <summary>A native call threw after it was entered, so whether it landed is unknown.</summary>
    internal const string CommitExceptionCode = "native-commit-exception";
    /// <summary>The terminal now reads as dormant.</summary>
    internal const string TerminalSleepingCode = "terminal-sleeping";
    /// <summary>The terminal now reads as available.</summary>
    internal const string TerminalAwakeCode = "terminal-awake";

    /// <summary>One recipient reference as the terminal it addresses, or null when no terminal this session
    /// knows stands behind it. The session supplies the reader, so this half never reaches a second lookup of its
    /// own and a test can hand it one.</summary>
    internal delegate LG_ComputerTerminal? TerminalResolver(EntityReference? reference);

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _ready;
    private readonly TerminalResolver _terminals;
    private readonly Action<string> _report;

    internal MapStateActions(RuntimeKernel kernel, Func<bool> ready,
        TerminalResolver terminals, Action<string> report)
    {
        _kernel = kernel;
        _ready = ready;
        _terminals = terminals;
        _report = report;
    }

    // ---- forge.action.map.interaction_state --------------------------------------------------------------

    internal CommandResult HandleInteraction(CommandContext context)
        => Set(context.Inputs, context.Parameters, context.IsHost);

    internal CommandResult Set(JsonElement inputs, JsonElement parameters, bool isHost)
    {
        if (!isHost || !_ready()) return CommandResult.Rejected(AuthorityCode);
        var targets = Recipients(inputs);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (Operation(parameters) is not { } enable) return CommandResult.Rejected(OperationCode);

        var rows = new List<MapStateRow>(targets.Length);
        bool stop = false;
        foreach (var target in targets)
        {
            if (stop)
            {
                rows.Add(Row(target, CommandStatuses.Rejected, CommitStates.None, NotAttemptedCode, targets.Length));
                continue;
            }
            if (!Resolve(target, out var terminal, out var refusal))
            {
                rows.Add(Row(target, CommandStatuses.Rejected, CommitStates.None, refusal, targets.Length));
                continue;
            }
            var (status, commit, code) = Run(() => Switch(terminal, enable));
            if (commit == CommitStates.Unknown) stop = true;
            rows.Add(Row(target, status, commit, code, targets.Length));
        }
        return Aggregate(rows, "map-state-all-rejected", "map-state-all-unknown");
    }

    /// <summary>One terminal's own availability: the state it reports is the state it holds, so a request for
    /// the state the terminal already reports wrote nothing and is still confirmed.</summary>
    private static MapActionOutcome Switch(LG_ComputerTerminal terminal, bool enable)
    {
        var wanted = enable ? TERM_State.Awake : TERM_State.Sleeping;
        var code = enable ? TerminalAwakeCode : TerminalSleepingCode;
        if (terminal.CurrentStateName == wanted) return MapActionOutcome.AlreadyInState(code);
        terminal.CurrentStateName = wanted;
        return MapActionOutcome.Issued(code);
    }

    /// <summary>Runs one terminal's own native write and turns its outcome into a result row. An entry that was
    /// invoked is a confirmed commit, a terminal that already reads as the requested state is a confirmed commit
    /// that wrote nothing, a refusal is a rejected row with the layer's code, and a call that threw after it was
    /// entered is an unknown commit — the write may or may not have landed, so the terminal is not retried and
    /// the rows after it are not attempted.</summary>
    private (string Status, string CommitState, string Code) Run(Func<MapActionOutcome> entry)
    {
        MapActionOutcome outcome;
        try { outcome = entry(); }
        catch (Exception error)
        {
            _report("map.state-commit-exception: " + error.GetType().Name);
            return (CommandStatuses.Failed, CommitStates.Unknown, CommitExceptionCode);
        }
        switch (outcome.Commit)
        {
            case MapActionCommit.Issued:
            case MapActionCommit.AlreadyInState:
                return (CommandStatuses.Succeeded, CommitStates.Confirmed, outcome.Code);
            default:
                return (CommandStatuses.Rejected, CommitStates.None, outcome.Code);
        }
    }

    /// <summary>One recipient reference as the terminal this row may write. The reference has to be one the
    /// kernel still answers for and the address it carries has to resolve through the terminal reader the
    /// session supplied; a door is refused by name, because this build exposes its interaction as a read with no
    /// write behind it, and a reference no category claims is refused rather than read through the wrong
    /// one.</summary>
    private bool Resolve(EntityReference? reference, out LG_ComputerTerminal terminal, out string refusal)
    {
        terminal = null!;
        if (reference == null) { refusal = KindCode; return false; }
        if (!_kernel.IsEntityCurrent(reference)) { refusal = StaleCode; return false; }
        if (DoorActionCommands.IsDoor(reference)) { refusal = DoorInteractionCode; return false; }
        if (_terminals(reference) is not { } resolved) { refusal = KindCode; return false; }
        terminal = resolved;
        refusal = "";
        return true;
    }

    /// <summary>One recipient's row of the result.</summary>
    private readonly record struct MapStateRow(EntityReference Target, string Status, string CommitState, string Code,
        [property: JsonPropertyName("target_count")] int TargetCount);

    private static MapStateRow Row(EntityReference target, string status, string commit, string code, int count)
        => new(target, status, commit, code, count);

    /// <summary>The command-level conclusion a host-authoritative action ends in, in the same shape the door
    /// rows' own aggregate has: every recipient committed is a success, none is a rejection, and anything in
    /// between is a partial that stays unknown while one row is. The command's code is a single row's own code
    /// when the rows agree, which is what makes a one-recipient command report the native refusal by name.</summary>
    private static CommandResult Aggregate(IReadOnlyList<MapStateRow> rows, string allRejected, string allUnknown)
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
        if (unknown == 0) return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(rows, allRejected), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(rows, allUnknown), "", outputs);
    }

    /// <summary>The one code a command reports: the single row's own code, or the shared code when the rows
    /// disagreed.</summary>
    private static string Single(IReadOnlyList<MapStateRow> rows, string fallback)
    {
        var first = rows[0].Code;
        foreach (var row in rows) if (row.Code != first) return fallback;
        return first;
    }

    private static EntityReference[] Recipients(JsonElement inputs)
        => inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty("targets", out var value)
           && value.ValueKind == JsonValueKind.Array
            ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(value.EnumerateArray(), RuntimeJson.Entity))
            : Array.Empty<EntityReference>();

    /// <summary>The one structural setting a request carries, or null when it is missing or names a setting this
    /// row does not have.</summary>
    private static bool? Operation(JsonElement parameters)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("operation", out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() switch { "enable" => true, "disable" => false, _ => (bool?)null }
            : null;
}
