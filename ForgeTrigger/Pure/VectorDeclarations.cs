using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>The two-point family: the distance between two positions and the unit direction from one to the other.
/// Both ports carry the catalog's metre unit, and the answer is the catalog's own output — metres for the distance,
/// an unqualified unit vector for the direction. Neither row reads an entity: the positions arrive as values, which
/// is what keeps the family in the `pure` tier.</summary>
internal static class VectorDeclarations
{
    internal static IReadOnlyList<PureNode> Nodes { get; } = new[]
    {
        PureModule.Row("forge.modifier.value.distance", "modifier", "两点距离", "按两个位置算出以米为单位的距离。",
            PureModule.Inputs(PureModule.PortWithUnit("from", "vector3", "m"), PureModule.PortWithUnit("to", "vector3", "m")),
            PureModule.Outputs(PureModule.PortWithUnit("value", "number", "m")), PureModule.Parameters(), (JsonElement?)null,
            new HandlerShape().Inputs("from", "to").Outputs("value"), PureModule.Distance),
        PureModule.Row("forge.modifier.value.direction", "modifier", "两点方向", "给出从第一个位置指向第二个位置的单位向量，长度永远是 1。",
            PureModule.Inputs(PureModule.PortWithUnit("from", "vector3", "m"), PureModule.PortWithUnit("to", "vector3", "m")),
            PureModule.Outputs(PureModule.Typed("value", "vector3")), PureModule.Parameters(), (JsonElement?)null,
            new HandlerShape().Inputs("from", "to").Outputs("value"), PureModule.Direction)
    };
}
