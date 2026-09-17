using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The one map-object state row this provider owns: the interaction switch
/// (`forge.action.map.interaction_state`). It is the row the EOS feature's stopped rows fold into
/// (`object_state`, `terminal_set_active`, `door_interaction_state`): one row that turns a map object's own
/// interaction on or off. The native member is `Interact_Base.SetActive(System.Boolean)` — the interactable
/// leaving the world — reached from the map object the recipient addresses.
///
/// There is no alarm row here. The rulings deleted `forge.action.map.alarm` (the row `door_alarm` had been
/// renamed into): a door's alarm is the chained puzzle instance its lock component holds, the very instance
/// `forge.action.map.scan_state` writes, and one native write keeps one card. A plan that wants the puzzle a
/// door carries reads it from `forge.query.map.door_state`'s `puzzle` output.
///
/// The row is host-authoritative this round: a usable object is world state. A row is declared here, in the
/// provider that owns it, exactly as every other family's rows are; the native half supplies only the body.</summary>
public static class MapStateContract
{
    // ---- forge.action.map.interaction_state ---------------------------------------------------------------

    public const string InteractionCapabilityId = "forge.action.map.interaction_state";
    public const string InteractionBindingId = ModuleDefinition.ProviderId + ".binding.map.interaction_state";
    public const string InteractionHandlerName = "gtfo.map.interaction_state";

    /// <summary>The permission the interaction write needs: it decides whether a player may touch an object at
    /// all.</summary>
    public const string InteractionPermission = "map.interaction_state";

    /// <summary>The two settings: the object's interaction is available, or it is not. `disable` is the state a
    /// dormant terminal or a sealed door is in.</summary>
    public static readonly string[] InteractionOperations = { "enable", "disable" };

    public static HandlerShape InteractionShape { get; } = new HandlerShape()
        .Inputs("targets").Outputs("result").Parameters("operation");

    /// <summary>The map objects this row addresses: a door and a terminal are both map objects of this
    /// provider's one entity kind.</summary>
    public const string TargetEntityKind = MapObjectModule.EntityKind;

    /// <summary>The domain list the row carries: a map object is something a room and a logic graph act on.</summary>
    public static readonly string[] Domains = { "map", "room", "logic" };

    public static IReadOnlyDictionary<string, HandlerShape> Shapes() =>
        new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [InteractionHandlerName] = InteractionShape
        };

    public static object[] CapabilityRows() => new object[] { InteractionRow() };

    public static object[] BindingRows() => new object[]
    {
        BindingRow(InteractionCapabilityId, InteractionBindingId, InteractionHandlerName)
    };

    public static BindingSupport[] Supports() => new[]
    {
        new BindingSupport(InteractionBindingId, "implementation-only", new[] { InteractionPermission })
    };

    /// <summary>The interaction row: one setting per request, applied to every recipient the plan addressed. The
    /// four door parts and the terminal's sleep state the ruling names are **not** parameters here — this build's
    /// `LG_SecurityDoor_Locks` carries no per-part interaction switch and `LG_ComputerTerminal` no state write —
    /// so the row declares the setting it can keep and the handler refuses nothing it cannot carry.</summary>
    private static object InteractionRow() => new
    {
        id = InteractionCapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "开关地图对象交互",
        version = "1.0.0",
        parameters = new { description = "启用或停用目标地图对象自己的交互。" },
        graph = new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "targets", type = "entity", cardinality = "many", entityKinds = new[] { TargetEntityKind } }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.map.interaction_state",
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" },
                        new { id = "target_count", type = "integer" }
                    }
                }
            },
            parameters = new object[]
            {
                new { id = "operation", type = "enum", role = "structural", required = true, values = InteractionOperations }
            },
            recipients = new
            {
                input = "targets", target = "entity", cardinality = "many",
                requires = new[] { InteractionPermission }, result = "result"
            }
        }
    };

    private static object BindingRow(string capability, string binding, string handler) => new
    {
        id = binding,
        capabilityId = capability,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };
}
