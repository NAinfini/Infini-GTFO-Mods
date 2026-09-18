using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.NativeObjectiveActions;

/// <summary>
/// One command context for a case to drive a handler with. The kernel builds this type for a dispatched step,
/// and its constructor is assembly-internal, so a case that is not running the whole dispatch walk builds the
/// same object through it — the alternative, a second public constructor for tests, would be a hole in the
/// boundary the kernel keeps.
///
/// The parameters a case passes are the compiled form's own indices, because a structural enum travels as the
/// index of its member and the kernel resolves it to the member's name at the handler boundary
/// (`RuntimeJson.ResolveEnumParameters`). The same resolution runs here, through the runtime's own method and
/// against the row's own declaration, so a case drives the handler with what a dispatch would hand it instead of
/// with a hand-written name the production path never produces.
/// </summary>
internal static class Contexts
{
    private static readonly ConstructorInfo Constructor = typeof(CommandContext)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[]
        {
            typeof(RuntimeEvent), typeof(long), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(JsonElement), typeof(JsonElement), typeof(bool), typeof(Func<EntityReference, object?>)
        }, null) ?? throw new InvalidOperationException("CommandContext's own constructor was not found.");

    private static readonly MethodInfo ResolveEnumParameters = typeof(RuntimeJson)
        .GetMethod("ResolveEnumParameters", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("RuntimeJson.ResolveEnumParameters was not found.");

    /// <summary>One context over the given row, with its own parameters resolved the way the kernel resolves
    /// them. <paramref name="capabilityId"/> is the action row the handler answers, whose declared parameters are
    /// the index basis of the values a case passes.</summary>
    internal static CommandContext For(string capabilityId, object? inputs, object? parameters, bool isHost = true)
    {
        var origin = new RuntimeEvent("test.event", "test.binding", 1, 0, "test.scope", RuntimeJson.EmptyObject);
        return (CommandContext)Constructor.Invoke(new object?[]
        {
            origin, 0L, "test.command", "test.plan", "author.resource", "revision-1", "A_action",
            Resolve(capabilityId, parameters), Bag(inputs), isHost, new Func<EntityReference, object?>(_ => null)
        })!;
    }

    private static JsonElement Bag(object? value) => value == null ? RuntimeJson.EmptyObject : RuntimeJson.From(value);

    /// <summary>The kernel's own enum resolution, over the row's own declaration: a numeric parameter becomes the
    /// member name at its index, while a value that is not a number is left as it is. An index past the row's own
    /// member list is left as the number it is, because a real plan cannot compile one — the loader refuses it —
    /// and a case that drives one is asking what the handler does with a value it never sees in a plan.</summary>
    internal static JsonElement Resolve(string capabilityId, object? parameters)
    {
        var bag = Bag(parameters);
        var graph = Graph(capabilityId);
        foreach (var definition in graph.GetProperty("parameters").EnumerateArray())
        {
            if (definition.GetProperty("type").GetString() != "enum") continue;
            var name = definition.GetProperty("id").GetString()!;
            if (!bag.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) continue;
            if (!value.TryGetInt32(out int index) || index < 0 || index >= definition.GetProperty("values").GetArrayLength())
                continue;
            return (JsonElement)ResolveEnumParameters.Invoke(null, new object[] { bag, Row(capabilityId) })!;
        }
        return bag;
    }

    /// <summary>The row's own graph, taken from the contract under test rather than restated here, so a renamed
    /// parameter fails a case instead of drifting.</summary>
    private static JsonElement Graph(string capabilityId)
        => Row(capabilityId).GetProperty("graph");

    /// <summary>The capability row itself, exactly what a registration declares it as.</summary>
    internal static JsonElement Row(string capabilityId)
    {
        if (capabilityId == SessionActionContract.CheckpointSaveCapability)
            return RuntimeJson.From(SessionActionContract.CapabilityRows()[0]);
        return RuntimeJson.From(ObjectiveActionContract.Graphs[capabilityId]);
    }
}
