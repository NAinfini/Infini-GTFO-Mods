using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// The two contexts a case drives a handler with. The kernel builds both for a dispatched step and both have
/// assembly-internal constructors, so a case that is not running the whole dispatch walk builds the same objects
/// through them — the alternative, a second public constructor, would be a hole in the boundary the kernel keeps.
///
/// The parameters a case passes are the compiled form's own indices, because a structural enum travels as the
/// index of its member and the kernel resolves it to the member's name at the handler boundary. The same
/// resolution runs here, through the runtime's own method and against the row's own declaration, so a case drives
/// the handler with what a dispatch would hand it rather than with a hand-written name the production path never
/// produces.
/// </summary>
internal static class Contexts
{
    private static readonly ConstructorInfo CommandConstructor = typeof(CommandContext)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[]
        {
            typeof(RuntimeEvent), typeof(long), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(JsonElement), typeof(JsonElement), typeof(bool)
        }, null) ?? throw new InvalidOperationException("CommandContext's own constructor was not found.");

    private static readonly ConstructorInfo EvaluationConstructor = typeof(EvaluationContext)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[]
        {
            typeof(string), typeof(JsonElement), typeof(JsonElement), typeof(RuntimeQuerySession),
            typeof(RuntimeActorContext), typeof(RuntimeFactionRelations)
        }, null) ?? throw new InvalidOperationException("EvaluationContext's own constructor was not found.");

    private static readonly MethodInfo ResolveEnumParameters = typeof(RuntimeJson)
        .GetMethod("ResolveEnumParameters", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("RuntimeJson.ResolveEnumParameters was not found.");

    private static readonly ConstructorInfo SessionConstructor = typeof(RuntimeQuerySession)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(RuntimeKernel), typeof(string), typeof(bool) }, null)
        ?? throw new InvalidOperationException("RuntimeQuerySession's own constructor was not found.");

    /// <summary>One command context over the given row, with its own parameters resolved the way the kernel
    /// resolves them. `isHost` is the fact the dispatch carried: a host row requires it, a presentation row is
    /// only ever invoked with it false.</summary>
    internal static CommandContext Command(string capabilityId, object? inputs, object? parameters,
        bool isHost = true, long worldEpoch = 1)
    {
        var origin = new RuntimeEvent("test.event", "test.binding", worldEpoch, 0, "test.scope", RuntimeJson.EmptyObject);
        return (CommandContext)CommandConstructor.Invoke(new object?[]
        {
            origin, 0L, "test.command", "test.plan", "author.resource", "revision-1", "A_row",
            Resolve(capabilityId, parameters), Bag(inputs), isHost
        })!;
    }

    /// <summary>One evaluation context over the given row. The query session, the actors and the relations are
    /// left null on purpose: the row under test reads neither, and a case that made them up would be describing
    /// a world read the row does not perform.</summary>
    internal static EvaluationContext Evaluation(string capabilityId, object? inputs, object? parameters)
        => Evaluation(capabilityId, inputs, parameters, default);

    /// <summary>The same context with the session a `query` row reads the world through. The session is built by
    /// the runtime's own constructor so a case reads the same world epoch the kernel would hand a dispatched step;
    /// the actors and the relations stay null for the same reason as above.</summary>
    internal static EvaluationContext Evaluation(string capabilityId, object? inputs, object? parameters,
        RuntimeQuerySession session)
        => (EvaluationContext)EvaluationConstructor.Invoke(new object?[]
        {
            "A_row", Resolve(capabilityId, parameters), Bag(inputs), session, null!, null!
        })!;

    /// <summary>One session over a real kernel of the given world epoch, the way a dispatched `query` step's own
    /// session is built. Nothing is evaluated through it here: the case checks the row the session is handed to,
    /// and the kernel is standing only so the epoch is the kernel's own.</summary>
    internal static RuntimeQuerySession Session(long worldEpoch)
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "environment-facts"));
        // The kernel reports epoch zero until a world is begun, so the case begins its own either way: a session
        // with no world behind it answers no read, which is what a stale-epoch case is about.
        kernel.BeginWorld(worldEpoch);
        return (RuntimeQuerySession)SessionConstructor.Invoke(new object?[] { kernel, "A_row", true })!;
    }

    private static JsonElement Bag(object? value) => value == null ? RuntimeJson.EmptyObject : RuntimeJson.From(value);

    /// <summary>The kernel's own enum resolution, over the row's own declaration: a numeric parameter becomes the
    /// member name at its index, and a value that is not a number is left as it is.</summary>
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
    internal static JsonElement Graph(string capabilityId) => Row(capabilityId).GetProperty("graph");

    /// <summary>The capability row itself, exactly what a registration declares it as.</summary>
    internal static JsonElement Row(string capabilityId)
        => capabilityId == HudContract.ValueCapability
            ? RuntimeJson.From(HudContract.CapabilityRow())
            : RuntimeJson.From(EnvironmentContract.Rows()[capabilityId]);
}
