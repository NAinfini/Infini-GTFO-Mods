using System;
using GameData;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>The game-bound half of the three action rows: the one native entry each of them reaches, and the
/// `WardenObjectiveEventData` struct that entry takes. Every action here is a level event, so the struct's own
/// `Type` member selects the effect and the game's own executor carries it to every client — the same call the
/// vanilla mount points use. Nothing writes the state the event owns, which is what keeps a client's world
/// consistent: the executor is the only writer, exactly as it is for a level's own event list.
///
/// The event types below are `eWardenObjectiveEventType` members read from the build (20403457):
/// `DimensionFlashTeam` 7, `DimensionWarpTeam` 8, `AddToTimer` 24, `ResetTimer` 25, `WinOnDeath` 26,
/// `ForceInstantWin` 27, `ClearDimension` 30.
///
/// Every check a request can be refused by lives in the game-independent half, so the refusal a plan sees is
/// decided in one place; the native call only happens once a request is known to be legal. A call that throws is
/// reported as an unknown commit — the executor may already have written the event's replicated state before it
/// threw, and claiming nothing happened is a claim this layer cannot make.</summary>
internal static class LevelEventActions
{
    /// <summary>The countdown row: `add` carries the seconds the plan supplied, `reset` carries none. Both go
    /// through the world event executor rather than the objective manager's own setter, because the executor is
    /// the entry the game's own data uses and it treats an authored event and this one the same way.</summary>
    internal static CommandResult Timer(CommandContext context)
    {
        var module = Plugin.Session?.LevelEvents;
        if (module == null) return Unavailable();
        return module.ExecuteTimer(context,
            seconds => Execute(Event(eWardenObjectiveEventType.AddToTimer, data => data.Duration = seconds)),
            () => Execute(Event(eWardenObjectiveEventType.ResetTimer)));
    }

    /// <summary>The dimension row: `flash` and `warp` move the whole team, `clear` empties a dimension. The
    /// destination is the `DimensionIndex` field and the clear flag is the event's own `ClearDimension`, which is
    /// the field the vanilla `DimensionWarpTeam` entries use when they empty a dimension before moving into it.</summary>
    internal static CommandResult Dimension(CommandContext context)
    {
        var module = Plugin.Session?.LevelEvents;
        if (module == null) return Unavailable();
        return module.ExecuteDimension(context, (mode, dimension, clear) => Execute(Event(
            mode == "clear" ? eWardenObjectiveEventType.ClearDimension : mode == "warp"
                ? eWardenObjectiveEventType.DimensionWarpTeam
                : eWardenObjectiveEventType.DimensionFlashTeam,
            data =>
            {
                data.DimensionIndex = (eDimensionIndex)dimension;
                data.ClearDimension = clear;
            })));
    }

    /// <summary>The expedition-end row: `instant_win` ends the expedition now, `win_on_death` makes the next wipe
    /// the win the objective's own completion check looks for.</summary>
    internal static CommandResult ExpeditionEnd(CommandContext context)
    {
        var module = Plugin.Session?.LevelEvents;
        if (module == null) return Unavailable();
        return module.ExecuteExpeditionEnd(context, ending => Execute(Event(
            ending == "win_on_death" ? eWardenObjectiveEventType.WinOnDeath : eWardenObjectiveEventType.ForceInstantWin)));
    }

    /// <summary>The one entry every action above reaches: the game's own level-event executor, with the delay the
    /// plan's own step already applied as zero and the struct passed by value, which is what the interop signature
    /// takes. A call that throws is reported as a failed commit through the game-independent half, because the
    /// executor may already have written the event's replicated state and claiming nothing happened is a claim
    /// this layer cannot make.</summary>
    private static void Execute(WardenObjectiveEventData data)
    {
        try { WorldEventManager.ExecuteEvent(data, 0f); }
        catch (Exception error) { throw new LevelEventCommitException(error); }
    }

    private static WardenObjectiveEventData Event(eWardenObjectiveEventType type) => new() { Type = type };

    private static WardenObjectiveEventData Event(eWardenObjectiveEventType type, Action<WardenObjectiveEventData> setup)
    {
        var data = new WardenObjectiveEventData { Type = type };
        setup(data);
        return data;
    }

    private static CommandResult Unavailable() => LevelEventModule.Refused("map-session-unavailable");

    /// <summary>The one failure the native call can produce: the executor was reached and did not return
    /// normally. It is its own type so the game-independent half can tell "the world refused to write" from a
    /// mistake in argument handling, which would be a programming error rather than a commit outcome.</summary>
    private sealed class LevelEventCommitException : Exception
    {
        internal LevelEventCommitException(Exception inner) : base("level event commit failed", inner) { }
    }
}
