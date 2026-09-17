using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The two event-table actions that drive the objective machine: the sub-objective text a level's own
/// event list writes (`eWardenObjectiveEventType.UpdateCustomSubObjective`, 11) and the step a progression
/// objective advances by (`eWardenObjectiveEventType.StepProgressionObjective`, 18).
///
/// Both are one member of `WardenObjectiveEventData` handed to `WorldEventManager.ExecuteEvent`, which is the
/// entry every vanilla mount point already uses, so neither row invents a second write path to the objective
/// machine. `WardenObjectiveEventData` is the evidence for every port below: `CustomSubObjectiveHeader` and
/// `CustomSubObjective` are the two `LocalizedText` members the display row carries (dump.cs:646601 and 646603),
/// `Layer` is the `LG_LayerType` both rows name their objective by (dump.cs:646587), and `Count` is the integer
/// the progression row advances by (dump.cs:646692). The event types themselves are constants of
/// `eWardenObjectiveEventType` (dump.cs:647028 and 647035).
///
/// The two ids are **new** canonical ids: the site's `catalog/native-event-types.json` records both native types
/// with `canonical: null`, and no row of `catalog/capability-catalog.json` declares them, so this file is their
/// first declaration and the ledger's `vanilla-level.ev.update_custom_subobjective`,
/// `vanilla-level.obj.structure.text` and `vanilla-level.ev.step_progression_objective` rows are what they serve.
/// The one native event type whose canonical id already exists — `SpawnEnemyOnPoint` (16) mapping to
/// `forge.action.map.spawn_commit` — is deliberately *not* declared here: that catalog row's contract is a
/// `spawn_solve` solution handle committed against a map resource after re-validation, and the event-table entry
/// selects its point by a world event object filter, so binding the two together would be a handler that cannot
/// honor the ports it declares.
///
/// Every row is `host` execution: the executor writes the objective machine's replicated state, so the peer that
/// decides the write is the host and a client applying replicated state must not commit a second one. The ids
/// carry the site's own naming (`presentation` for what the HUD shows), which is a name and not an execution
/// tier — the same split `forge.action.presentation.lighting` already carries.
///
/// Each row's recipient port is its `objectives` resource input, declared non-optional the way the framework
/// requires an action to declare its recipients and refused by name when a plan supplies one, because no provider
/// in this runtime resolves an objective reference: the target the request really names is the row's own `layer`
/// parameter, which the native half reads.</summary>
public static class ObjectiveEventContract
{
    /// <summary>The provider every row here belongs to, spelled the way `WorldEventContract`, `LevelEventContract`
    /// and the other contracts of this package spell it: the declaration and every binding id below are built from
    /// it, and a focused case asserts the two agree.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    // ---- the two rows -------------------------------------------------------------------------------

    /// <summary>The checklist's "update custom sub-objective": the header and the body of the sub-objective line
    /// the objective panel shows. A `LocalizedText` carries free text through its string constructor, which is what
    /// lets a plan write text it computed rather than a localisation id it could not have known.</summary>
    public const string DisplayCapability = "forge.action.presentation.objective_display";

    /// <summary>The checklist's "step a progression objective": the objective's own progress moves forward by the
    /// step count the plan supplies, and the objective completes through the game's normal flow once the progress
    /// is full.</summary>
    public const string ProgressCapability = "forge.action.map.objective_progress";

    /// <summary>The two handler names the bindings below publish under and `MapPluginSession`'s handler table
    /// answers.</summary>
    public const string DisplayHandlerName = "gtfo.map.objective_display";
    public const string ProgressHandlerName = "gtfo.map.objective_progress";

    /// <summary>The permissions the two rows write under: the sub-objective text and the progress of an objective
    /// are the objective machine's own namespaces, spelled here the way the sibling objective rows spell
    /// `objective.state` and `objective.phase`.</summary>
    public const string DisplayPermission = "objective.text";
    public const string ProgressPermission = "objective.progress";

    /// <summary>The domain list both rows name: the objective machine is what a room, a logic step and a level
    /// event all act on, which is the set the package's other objective rows carry.</summary>
    internal static readonly string[] Domains = { "map", "room", "logic" };

    /// <summary>`LG_LayerType`'s members as the rows index them, in the enum's own order: the same three members
    /// every other layer-naming row of this package carries.</summary>
    internal static readonly string[] Layers = { "main", "secondary", "third" };

    // ---- refusal codes ------------------------------------------------------------------------------

    /// <summary>A request arriving on a peer that may not decide the write.</summary>
    public const string AuthorityCode = "authority-or-phase";
    /// <summary>A plan supplied the recipient resource this runtime cannot resolve.</summary>
    public const string TargetCode = "objective-target-unsupported";
    /// <summary>The `layer` parameter is missing or names a member `LG_LayerType` does not have.</summary>
    public const string LayerCode = "objective-layer-unknown";
    /// <summary>The display row was handed no header and no body, or a text member that is not a string.</summary>
    public const string TextCode = "objective-text-required";
    /// <summary>The progression row was handed a step count that is not a positive integer.</summary>
    public const string StepsCode = "objective-steps-invalid";
    /// <summary>The objective machine is not standing: a world that was torn down leaves no instance to write.</summary>
    public const string UnavailableCode = "objective-unavailable";
    /// <summary>The native entry threw. The executor may already have written the replicated state before it
    /// threw, so the commit state is `unknown` rather than `none`.</summary>
    public const string CommitExceptionCode = "native-commit-exception";

    /// <summary>The provider-side binding id of one capability: this provider's own namespace plus the capability's
    /// own suffix, which is the rule every other contract of this package already spells, so a binding id that
    /// disagrees with the capability it serves fails registration.</summary>
    public static string Binding(string capabilityId)
        => capabilityId.StartsWith("forge.", StringComparison.Ordinal)
            ? ProviderId + ".binding." + capabilityId["forge.".Length..].Replace('.', '_')
            : throw new RuntimeContractException("objective-event-capability", capabilityId);

    // ---- one request, decided before the native call --------------------------------------------------

    /// <summary>One display request the native half can carry: the layer's index in <see cref="Layers"/>, its
    /// member name for the result row, and the two texts. Both texts are required — a vanilla
    /// `UpdateCustomSubObjective` sample sets both members, and a request that writes one of them while leaving
    /// the other as whatever the objective's data block carried is not a text this row can describe.</summary>
    public readonly struct DisplayRequest
    {
        internal DisplayRequest(int layer, string layerName, string header, string body)
        {
            Layer = layer;
            LayerName = layerName;
            Header = header;
            Body = body;
        }

        public int Layer { get; }
        public string LayerName { get; }
        public string Header { get; }
        public string Body { get; }
    }

    /// <summary>One progression request: the layer and the number of steps to advance by, which the native
    /// `Count` member carries.</summary>
    public readonly struct ProgressRequest
    {
        internal ProgressRequest(int layer, string layerName, int steps)
        {
            Layer = layer;
            LayerName = layerName;
            Steps = steps;
        }

        public int Layer { get; }
        public string LayerName { get; }
        public int Steps { get; }
    }

    /// <summary>Every check the display row makes, in the order the request would be carried out: a peer that may
    /// not decide, a recipient this runtime cannot resolve, a layer every write names, and the two texts. The
    /// native half runs only a request this method approved.</summary>
    public static bool TryDisplay(CommandContext context, out DisplayRequest request, out string? code)
    {
        ArgumentNullException.ThrowIfNull(context);
        request = default;
        code = null;
        if (!context.IsHost) { code = AuthorityCode; return false; }
        if (Present(context.Inputs, "objectives")) { code = TargetCode; return false; }
        if (!TryLayer(context.Parameters, out int layer, out string? layerName)) { code = LayerCode; return false; }
        if (!TryText(context.Inputs, "header", out string? header)) { code = TextCode; return false; }
        if (!TryText(context.Inputs, "body", out string? body)) { code = TextCode; return false; }
        request = new DisplayRequest(layer, layerName!, header!, body!);
        return true;
    }

    /// <summary>Every check the progression row makes. A step count of zero or less is refused rather than folded
    /// into the nearest legal write: advancing an objective by nothing is not a step, and a negative count would
    /// move the progress backwards through a path no vanilla event uses.</summary>
    public static bool TryProgress(CommandContext context, out ProgressRequest request, out string? code)
    {
        ArgumentNullException.ThrowIfNull(context);
        request = default;
        code = null;
        if (!context.IsHost) { code = AuthorityCode; return false; }
        if (Present(context.Inputs, "objectives")) { code = TargetCode; return false; }
        if (!TryLayer(context.Parameters, out int layer, out string? layerName)) { code = LayerCode; return false; }
        if (!context.Inputs.TryGetProperty("steps", out var steps) || !steps.TryGetInt32(out int count) || count < 1)
        {
            code = StepsCode;
            return false;
        }
        request = new ProgressRequest(layer, layerName!, count);
        return true;
    }

    // ---- what a caller reads back ---------------------------------------------------------------------

    /// <summary>The two members the request asked to write, so a plan that displayed a sub-objective can read the
    /// text it wrote without a second query.</summary>
    public static JsonElement DisplayOutputs(DisplayRequest request) => RuntimeJson.From(new
    {
        layer = request.LayerName,
        header = request.Header,
        body = request.Body
    });

    /// <summary>The layer and the step count the objective was advanced by.</summary>
    public static JsonElement ProgressOutputs(ProgressRequest request) => RuntimeJson.From(new
    {
        layer = request.LayerName,
        steps = request.Steps
    });

    /// <summary>The refusal a caller answers with, in the framework's own vocabulary: a request this row cannot
    /// honor wrote nothing, so the commit state is `none`.</summary>
    public static CommandResult Refused(string code) => CommandResult.Rejected(code);

    /// <summary>The outcome of a native call that threw: the commit state is `unknown` because the executor may
    /// already have written the replicated state this layer cannot read back.</summary>
    public static CommandResult Failed(string code) => CommandResult.FailedUnknown(code);

    // ---- the declaration ------------------------------------------------------------------------------

    /// <summary>Every capability row this file declares, in the order the two handlers are registered.</summary>
    public static object[] CapabilityRows() => new object[] { DisplayRow(), ProgressRow() };

    /// <summary>One execute binding row per capability, spelled from the capability's own id.</summary>
    public static object[] BindingRows() => new object[]
    {
        Row(Binding(DisplayCapability), DisplayCapability, DisplayHandlerName),
        Row(Binding(ProgressCapability), ProgressCapability, ProgressHandlerName)
    };

    /// <summary>One registration support row per binding, each carrying the permission its own capability
    /// writes under.</summary>
    public static BindingSupport[] Supports() => new[]
    {
        new BindingSupport(Binding(DisplayCapability), "implementation-only", new[] { DisplayPermission }),
        new BindingSupport(Binding(ProgressCapability), "implementation-only", new[] { ProgressPermission })
    };

    /// <summary>One shape per handler, resolved once at registration against the capability its binding
    /// implements: every shape names exactly the ports and parameters its own row declares.</summary>
    public static readonly HandlerShape DisplayShape = new HandlerShape()
        .Inputs("objectives", "header", "body").Outputs("result").Parameters("layer");
    public static readonly HandlerShape ProgressShape = new HandlerShape()
        .Inputs("objectives", "steps").Outputs("result").Parameters("layer");

    public static IReadOnlyDictionary<string, HandlerShape> Shapes()
        => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [DisplayHandlerName] = DisplayShape,
            [ProgressHandlerName] = ProgressShape
        };

    /// <summary>The display row: the two texts are plan values rather than structural parameters, because a plan
    /// computes them at runtime and the result reports what was written.</summary>
    private static object DisplayRow() => new
    {
        id = DisplayCapability, owner = ProviderId, kind = "action", label = "改写子目标文字", version = "1.0.0",
        parameters = new { description = "把目标面板上的子目标标题与正文换成计划给出的文字。" },
        graph = new
        {
            domains = Domains, execution = "host",
            inputs = new object[]
            {
                Port("in", "execution"),
                Resource("objectives", "objective", "forge.resource.objective"),
                Port("header", "string"),
                Port("body", "string")
            },
            outputs = new object[]
            {
                Port("next", "execution"),
                Result("forge.result.presentation.objective_display", ("layer", "string"), ("header", "string"),
                    ("body", "string"))
            },
            parameters = new object[] { Enum("layer", Layers, required: true) },
            recipients = new
            {
                input = "objectives", target = "resource", cardinality = "one",
                requires = new[] { DisplayPermission }, result = "result"
            }
        }
    };

    /// <summary>The progression row: the step count is a plan value, because how far to advance is what a plan
    /// computed rather than a structural choice.</summary>
    private static object ProgressRow() => new
    {
        id = ProgressCapability, owner = ProviderId, kind = "action", label = "推进进度型目标", version = "1.0.0",
        parameters = new { description = "把目标面板上的进度型目标按计划给出的步数往前推进。" },
        graph = new
        {
            domains = Domains, execution = "host",
            inputs = new object[]
            {
                Port("in", "execution"),
                Resource("objectives", "objective", "forge.resource.objective"),
                Port("steps", "integer")
            },
            outputs = new object[]
            {
                Port("next", "execution"),
                Result("forge.result.map.objective_progress", ("layer", "string"), ("steps", "integer"))
            },
            parameters = new object[] { Enum("layer", Layers, required: true) },
            recipients = new
            {
                input = "objectives", target = "resource", cardinality = "one",
                requires = new[] { ProgressPermission }, result = "result"
            }
        }
    };

    /// <summary>One execute binding row: this provider's own binding id, the capability it implements, the handler
    /// name the session's table answers and `execute` as the role.</summary>
    private static object Row(string bindingId, string capabilityId, string handler) => new
    {
        id = bindingId, capabilityId, providerId = ProviderId, handler, role = "execute", status = "implemented",
        dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    /// <summary>The result row every action of this family carries: the four shared columns and this row's own
    /// fields, in the order the capability above declares them.</summary>
    private static object Result(string schema, params (string Id, string Type)[] extra)
    {
        var fields = new List<object>
        {
            new { id = "target", type = "entity" },
            new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" },
            new { id = "code", type = "string" }
        };
        foreach (var (id, type) in extra) fields.Add(new { id, type });
        return new { id = "result", type = "result", schema, fields };
    }

    private static object Port(string id, string type) => new { id, type };

    /// <summary>One resource input port. It is the row's recipient port: the framework requires an action to
    /// declare one, and this is the reference the catalog's objective rows name, so it travels on both rows here
    /// rather than being left out.</summary>
    private static object Resource(string id, string resourceKind, string schema)
        => new { id, type = "resource", resourceKind, schema };

    /// <summary>A structural enum that inlines its own members: the runtime accepts a structural enum with no
    /// shared set, which is what the layer vocabulary is.</summary>
    private static object Enum(string id, string[] values, bool required = false)
        => new { id, type = "enum", role = "structural", required, values };

    /// <summary>One text port: a plan value, so an absent member and a member of another type are both the
    /// refusal <see cref="TextCode"/> rather than a text this row would write empty.</summary>
    private static bool TryText(JsonElement inputs, string port, out string? text)
    {
        text = null;
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(port, out var value)) return false;
        if (value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString();
        return !string.IsNullOrEmpty(text);
    }

    /// <summary>The layer a request names, as its index in <see cref="Layers"/>: the member name a structural enum
    /// resolves to, or the ordinal itself when the plan handed a promoted constant. Anything else is refused by
    /// name rather than folded into the nearest layer.</summary>
    private static bool TryLayer(JsonElement parameters, out int layer, out string? name)
    {
        layer = -1;
        name = null;
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("layer", out var value))
            return false;
        if (value.ValueKind == JsonValueKind.String)
        {
            int index = Array.IndexOf(Layers, value.GetString());
            if (index < 0) return false;
            layer = index;
            name = Layers[index];
            return true;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int ordinal)
            && ordinal >= 0 && ordinal < Layers.Length)
        {
            layer = ordinal;
            name = Layers[ordinal];
            return true;
        }
        return false;
    }

    /// <summary>Whether a plan supplied the recipient port a row declares. An absent port, a JSON null and an
    /// empty array all mean the plan asked for no target, which is the only form these rows serve.</summary>
    private static bool Present(JsonElement inputs, string port)
        => inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(port, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0);
}
