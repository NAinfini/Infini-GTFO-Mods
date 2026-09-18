using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The door action rows this provider implements for the one `gtfo.map_object` namespace. Every row is
/// declared here — capability shape, binding row, registration support and handler shape — so the
/// game-independent declaration, the runtime registration and the website catalog cannot describe the same row
/// three different ways. The capability shapes are the catalog's, port for port, including its `recipients`
/// contract; a catalog port no native entry can carry is deleted on both sides rather than declared and refused,
/// so an author never sees an option the game ignores.
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

    /// <summary>The permissions the catalog's own recipient contracts name, one per row. They are the whole
    /// permission a plan pinning the binding declares, because no row here depends on another binding.</summary>
    public const string OpenPermission = "door.control";
    public const string ClosePermission = "door.control";

    /// <summary>The catalog's domain list for these rows: a door is a map object that rooms and logic graphs
    /// act on, and all three rows name the same set.</summary>
    internal static readonly string[] Domains = { "map", "room", "logic" };

    public const string OpenHandlerName = "gtfo.map_object.door_open";
    public const string CloseHandlerName = "gtfo.map_object.door_close";

    /// <summary>Internal binding ids, by capability: this provider's own namespace plus the capability's own
    /// suffix, so the counterpart of a row is readable from either side.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.".Length..];

    /// <summary>The open row's own ports: the recipient collection. `bypass_policy` is the row's one structural
    /// choice and is read from the node's own constant bag; a requested `duration` is gone with the catalog port,
    /// because the native interaction entry takes none.</summary>
    public static readonly HandlerShape OpenShape = new HandlerShape()
        .Inputs("doors").Outputs("result").Parameters("bypass_policy");

    /// <summary>The close row's ports: the same recipient collection and no structural parameter at all. The two
    /// policies the catalog used to declare are gone with the catalog ports, because the native interaction entry
    /// carries neither an occupancy decision nor a force form.</summary>
    public static readonly HandlerShape CloseShape = new HandlerShape()
        .Inputs("doors").Outputs("result");

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
    internal static object Many(string id, string type, string[] entityKinds)
        => new { id, type, cardinality = "many", entityKinds };
    internal static object Field(string id, string type) => new { id, type };
    internal static object EnumField(string id, string schema) => new { id, type = "enum", schema };
    internal static object Structural(string id, bool required, params string[] values)
        => new { id, type = "enum", role = "structural", required, values };

    /// <summary>The fixed result columns every row here carries, in the framework's own order.</summary>
    private static object[] FixedFields() => new object[]
    {
        Field("target", "entity"), EnumField("status", "execution_outcome"),
        EnumField("committed", "commit_state"), Field("code", "string")
    };

    /// <summary>The open row, spelled exactly as the catalog carries it: the recipients and the one structural
    /// bypass policy. The catalog's `duration` port is gone on both sides, because the door's own interaction
    /// entry takes no caller-set duration.</summary>
    public static object OpenRow() => new
    {
        id = OpenCapability,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "请求打开门",
        version = "1.0.0",
        parameters = new { description = "请求打开门。" },
        graph = PrimitiveGraphSource.Get(OpenCapability)
    };

    /// <summary>The close row, spelled exactly as the catalog carries it: the recipients and nothing else. Both
    /// structural policies are gone on both sides, because the native interaction entry carries neither an
    /// occupancy decision nor a force form.</summary>
    public static object CloseRow() => Row(CloseCapability, "请求关闭门", "请求关上门。",
        new object[]
        {
            Port("in", "execution"),
            Many("doors", "entity", new[] { MapObjectModule.EntityKind })
        },
        new object[]
        {
            Port("next", "execution"),
            ResultPort("forge.result.map.door_close",
                Field("target", "entity"), EnumField("status", "execution_outcome"),
                EnumField("committed", "commit_state"), Field("code", "string"),
                Field("target_count", "integer"))
        },
        Array.Empty<object>(),
        Recipients("doors", ClosePermission));

    /// <summary>The two capability rows in the order this module declares them. There is no third: the alarm
    /// action was deleted, because a door's alarm is the chained puzzle instance its lock component holds and
    /// `forge.action.map.scan_state` already writes that instance kind. A plan reads the instance from
    /// `forge.query.map.door_state`'s `puzzle` output, and this contract keeps only the two rows whose recipients
    /// are doors.</summary>
    public static object[] Rows() => new object[] { OpenRow(), CloseRow() };

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

    /// <summary>The two binding rows in the same order as <see cref="Rows"/>.</summary>
    public static object[] Bindings() => new object[]
    {
        BindingRow(OpenCapability, OpenHandlerName),
        BindingRow(CloseCapability, CloseHandlerName)
    };

    /// <summary>One binding's registration support: the one permission the catalog's recipient contract names
    /// for the row. A door is a map object whose own interaction, lock and alarm a plan reaches through this
    /// provider, so the permission is the row's, not the whole map-object namespace's read permission.</summary>
    public static BindingSupport Support(string capability, string permission)
        => new(Binding(capability), "implementation-only", new[] { permission });

    /// <summary>The two registration support rows, in the same order as <see cref="Bindings"/>.</summary>
    public static BindingSupport[] Supports() => new[]
    {
        Support(OpenCapability, OpenPermission),
        Support(CloseCapability, ClosePermission)
    };

    /// <summary>The handler shapes this provider's native half answers, keyed by handler name. A registration
    /// composes them into its own shape table; the native half never declares a second layout.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [OpenHandlerName] = OpenShape,
        [CloseHandlerName] = CloseShape
    };
}
