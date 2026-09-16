using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger.Targeting;

/// <summary>One row of the Trigger vocabulary that is evaluated on demand: the authoring catalog's own capability
/// shape, the binding that implements it as a `query` or `pure` step, the handler that binding resolves against,
/// and the role that binding is registered under — `observe` for a row that reads the world, `evaluate` for a row
/// that reads none. Nothing here restates a port, parameter or domain the catalog row does not declare.</summary>
public sealed record ObservedNode(string CapabilityId, string Kind, string Execution, string Role, string Label,
    string Description, JsonElement Graph, string BindingId, string HandlerName, HandlerShape Shape,
    EvaluatorHandler Evaluate);

/// <summary>One family of observation rows: the declaration table a batch owns, plus the registration tables that
/// table answers with. A family declares rows and nothing else, and a module composes the families it registers —
/// so a batch adds one file and one composition line, and two batches never edit one shared table.</summary>
public sealed class ObservedFamily
{
    private ObservedFamily(ObservedNode[] nodes)
    {
        Nodes = Array.AsReadOnly(nodes);
        Evaluators = nodes.ToDictionary(node => node.HandlerName, node => node.Evaluate, StringComparer.Ordinal);
        Shapes = nodes.ToDictionary(node => node.HandlerName, node => node.Shape, StringComparer.Ordinal);
        Support = Array.AsReadOnly(nodes
            .Select(node => new BindingSupport(node.BindingId, "implementation-only", Array.Empty<string>())).ToArray());
    }

    /// <summary>The declared rows in registration order. Public because the contract tests iterate the same table
    /// the module registers instead of restating it.</summary>
    public IReadOnlyList<ObservedNode> Nodes { get; }

    public JsonElement[] Capabilities => Nodes.Select(node => RuntimeJson.From(new
    {
        id = node.CapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = node.Kind,
        label = node.Label,
        version = ObservedDeclaration.CapabilityVersion,
        parameters = new { description = node.Description },
        graph = node.Graph
    })).ToArray();

    public JsonElement[] Bindings => Nodes.Select(node => RuntimeJson.From(new
    {
        id = node.BindingId,
        capabilityId = node.CapabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler = node.HandlerName,
        role = node.Role,
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    })).ToArray();

    public IReadOnlyDictionary<string, EvaluatorHandler> Evaluators { get; }

    public IReadOnlyDictionary<string, HandlerShape> Shapes { get; }

    public IReadOnlyList<BindingSupport> Support { get; }

    /// <summary>One family from the rows one batch declares. A handler name two rows share is a construction error
    /// here, the same way the registry refuses it at registration: one name is one handler.</summary>
    public static ObservedFamily Declare(params ObservedNode[] nodes) => new(nodes);

    /// <summary>The families one module registers, joined in composition order. The tables are joined rather than
    /// registered as separate modules, so the registry resolves one shape per binding across the whole provider.</summary>
    public static ObservedFamily Compose(params ObservedFamily[] families)
        => new(families.SelectMany(family => family.Nodes).ToArray());
}

/// <summary>The one row factory and the one set of JSON piece builders every family declares through: the kind and
/// the two ids are derived from the capability id, so the ids a module registers can never drift apart, and the
/// binding role follows the row's own tier — a `query` row observes, a `pure` row evaluates — rather than being
/// spelled a second time beside it.</summary>
internal static class ObservedDeclaration
{
    /// <summary>Every row is authoring metadata the website owns; the capability version is the module's own
    /// contract revision, not a copy of a catalog field.</summary>
    internal const string CapabilityVersion = "1.0.0";

    /// <summary>The one domain list every logic row of the catalog declares.</summary>
    internal static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "logic" };

    internal static readonly JsonElement[] NoPorts = Array.Empty<JsonElement>();
    internal static readonly JsonElement[] NoParameters = Array.Empty<JsonElement>();

    /// <summary>One row. The capability id is the row's identity, and its last segment names the binding and the
    /// handler; its second segment is the catalog kind (`condition`, `selector`), which every id of these tables
    /// spells the same way. A row whose ports cannot express the read it performs declares it through
    /// <paramref name="reads"/>, exactly as the catalog row does (`reads: ["world"]`).</summary>
    internal static ObservedNode Node(string capabilityId, string execution, string label, string description,
        JsonElement[] inputs, JsonElement[] outputs, JsonElement[] parameters, HandlerShape shape, EvaluatorHandler evaluate,
        string[]? reads = null)
    {
        var segments = capabilityId.Split('.');
        var kind = segments[1];
        var name = segments[^1];
        return new ObservedNode(capabilityId, kind, execution, execution == "query" ? "observe" : "evaluate",
            label, description, Graph(execution, inputs, outputs, parameters, reads),
            ModuleDefinition.ProviderId + ".binding." + name, "trigger." + kind + "." + name, shape, evaluate);
    }

    // Every piece below is built as a JsonElement rather than as an `object`, so a collection keeps its JSON type
    // instead of being written by an object-typed slot; the catalog row is compared field for field.
    internal static JsonElement[] Inputs(params JsonElement[] ports) => ports;
    internal static JsonElement[] Outputs(params JsonElement[] ports) => ports;
    internal static JsonElement[] Parameters(params JsonElement[] parameters) => parameters;

    /// <summary>The catalog's structural `empty` parameter, which every collection-answering row declares.</summary>
    internal static JsonElement EmptyPolicyParameter() => StructuralEnum("empty", "empty_policy");

    internal static JsonElement StructuralEnum(string id, string set) => RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        ["id"] = RuntimeJson.From(id), ["type"] = RuntimeJson.From("enum"), ["role"] = RuntimeJson.From("structural"),
        ["required"] = RuntimeJson.From(true), ["set"] = RuntimeJson.From(set)
    });

    /// <summary>A structural enum whose members are the row's own inline list, the way the catalog declares the
    /// partition `field`: the compiled value is the member's index into this list, and the handler reads it back as
    /// the member name.</summary>
    internal static JsonElement StructuralEnumValues(string id, string[] values, bool required = true) => RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        ["id"] = RuntimeJson.From(id), ["type"] = RuntimeJson.From("enum"), ["role"] = RuntimeJson.From("structural"),
        ["required"] = RuntimeJson.From(required), ["values"] = RuntimeJson.From(values)
    });

    /// <summary>Every row of these tables is evaluated on demand, so the tier is the row's own `query` or `pure`.
    /// A row that declares a read carries it beside its ports, in the catalog's own field.</summary>
    internal static JsonElement Graph(string execution, JsonElement[] inputs, JsonElement[] outputs, JsonElement[] parameters,
        string[]? reads = null)
    {
        var graph = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["domains"] = RuntimeJson.From(Domains),
            ["execution"] = RuntimeJson.From(execution),
            ["inputs"] = RuntimeJson.From(inputs),
            ["outputs"] = RuntimeJson.From(outputs),
            ["parameters"] = RuntimeJson.From(parameters)
        };
        if (reads != null) graph["reads"] = RuntimeJson.From(reads);
        return RuntimeJson.From(graph);
    }

    internal static JsonElement Entity(string id) => Port(id, "entity", new Dictionary<string, JsonElement>());
    /// <summary>The catalog's parameterized port: the class it declares is replaced by the member its structural
    /// `value_type` parameter names, so one port carries a number, a count, text or an entity reference. The
    /// declared class is `entity`, the widest of the four, exactly as the website rule declares it.</summary>
    internal static JsonElement TypedValue(string id)
        => Port(id, "entity", new Dictionary<string, JsonElement> { ["valueTypeParameter"] = RuntimeJson.From("value_type") });
    /// <summary>An optional port: the catalog declares the row may be written without it, and what an unwritten
    /// one means is the row's own rule rather than a default this table invents.</summary>
    internal static JsonElement OptionalNumber(string id)
        => Port(id, "number", new Dictionary<string, JsonElement> { ["optional"] = RuntimeJson.From(true) });
    /// <summary>The one structural parameter that types a row's ports: a closed inline list, so the compiled
    /// member index is an index into this list and not into some larger set. Member order is the order the catalog
    /// declares, so the parameter's first member is the `number` an unwritten value resolves to.</summary>
    internal static JsonElement OptionalValueTypeParameter()
        => StructuralEnumValues("value_type", PureConditions.ValueTypes, required: false);
    /// <summary>The catalog's resource port: the kind it carries is part of the port, so a value of another kind
    /// is refused at the boundary rather than read as an id this row would have to interpret.</summary>
    internal static JsonElement Resource(string id, string resourceKind, string schema)
        => Port(id, "resource", new Dictionary<string, JsonElement>
        {
            ["resourceKind"] = RuntimeJson.From(resourceKind), ["schema"] = RuntimeJson.From(schema)
        });
    internal static JsonElement NullableEntity(string id)
        => Port(id, "entity", new Dictionary<string, JsonElement> { ["nullable"] = RuntimeJson.From(true) });
    internal static JsonElement Many(string id)
        => Port(id, "entity", new Dictionary<string, JsonElement> { ["cardinality"] = RuntimeJson.From("many") });
    internal static JsonElement ManyNumber(string id)
        => Port(id, "number", new Dictionary<string, JsonElement> { ["cardinality"] = RuntimeJson.From("many") });
    internal static JsonElement Integer(string id) => Port(id, "integer", new Dictionary<string, JsonElement>());
    internal static JsonElement Number(string id, string unit)
        => Port(id, "number", new Dictionary<string, JsonElement> { ["unit"] = RuntimeJson.From(unit) });
    internal static JsonElement Vector(string id, string unit)
        => Port(id, "vector3", new Dictionary<string, JsonElement> { ["unit"] = RuntimeJson.From(unit) });
    internal static JsonElement Text(string id) => Port(id, "string", new Dictionary<string, JsonElement>());
    internal static JsonElement Bool(string id) => Port(id, "boolean", new Dictionary<string, JsonElement>());
    internal static JsonElement Enum(string id, string schema)
        => Port(id, "enum", new Dictionary<string, JsonElement> { ["schema"] = RuntimeJson.From(schema) });
    internal static JsonElement Port(string id, string type, Dictionary<string, JsonElement> fields)
    {
        var port = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["id"] = RuntimeJson.From(id), ["type"] = RuntimeJson.From(type) };
        foreach (var field in fields) port[field.Key] = field.Value;
        return RuntimeJson.From(port);
    }
}

/// <summary>The reads every observation handler shares: a required port, a declared enum member, the structural
/// `empty` policy, and the one budgeted session read. Each of them refuses by name instead of answering a default,
/// because an absent port answered with a zero or an empty set would read exactly like an observed world.</summary>
internal static class ObservedEvaluation
{
    /// <summary>One required port of a resolved frame. The catalog declares these ports non-optional, so a frame
    /// without one is refused by name rather than answered with a default.</summary>
    internal static JsonElement Required(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new RuntimeContractException("missing-field", port);

    internal static EntityReference Entity(EvaluationContext context, string port) => RuntimeJson.Entity(Required(context, port));

    internal static string PortText(EvaluationContext context, string port) => Required(context, port).GetString()!;

    /// <summary>One required structural parameter of the resolved frame, spelled as its member name. A parameter
    /// the row declares is never an input, so it is read from the frame's parameter bag and never from a port.</summary>
    internal static string ParameterText(EvaluationContext context, string parameter)
        => context.Parameters.TryGetProperty(parameter, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()!
            : throw new RuntimeContractException("missing-field", parameter);

    /// <summary>The references one collection port carries. A port the plan left empty is an empty set; a port that
    /// is not there at all is refused, so an absent candidate collection never reads as an observed empty world.</summary>
    internal static IReadOnlyList<EntityReference> Candidates(EvaluationContext context, string port)
    {
        var value = Required(context, port);
        if (value.ValueKind != JsonValueKind.Array)
            throw new RuntimeContractException("missing-field", "Required entity collection is not an array: " + port);
        return Array.AsReadOnly(value.EnumerateArray().Select(RuntimeJson.Entity).ToArray());
    }

    /// <summary>The references one event-sourced collection port carries. A collection a trigger does not carry is
    /// empty here, which the row's own `empty` policy then answers for.</summary>
    internal static IReadOnlyList<EntityReference> Collection(EvaluationContext context, string port)
        => !context.Inputs.TryGetProperty(port, out var value) || value.ValueKind == JsonValueKind.Null
            ? Array.Empty<EntityReference>()
            : Array.AsReadOnly(value.EnumerateArray().Select(RuntimeJson.Entity).ToArray());

    /// <summary>A requested count as <see cref="ReferenceCollections"/> itself bounds it, so an out-of-range size
    /// is refused with the collection's own code rather than by an integer conversion.</summary>
    internal static int RequestedCount(JsonElement value, int minimum, int maximum)
    {
        var count = value.GetInt64();
        if (count < minimum || count > maximum)
            throw new RuntimeContractException("pure-selection-count",
                "Requested selection must be in [" + minimum + ", " + maximum + "].");
        return (int)count;
    }

    /// <summary>One row's selection count (`count` on the catalog's random row, `max_targets` on the limit and
    /// ranking rows), bounded exactly as the collection algebra bounds a selection.</summary>
    internal static int Selection(EvaluationContext context, string port)
        => RequestedCount(Required(context, port), 1, ReferenceCollections.MaximumSelection);

    /// <summary>One reference's current observation. A read the kernel could not complete rejects the step with
    /// the session's own code; there is no partial answer to fall back on.</summary>
    internal static RuntimeEntitySnapshot Observed(EvaluationContext context, string port)
    {
        if (!context.Query.TrySnapshot(RuntimeJson.Entity(Required(context, port)), out var snapshot, out var code))
            throw new RuntimeContractException(code, "Entity observation failed: " + code);
        return snapshot!;
    }

    /// <summary>The catalog's structural empty policy, read from one row's resolved parameter frame. `fail` refuses
    /// an empty answer, `skip` only tells a downstream scheduler to skip work, so the selected value itself stays
    /// empty for both of them.</summary>
    internal static EmptySelectionPolicy EmptyPolicy(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("empty", out var value) || value.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("missing-field", "empty");
        return value.GetString() switch
        {
            "emit-empty" => EmptySelectionPolicy.EmitEmpty,
            "skip" => EmptySelectionPolicy.Skip,
            "fail" => EmptySelectionPolicy.Fail,
            var other => throw new RuntimeContractException("pure-operation", "Unknown empty selection policy: " + other)
        };
    }

    /// <summary>One collection answer: the selected references with the row's own structural `empty` policy
    /// applied, which is the set policy of the catalog and never a policy of the world.</summary>
    internal static JsonElement Targets(EvaluationContext context, IReadOnlyList<EntityReference> selected)
        => RuntimeJson.From(new { targets = ReferenceCollections.ApplyEmptyPolicy(selected, EmptyPolicy(context.Parameters)) });

    /// <summary>The catalog's comparison member names, in the order the C# enum declares them.</summary>
    internal static ScalarComparison Comparison(string member) => member switch
    {
        "eq" => ScalarComparison.Equal, "ne" => ScalarComparison.NotEqual,
        "lt" => ScalarComparison.Less, "lte" => ScalarComparison.LessOrEqual,
        "gt" => ScalarComparison.Greater, "gte" => ScalarComparison.GreaterOrEqual,
        var other => throw new RuntimeContractException("pure-operation", "Unknown operator member: " + other)
    };

    internal static JsonElement Value(bool value) => RuntimeJson.From(new { value });
    internal static JsonElement Value(EntityReference? value) => RuntimeJson.From(new { target = value });
}
