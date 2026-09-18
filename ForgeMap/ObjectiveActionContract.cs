using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The objective rows this provider executes: the catalog's three `forge.action.map.*` action rows whose
/// native half is the objective machine (`objective_state`, `objective_phase`) and the level's own exit item
/// (`extraction_enable`). They live in the game-independent assembly because the runtime module, the manifest and
/// the website read them there; the handler and the native write path are the native assembly's
/// `ObjectiveActionHandler` and `ObjectiveActions`, which answer through these same shapes. A registration only
/// declares what it answers, which is why this file is a declaration and not part of the identity table.
///
/// The three capability rows travel with this file rather than with the runtime's trigger contract, because no
/// canonical owner declares them yet: <see cref="CapabilityRows"/> is what the Map registration appends to its
/// own `capabilities` array, which is also what makes the binding rows below registrable at all.
///
/// Three parts of the catalog's shape are declared optional here rather than required, because a required port is
/// a port every plan must wire and no native path in this runtime carries them: `expected_state` and
/// `expected_phase` are the values the interaction channel has no field to compare against, and `participants`
/// names recipients neither the objective machine nor the exit item takes. The handler refuses each of them by
/// name when a plan supplies one anyway. The target a request really acts on is named by this row's own structural
/// parameters, which the native half reads: the objective layer and chain for the two objective rows and the layer
/// for the extraction.
///
/// The two `objectives` resource inputs and the extraction row's `extractions` input are each row's recipient
/// port, which the framework requires an action to declare and requires to be really there: they are declared
/// non-optional like every other row's recipient port, and the handler refuses a plan that supplies one, because
/// no provider in this runtime resolves a resource or entity reference the way an action target would have to
/// be resolved. Both `objectives` inputs are declared as the one reference the row can lay out rather than the
/// catalog's `many` collection: a collection of resources has no frame layout, so the catalog's cardinality is
/// the one part of its shape this build cannot carry, exactly as on the level-event timer row.</summary>
public static class ObjectiveActionContract
{
    public const string StateCapability = "forge.action.map.objective_state";
    public const string PhaseCapability = "forge.action.map.objective_phase";
    public const string ExtractionCapability = "forge.action.map.extraction_enable";

    public const string StateBindingId = ModuleDefinition.ProviderId + ".binding.objective_state";
    public const string PhaseBindingId = ModuleDefinition.ProviderId + ".binding.objective_phase";
    public const string ExtractionBindingId = ModuleDefinition.ProviderId + ".binding.extraction_enable";

    public const string StateHandlerName = "gtfo.map.objective_state";
    public const string PhaseHandlerName = "gtfo.map.objective_phase";
    public const string ExtractionHandlerName = "gtfo.map.extraction_enable";

    /// <summary>The permission the objective rows write under: a request moves the state of an objective this
    /// map's own data blocks declare, so the plan must be able to act on the map it is attached to.</summary>
    public const string ObjectiveWritePermission = "objective.state";

    /// <summary>The permission the extraction row writes under: it arms the exit item the level itself built.</summary>
    public const string ExtractionWritePermission = "extraction.control";

    /// <summary>The layer members the structural `layer` parameter indexes, in the order `LG_LayerType`
    /// declares them: the same three layers the native objective state is kept per.</summary>
    public static readonly string[] Layers = { "main", "secondary", "third" };

    /// <summary>The transition policies the catalog's `objective_phase` parameter offers. Only the forcing one
    /// reaches a native entry: the interaction channel carries no expected phase for a strict request to check,
    /// so the handler refuses `strict` instead of serving it as a force.</summary>
    public static readonly string[] TransitionPolicies = { "strict", "force" };

    // One shape per handler, resolved once at registration against the capability its binding implements. Every
    // one names exactly the ports and parameters its row declares, so the shape and the row cannot drift.
    public static readonly HandlerShape StateShape = new HandlerShape()
        .Inputs("objectives", "state", "expected_state").Outputs("result")
        .Parameters("layer", "chain", "sub_objective", "item_id", "extra_time");
    public static readonly HandlerShape PhaseShape = new HandlerShape()
        .Inputs("objectives", "phase", "expected_phase").Outputs("result")
        .Parameters("layer", "chain", "transition_policy", "event_break_index", "event_index");
    public static readonly HandlerShape ExtractionShape = new HandlerShape()
        .Inputs("extractions", "enabled", "participants").Outputs("result").Parameters("layer");

    /// <summary>The three execute binding rows, in the order the module declares its other action bindings. What
    /// the Map provider's own declaration takes is binding rows, not capability rows: a binding names the
    /// capability it implements, and the row itself is one of <see cref="CapabilityRows"/>.</summary>
    public static object[] Bindings() => new object[]
    {
        Row(StateBindingId, StateCapability, StateHandlerName),
        Row(PhaseBindingId, PhaseCapability, PhaseHandlerName),
        Row(ExtractionBindingId, ExtractionCapability, ExtractionHandlerName)
    };

    /// <summary>The three registration support rows, in the same order as <see cref="Bindings"/>.</summary>
    public static BindingSupport[] Supports() => new[]
    {
        new BindingSupport(StateBindingId, "implementation-only", new[] { ObjectiveWritePermission }),
        new BindingSupport(PhaseBindingId, "implementation-only", new[] { ObjectiveWritePermission }),
        new BindingSupport(ExtractionBindingId, "implementation-only", new[] { ExtractionWritePermission })
    };

    /// <summary>The three capability rows, in the same order as <see cref="Bindings"/>: one action row per id
    /// carrying this provider's own graph. This is the half the registration appends to its own capability list
    /// and the half the website's catalog is compared with; the binding rows carry no graph of their own. A
    /// game-independent test reads one row against the handler's own shape.</summary>
    public static readonly IReadOnlyDictionary<string, object> Graphs = new Dictionary<string, object>(StringComparer.Ordinal)
    {
        [StateCapability] = State(),
        [PhaseCapability] = Phase(),
        [ExtractionCapability] = Extraction()
    };

    /// <summary>The same three graphs as the capability objects a registration appends, in binding order.</summary>
    public static object[] CapabilityRows() => new object[] { State(), Phase(), Extraction() };

    /// <summary>One execute binding row: this provider's own id under its own namespace, the canonical capability
    /// it implements and the handler the native half supplies. Every one of the five depends on no other
    /// binding, so the closure of a plan that pins one is that row alone.</summary>
    public static object Row(string bindingId, string capabilityId, string handler) => new
    {
        id = bindingId,
        capabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    private static object State() => Action(StateCapability, StateBindingId, StateHandlerName, "强制完成目标",
        "改变任务目标的状态。", PrimitiveGraphSource.Get(StateCapability));

    private static object Phase() => Action(PhaseCapability, PhaseBindingId, PhaseHandlerName, "推进进程目标一步",
        "切换任务目标的阶段。", PrimitiveGraphSource.Get(PhaseCapability));

    private static object Extraction() => Action(ExtractionCapability, ExtractionBindingId, ExtractionHandlerName,
        "设置撤离可用状态", "把整层自己的出口条件装上或卸下。", new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                Port("in", "execution"),
                // The catalog names the exit as an entity input and the participants as a second one. The exit is
                // this row's recipient port, so it is declared the way the framework requires; the participants
                // stay optional and are refused by name when a plan supplies one.
                Many("extractions", "entity"),
                Port("enabled", "boolean"),
                OptionalMany("participants", "entity", new[] { "gtfo.player" })
            },
            outputs = new object[] { Port("next", "execution"), Result("forge.result.map.extraction_enable") },
            parameters = new object[] { Enum("layer", Layers, required: true) },
            recipients = new
            {
                input = "extractions", target = "entity", cardinality = "many",
                requires = new[] { "extraction.control" }, result = "result"
            }
        });

    /// <summary>One action row: the catalog's label, description and domains, this build's `host` execution, the
    /// catalog's result schema, and the ports and structural parameters the native half reads.</summary>
    private static object Action(string capabilityId, string bindingId, string handler, string label, string description, object graph)
        => new
        {
            id = capabilityId, owner = ModuleDefinition.ProviderId, kind = "action", label, version = "1.0.0",
            parameters = new { description },
            graph
        };

    /// <summary>The result row the catalog declares for these capabilities, field for field: the four shared
    /// columns and the request's own target count.</summary>
    private static object Result(string schema) => new
    {
        id = "result", type = "result", schema,
        fields = new object[]
        {
            new { id = "target", type = "entity" },
            new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" },
            new { id = "code", type = "string" },
            new { id = "target_count", type = "integer" }
        }
    };

    /// <summary>The catalog's domain list for these five rows, unchanged.</summary>
    private static readonly string[] Domains = { "map", "room", "logic" };

    private static object Port(string id, string type) => new { id, type };
    private static object Optional(string id, string type) => new { id, type, optional = true };
    private static object Many(string id, string type) => new { id, type, cardinality = "many" };
    private static object OptionalMany(string id, string type) => new { id, type, cardinality = "many", optional = true };
    private static object OptionalMany(string id, string type, string[] entityKinds) => new { id, type, cardinality = "many", optional = true, entityKinds };
    /// <summary>One resource input port. The catalog declares the objective rows' `objectives` as a `many`
    /// collection; this build declares it as the one reference the row can be handed, because a collection of
    /// resources has no frame layout (`RuntimeGraphContracts.FramePort` refuses it), exactly as the level-event
    /// timer row of this package already declares the same input.</summary>
    private static object Resource(string id, string resourceKind, string schema)
        => new { id, type = "resource", resourceKind, schema };

    /// <summary>A structural enum that inlines its own members: the runtime accepts a structural enum with no
    /// shared set, which is what these three vocabularies are — no shared set spells them.</summary>
    private static object Enum(string id, string[] values, bool required = false)
        => new { id, type = "enum", role = "structural", required, values };

    private static object Integer(string id, bool required = false)
        => new { id, type = "integer", role = "structural", required };

    private static object Number(string id, bool required = false)
        => new { id, type = "number", role = "structural", required };
}
