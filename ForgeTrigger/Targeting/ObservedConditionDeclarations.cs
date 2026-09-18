using System;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

/// <summary>The pure comparison condition retained by ForgeTrigger. World reads live in the Data/Observation
/// layer; life state, health, position and other observed values are read first and then compared here. This
/// prevents predicates from becoming a second hidden world-read vocabulary.</summary>
public static class ObservedConditionDeclarations
{
    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        // ---- the typed comparison --------------------------------------------------------------------------
        // The catalog's one `g-compare` row (rule 146.4b): the two operands are typed by the structural
        // `value_type` parameter, so a number, a count, a piece of text and an entity reference are all ordered by
        // one row instead of one row per class. It is a `query` row and not a `pure` one because the `entity`
        // member is a world value (rule 142.1); the comparison itself still reads no world, which is why its
        // handler never touches `context.Query`.
        ObservedDeclaration.Primitive("forge.condition.predicate.compare", "比较（大于、等于、小于……）",
            "按选定关系比较两个值：数字可以带容差，文字按顺序比较，对象只判是不是同一个。",
            new HandlerShape().Inputs("left", "right", "operator", "tolerance").Outputs("value").Parameters("value_type"),
            CompareHandler));

    // ---- handlers ------------------------------------------------------------------------------------------

    /// <summary>The typed comparison. The operator arrives as the member name the kernel resolved from its
    /// compiled index, and `value_type` is the member the plan compiled — or nothing, when the author left the
    /// optional parameter unwritten, which resolves to the parameter's first member exactly as the contract does.
    /// `tolerance` is optional because only the numeric members read it.</summary>
    private static JsonElement CompareHandler(EvaluationContext context)
        => ObservedEvaluation.Value(PureConditions.Compare(
            ObservedEvaluation.Required(context, "left"), ObservedEvaluation.Required(context, "right"),
            ObservedEvaluation.Comparison(ObservedEvaluation.PortText(context, "operator")),
            context.Inputs.TryGetProperty("tolerance", out var tolerance) && tolerance.ValueKind == JsonValueKind.Number
                ? tolerance.GetDouble() : (double?)null,
            context.Parameters.TryGetProperty("value_type", out var valueType) && valueType.ValueKind == JsonValueKind.String
                ? valueType.GetString() : null));

}
