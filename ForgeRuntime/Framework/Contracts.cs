using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ForgeRuntime.Framework;

public sealed record RuntimeIdentity(string Id, string Version, string ApiVersion, string GameBuild);

public sealed record RuntimeLimits
{
    public int MaxEntrypoints { get; init; } = 32;
    public int MaxStepsPerEntrypoint { get; init; } = 128;
    public int MaxTotalSteps { get; init; } = 512;
    public int MaxEventsPerTick { get; init; } = 128;
    public int MaxCommandsPerTick { get; init; } = 512;
    public int MaxQueuedEvents { get; init; } = 1024;
    public int MaxCausalDepth { get; init; } = 16;
}

public sealed record EntityReference(string Id, long WorldEpoch, long LifeEpoch);
public sealed record BindingSupport(string BindingId, string Verification, IReadOnlyList<string> RequiredPermissions);
public delegate CommandResult CommandHandler(CommandContext context);

/// <summary>RegistryJson is the same ForgeRegistry seed consumed by the website; handlers do not define alternative node semantics.</summary>
public sealed record RuntimeModule(string ApiVersion, string RegistryJson,
    IReadOnlyDictionary<string, CommandHandler> Handlers, IReadOnlyList<BindingSupport> BindingSupport,
    IReadOnlyDictionary<string, Func<EntityReference, bool>>? EntityResolvers = null)
{
    /// <summary>Optional read-only targeting snapshots, owned by the same registered entity namespace.</summary>
    public IReadOnlyDictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>? EntityObservers { get; init; }
    /// <summary>Optional native-instance lookups, owned by the same registered entity namespace. The input is whatever
    /// native object the owning provider documents; any other object must return null rather than guess.</summary>
    public IReadOnlyDictionary<string, Func<object, EntityReference?>>? EntityInstanceResolvers { get; init; }
}

public sealed record RuntimeEvent(string EventId, string BindingId, long WorldEpoch, long SimulationTick,
    string ScopeId, JsonElement Outputs, EntityReference? Source = null,
    string? CauseId = null, string? RootEventId = null, int CausalDepth = 0);

public sealed record RuntimeFact(string BindingId, JsonElement Outputs);

public sealed class CommandContext
{
    internal CommandContext(RuntimeEvent origin, long tick, string commandId, string planId, string resourceId,
        string resourceRevision, string nodeId, JsonElement parameters, JsonElement inputs)
    {
        EventId = origin.EventId; CauseId = origin.CauseId; RootEventId = origin.RootEventId ?? origin.EventId;
        WorldEpoch = origin.WorldEpoch; SimulationTick = tick; ScheduledTick = origin.SimulationTick;
        ScopeId = origin.ScopeId; Source = origin.Source; CausalDepth = origin.CausalDepth;
        CommandId = commandId; PlanId = planId; ResourceId = resourceId; ResourceRevision = resourceRevision;
        NodeId = nodeId; Parameters = parameters; Inputs = inputs;
    }
    public string EventId { get; }
    public string? CauseId { get; }
    public string RootEventId { get; }
    public long WorldEpoch { get; }
    public long SimulationTick { get; }
    public long ScheduledTick { get; }
    public string ScopeId { get; }
    public EntityReference? Source { get; }
    public int CausalDepth { get; }
    public string CommandId { get; }
    public string PlanId { get; }
    public string ResourceId { get; }
    public string ResourceRevision { get; }
    public string NodeId { get; }
    public bool IsHost => true;
    public JsonElement Parameters { get; }
    public JsonElement Inputs { get; }
    public EntityReference GetEntityInput(string input) => RuntimeJson.Entity(Inputs.GetProperty(input));
}

public static class CommandStatuses
{
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

public static class CommitStates
{
    public const string None = "none";
    public const string Confirmed = "confirmed";
    public const string Unknown = "unknown";
}

public sealed class CommandResult
{
    public const int MaximumFacts = 128;
    public const int MaximumDetailBytes = 64 * 1024;
    private const int MaximumResultBytes = 256 * 1024;

    private CommandResult(string status, string commitState, string code, string detail, JsonElement outputs, IReadOnlyList<RuntimeFact> facts)
    {
        Status = status;
        CommitState = commitState;
        Code = code;
        Detail = detail;
        Outputs = outputs;
        Facts = facts;
    }

    public string Status { get; }
    public string CommitState { get; }
    public string Code { get; }
    public string Detail { get; }
    public JsonElement Outputs { get; }
    public IReadOnlyList<RuntimeFact> Facts { get; }

    public static CommandResult Succeeded(JsonElement outputs, params RuntimeFact[] facts)
        => Create(CommandStatuses.Succeeded, CommitStates.Confirmed, "committed", "", outputs, facts);

    public static CommandResult Partial(JsonElement outputs, string commitState, params RuntimeFact[] facts)
        => Create(CommandStatuses.Partial, commitState, "partial", "", outputs, facts);

    public static CommandResult Partial(JsonElement outputs, params RuntimeFact[] facts)
        => Partial(outputs, CommitStates.Confirmed, facts);

    public static CommandResult Rejected(string code, string detail = "")
        => Create(CommandStatuses.Rejected, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult Failed(string code, string detail = "")
        => Create(CommandStatuses.Failed, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult FailedUnknown(string code, string detail = "", params RuntimeFact[] facts)
        => Create(CommandStatuses.Failed, CommitStates.Unknown, code, detail, RuntimeJson.EmptyObject, facts);

    public static CommandResult FailedUnknown(JsonElement outputs, string code, string detail = "", params RuntimeFact[] facts)
        => Create(CommandStatuses.Failed, CommitStates.Unknown, code, detail, outputs, facts);

    public static CommandResult Cancelled(string code, string detail = "")
        => Create(CommandStatuses.Cancelled, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult Expired(string code, string detail = "")
        => Create(CommandStatuses.Expired, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult Create(string status, string commitState, string code, string detail,
        JsonElement outputs, params RuntimeFact[] facts)
    {
        var safeStatus = RuntimeJson.Text(status);
        var safeCommitState = RuntimeJson.Text(commitState);
        var safeCode = RuntimeJson.Text(code);
        var safeDetail = ValidatedDetail(detail);
        var safeOutputs = SnapshotOutputs(outputs);
        var safeFacts = SnapshotFacts(safeOutputs, facts);
        return new CommandResult(safeStatus, safeCommitState, safeCode, safeDetail, safeOutputs, safeFacts);
    }

    private static string ValidatedDetail(string? detail)
    {
        detail ??= "";
        RuntimeJson.Require(Encoding.UTF8.GetByteCount(detail) <= MaximumDetailBytes, "result-detail-budget", "Command detail exceeds 64 KiB.");
        return detail;
    }

    private static JsonElement SnapshotOutputs(JsonElement outputs)
    {
        RuntimeJson.Require(outputs.ValueKind != JsonValueKind.Undefined, "invalid-result-output", "Command outputs must be a JSON value.");
        var text = outputs.GetRawText();
        RuntimeJson.Require(Encoding.UTF8.GetByteCount(text) <= RuntimeKernel.MaximumEventPayloadBytes, "result-payload-budget", "Command outputs exceed 64 KiB.");
        return RuntimeJson.Parse(text);
    }

    private static IReadOnlyList<RuntimeFact> SnapshotFacts(JsonElement outputs, RuntimeFact[] facts)
    {
        if (facts == null) throw new RuntimeContractException("invalid-result-facts", "Fact collection cannot be null.");
        RuntimeJson.Require(facts.Length <= MaximumFacts, "fact-budget", "A command can publish at most 128 committed facts.");
        var snapshot = new List<RuntimeFact>(facts.Length);
        long bytes = Encoding.UTF8.GetByteCount(outputs.GetRawText());
        foreach (var fact in facts)
        {
            if (fact == null) throw new RuntimeContractException("invalid-result-fact", "Committed fact cannot be null.");
            var bindingId = RuntimeJson.Text(fact.BindingId);
            RuntimeJson.Require(fact.Outputs.ValueKind != JsonValueKind.Undefined, "invalid-fact-output", "Committed fact outputs must be a JSON value.");
            var text = fact.Outputs.GetRawText();
            var size = Encoding.UTF8.GetByteCount(text);
            RuntimeJson.Require(size <= RuntimeKernel.MaximumEventPayloadBytes, "result-fact-payload-budget", "Committed fact exceeds 64 KiB.");
            bytes += size;
            RuntimeJson.Require(bytes <= MaximumResultBytes, "result-facts-budget", "Committed facts exceed the 256 KiB result budget.");
            snapshot.Add(new RuntimeFact(bindingId, RuntimeJson.Parse(text)));
        }
        return snapshot.AsReadOnly();
    }

    internal static string TruncateCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "unknown";
        var text = code!.Trim();
        if (text.Length > 256) text = text[..256];
        foreach (var c in text) if (c <= 31 || c == 127) return "invalid-code";
        return text;
    }

    internal static string TruncateDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return "";
        return detail!.Length <= 4096 ? detail : detail[..4096];
    }
}

internal static class CommandResultRules
{
    internal static bool TryValidate(CommandResult result, out string violation)
    {
        violation = "";
        if (result.Status is not (CommandStatuses.Succeeded or CommandStatuses.Partial or CommandStatuses.Rejected
            or CommandStatuses.Failed or CommandStatuses.Cancelled or CommandStatuses.Expired))
        {
            violation = "unknown-status";
            return false;
        }

        switch (result.Status)
        {
            case CommandStatuses.Succeeded:
                if (result.CommitState != CommitStates.Confirmed) { violation = "succeeded-requires-confirmed"; return false; }
                return true;
            case CommandStatuses.Partial:
                if (result.CommitState is not (CommitStates.Confirmed or CommitStates.Unknown)) { violation = "partial-commit-state"; return false; }
                if (result.Facts.Count == 0) { violation = "partial-requires-known-commit"; return false; }
                return true;
            case CommandStatuses.Rejected:
            case CommandStatuses.Cancelled:
            case CommandStatuses.Expired:
                if (result.CommitState != CommitStates.None) { violation = "terminal-status-requires-none"; return false; }
                if (result.Facts.Count != 0) { violation = "terminal-status-cannot-carry-facts"; return false; }
                return true;
            case CommandStatuses.Failed:
                if (result.CommitState is not (CommitStates.None or CommitStates.Unknown)) { violation = "failed-commit-state"; return false; }
                if (result.CommitState == CommitStates.None && result.Facts.Count != 0) { violation = "failed-none-cannot-carry-facts"; return false; }
                return true;
            default:
                violation = "unknown-status";
                return false;
        }
    }
}

public sealed record DispatchResult(string Status, string Code, string EventId);
public sealed record CommandReceipt(string CommandId, string EventId, string? CauseId, string RootEventId,
    string PlanId, string ResourceId, string ResourceRevision, string NodeId, string BindingId,
    long WorldEpoch, long SimulationTick, CommandResult Result);
public sealed record EventReceipt(string EventId, string Status, string Code);
public sealed record TickResult(int EventsProcessed, int CommandsExecuted, int DeferredEvents,
    IReadOnlyList<CommandReceipt> Commands, IReadOnlyList<EventReceipt> Events)
{
    public IReadOnlyList<ScheduleReceipt> Schedules { get; init; } = Array.Empty<ScheduleReceipt>();
    public IReadOnlyList<StateLeaseReceipt> StateLeases { get; init; } = Array.Empty<StateLeaseReceipt>();
}

public sealed class RuntimeContractException : Exception
{
    public RuntimeContractException(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
}

