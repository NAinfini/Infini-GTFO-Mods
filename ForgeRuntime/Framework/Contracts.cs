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
    IReadOnlyDictionary<string, Func<EntityReference, bool>>? EntityResolvers = null);

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

public sealed class CommandResult
{
    private CommandResult(string status, string code, string detail, JsonElement outputs, IReadOnlyList<RuntimeFact> facts)
    { Status = status; Code = code; Detail = detail; Outputs = outputs; Facts = facts; }
    public string Status { get; }
    public string Code { get; }
    public string Detail { get; }
    public JsonElement Outputs { get; }
    public IReadOnlyList<RuntimeFact> Facts { get; }
    public static CommandResult Succeeded(JsonElement outputs, params RuntimeFact[] facts)
        {
        RuntimeJson.Require(facts.Length <= 128, "fact-budget", "A command can publish at most 128 committed facts.");
        var outputText = outputs.GetRawText();
        var bytes = Encoding.UTF8.GetByteCount(outputText);
        RuntimeJson.Require(bytes <= RuntimeKernel.MaximumEventPayloadBytes, "result-payload-budget", "Command outputs exceed 64 KiB.");
        var snapshot = new List<RuntimeFact>(facts.Length);
        foreach (var fact in facts)
        {
            var text = fact.Outputs.GetRawText(); var size = Encoding.UTF8.GetByteCount(text); bytes += size;
            RuntimeJson.Require(size <= RuntimeKernel.MaximumEventPayloadBytes && bytes <= 256 * 1024, "result-facts-budget", "Committed facts exceed the 256 KiB result budget.");
            snapshot.Add(new RuntimeFact(fact.BindingId, RuntimeJson.Parse(text)));
        }
        return new("succeeded", "committed", "", RuntimeJson.Parse(outputText), snapshot.AsReadOnly());
    }
    public static CommandResult Rejected(string code, string detail = "")
        => new("rejected", code, detail, RuntimeJson.EmptyObject, Array.Empty<RuntimeFact>());
    public static CommandResult Failed(string code, string detail = "")
        => new("failed", code, detail, RuntimeJson.EmptyObject, Array.Empty<RuntimeFact>());
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

