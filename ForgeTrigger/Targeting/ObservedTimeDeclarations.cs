using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Targeting;

/// <summary>The session's own clock, as the one row a plan reads it through. A duration an author compares against
/// is a span of this clock, so the vocabulary needs a row that answers the clock itself: without one a card that
/// wants "has this been true for two seconds" has no tick to subtract from, and the alternative — every provider
/// publishing its own elapsed seconds — is a second clock per row.
///
/// The value is the frame's own tick, the one the kernel hands the evaluation through
/// <see cref="EvaluationContext.Tick"/>: the advancing machine's current tick, which is the value the scheduler
/// booked the dispatch at. The row therefore reads the world's time without reading anything else, which is why it
/// is a `state` row in the `query` tier rather than a `pure` one.</summary>
public static class ObservedTimeDeclarations
{
    /// <summary>The row in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Node("forge.query.time.tick", "query", "当前时刻", "读出发令这一帧的模拟计时（tick）。",
            ObservedDeclaration.NoPorts, ObservedDeclaration.Outputs(ObservedDeclaration.Integer("tick")),
            ObservedDeclaration.NoParameters, new HandlerShape().Outputs("tick"), TickHandler, new[] { "world" },
            kind: "state"));

    /// <summary>The clock one evaluation belongs to, read from the context the kernel built for it.</summary>
    public static long Tick(EvaluationContext context) => context.Tick;

    private static JsonElement TickHandler(EvaluationContext context) => RuntimeJson.From(new { tick = Tick(context) });
}
