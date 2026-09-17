using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;
using GameData;
using LevelGeneration;
using Localization;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the two objective-event rows: one instance per Map session, handed to the one
/// registration that declares them, exactly as `ObjectiveActionHandler` is.
///
/// Both rows are one `WardenObjectiveEventData` handed to `WorldEventManager.ExecuteEvent`, which is the entry a
/// level's own event list uses, so the write replicates the way a vanilla event's does and nothing here writes the
/// objective machine's state directly. The struct carries only the members its own row names: the display row sets
/// `Layer`, `CustomSubObjectiveHeader` and `CustomSubObjective`, and the progression row sets `Layer` and `Count`.
/// The event types are `eWardenObjectiveEventType.UpdateCustomSubObjective` (11) and `StepProgressionObjective`
/// (18), both read from the build (20403457).
///
/// Every check a request can be refused by lives in the game-independent half, so the refusal a plan sees is
/// decided in one place and a case can drive it without the game; what is left here is the readiness gate, the
/// singleton the executor answers from, and the one call. A call that throws is reported as an unknown commit,
/// because the executor may already have written the replicated state before it threw.</summary>
internal sealed class ObjectiveEventActions
{
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private ObjectiveEventActions(Func<bool> canExecute, Action<string> report)
    {
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The half the session builds: its own readiness gate, and the log line a failure is reported
    /// through rather than thrown through the dispatcher.</summary>
    internal static ObjectiveEventActions For(Func<bool> canExecute, Action<string> report)
        => new(canExecute, report);

    /// <summary>The handler bodies, bound to this instance: the session hands one method group per binding to the
    /// registration that declares it.</summary>
    internal CommandResult HandleDisplay(CommandContext context) => Display(context);
    internal CommandResult HandleProgress(CommandContext context) => Progress(context);

    /// <summary>The `forge.action.presentation.objective_display` command: the two texts become the machine's own
    /// `LocalizedText` values, which is how a plan writes computed text rather than a localisation id.</summary>
    private CommandResult Display(CommandContext context)
    {
        if (!ObjectiveEventContract.TryDisplay(context, out var request, out string? code))
            return ObjectiveEventContract.Refused(code!);
        var data = new WardenObjectiveEventData { Type = eWardenObjectiveEventType.UpdateCustomSubObjective };
        data.Layer = (LG_LayerType)request.Layer;
        data.CustomSubObjectiveHeader = new LocalizedText(request.Header);
        data.CustomSubObjective = new LocalizedText(request.Body);
        return Commit(data, "display", ObjectiveEventContract.DisplayOutputs(request));
    }

    /// <summary>The `forge.action.map.objective_progress` command: the step count is the struct's own `Count`
    /// member, which is the field a vanilla `StepProgressionObjective` entry uses.</summary>
    private CommandResult Progress(CommandContext context)
    {
        if (!ObjectiveEventContract.TryProgress(context, out var request, out string? code))
            return ObjectiveEventContract.Refused(code!);
        var data = new WardenObjectiveEventData { Type = eWardenObjectiveEventType.StepProgressionObjective };
        data.Layer = (LG_LayerType)request.Layer;
        data.Count = request.Steps;
        return Commit(data, "progress", ObjectiveEventContract.ProgressOutputs(request));
    }

    /// <summary>The one entry both rows reach. The session's own gate and the manager singleton are read on every
    /// request rather than captured once, because the instance behind the manager is replaced when a level is
    /// rebuilt.</summary>
    private CommandResult Commit(WardenObjectiveEventData data, string key, JsonElement outputs)
    {
        if (!_canExecute() || WorldEventManager.Current == null)
            return ObjectiveEventContract.Refused(ObjectiveEventContract.UnavailableCode);
        try
        {
            WorldEventManager.ExecuteEvent(data, 0f);
        }
        catch (Exception error)
        {
            ReportOnce(key, "objective " + key + " event threw: " + error.GetType().Name);
            return ObjectiveEventContract.Failed(ObjectiveEventContract.CommitExceptionCode);
        }
        return CommandResult.Succeeded(outputs);
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _report(message);
    }
}
