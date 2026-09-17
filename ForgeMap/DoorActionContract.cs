using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The door action rows this provider implements for the one `gtfo.map_object` namespace. Every row is
/// declared here — capability shape, binding row, registration support and handler shape — so the
/// game-independent declaration, the runtime registration and the website catalog cannot describe the same row
/// three different ways. The capability shapes are the catalog's, port for port, including its `recipients`
/// contract; a port the native side cannot carry is declared optional here and refused by name when a plan
/// supplies one anyway.
///
/// Three of the six catalog rows are **not** declared, because no binding here could keep their promise:
///
/// - `forge.action.map.door_lock` needs a native `GateKeyItem` instance for its `specific` policy, which only an
///   item-resource provider can resolve (R-ABI batch C's resource registry), and its `none` policy is a lock
///   component setup whose interaction text the catalog row has no port for. The native writes themselves exist
///   in `Native/DoorActions.cs`; what is missing is the input half, so the row stays unregistered instead of
///   being bound to a handler that can only refuse.
/// - `forge.action.map.door_unlock` has no honest native entry at all: the door's interaction entry carries an
///   `onlyUnlock` parameter nothing but its name describes, and every other candidate needs a player at the door
///   or would be a direct field write that the game does not replicate. The row's recipients are the doors
///   themselves, never a handle: the `locks` handle it used to take is gone with the handle kinds no native
///   object stands behind (ruling 160.3).
/// - `forge.action.map.door_damage` addresses a `security` entrance, and on build 20403457
///   `LG_SecurityDoor.AttemptDamage` is the folded empty body while the damageable `LG_WeakDoor` has no address
///   in this provider's address grammar. The native refusal is recorded in `Native/DoorActions.cs` and in
///   `evidence/door-actions.json` rather than registered as a row that always rejects.
///
/// A plan that pins one of those three capabilities therefore fails to resolve a binding at load, which is the
/// truthful answer: the capability is not implemented by this provider.</summary>
public static class DoorActionContract
{
    public const string OpenCapability = "forge.action.map.door_open";
    public const string CloseCapability = "forge.action.map.door_close";
    public const string AlarmCapability = "forge.action.map.door_alarm";

    /// <summary>The permissions the catalog's own recipient contracts name, one per row. They are the whole
    /// permission a plan pinning the binding declares, because no row here depends on another binding.</summary>
    public const string OpenPermission = "door.control";
    public const string ClosePermission = "door.control";
    public const string AlarmPermission = "door.alarm";

    /// <summary>The catalog's domain list for these rows: a door is a map object that rooms and logic graphs
    /// act on, and all three rows name the same set.</summary>
    internal static readonly string[] Domains = { "map", "room", "logic" };

    public const string OpenHandlerName = "gtfo.map_object.door_open";
    public const string CloseHandlerName = "gtfo.map_object.door_close";
    public const string AlarmHandlerName = "gtfo.map_object.door_alarm";

    /// <summary>Internal binding ids, by capability: this provider's own namespace plus the capability's own
    /// suffix, so the counterpart of a row is readable from either side.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.".Length..];

    /// <summary>The open row's own ports, in the catalog's order: the recipient collection and the requested
    /// animation duration. `bypass_policy` is the row's one structural choice and is read from the node's own
    /// constant bag.</summary>
    public static readonly HandlerShape OpenShape = new HandlerShape()
        .Inputs("doors", "duration").Outputs("result").Parameters("bypass_policy");

    /// <summary>The close row's ports: the same recipient collection and the two structural policies the
    /// catalog declares.</summary>
    public static readonly HandlerShape CloseShape = new HandlerShape()
        .Inputs("doors").Outputs("result").Parameters("occupancy_policy", "force_policy");

    /// <summary>The alarm row's ports: the recipient collection, the required source reference the native path
    /// carries nowhere, the author-named alarm resource and the handle the row never produces. Both the alarm
    /// resource and the handle are declared so an authored plan still compiles — a required port a plan cannot
    /// satisfy would make every alarm step unloadable — and the handler refuses a supplied resource by name.</summary>
    public static readonly HandlerShape AlarmShape = new HandlerShape()
        .Inputs("doors", "source", "alarm").Outputs("result", "alarm_handle").Parameters("mode");

    /// <summary>One action row: the catalog's id, label, description, domains, execution, ports, parameters and
    /// recipient contract. The capability rows below are this provider's own because the catalog is the only
    /// other place that carries them.</summary>
    internal static object Row(string capability, string label, string description, object[] inputs,
        object[] outputs, object[] parameters, object recipients)
        => new
        {
            id = capability, owner = ModuleDefinition.ProviderId, kind = "action", label, version = "1.0.0",
            parameters = new { description },
            graph = new { domains = Domains, execution = "host", inputs, outputs, parameters, recipients }
        };

    /// <summary>The recipient contract every row here carries: the collection a plan wired in, the entity
    /// target kind, the cardinality the port declares, the catalog's own requirement name and the row's result
    /// port.</summary>
    internal static object Recipients(string input, string requirement, string result = "result", string? handle = null)
        => handle == null
            ? new { input, target = "entity", cardinality = "many", requires = new[] { requirement }, result }
            : new { input, target = "entity", cardinality = "many", requires = new[] { requirement }, result, handle };

    /// <summary>The result port every row here declares. The four fixed columns are the framework's result row
    /// for every action; the row's own columns follow them in the order the catalog lists them.</summary>
    internal static object ResultPort(string schema, params object[] fields)
        => new { id = "result", type = "result", schema, fields };

    internal static object Port(string id, string type) => new { id, type };
    internal static object Many(string id, string type) => new { id, type, cardinality = "many" };
    internal static object Optional(string id, string type) => new { id, type, optional = true };
    internal static object OptionalTicks(string id) => new { id, type = "integer", unit = "tick", optional = true };
    internal static object OptionalResource(string id, string resourceKind, string schema)
        => new { id, type = "resource", resourceKind, schema, optional = true };
    internal static object OptionalHandle(string id, string handleKind, string lifetime)
        => new { id, type = "handle", handleKind, lifetime, optional = true };
    internal static object Field(string id, string type) => new { id, type };
    internal static object EnumField(string id, string schema) => new { id, type = "enum", schema };
    internal static object UnitField(string id, string type, string unit) => new { id, type, unit };
    internal static object Structural(string id, bool required, params string[] values)
        => new { id, type = "enum", role = "structural", required, values };

    /// <summary>The fixed result columns every row here carries, in the framework's own order.</summary>
    private static object[] FixedFields() => new object[]
    {
        Field("target", "entity"), EnumField("status", "execution_outcome"),
        EnumField("committed", "commit_state"), Field("code", "string")
    };

    /// <summary>The open row, spelled exactly as the catalog carries it. `duration` is optional because the
    /// door's own interaction entry takes no caller-set duration: the catalog's required port would otherwise
    /// demand a value the native path cannot honour, and the handler refuses a non-zero one by name.</summary>
    public static object OpenRow() => Row(OpenCapability, "请求打开门", "请求打开门。",
        new object[]
        {
            Port("in", "execution"),
            Many("doors", "entity"),
            OptionalTicks("duration")
        },
        new object[]
        {
            Port("next", "execution"),
            ResultPort("forge.result.map.door_open",
                Field("target", "entity"), EnumField("status", "execution_outcome"),
                EnumField("committed", "commit_state"), Field("code", "string"),
                UnitField("duration", "integer", "tick"), Field("target_count", "integer"))
        },
        new object[] { Structural("bypass_policy", true, "respect", "force") },
        Recipients("doors", OpenPermission));

    /// <summary>The close row, spelled exactly as the catalog carries it. Both structural policies stay
    /// required: they are the plan's own choice, and the native interaction entry carries neither, so a request
    /// for `crush` or `force` is refused by name rather than quietly closed as a plain close.</summary>
    public static object CloseRow() => Row(CloseCapability, "请求关闭门", "请求关上门。",
        new object[]
        {
            Port("in", "execution"),
            Many("doors", "entity")
        },
        new object[]
        {
            Port("next", "execution"),
            ResultPort("forge.result.map.door_close",
                Field("target", "entity"), EnumField("status", "execution_outcome"),
                EnumField("committed", "commit_state"), Field("code", "string"),
                Field("target_count", "integer"))
        },
        new object[]
        {
            Structural("occupancy_policy", true, "block", "crush"),
            Structural("force_policy", true, "normal", "force")
        },
        Recipients("doors", ClosePermission));

    /// <summary>The alarm row, spelled exactly as the catalog carries it, except for the two ports the native
    /// path cannot carry: the author-named `alarm` resource has no provider in this runtime (the door's own
    /// chained puzzle is the alarm, not a block a plan names) and `alarm_handle` is never produced because the
    /// runtime mints no provider handle for a command yet (R-ABI batch C). Both are declared optional — a
    /// handle that is never produced is declared as the absence it is rather than filled with a placeholder —
    /// and the handler refuses a supplied resource by name.</summary>
    public static object AlarmRow() => Row(AlarmCapability, "启动或解除门警报", "启动或解除门上的警报。",
        new object[]
        {
            Port("in", "execution"),
            Many("doors", "entity"),
            Port("source", "entity"),
            OptionalResource("alarm", "encounter", "forge.resource.encounter")
        },
        new object[]
        {
            Port("next", "execution"),
            ResultPort("forge.result.map.door_alarm",
                Field("target", "entity"), EnumField("status", "execution_outcome"),
                EnumField("committed", "commit_state"), Field("code", "string"),
                Field("target_count", "integer")),
            OptionalHandle("alarm_handle", "effect", "encounter")
        },
        new object[] { Structural("mode", true, "start", "stop") },
        Recipients("doors", AlarmPermission, handle: "alarm_handle"));

    /// <summary>The three capability rows in the order this module declares them.</summary>
    public static object[] Rows() => new object[] { OpenRow(), CloseRow(), AlarmRow() };

    /// <summary>One execute binding row: this provider's own id, the canonical capability, the handler the
    /// native half supplies, and no dependencies — the closure of a plan that pins it is the row itself.</summary>
    public static object BindingRow(string capability, string handler) => new
    {
        id = Binding(capability),
        capabilityId = capability,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    /// <summary>The three binding rows in the same order as <see cref="Rows"/>.</summary>
    public static object[] Bindings() => new object[]
    {
        BindingRow(OpenCapability, OpenHandlerName),
        BindingRow(CloseCapability, CloseHandlerName),
        BindingRow(AlarmCapability, AlarmHandlerName)
    };

    /// <summary>One binding's registration support: the one permission the catalog's recipient contract names
    /// for the row. A door is a map object whose own interaction, lock and alarm a plan reaches through this
    /// provider, so the permission is the row's, not the whole map-object namespace's read permission.</summary>
    public static BindingSupport Support(string capability, string permission)
        => new(Binding(capability), "implementation-only", new[] { permission });

    /// <summary>The three registration support rows, in the same order as <see cref="Bindings"/>.</summary>
    public static BindingSupport[] Supports() => new[]
    {
        Support(OpenCapability, OpenPermission),
        Support(CloseCapability, ClosePermission),
        Support(AlarmCapability, AlarmPermission)
    };

    /// <summary>The handler shapes this provider's native half answers, keyed by handler name. A registration
    /// composes them into its own shape table; the native half never declares a second layout.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [OpenHandlerName] = OpenShape,
        [CloseHandlerName] = CloseShape,
        [AlarmHandlerName] = AlarmShape
    };
}
