using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>The condition family: the value-only predicates. Each answers one boolean from its own inputs and reads
/// nothing else. The probability gate is not a row of its own: it is the random draw compared through the
/// comparison row, so there is one random row and one comparison row rather than a third spelling of the same
/// decision. A value inside an interval is a pair of comparisons rather than a fourth predicate, which is why the
/// interval row is deleted with the rest of the rows the node list does not have.
///
/// The comparison itself is not a `pure` row: the catalog declares it `execution: query` because its two operands
/// are typed by one structural parameter and one of that parameter's members is `entity`, a world value (rule
/// 142.1). It is declared by <see cref="ForgeTrigger.Targeting.ObservedConditionDeclarations"/> beside the other
/// evaluated conditions, and it compares through the same <see cref="PureConditions"/> helpers this family
/// already used. What stays here is the one row whose whole frame is plain values.</summary>
internal static class ConditionDeclarations
{
    internal static IReadOnlyList<PureNode> Nodes { get; } = new[]
    {
        PureModule.Row("forge.condition.predicate.not", "condition", "与 / 或 / 非 · predicate not", "把条件反过来。",
            PureModule.Inputs(PureModule.Bool("input")), PureModule.Outputs(PureModule.Bool("value")),
            PureModule.Parameters(), (JsonElement?)null,
            new HandlerShape().Inputs("input").Outputs("value"), PureModule.Not)
    };
}
