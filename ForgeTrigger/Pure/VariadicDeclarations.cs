using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>The variadic family: one ordered computation whose inputs the plan expands, declared with the
/// catalog's own `input_count` bound. The shape names no port, because a capability that expands its ports per
/// plan has no registration-time layout; the handler reads the frame's own port order.</summary>
internal static class VariadicDeclarations
{
    private static readonly JsonElement VariadicNumber = PureModule.Variadic("number");
    private static readonly JsonElement VariadicBoolean = PureModule.Variadic("boolean");

    internal static IReadOnlyList<PureNode> Nodes { get; } = new[]
    {
        PureModule.Row("forge.modifier.value.add", "modifier", "加法", "把几个数加起来。",
            PureModule.VariadicInputs("number"), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(PureModule.CountParameter()), VariadicNumber,
            new HandlerShape(), PureModule.Add, variadicShape: true),
        PureModule.Row("forge.modifier.value.multiply", "modifier", "乘法", "把几个数乘起来。",
            PureModule.VariadicInputs("number"), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(PureModule.CountParameter()), VariadicNumber,
            new HandlerShape(), PureModule.Multiply, variadicShape: true),
        PureModule.Row("forge.modifier.value.minimum", "modifier", "最小值", "取最小的那个。",
            PureModule.VariadicInputs("number"), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(PureModule.CountParameter()), VariadicNumber,
            new HandlerShape(), PureModule.Minimum, variadicShape: true),
        PureModule.Row("forge.modifier.value.maximum", "modifier", "最大值", "取最大的那个。",
            PureModule.VariadicInputs("number"), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(PureModule.CountParameter()), VariadicNumber,
            new HandlerShape(), PureModule.Maximum, variadicShape: true),
        PureModule.Row("forge.condition.predicate.all", "condition", "所有条件满足", "所有输入条件都成立才成立。",
            PureModule.VariadicInputs("boolean"), PureModule.Outputs(PureModule.Bool("value")),
            PureModule.Parameters(PureModule.CountParameter()), VariadicBoolean,
            new HandlerShape(), PureModule.All, variadicShape: true),
        PureModule.Row("forge.condition.predicate.any", "condition", "至少一个条件满足", "任意一个输入条件成立就成立。",
            PureModule.VariadicInputs("boolean"), PureModule.Outputs(PureModule.Bool("value")),
            PureModule.Parameters(PureModule.CountParameter()), VariadicBoolean,
            new HandlerShape(), PureModule.Any, variadicShape: true)
    };
}
