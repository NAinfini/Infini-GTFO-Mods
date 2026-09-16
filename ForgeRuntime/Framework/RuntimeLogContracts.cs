using System;
using System.Collections.Generic;

namespace ForgeRuntime.Framework;

/// <summary>I-DIAG level. Ordered so an enabled check is one integer comparison; host cfg can only choose Off, Error or Info,
/// Trace exists only after <see cref="RuntimeKernel.ElevateLogging"/>.</summary>
public enum RuntimeLogLevel { Off = 0, Error = 1, Info = 2, Trace = 3 }

/// <summary>The one player-tier `Logging.Level` vocabulary, shared by the Runtime cfg and every package cfg: `off`, `error`
/// or `info`, case and surrounding whitespace insensitive. The SDK reads no cfg of its own; a package plugin reads its own
/// entry and parses the raw text here, so every package accepts the same values and rejects a malformed one with the same
/// message. `trace` is deliberately absent: only elevation reaches it.</summary>
public static class RuntimeLogConfiguration
{
    public static RuntimeLogLevel ParseLevel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "off" => RuntimeLogLevel.Off,
        "error" => RuntimeLogLevel.Error,
        "info" => RuntimeLogLevel.Info,
        _ => throw new InvalidOperationException("Unsupported Forge Logging.Level; use off, error or info. Native startup was not attempted.")
    };
}

/// <summary>Rate-limit tier the writer applies; Elevated is irreversible for the session.</summary>
public enum RuntimeLogTier { Player, Elevated }

/// <summary>Single source of the I-DIAG event codes the kernel writes. `adapter.attached` and
/// `adapter.failed` are absent until I-ADAPTER-SCHEMA rules them, and so are the per-package native diagnostic codes.</summary>
public static class RuntimeLogCodes
{
    public const string LogDropped = "log.dropped";
    public const string LogLevel = "log.level";
    public const string PlanLoaded = "plan.loaded";
    public const string PlanRejected = "plan.rejected";
    public const string RegistrationRejected = "registration.rejected";
    public const string BindingRegistered = "binding.registered";
    public const string WorldBegan = "world.began";
    public const string TriggerFired = "trigger.fired";
    public const string EventRejected = "event.rejected";
    public const string BudgetExceeded = "budget.exceeded";
    public const string EventDeferred = "event.deferred";
    public const string EventCancelled = "event.cancelled";
    public const string StepStarted = "step.started";
    public const string StepFinished = "step.finished";
    public const string EntryStopped = "entry.stopped";
    public const string ObserverFailed = "observer.failed";
    public const string RuntimeSuspended = "runtime.suspended";
    /// <summary>The one layout-bearing code. It is written by the kernel on behalf of the authoring layer that
    /// observed the generated level, because the per-package record point does not exist yet; the layout itself
    /// is <see cref="RuntimeLogLayout"/>.</summary>
    public const string MapLayoutGenerated = "map.layout-generated";
}

/// <summary>Kernel reason codes that are not one of the surfaced contract codes (value validation, plan, registration,
/// graph, step result and event groups are all carried by the exception or result that produced them).</summary>
public static class RuntimeLogReasonCodes
{
    /// <summary>An illegal handler result is recorded as failed/unknown under this code, the one combination the
    /// result rules do not otherwise express.</summary>
    public const string InvalidHandlerResult = "invalid-handler-result";
    public const string LifecycleObserverFailed = "lifecycle-observer-failed";
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
    /// <summary>The plan file's BepInEx-relative path, `/` separated. Carried by plan.loaded and plan.rejected.</summary>
    public string? Path { get; init; }
    /// <summary>Permissions the plan received, ordinal sorted. Carried by plan.loaded only.</summary>
    public IReadOnlyList<string>? Permissions { get; init; }
    public string? Entry { get; init; }
    public string? Step { get; init; }
    public string? Binding { get; init; }
    /// <summary>The actual generated level layout. Only ever set for <see cref="RuntimeLogCodes.MapLayoutGenerated"/>;
    /// the writer serializes it as the record's own `layout` object and the website validates that shape strictly.</summary>
    public RuntimeLogLayout? Layout { get; init; }
    public RuntimeLogResult? Result { get; init; }
    /// <summary>Free-text detail folded into the writer's composed message only; it is never its own JSON field.
    /// Carries the comma-joined, ordinal-sorted paths of every file in a plan-conflict group.</summary>
    public string? Detail { get; init; }
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
