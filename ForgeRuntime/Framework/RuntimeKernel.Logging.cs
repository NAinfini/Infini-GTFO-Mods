using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeRuntime.Framework;

public sealed partial class RuntimeKernel
{
    private readonly IRuntimeLogSink? logSink;
    private readonly Dictionary<string, RuntimeLogGate> logGates = new(StringComparer.Ordinal);
    private RuntimeLogLevels? logLevels;

    /// <summary>Host constructor. The sink is fixed for this kernel's lifetime. <paramref name="runtimeLogLevel"/> is the host cfg
    /// level of the Runtime's own provider (<see cref="RuntimeIdentity.Id"/>); domain providers are not in the level table yet
    /// because their level arrives with registration, so looking them up is a contract violation rather than a default.</summary>
    public RuntimeKernel(RuntimeIdentity identity, RuntimeLimits limits, IRuntimeLogSink logSink, RuntimeLogLevel runtimeLogLevel)
        : this(identity, limits)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        RuntimeJson.Require(runtimeLogLevel is RuntimeLogLevel.Off or RuntimeLogLevel.Error or RuntimeLogLevel.Info,
            "log-level", "Configured log levels are off, error or info; trace is only reachable through elevation.");
        this.logSink = logSink;
        logGates.Add(identity.Id, new RuntimeLogGate(runtimeLogLevel));
        PublishLogLevels(RuntimeLogTier.Player);
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
}
