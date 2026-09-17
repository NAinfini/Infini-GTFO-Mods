using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>The list family of the pure tier: the row that reads one item out of a list a step already carries. It
/// never walks the world — the list arrives on the port — so it stays a `pure` row, and it answers the same way
/// for an empty list whatever the policy: a list with no members has no item to take.
///
/// The four `clamp` members are one-sided boundaries plus two ways of having none. `min` takes the first member
/// for an index below the list and nothing for an index past the end; `max` takes the last member for an index
/// past the end and nothing below; `wrap` counts from the other end for a negative index and modulo for an index
/// past the end, so the row is a rotation read; `empty` answers the empty string for either side. An index that
/// is inside the list is the same member under all four.</summary>
internal static class ListDeclarations
{
    /// <summary>The row's own four members, in the catalog's order: the compiled value is the member's index into
    /// this list, so the order is the wire and the handler reads the member by position.</summary>
    internal static readonly string[] ClampPolicies = { "min", "max", "wrap", "empty" };

    internal static IReadOnlyList<PureNode> Nodes { get; } = new[]
    {
        PureModule.Row("forge.modifier.list.element_at", "modifier", "按序号取列表项", "按序号从列表里取一项，越界时可以取边界、回绕或取空。",
            PureModule.Inputs(PureModule.Many("items", "string"), PureModule.Integer("index")),
            PureModule.Outputs(PureModule.Text("value")),
            PureModule.Parameters(PureModule.InlineEnumParameter("clamp", ClampPolicies)), (JsonElement?)null,
            new HandlerShape().Inputs("items", "index").Outputs("value").Parameters("clamp"), ElementAt)
    };

    /// <summary>One item of the list by index, under the policy that index was written with. A list is read as it
    /// arrives and a repeated text stays a member, so the row never deduplicates a list somebody authored.</summary>
    internal static string ElementAt(IReadOnlyList<string> items, long index, string clamp)
    {
        if (items.Count == 0) return string.Empty;
        switch (clamp)
        {
            case "wrap":
                var wrapped = index % items.Count;
                return items[(int)(wrapped < 0 ? wrapped + items.Count : wrapped)];
            case "min":
                return index < 0 ? items[0] : index >= items.Count ? string.Empty : items[(int)index];
            case "max":
                return index >= items.Count ? items[^1] : index < 0 ? string.Empty : items[(int)index];
            default:
                return index < 0 || index >= items.Count ? string.Empty : items[(int)index];
        }
    }

    private static JsonElement ElementAt(EvaluationContext context)
    {
        var items = context.Inputs.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(Text).ToArray() : Array.Empty<string>();
        var index = Integer(context.Inputs.GetProperty("index"));
        var clamp = context.Parameters.GetProperty("clamp").GetString()!;
        if (Array.IndexOf(ClampPolicies, clamp) < 0)
            throw new RuntimeContractException("pure-operation", "Unknown clamp member: " + clamp);
        return RuntimeJson.From(new { value = ElementAt(items, index, clamp) });
    }

    /// <summary>One text of the list as the port carries it: the row's own read of a `string many` slot, refused
    /// by name where a member is not text rather than read as an empty item.</summary>
    private static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new RuntimeContractException("pure-operation", "A list of text requires text members.");

    /// <summary>The index as the integer port carries it. A non-integral number is refused rather than truncated:
    /// the frame would otherwise accept a value its own contract says the port never carries.</summary>
    private static long Integer(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number == Math.Truncate(number)
            && number >= long.MinValue && number <= long.MaxValue
            ? (long)number
            : throw new RuntimeContractException("pure-operation", "An index requires an integral operand.");
}
