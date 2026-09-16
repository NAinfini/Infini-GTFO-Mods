using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

/// <summary>One node of the Trigger vocabulary: the authoring catalog's own capability shape, the `evaluate`
/// binding that implements it, and the handler that binding resolves against. The family declaration tables are
/// the single declaration the module registers; nothing here restates a port, parameter or domain the catalog row
/// does not declare.</summary>
public sealed record PureNode(string CapabilityId, string Kind, string Label, string Description, JsonElement Graph,
    string BindingId, string HandlerName, HandlerShape Shape, EvaluatorHandler Evaluate);

/// <summary>The production Trigger nodes that compute a value without reading the world, published from one
/// declaration table per family. Every handler calls the existing stateless helpers in this namespace and reads its
/// inputs by the catalog's own port ids; the shapes are resolved against those same catalog shapes at registration,
/// so a name only one half knows is a registration error.
///
/// The vocabulary is the authoring node list's own: a fixed number, the eight arithmetic rows the calculation node
/// names (add, subtract, multiply, divide, minimum, maximum, absolute, round), the clamp, the host-evaluated random
/// draw, the two text rows, and the logic predicates (all, any, not). The comparison is not here: the catalog
/// declares its typed row `query` because one `value_type` member is an entity, so it is declared with the other
/// evaluated conditions and compares through the same <see cref="PureConditions"/> helpers. Rows the list does not
/// have — the vector, curve, falloff, remap, by-* and collection families, `chance`, `lerp`, `select_value` and
/// `power` — are deleted rather than left declared: a declared row nobody can author is a second vocabulary, and
/// the random draw merged the probability gate into itself (a probability is that draw compared through
/// `forge.condition.predicate.compare`).
///
/// `forge.modifier.value.random_range` is evaluated by the host only, because only the host runs a plan: the pure
/// tier is never evaluated on a client, and the draw's result reaches clients the way every other step result does,
/// with the execution that produced it. Its seed stays an explicit input — the pure tier is re-evaluable on demand
/// and therefore has to stay a function of what the plan wrote, so the row needs no level seed and reads none.</summary>
public static class PureModule
{
    /// <summary>Every row is authoring metadata the website owns; the capability version is the module's own
    /// contract revision, not a copy of a catalog field.</summary>
    private const string CapabilityVersion = "1.0.0";

    // The static data every row reads comes before `Nodes`: a field declared after the tables are built would
    // still be null while a declaration table is being built.
    /// <summary>The one domain list every logic row of the catalog declares.</summary>
    private static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "logic" };

    /// <summary>Member order is the wire: an enum's compiled value is its index into the catalog's own set (or its
    /// inline `values`), so each array below is declared in the order of the C# enum it maps onto.</summary>
    internal static readonly string[] RoundingModes = { "floor", "ceil", "nearest", "truncate" };
    internal static readonly string[] ZeroPolicies = { "reject", "zero", "passthrough" };

    /// <summary>One family per table, in catalog order: the value-only rows a `pure` step evaluates. Public
    /// because the contract tests iterate the same tables the module registers instead of restating them.</summary>
    public static IReadOnlyList<PureNode> Nodes { get; } = ScalarDeclarations.Nodes
        .Concat(VariadicDeclarations.Nodes)
        .Concat(TextDeclarations.Nodes)
        .Concat(ConditionDeclarations.Nodes)
        .ToArray();

    public static JsonElement[] Capabilities => Nodes.Select(node => RuntimeJson.From(new
    {
        id = node.CapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = node.Kind,
        label = node.Label,
        version = CapabilityVersion,
        parameters = new { description = node.Description },
        graph = node.Graph
    })).ToArray();

    public static JsonElement[] Bindings => Nodes.Select(node => RuntimeJson.From(new
    {
        id = node.BindingId,
        capabilityId = node.CapabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler = node.HandlerName,
        role = "evaluate",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    })).ToArray();

    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators { get; } =
        Nodes.ToDictionary(node => node.HandlerName, node => node.Evaluate, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, HandlerShape> Shapes { get; } =
        Nodes.ToDictionary(node => node.HandlerName, node => node.Shape, StringComparer.Ordinal);

    public static IReadOnlyList<BindingSupport> Support { get; } = Nodes
        .Select(node => new BindingSupport(node.BindingId, "implementation-only", Array.Empty<string>())).ToArray();

    /// <summary>Members of a declaration table are in catalog order: one family reads the enum whose compiled
    /// value is the member's index into that set. Every row of this module evaluates in the `pure` tier, and
    /// `variadic` is null for every row the plan does not expand.</summary>
    internal static PureNode Row(string capabilityId, string kind, string label, string description,
        JsonElement[] inputs, JsonElement[] outputs, JsonElement[] parameters, JsonElement? variadic,
        HandlerShape shape, EvaluatorHandler evaluate, bool variadicShape = false, string[]? domains = null)
    {
        var name = capabilityId[(capabilityId.LastIndexOf('.') + 1)..];
        return new PureNode(capabilityId, kind, label, description,
            DeclarationGraph("pure", domains ?? Domains, inputs, outputs, parameters, variadic),
            ModuleDefinition.ProviderId + ".binding." + name, "trigger." + kind + "." + name,
            variadicShape ? new HandlerShape() : shape, evaluate);
    }

    /// <summary>The catalog declares a variadic row's two base ports itself and expands the rest per plan; the C#
    /// row repeats that declaration rather than deriving it, because the catalog row is the contract. A capability
    /// that expands its ports per plan has no registration-time layout, so a handler shape for one declares no
    /// port at all.</summary>
    internal static JsonElement[] VariadicInputs(string type) => new[] { Typed("a", type), Typed("b", type) };

    internal static JsonElement[] Inputs(params JsonElement[] ports) => ports;
    internal static JsonElement[] Outputs(params JsonElement[] ports) => ports;
    internal static JsonElement[] Parameters(params JsonElement[] parameters) => parameters;

    internal static JsonElement Number(string id) => Typed(id, "number");
    internal static JsonElement Integer(string id) => Typed(id, "integer");
    internal static JsonElement Bool(string id) => Typed(id, "boolean");
    internal static JsonElement Text(string id) => Typed(id, "string");
    internal static JsonElement Enum(string id, string schema) => Port(id, "enum", schema);
    internal static JsonElement Typed(string id, string type) => Port(id, type);

    /// <summary>A parameter the plan may leave out; the catalog declares the bound, not a default value.</summary>
    internal static JsonElement CountParameter() => Structural("input_count", "integer", 2, 32);
    internal static JsonElement ValueParameter(string id, string type)
        => Parameter(id, type, "value", required: true);
    internal static JsonElement SetEnumParameter(string id, string set)
        => Parameter(id, "enum", "value", required: true, set: set);
    internal static JsonElement InlineEnumParameter(string id, string[] values)
        => Parameter(id, "enum", "structural", required: true, values: values);

    private static JsonElement DeclarationGraph(string execution, string[] domains, JsonElement[] inputs,
        JsonElement[] outputs, JsonElement[] parameters, JsonElement? variadic)
    {
        var graph = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["domains"] = RuntimeJson.From(domains),
            ["execution"] = RuntimeJson.From(execution),
            ["inputs"] = RuntimeJson.From(inputs),
            ["outputs"] = RuntimeJson.From(outputs),
            ["parameters"] = RuntimeJson.From(parameters)
        };
        if (variadic is { } expanded) graph["variadic"] = expanded;
        return RuntimeJson.From(graph);
    }

    internal static JsonElement Variadic(string type) => RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        ["side"] = RuntimeJson.From("inputs"),
        ["parameter"] = RuntimeJson.From("input_count"),
        ["port"] = Typed("input", type)
    });

    private static JsonElement Port(string id, string type, string? schema = null)
    {
        var port = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["id"] = RuntimeJson.From(id), ["type"] = RuntimeJson.From(type)
        };
        if (schema != null) port["schema"] = RuntimeJson.From(schema);
        return RuntimeJson.From(port);
    }

    private static JsonElement Structural(string id, string type, int minimum, int maximum)
    {
        var parameter = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["id"] = RuntimeJson.From(id), ["type"] = RuntimeJson.From(type), ["role"] = RuntimeJson.From("structural"),
            ["required"] = RuntimeJson.From(false), ["minimum"] = RuntimeJson.From(minimum), ["maximum"] = RuntimeJson.From(maximum)
        };
        return RuntimeJson.From(parameter);
    }
    internal static JsonElement Parameter(string id, string type, string role, bool required,
        string? set = null, string[]? values = null, int? minimum = null, int? maximum = null)
    {
        var parameter = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["id"] = RuntimeJson.From(id), ["type"] = RuntimeJson.From(type), ["role"] = RuntimeJson.From(role),
            ["required"] = RuntimeJson.From(required)
        };
        if (set != null) parameter["set"] = RuntimeJson.From(set);
        if (values != null) parameter["values"] = RuntimeJson.From(values);
        if (minimum is { } lower) parameter["minimum"] = RuntimeJson.From(lower);
        if (maximum is { } upper) parameter["maximum"] = RuntimeJson.From(upper);
        return RuntimeJson.From(parameter);
    }

    // ---- handlers ------------------------------------------------------------------------------------------
    // Each handler reads its inputs by the catalog's port ids and returns one property per declared output.
    // The declaration tables above name these by method group; the handler name a binding publishes is derived
    // from the capability id, so a row and its implementation cannot drift apart.

    internal static JsonElement Constant(EvaluationContext context)
        => Value(ScalarNodes.Constant(Parameter(context, "value")));
    internal static JsonElement Add(EvaluationContext context)
        => Value(VariadicNodes.Reduce(ScalarOperation.Add, Numbers(context)));
    internal static JsonElement Subtract(EvaluationContext context)
        => Value(ScalarNodes.Binary(ScalarOperation.Subtract, Input(context, "a"), Input(context, "b")));
    internal static JsonElement Multiply(EvaluationContext context)
        => Value(VariadicNodes.Reduce(ScalarOperation.Multiply, Numbers(context)));
    internal static JsonElement Divide(EvaluationContext context)
        => Value(ScalarNodes.Divide(Input(context, "a"), Input(context, "b"),
            (DivisionZeroPolicy)Member(ZeroPolicies, EnumParameter(context, "zero_policy"), "zero_policy")));
    internal static JsonElement Minimum(EvaluationContext context)
        => Value(VariadicNodes.Reduce(ScalarOperation.Minimum, Numbers(context)));
    internal static JsonElement Maximum(EvaluationContext context)
        => Value(VariadicNodes.Reduce(ScalarOperation.Maximum, Numbers(context)));
    internal static JsonElement Clamp(EvaluationContext context)
        => Value(ScalarNodes.Clamp(Input(context, "value"), Input(context, "minimum"), Input(context, "maximum")));
    internal static JsonElement Absolute(EvaluationContext context)
        => Value(ScalarNodes.Absolute(Input(context, "value")));
    internal static JsonElement Round(EvaluationContext context)
        => Value(ScalarNodes.Round(Input(context, "value"),
            (ScalarRounding)Member(RoundingModes, EnumParameter(context, "mode"), "mode")));
    internal static JsonElement RandomRange(EvaluationContext context)
        => Value(SeededNodes.Uniform(Input(context, "minimum"), Input(context, "maximum"), Whole(context, "seed")));
    /// <summary>The one text row: the template with its format item filled from the value.</summary>
    internal static JsonElement Text(EvaluationContext context)
        => Value(TextNodes.Compose(PortText(context, "template"), Input(context, "value")));
    internal static JsonElement All(EvaluationContext context) => Value(VariadicNodes.All(Flags(context)));
    internal static JsonElement Any(EvaluationContext context) => Value(VariadicNodes.Any(Flags(context)));
    internal static JsonElement Not(EvaluationContext context) => Value(PureConditions.Not(Flag(context, "input")));

    /// <summary>Every input port of a variadic row is one slot of the same computation. The frame keeps the
    /// contract's own port order, which is the order the author wired, so the values are read in that order; the
    /// 2..32 count bound lives in <see cref="VariadicNodes"/>, not here.</summary>
    private static double[] Numbers(EvaluationContext context)
    {
        var values = new List<double>();
        foreach (var port in context.Inputs.EnumerateObject()) values.Add(port.Value.GetDouble());
        return values.ToArray();
    }
    private static bool[] Flags(EvaluationContext context)
    {
        var values = new List<bool>();
        foreach (var port in context.Inputs.EnumerateObject()) values.Add(port.Value.GetBoolean());
        return values.ToArray();
    }

    private static double Input(EvaluationContext context, string port) => context.Inputs.GetProperty(port).GetDouble();
    private static bool Flag(EvaluationContext context, string port) => context.Inputs.GetProperty(port).GetBoolean();
    private static long Whole(EvaluationContext context, string port) => context.Inputs.GetProperty(port).GetInt64();
    private static string PortText(EvaluationContext context, string port) => context.Inputs.GetProperty(port).GetString()!;
    private static double Parameter(EvaluationContext context, string name) => context.Parameters.GetProperty(name).GetDouble();
    private static string EnumParameter(EvaluationContext context, string name) => context.Parameters.GetProperty(name).GetString()!;

    /// <summary>Resolves a member name the kernel already mapped back from its compiled index. The C# enums are
    /// declared in the catalog set's own order, so the member's position in the array is the enum value.</summary>
    private static int Member(string[] members, string name, string parameter)
    {
        var index = Array.IndexOf(members, name);
        if (index < 0) throw new RuntimeContractException("pure-operation", "Unknown " + parameter + " member: " + name);
        return index;
    }

    private static JsonElement Value(double value) => RuntimeJson.From(new { value });
    private static JsonElement Value(bool value) => RuntimeJson.From(new { value });
    private static JsonElement Value(string value) => RuntimeJson.From(new { value });
}
