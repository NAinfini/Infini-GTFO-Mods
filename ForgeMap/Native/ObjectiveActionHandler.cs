using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMap;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The three `forge.action.map.*` command handlers: the objective machine's interaction channel and the level's
/// own exit item. One instance per Map session, handed to the one registration that declares the rows;
/// `CanExecute` is the session's own readiness gate and the two lookups are the native singletons the native half
/// already reaches.
///
/// Every handler is one row per request rather than one row per target, and that shape is native rather than
/// chosen: `WardenObjectiveManager.AttemptInteract` names exactly one layer and one chain in the struct it
/// carries, and the exit item belongs to the level. A request that addresses several objectives at once could
/// only be served by looping the same entry, and a row per target would then describe the loop instead of what
/// the entry did. The rows the catalog's result schema declares are written with the one target count the request
/// really had.
///
/// The whole plan-level request check runs before the first write, because a command that cannot be carried out
/// as asked must not half-apply: a resource reference, a recipients collection and an expected value with no
/// native check are each refused by name.
/// </summary>
internal sealed class ObjectiveActionHandler
{
    /// <summary>The one authority refusal, spelled the way the other Map and Weapon handlers spell it.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    /// <summary>A documented port the native path cannot carry was supplied by the plan.</summary>
    internal const string ObjectivesUnsupported = "objective-target-unsupported";
    internal const string ExtractionsUnsupported = "extraction-target-unsupported";
    internal const string ParticipantsUnsupported = "participants-unsupported";
    internal const string StateMismatch = "objective-state-mismatch";
    internal const string PhaseMismatch = "objective-phase-mismatch";
    internal const string NativeCommitException = "native-commit-exception";
    /// <summary>A whole-layer member was asked for with a chain, or the opposite.</summary>
    internal const string TargetShapeUnsupported = "objective-target-shape-unsupported";

    /// <summary>The state members this row carries, and the interaction each one reaches. The names are the
    /// vocabulary this provider publishes for the catalog's `state` input: the member's own native name, in the
    /// case the enum declares it. The interaction types the sibling rows own are deliberately absent — a request
    /// for one of them is refused by name instead of being served under a row that does not describe it.</summary>
    internal static readonly IReadOnlyDictionary<string, ObjectiveStateKind> StateMembers =
        new Dictionary<string, ObjectiveStateKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["DiscoverObjective"] = ObjectiveStateKind.Discover,
            ["StartObjective"] = ObjectiveStateKind.Start,
            ["UpdateSubObjective"] = ObjectiveStateKind.UpdateSubObjective,
            ["CustomSubObjectiveUpdate"] = ObjectiveStateKind.CustomSubObjectiveUpdate,
            ["SetItemSolved"] = ObjectiveStateKind.SetItemSolved,
            ["SetExtraTime"] = ObjectiveStateKind.SetExtraTime,
            ["SetSolveOnDeath"] = ObjectiveStateKind.SetSolveOnDeath,
            ["SetExitWaveTriggered"] = ObjectiveStateKind.SetExitWaveTriggered
        };

    /// <summary>The phase members this row carries: the objective's own event chain step, the win-condition
    /// member, and the whole-layer force completion. `CompleteChain` is this provider's own name for the one
    /// member that is not an interaction type.</summary>
    internal static readonly IReadOnlyDictionary<string, ObjectivePhaseKind> PhaseMembers =
        new Dictionary<string, ObjectivePhaseKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["EventUpdate"] = ObjectivePhaseKind.EventUpdate,
            ["SolveWinCondition"] = ObjectivePhaseKind.SolveWinCondition,
            ["CompleteChain"] = ObjectivePhaseKind.CompleteChain
        };

    /// <summary>One result row in the catalog's own field order. `target` is left out: the target of these
    /// actions is an objective, a landing or a session, and none of them is an entity this runtime tracks, so
    /// writing a reference here would name an object that no kind resolves.</summary>
    private sealed record ActionResultRow(string Status,
        [property: JsonPropertyName("committed")] string CommitState, string Code,
        [property: JsonPropertyName("target_count")] int TargetCount);

    private readonly Func<bool> _canExecute;
    private readonly Func<WardenObjectiveManager?> _objectiveManager;
    private readonly Func<ElevatorShaftLanding?> _landing;
    private readonly Action<string> _report;

    internal ObjectiveActionHandler(Func<bool> canExecute, Func<WardenObjectiveManager?> objectiveManager,
        Func<ElevatorShaftLanding?> landing, Action<string> report)
    {
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _objectiveManager = objectiveManager ?? throw new ArgumentNullException(nameof(objectiveManager));
        _landing = landing ?? throw new ArgumentNullException(nameof(landing));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The production wiring: the session's own readiness gate plus the two native singletons, each
    /// read late through the property that owns it. Every lookup answers the game's current instance — the
    /// manager and the landing are process-wide singletons, and a world that was torn down leaves the property
    /// null, which is what the handlers refuse as `objective-unavailable`. The lookups are read on every request
    /// rather than captured once, because the instance behind each one is replaced when a level is rebuilt.</summary>
    internal static ObjectiveActionHandler For(Func<bool> canExecute, Action<string> report)
        => new(canExecute, () => WardenObjectiveManager.Current, () => ElevatorShaftLanding.Current, report);

    /// <summary>The single handler body, bound to this instance. The session hands one method group per binding
    /// to the registration that declares it.</summary>
    internal CommandResult HandleState(CommandContext context) => State(context);
    internal CommandResult HandlePhase(CommandContext context) => Phase(context);
    internal CommandResult HandleExtraction(CommandContext context) => Extraction(context);

    /// <summary>The `forge.action.map.objective_state` command.</summary>
    internal CommandResult State(CommandContext context)
    {
        if (!Authoritative()) return CommandResult.Rejected(AuthorityCode);
        if (Present(context.Inputs, "objectives")) return CommandResult.Rejected(ObjectivesUnsupported);
        var request = Read(context.Parameters);
        if (!request.Ok) return CommandResult.Rejected(request.Code!);
        string? state = Text(context.Inputs, "state");
        if (state == null || !StateMembers.TryGetValue(state, out var kind))
            return CommandResult.Rejected(ObjectiveActions.StateKindUnknown);
        if (!Expected(context.Inputs, "expected_state", state)) return CommandResult.Rejected(StateMismatch);
        var manager = _objectiveManager();
        if (manager == null) return CommandResult.Rejected(ObjectiveActions.Unavailable);

        var outcome = ObjectiveActions.SetState(manager, request.Target, kind, null,
            request.SubObjective, request.ItemId, request.ExtraTime);
        return Report(outcome);
    }

    /// <summary>The `forge.action.map.objective_phase` command.</summary>
    internal CommandResult Phase(CommandContext context)
    {
        if (!Authoritative()) return CommandResult.Rejected(AuthorityCode);
        if (Present(context.Inputs, "objectives")) return CommandResult.Rejected(ObjectivesUnsupported);
        var request = Read(context.Parameters);
        if (!request.Ok) return CommandResult.Rejected(request.Code!);
        string? phase = Text(context.Inputs, "phase");
        if (phase == null || !PhaseMembers.TryGetValue(phase, out var kind))
            return CommandResult.Rejected(ObjectiveActions.PhaseKindUnknown);
        if (!Expected(context.Inputs, "expected_phase", phase)) return CommandResult.Rejected(PhaseMismatch);
        // The interaction channel is the forcing form: it carries no expected phase for a strict request to be
        // checked against, so `strict` is refused by name rather than served as a forced transition.
        if (request.TransitionPolicy != "force") return CommandResult.Rejected(ForcePolicyRequired);
        var manager = _objectiveManager();
        if (manager == null) return CommandResult.Rejected(ObjectiveActions.Unavailable);

        var outcome = ObjectiveActions.SetPhase(manager, request.Target, kind, null,
            new ObjectiveEventStep(request.EventBreakIndex, request.EventIndex));
        return Report(outcome);
    }

    /// <summary>The `forge.action.map.extraction_enable` command.</summary>
    internal CommandResult Extraction(CommandContext context)
    {
        if (!Authoritative()) return CommandResult.Rejected(AuthorityCode);
        if (Present(context.Inputs, "extractions")) return CommandResult.Rejected(ExtractionsUnsupported);
        if (Present(context.Inputs, "participants")) return CommandResult.Rejected(ParticipantsUnsupported);
        var request = Read(context.Parameters);
        if (!request.Ok) return CommandResult.Rejected(request.Code!);
        // The exit item belongs to the level the process stands in, so the landing is the one the native half
        // already holds. The layer parameter selects which layer's win condition the request is about and is
        // checked against the layer the manager keeps its data for.
        if (!ObjectiveActions.Reachable(ObjectiveTarget.LayerOnly(request.Target.Layer)))
            return CommandResult.Rejected(ObjectiveActions.NoObjectiveData);
        var landing = _landing();
        if (landing == null) return CommandResult.Rejected(ObjectiveActions.Unavailable);
        var element = context.Inputs.GetProperty("enabled");
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return CommandResult.Rejected(ExtractionEnabledRequired);

        return Report(ObjectiveActions.SetExtraction(landing, element.GetBoolean()));
    }

    /// <summary>Whether this side may submit at all: the session's own readiness gate. The kernel's lifecycle
    /// snapshot already carries the host flag, which is the same authority the native objective manager and the
    /// landing are their state replicators for.</summary>
    private bool Authoritative()
    {
        try { return _canExecute(); }
        catch (Exception) { return false; }
    }

    /// <summary>One action's own outcome as the command result: the code the layer decided, and the one row the
    /// request had. A refusal is a rejection with no commit, an invoked entry is a success whose commit is
    /// confirmed — the layer only ever answers `Issued` after the native call returned — and a native call that
    /// threw is an unknown commit, because the entry may already have written its replicated state.</summary>
    private CommandResult Report(MapActionOutcome outcome)
    {
        string status = outcome.Commit switch
        {
            MapActionCommit.Issued => CommandStatuses.Succeeded,
            MapActionCommit.AlreadyInState => CommandStatuses.Succeeded,
            _ => CommandStatuses.Rejected
        };
        string commit = outcome.Commit == MapActionCommit.Refused ? CommitStates.None : CommitStates.Confirmed;
        if (outcome.Commit == MapActionCommit.Refused && outcome.Code == ObjectiveActions.Unavailable)
            return CommandResult.Rejected(outcome.Code);
        var rows = new[] { new ActionResultRow(status, commit, outcome.Code, 1) };
        return CommandResult.Create(status, commit, outcome.Code, "", RuntimeJson.From(new { results = rows }));
    }

    /// <summary>What one request's structural parameters say. `Ok` is false when a parameter the native call
    /// cannot be made without is missing or names a member the layer does not have, and `Code` then carries the
    /// one reason; every other member is read only for the members that take it.</summary>
    private readonly struct Request
    {
        internal Request(bool ok, string? code, ObjectiveTarget target, string transitionPolicy,
            int subObjective, int itemId, float extraTime, int eventBreakIndex, int eventIndex)
        {
            Ok = ok; Code = code; Target = target; TransitionPolicy = transitionPolicy;
            SubObjective = subObjective; ItemId = itemId; ExtraTime = extraTime;
            EventBreakIndex = eventBreakIndex; EventIndex = eventIndex;
        }

        internal bool Ok { get; }
        internal string? Code { get; }
        internal ObjectiveTarget Target { get; }
        internal string TransitionPolicy { get; }
        internal int SubObjective { get; }
        internal int ItemId { get; }
        internal float ExtraTime { get; }
        internal int EventBreakIndex { get; }
        internal int EventIndex { get; }

        internal static Request Refused(string code) => new(false, code, default, "", 0, ObjectiveActions.NoItem, 0, 0, 0);
    }

    /// <summary>The one place the structural parameters are decoded. The enum parameters arrive as the member
    /// names the row inlines, because the kernel resolves a structural enum's compiled index before a handler
    /// reads it; a name outside the row's own list is refused rather than mapped to a nearby layer.</summary>
    private static Request Read(JsonElement parameters)
    {
        string? layer = Text(parameters, "layer");
        int index = layer == null ? -1 : Array.IndexOf(ObjectiveActionContract.Layers, layer);
        if (index < 0) return Request.Refused(LayerUnknown);
        var target = new ObjectiveTarget((LG_LayerType)index,
            Integer(parameters, "chain", -1));
        return new Request(true, null, target, Text(parameters, "transition_policy") ?? "force",
            Integer(parameters, "sub_objective", 0), Integer(parameters, "item_id", ObjectiveActions.NoItem),
            (float)Number(parameters, "extra_time", 0),
            Integer(parameters, "event_break_index", 0), Integer(parameters, "event_index", 0));
    }

    /// <summary>Whether the request's own expected value agrees with the member it named. The check is the one
    /// this provider can make without a native read: the expected value must name the same member the request
    /// asks for, and the member is the last dot-separated segment of either spelling (`objective.SetExtraTime`
    /// and `SetExtraTime` both name it). An absent expected value asks for nothing and always agrees.</summary>
    private static bool Expected(JsonElement inputs, string port, string member)
    {
        string? expected = Text(inputs, port);
        if (expected == null) return true;
        int dot = expected.LastIndexOf('.');
        string tail = dot >= 0 ? expected[(dot + 1)..] : expected;
        return string.Equals(tail, member, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a plan actually supplied an input: absent, or a present null, is the same "not asked
    /// for". A collection input is only meaningful when it holds something.</summary>
    internal static bool Present(JsonElement inputs, string port)
        => inputs.TryGetProperty(port, out var value)
           && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
           && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0);

    private static string? Text(JsonElement bag, string name)
        => bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Integer(JsonElement bag, string name, int fallback)
        => bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : fallback;

    private static double Number(JsonElement bag, string name, double fallback)
        => bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : fallback;

    internal const string LayerUnknown = "objective-layer-unknown";
    internal const string ExtractionEnabledRequired = "extraction-enabled-required";
    internal const string ForcePolicyRequired = "objective-transition-policy-unsupported";
}
