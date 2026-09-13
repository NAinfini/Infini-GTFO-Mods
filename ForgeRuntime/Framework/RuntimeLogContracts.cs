using System.Collections.Generic;

namespace ForgeRuntime.Framework;

/// <summary>I-DIAG level. Ordered so an enabled check is one integer comparison; host cfg can only choose Off, Error or Info,
/// Trace exists only after <see cref="RuntimeKernel.ElevateLogging"/>.</summary>
public enum RuntimeLogLevel { Off = 0, Error = 1, Info = 2, Trace = 3 }

/// <summary>Rate-limit tier the writer applies; Elevated is irreversible for the session.</summary>
public enum RuntimeLogTier { Player, Elevated }

/// <summary>Single source of I-DIAG event codes. Only the writer's own codes exist until the website code table is ruled.</summary>
public static class RuntimeLogCodes
{
    public const string LogDropped = "log.dropped";
    public const string LogLevel = "log.level";
}

public readonly struct RuntimeLogPlan
{
    public string PlanId { get; init; }
    public string ResourceId { get; init; }
    public string ResourceRevision { get; init; }
}

/// <summary>Status and commit stay strings: their vocabulary is validated once the code-table constants land, not here.</summary>
public readonly struct RuntimeLogResult
{
    public string Status { get; init; }
    public string? Commit { get; init; }
    public string Reason { get; init; }
}

/// <summary>One I-DIAG line before the writer stamps schema, origin and seq. Passed by <c>in</c>, never boxed.
/// It has no message: the sink composes it off the simulation thread, so call sites never build strings.
/// It has no inputs: those belong to the ForgeDevelopment trace recorder and are not implemented.</summary>
public readonly struct RuntimeLogRecord
{
    public RuntimeLogLevel Level { get; init; }
    public string Code { get; init; }
    public string Provider { get; init; }
    /// <summary>The provider a record is about when it is not the owner, e.g. the rejected provider of registration.rejected.</summary>
    public string? SubjectProvider { get; init; }
    public long Tick { get; init; }
    public long WorldEpoch { get; init; }
    public long? Frame { get; init; }
    public string? CommandId { get; init; }
    public string? EventId { get; init; }
    public string? CauseId { get; init; }
    public string? RootEventId { get; init; }
    public RuntimeLogPlan? Plan { get; init; }
    public string? Entry { get; init; }
    public string? Step { get; init; }
    public string? Binding { get; init; }
    public RuntimeLogResult? Result { get; init; }
}

public readonly record struct RuntimeLogProviderLevel(string Provider, RuntimeLogLevel Level);

/// <summary>Immutable level-table snapshot. The kernel replaces the instance whenever levels change, which tells the
/// sink to write log.level again before the next record.</summary>
public sealed class RuntimeLogLevels
{
    internal RuntimeLogLevels(string runtimeProvider, IReadOnlyList<RuntimeLogProviderLevel> providers, RuntimeLogTier tier)
    { RuntimeProvider = runtimeProvider; Providers = providers; Tier = tier; }
    /// <summary>Owner of log.level and log.dropped.</summary>
    public string RuntimeProvider { get; }
    public IReadOnlyList<RuntimeLogProviderLevel> Providers { get; }
    public RuntimeLogTier Tier { get; }
}

/// <summary>Implemented by the host and passed to the RuntimeKernel constructor; there is no registration or replacement API.
/// The kernel calls it only on its simulation thread, after the level gate passed, so implementations must not block,
/// format or serialize inside the call.</summary>
public interface IRuntimeLogSink
{
    void Write(in RuntimeLogRecord record, RuntimeLogLevels levels);
}

/// <summary>Per-provider level field shared by every call site of that provider, so elevation reaches handles created earlier.</summary>
internal sealed class RuntimeLogGate
{
    internal RuntimeLogGate(RuntimeLogLevel level) { Level = level; }
    internal RuntimeLogLevel Level { get; set; }
    internal bool IsEnabled(RuntimeLogLevel level) => level <= Level;
}
