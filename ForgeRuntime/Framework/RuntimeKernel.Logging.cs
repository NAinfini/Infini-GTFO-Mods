using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeRuntime.Framework;

public sealed partial class RuntimeKernel
{
    private readonly IRuntimeLogSink? logSink;
    private readonly Dictionary<string, RuntimeLogGate> logGates = new(StringComparer.Ordinal);
    private readonly RuntimeLogLevel runtimeLogLevel;
    private RuntimeLogLevels? logLevels;

    /// <summary>Host constructor. The sink is fixed for this kernel's lifetime. <paramref name="runtimeLogLevel"/> is the host cfg
    /// level of the Runtime's own provider (<see cref="RuntimeIdentity.Id"/>); a domain provider has no level until its package
    /// hands one over with <see cref="RuntimeKernel.RegisterModule"/>, so looking one up earlier is a contract violation
    /// rather than a default.</summary>
    public RuntimeKernel(RuntimeIdentity identity, RuntimeLimits limits, IRuntimeLogSink logSink, RuntimeLogLevel runtimeLogLevel)
        : this(identity, limits)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        RuntimeJson.Require(runtimeLogLevel is RuntimeLogLevel.Off or RuntimeLogLevel.Error or RuntimeLogLevel.Info,
            "log-level", "Configured log levels are off, error or info; trace is only reachable through elevation.");
        this.logSink = logSink;
        this.runtimeLogLevel = runtimeLogLevel;
        logGates.Add(identity.Id, new RuntimeLogGate(runtimeLogLevel));
        PublishLogLevels(RuntimeLogTier.Player);
    }

    /// <summary>Adds the entry a registering provider's level needs, without republishing the table: registration publishes
    /// the new table and then emits its <c>binding.registered</c> records against it, so every record carries a table that
    /// already lists its own provider and one registration never leaves two tables behind. A provider registered after
    /// elevation is Trace like every other one, never its cfg value; a kernel without a sink keeps no table at all. The
    /// Runtime's own entry belongs to the host constructor, so a module claiming that provider id can neither replace nor
    /// drop it.</summary>
    private void RegisterLogGate(string providerId, RuntimeLogLevel level)
    {
        if (logSink == null || providerId == Identity.Id) return;
        logGates[providerId] = new RuntimeLogGate(logLevels!.Tier == RuntimeLogTier.Elevated ? RuntimeLogLevel.Trace : level);
    }

    /// <summary>Drops the entry of an unregistered provider and republishes the table. The Runtime's own entry belongs to the
    /// host constructor, not to a module, so a module claiming that provider id can never take Runtime logging down with it.</summary>
    private void UnregisterLogGate(string providerId)
    {
        if (logSink == null || providerId == Identity.Id) return;
        logGates.Remove(providerId);
        PublishLogLevels(logLevels!.Tier);
    }

    /// <summary>Raises every provider to Trace for the rest of the session. Accepted once, only while registration is open,
    /// so the whole session is recorded under one tier and one set of limits.</summary>
    public void ElevateLogging()
    {
        Mutable();
        RuntimeJson.Require(logLevels != null, "log-unconfigured", "This kernel was constructed without a log sink.");
        RuntimeJson.Require(IsRegistrationOpen, "log-elevation-rejected", "Log elevation is only accepted before StartRuntime closes registration.");
        RuntimeJson.Require(logLevels!.Tier == RuntimeLogTier.Player, "log-elevation-rejected", "Log elevation is accepted once and cannot be undone.");
        foreach (var gate in logGates.Values) gate.Level = RuntimeLogLevel.Trace;
        PublishLogLevels(RuntimeLogTier.Elevated);
    }

    internal RuntimeLogGate LogGate(string providerId)
    {
        ReadThread();
        RuntimeJson.Require(logGates.TryGetValue(providerId, out var gate), "log-provider-unregistered", providerId);
        return gate!;
    }

    /// <summary>The only path from a record point to the sink. Call sites compare their gate first; reaching here with a
    /// disabled level means that branch was skipped.</summary>
    internal void WriteLog(in RuntimeLogRecord record)
    {
        ReadThread();
        RuntimeJson.Require(logSink != null, "log-unconfigured", "This kernel was constructed without a log sink.");
        RuntimeJson.Require(record.Code != null && record.Provider != null
            && (record.Plan is not { } plan || (plan.PlanId != null && plan.ResourceId != null && plan.ResourceRevision != null))
            && (record.Result is not { } result || (result.Status != null && result.Reason != null)),
            "log-record", "A log record needs code, provider, complete plan and result fields.");
        RuntimeJson.Require(record.Level != RuntimeLogLevel.Off && LogGate(record.Provider!).IsEnabled(record.Level),
            "log-level-disabled", record.Code!);
        logSink!.Write(in record, logLevels!);
    }

    private void PublishLogLevels(RuntimeLogTier tier)
        => logLevels = new RuntimeLogLevels(Identity.Id,
            logGates.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new RuntimeLogProviderLevel(x.Key, x.Value.Level)).ToArray(), tier);

    // ---- Kernel record points (§3.2 event code and reason code tables). ------------------------------------------
    // Every point below is one gate comparison while disabled: the level of a code is fixed by the table, so a site never
    // inspects a status to decide whether to look. The record is a readonly struct handed to the sink by `in`, and no site
    // concatenates a string — the writer composes the message off the simulation thread.

    /// <summary>The provider a binding belongs to. `step.*` and `trigger.fired` are attributed to it rather than to Runtime.</summary>
    private string BindingProvider(string bindingId) => RuntimeJson.Text(registry.Bindings[bindingId], "providerId");

    /// <summary>Event, plan and entry behind one executed step. Read-only and passed by `in`, so a record point adds no
    /// allocation to the dispatch loop; the entry node id is the `entry` field of `step.*` and `entry.stopped`.</summary>
    internal readonly struct StepOrigin
    {
        internal StepOrigin(in RuntimeEvent origin, in RuntimeLogPlan plan, string entry)
        {
            EventId = origin.EventId; CauseId = origin.CauseId; RootEventId = origin.RootEventId ?? origin.EventId;
            Plan = plan; Entry = entry;
        }
        internal string EventId { get; }
        internal string? CauseId { get; }
        internal string RootEventId { get; }
        internal RuntimeLogPlan Plan { get; }
        internal string Entry { get; }
    }

    private bool RuntimeErrorEnabled => logSink != null && LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Error);

    private void LogRegistrationRejected(string provider, string code, string detail)
    {
        if (!RuntimeErrorEnabled) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.RegistrationRejected, Provider = Identity.Id,
            SubjectProvider = provider == Identity.Id ? null : provider, Tick = CurrentTick, WorldEpoch = WorldEpoch, Detail = detail,
            Result = new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = code } });
    }

    private void LogBindingRegistered(string provider, string bindingId)
    {
        if (logSink == null || !LogGate(provider).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Info, Code = RuntimeLogCodes.BindingRegistered, Provider = provider,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, Binding = bindingId });
    }

    private void LogWorldBegan()
    {
        if (logSink == null || !LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Info, Code = RuntimeLogCodes.WorldBegan, Provider = Identity.Id,
            Tick = CurrentTick, WorldEpoch = WorldEpoch });
    }

    /// <summary>Enqueue of any event: a domain publish, a forwarded committed fact or an admitted schedule pulse. The record
    /// belongs to the provider that owns the trigger binding, and `rootEventId` falls back to the event's own id at a root.</summary>
    private void LogTriggerFired(string provider, string bindingId, string eventId, string? causeId, string rootEventId)
    {
        if (logSink == null || !LogGate(provider).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Info, Code = RuntimeLogCodes.TriggerFired, Provider = provider,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, EventId = eventId, CauseId = causeId, RootEventId = rootEventId, Binding = bindingId });
    }

    /// <summary>An event refused outside the dispatch budget checks: a publish, a schedule or the non-host queue drain.
    /// <paramref name="subjectProvider"/> is the publisher when the kernel still knows it, and null when the refusal
    /// happened before that was resolved.</summary>
    private void LogEventRejected(string bindingId, string eventId, string code, string? subjectProvider = null)
    {
        if (!RuntimeErrorEnabled) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.EventRejected, Provider = Identity.Id,
            SubjectProvider = subjectProvider, Tick = CurrentTick, WorldEpoch = WorldEpoch, EventId = eventId, Binding = bindingId,
            Result = new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = code } });
    }

    /// <summary>A dispatch-phase budget refusal. The code table keeps it apart from `event.rejected`, so one refusal is
    /// recorded once.</summary>
    private void LogBudgetExceeded(string bindingId, string eventId, string code)
    {
        if (!RuntimeErrorEnabled) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.BudgetExceeded, Provider = Identity.Id,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, EventId = eventId, Binding = bindingId,
            Result = new RuntimeLogResult { Status = CommandStatuses.Rejected, Reason = code } });
    }

    /// <summary>At most one per tick: an event left at the queue head by an exhausted per-tick or per-plan budget.</summary>
    private void LogEventDeferred(string bindingId, string eventId, string code)
    {
        if (logSink == null || !LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Trace, Code = RuntimeLogCodes.EventDeferred, Provider = Identity.Id,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, EventId = eventId, Binding = bindingId,
            Result = new RuntimeLogResult { Status = "deferred", Reason = code } });
    }

    /// <summary>A queued event dropped before dispatch because its publisher or scope is gone, or its plan was unloaded.
    /// The publisher is still known here, so it is written as the subject provider.</summary>
    private void LogEventCancelled(string bindingId, string eventId, string code, string subjectProvider)
    {
        if (logSink == null || !LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Trace, Code = RuntimeLogCodes.EventCancelled, Provider = Identity.Id,
            SubjectProvider = subjectProvider, Tick = CurrentTick, WorldEpoch = WorldEpoch, EventId = eventId, Binding = bindingId,
            Result = new RuntimeLogResult { Status = CommandStatuses.Cancelled, Reason = code } });
    }

    private void LogStepStarted(in StepOrigin origin, string provider, string step, string bindingId, string commandId)
    {
        if (logSink == null || !LogGate(provider).IsEnabled(RuntimeLogLevel.Trace)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Trace, Code = RuntimeLogCodes.StepStarted, Provider = provider,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, CommandId = commandId, EventId = origin.EventId, CauseId = origin.CauseId,
            RootEventId = origin.RootEventId, Plan = origin.Plan, Entry = origin.Entry, Step = step, Binding = bindingId });
    }

    private void LogStepFinished(in StepOrigin origin, string provider, string step, string bindingId, string commandId, in CommandResult result)
    {
        var level = StepLevel(result);
        if (logSink == null || !LogGate(provider).IsEnabled(level)) return;
        WriteLog(new RuntimeLogRecord { Level = level, Code = RuntimeLogCodes.StepFinished, Provider = provider,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, CommandId = commandId, EventId = origin.EventId, CauseId = origin.CauseId,
            RootEventId = origin.RootEventId, Plan = origin.Plan, Entry = origin.Entry, Step = step, Binding = bindingId,
            Result = new RuntimeLogResult { Status = result.Status, Commit = result.CommitState, Reason = result.Code } });
    }

    /// <summary>Step result codes are the kernel's own: nothing is normalized here, and a code the reason table does not list
    /// is a table gap to report rather than something the kernel invents.</summary>
    private static RuntimeLogLevel StepLevel(in CommandResult result)
        => result.Status == CommandStatuses.Failed || result.CommitState == CommitStates.Unknown
            ? RuntimeLogLevel.Error : RuntimeLogLevel.Info;

    private void LogEntryStopped(in StepOrigin origin, string step, string commandId)
    {
        if (logSink == null || !LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Info)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Info, Code = RuntimeLogCodes.EntryStopped, Provider = Identity.Id,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, CommandId = commandId, EventId = origin.EventId, Plan = origin.Plan,
            Entry = origin.Entry, Step = step });
    }

    /// <summary>An observer callback threw and the kernel removed it. The record belongs to the observer's provider.</summary>
    private void LogObserverFailed(string provider, string detail)
    {
        if (logSink == null || !LogGate(provider).IsEnabled(RuntimeLogLevel.Error)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.ObserverFailed, Provider = provider,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, Detail = detail,
            Result = new RuntimeLogResult { Status = CommandStatuses.Failed, Reason = RuntimeLogReasonCodes.LifecycleObserverFailed } });
    }

    /// <summary>One record per pause, including a failed startup and a stop. <paramref name="detail"/> is the host's own text.</summary>
    public void LogSuspended(string code, string? detail = null)
    {
        ReadThread();
        if (logSink == null || !LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Error)) return;
        WriteLog(new RuntimeLogRecord { Level = RuntimeLogLevel.Error, Code = RuntimeLogCodes.RuntimeSuspended, Provider = Identity.Id,
            Tick = CurrentTick, WorldEpoch = WorldEpoch, Detail = detail,
            Result = new RuntimeLogResult { Status = CommandStatuses.Failed, Reason = code } });
    }
}
