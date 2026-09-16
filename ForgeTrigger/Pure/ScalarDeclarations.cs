using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>The scalar family: arithmetic, rounding, the clamp and the seeded random draw. One number in, one
/// number out, with the catalog's own structural policies for division, rounding and the seed. The draw is the
/// host's own row: a plan only runs on the host, so the value is evaluated there and reaches every client with the
/// execution that produced it — which is why it needs no level seed and reads none.</summary>
internal static class ScalarDeclarations
{
    internal static IReadOnlyList<PureNode> Nodes { get; } = new[]
    {
        PureModule.Row("forge.modifier.value.constant", "modifier", "数字（固定值）", "给出一个固定的数。",
            PureModule.Inputs(), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(PureModule.ValueParameter("value", "number")),
            null, new HandlerShape().Outputs("value").Parameters("value"), PureModule.Constant),
        PureModule.Row("forge.modifier.value.subtract", "modifier", "计算（加减乘除、最大最小、绝对值、取整） · value subtract", "相减。",
            PureModule.Inputs(PureModule.Number("a"), PureModule.Number("b")), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(), (JsonElement?)null,
            new HandlerShape().Inputs("a", "b").Outputs("value"), PureModule.Subtract),
        PureModule.Row("forge.modifier.value.divide", "modifier", "计算（加减乘除、最大最小、绝对值、取整） · value divide", "相除，除零时按你选的规则处理。",
            PureModule.Inputs(PureModule.Number("a"), PureModule.Number("b")), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(PureModule.InlineEnumParameter("zero_policy", PureModule.ZeroPolicies)), (JsonElement?)null,
            new HandlerShape().Inputs("a", "b").Outputs("value").Parameters("zero_policy"), PureModule.Divide),
        PureModule.Row("forge.modifier.value.clamp", "modifier", "限制在范围内", "把数值卡在上下限之间。",
            PureModule.Inputs(PureModule.Number("value"), PureModule.Number("minimum"), PureModule.Number("maximum")),
            PureModule.Outputs(PureModule.Number("value")), PureModule.Parameters(), (JsonElement?)null,
            new HandlerShape().Inputs("value", "minimum", "maximum").Outputs("value"), PureModule.Clamp),
        PureModule.Row("forge.modifier.value.absolute", "modifier", "计算（加减乘除、最大最小、绝对值、取整） · value absolute", "取绝对值。",
            PureModule.Inputs(PureModule.Number("value")), PureModule.Outputs(PureModule.Number("value")),
            PureModule.Parameters(), (JsonElement?)null,
            new HandlerShape().Inputs("value").Outputs("value"), PureModule.Absolute),
        PureModule.Row("forge.modifier.value.round", "modifier", "计算（加减乘除、最大最小、绝对值、取整） · value round", "按你选的规则取整。",
            PureModule.Inputs(PureModule.Number("value")), PureModule.Outputs(PureModule.Integer("value")),
            PureModule.Parameters(PureModule.SetEnumParameter("mode", "rounding_mode")), (JsonElement?)null,
            new HandlerShape().Inputs("value").Outputs("value").Parameters("mode"), PureModule.Round),
        PureModule.Row("forge.modifier.value.random_range", "modifier", "随机数", "在一个区间里随机取数，同种子同结果。",
            PureModule.Inputs(PureModule.Number("minimum"), PureModule.Number("maximum"), PureModule.Integer("seed")),
            PureModule.Outputs(PureModule.Number("value")), PureModule.Parameters(), (JsonElement?)null,
            new HandlerShape().Inputs("minimum", "maximum", "seed").Outputs("value"), PureModule.RandomRange)
    };
}
