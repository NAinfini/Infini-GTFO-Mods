using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>The condition family: the value-only predicates. Each answers one boolean from its own inputs and reads
/// nothing else. The probability gate is not a row of its own: it is the random draw compared through the
/// comparison row, so there is one random row and one comparison row rather than a third spelling of the same
/// decision. A value inside an interval is a pair of comparisons rather than a fourth predicate, which is why the
/// interval row is deleted with the rest of the rows the node list does not have.
///
/// The typed comparison is not a `pure` row: the catalog declares it `execution: query` because its two operands
/// are typed by one structural parameter and one of that parameter's members is `entity`, a world value (rule
/// 142.1). It is declared by <see cref="ForgeTrigger.Targeting.ObservedConditionDeclarations"/> beside the other
/// evaluated conditions, and it compares through the same <see cref="PureConditions"/> helpers this family
/// already used. What stays here is the one predicate over plain flags and the enum comparison below.
///
/// The enum comparison is the one row that is not declared per set: its `enum_set` parameter chooses the set, and
/// both its ports read that choice instead of naming a set of their own, so one row covers all of them. The two
/// operands are values rather than world reads, which is what keeps the row in the `pure` tier while the typed
/// comparison beside it has to observe.</summary>
internal static class ConditionDeclarations
{
    internal static IReadOnlyList<PureNode> Nodes { get; } = new[]
    {
        PureModule.OperatorRow("forge.condition.predicate.not", "条件取反", "把条件反过来。",
            new HandlerShape().Inputs("input").Outputs("value"), PureModule.Not),
        PureModule.OperatorRow("forge.condition.enum.compare", "枚举等于", "把枚举值与该集合中的一个成员比较；值可能没有时不通过。",
            new HandlerShape().Inputs("value", "equals").Outputs("value"), PureModule.EnumCompare, name: "enum_compare")
    };
}
