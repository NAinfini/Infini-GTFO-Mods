using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

/// <summary>The observation conditions: the rows that answer one boolean about the world a step was handed. Every
/// handler reads its inputs by the catalog's own port ids and its reads through the step's own
/// <see cref="RuntimeQuerySession"/>, so a fact the kernel could not observe is that step's refusal rather than a
/// false that looks observed.
///
/// The two rows here are the life-state reads the authoring node list has: whether the reference is in one named
/// life state, and whether that state is `downed`. The counting, distance, kind, tag, presence and relation
/// conditions are deleted — the list's comparison node compares two values the plan already has, and a condition
/// that re-read the world to answer the same question would be a second vocabulary beside the value rows.
///
/// The public half of this family is the world read itself — <see cref="LifeState"/> — so the conditions can be
/// exercised and composed without an event, exactly like the selection rows of the space family.</summary>
public static class ObservedConditionDeclarations
{
    /// <summary>The catalog's `recipient_life_state` members, in its declared order. A member outside this list is
    /// refused by name instead of being answered `false`, which would look like a world in which nothing matched.</summary>
    public static readonly string[] LifeStates = { "alive", "downed", "dead" };

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
            CompareHandler),
        // ---- life-state conditions --------------------------------------------------------------------------
        // The observed life state is the whole fact: `downed` is published from the native player downed
        // locomotion state, so this family reads the state a provider observed instead of testing an entity kind.
        ObservedDeclaration.Node("forge.condition.predicate.alive", "query", "敌人是否存活", "判断目标还活着且能被作用。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Entity("subject"), ObservedDeclaration.Enum("life_state", "recipient_life_state")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Bool("value")), ObservedDeclaration.NoParameters,
            new HandlerShape().Inputs("subject", "life_state").Outputs("value"), LifeStateHandler),
        ObservedDeclaration.Node("forge.condition.predicate.player_downed", "query", "玩家是否倒地", "判断玩家倒地了。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Entity("subject")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Bool("value")), ObservedDeclaration.NoParameters,
            new HandlerShape().Inputs("subject").Outputs("value"), PlayerDowned));

    /// <summary>One reference's current life state, read through the step's own session. A subject the kernel could
    /// not observe is that session's refusal, never a life state answered from the reference's own spelling.</summary>
    public static string LifeState(RuntimeQuerySession session, EntityReference subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return ObservedSpaceNodes.Observe(session, new[] { subject }).Single().LifeState;
    }

    /// <summary>Whether one observed life state is exactly the requested member of the catalog's own set. A member
    /// outside that set is refused by name, so a misspelled state cannot read as "not alive".</summary>
    public static bool IsLifeState(string observed, string requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (!LifeStates.Contains(requested, StringComparer.Ordinal))
            throw new RuntimeContractException("entity-life-state", "Unknown life state: " + requested);
        return string.Equals(observed, requested, StringComparison.Ordinal);
    }

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

    /// <summary>Whether the subject's observed life state is exactly the requested member.</summary>
    private static JsonElement LifeStateHandler(EvaluationContext context)
        => ObservedEvaluation.Value(IsLifeState(
            LifeState(context.Query, ObservedEvaluation.Entity(context, "subject")),
            ObservedEvaluation.PortText(context, "life_state")));

    /// <summary>A player life is down between the native downed transition and its revive or death; that state is
    /// what the owning provider observes, and this row answers it without testing an entity kind of its own.</summary>
    private static JsonElement PlayerDowned(EvaluationContext context)
        => ObservedEvaluation.Value(IsLifeState(
            LifeState(context.Query, ObservedEvaluation.Entity(context, "subject")), "downed"));
}
