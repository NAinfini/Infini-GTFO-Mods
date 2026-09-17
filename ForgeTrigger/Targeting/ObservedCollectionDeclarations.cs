using System;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

/// <summary>The collection selectors: the rows that answer with a set built from the sets a plan already holds.
/// Every one of them declares the same structural `empty` policy — the catalog's own set, applied to the set the
/// handler answered with, never to the world — and none of them reads the world at all: the candidates arrive as
/// ports, and the references they answer with are checked by the kernel before a consumer sees them.</summary>
public static class ObservedCollectionDeclarations
{
    /// <summary>The `seed_mode` members, in the catalog's own order: the number the author wrote on the `seed`
    /// port, or the number this session draws with, so the same pick happens again only when the same run is
    /// replayed.</summary>
    internal static readonly string[] SeedModes = { "fixed", "session" };

    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Node("forge.selector.target.distinct", "query", "按稳定实体身份去重", "去掉重复的目标。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("candidates")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("candidates").Outputs("targets").Parameters("empty"), Distinct),
        ObservedDeclaration.Node("forge.selector.target.limit", "query", "限制最多目标数", "最多留下这么多个。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("candidates"), ObservedDeclaration.Integer("max_targets")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("candidates", "max_targets").Outputs("targets").Parameters("empty"), Limit),
        ObservedDeclaration.Node("forge.selector.target.shuffle", "query", "有种子的稳定打乱", "打乱顺序，同一个种子顺序一样。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("candidates"), ObservedDeclaration.Integer("seed")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("candidates", "seed").Outputs("targets").Parameters("empty"), Shuffle),
        ObservedDeclaration.Node("forge.selector.target.random", "query", "从候选里随机取 N 个", "随机挑几个，同一个种子结果一样。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("candidates"), ObservedDeclaration.Integer("count"),
                ObservedDeclaration.Integer("seed")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")),
            ObservedDeclaration.Parameters(ObservedDeclaration.StructuralEnumValues("seed_mode", SeedModes),
                ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("candidates", "count", "seed").Outputs("targets").Parameters("seed_mode", "empty"), Random),
        ObservedDeclaration.Node("forge.selector.target.union", "query", "合并目标集合", "把两组目标合成一组。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("a"), ObservedDeclaration.Many("b")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("a", "b").Outputs("targets").Parameters("empty"), Union),
        ObservedDeclaration.Node("forge.selector.target.intersection", "query", "目标集合交集", "只留两组里都有的。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("a"), ObservedDeclaration.Many("b")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("a", "b").Outputs("targets").Parameters("empty"), Intersection),
        ObservedDeclaration.Node("forge.selector.target.difference", "query", "排除目标集合", "从第一组里去掉第二组。",
            ObservedDeclaration.Inputs(ObservedDeclaration.Many("a"), ObservedDeclaration.Many("b")),
            ObservedDeclaration.Outputs(ObservedDeclaration.Many("targets")), ObservedDeclaration.Parameters(ObservedDeclaration.EmptyPolicyParameter()),
            new HandlerShape().Inputs("a", "b").Outputs("targets").Parameters("empty"), Difference));

    // ---- handlers ------------------------------------------------------------------------------------------

    private static JsonElement Distinct(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ReferenceCollections.Distinct(ObservedEvaluation.Collection(context, "candidates")));
    private static JsonElement Limit(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ReferenceCollections.Limit(ObservedEvaluation.Collection(context, "candidates"),
            ObservedEvaluation.Selection(context, "max_targets")).Selected);
    private static JsonElement Shuffle(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ReferenceCollections.Shuffle(ObservedEvaluation.Collection(context, "candidates"),
            ObservedEvaluation.Required(context, "seed").GetInt64()));
    private static JsonElement Random(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ReferenceCollections.Random(ObservedEvaluation.Collection(context, "candidates"),
            ObservedEvaluation.Seed(context, "seed", ObservedEvaluation.ParameterText(context, "seed_mode")),
            ObservedEvaluation.Selection(context, "count")).Selected);
    private static JsonElement Union(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ReferenceCollections.Union(ObservedEvaluation.Collection(context, "a"),
            ObservedEvaluation.Collection(context, "b")));
    private static JsonElement Intersection(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ReferenceCollections.Intersection(ObservedEvaluation.Collection(context, "a"),
            ObservedEvaluation.Collection(context, "b")));
    private static JsonElement Difference(EvaluationContext context)
        => ObservedEvaluation.Targets(context, ReferenceCollections.Difference(ObservedEvaluation.Collection(context, "a"),
            ObservedEvaluation.Collection(context, "b")));
}
