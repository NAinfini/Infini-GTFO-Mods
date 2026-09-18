using ForgeRuntime.Framework;

namespace ForgeDevelopment.Native;

/// <summary>Stable debug vocabulary for the behavior editor. Runtime log codes remain the transport; this class
/// gives ForgeDevelopment the author-facing phase so recorder queries never have to infer semantics from strings.</summary>
internal static class BehaviorTraceContract
{
    internal readonly record struct Phase(string Scope, string Moment);

    internal static bool TryClassify(string? code, out Phase phase)
    {
        phase = code switch
        {
            RuntimeLogCodes.TriggerFired => new("trigger", "accepted"),
            RuntimeLogCodes.DispatchStarted => new("trigger", "before"),
            RuntimeLogCodes.DispatchFinished => new("trigger", "after"),
            RuntimeLogCodes.EventRejected => new("trigger", "rejected"),
            RuntimeLogCodes.EventDeferred => new("trigger", "deferred"),
            RuntimeLogCodes.EventCancelled => new("trigger", "cancelled"),
            RuntimeLogCodes.EntryStarted => new("behavior-entry", "before"),
            RuntimeLogCodes.EntryFinished => new("behavior-entry", "after"),
            RuntimeLogCodes.EntryStopped => new("behavior-entry", "stopped"),
            RuntimeLogCodes.StepStarted => new("behavior-node", "before"),
            RuntimeLogCodes.StepFinished => new("behavior-node", "after"),
            _ => default
        };
        return phase.Scope != null;
    }
}
