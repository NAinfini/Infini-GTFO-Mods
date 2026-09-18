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
        PureModule.OperatorRow("forge.modifier.value.add", "计算（加减乘除、最大最小、绝对值、取整）", "把几个数加起来。",
            new HandlerShape(), PureModule.Add),
        PureModule.OperatorRow("forge.modifier.value.multiply", "相乘", "把几个数乘起来。",
            new HandlerShape(), PureModule.Multiply),
        PureModule.Row("forge.modifier.value.minimum", "modifier", "取最小值", "取最小的那个。",
            PureModule.VariadicInputs("number"), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(PureModule.CountParameter()), VariadicNumber,
            new HandlerShape(), PureModule.Minimum, variadicShape: true),
        PureModule.Row("forge.modifier.value.maximum", "modifier", "取最大值", "取最大的那个。",
            PureModule.VariadicInputs("number"), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(PureModule.CountParameter()), VariadicNumber,
            new HandlerShape(), PureModule.Maximum, variadicShape: true),
        PureModule.OperatorRow("forge.condition.predicate.all", "与 / 或 / 非", "所有输入条件都成立才成立。",
            new HandlerShape(), PureModule.All),
        PureModule.OperatorRow("forge.condition.predicate.any", "任一条件成立", "任意一个输入条件成立就成立。",
            new HandlerShape(), PureModule.Any)
    };
}
