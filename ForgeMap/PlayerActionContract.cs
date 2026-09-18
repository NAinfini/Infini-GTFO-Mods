using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The two player-domain actions this provider implements, one row each: what the catalog declares and
/// what the native half answers through. Both are host actions that end in a game entry point which replicates
/// by itself — a warp request that the session sends to every machine, and an infection change submitted with the
/// sync flag — so this package never writes a field a client would have to guess at.
///
/// The capability list, its ports, its parameters and its result schema are the catalog entry for the same id,
/// unchanged; the catalog is the authority for the shape and this module only implements it. The rows live in
/// the game-independent assembly because the runtime module, the manifest and the website read them there.
///
/// The remaining three player rows of the catalog are deliberately not declared here, because no binding of this
/// build could answer them end to end:
/// - `forge.action.player.respawn` has no native request path at all (`PlayerAgent.Alive` is a flag, not a life
///   allocation, and nothing spawns or replicates a replacement `PlayerAgent`);
/// - `forge.action.player.input_restrict` declares an `effect` handle and a per-action gate, while the only
///   native entry (`PlayerAgent.RequestToggleControlsEnabled`) toggles every control at once and mints nothing;
/// - `forge.action.player.checkpoint_inventory` consumes a `transaction` snapshot handle, which is the
///   transaction/reservation store the runtime does not have yet.
/// `ForgeMap/evidence/player-actions.json` carries the native basis for each conclusion. The
/// `forge.action.player.movement_profile` row was on that list until the synced-modifier adapter landed the effect
/// handle it needed; it now lives in <see cref="MovementProfileContract"/>, which states the one input the native
/// table still cannot carry.</summary>
public static class PlayerActionContract
{
    public const string TeleportCapabilityId = "forge.action.player.teleport";
    public const string TeleportHandlerName = "gtfo.player.teleport";
    /// <summary>The permission the catalog's own recipient contract names for the teleport row.</summary>
    public const string TeleportPermission = "player.teleport";

    public const string InfectionCapabilityId = "forge.action.player.infection_change";
    public const string InfectionHandlerName = "gtfo.player.infection";
    /// <summary>The permission the catalog's own recipient contract names for the infection row.</summary>
    public const string InfectionPermission = "infection.modify";

    /// <summary>The catalog's domain list for these rows, in its own order: a player action is a map, room, tool
    /// and consumable concern as much as a player one, and each row names the same set.</summary>
    public static readonly string[] Domains = { "map", "room", "tool", "consumable", "player" };

    /// <summary>This provider's binding id for a row: the capability's own suffix under the Map provider, so the
    /// counterpart of a binding is readable from either side.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.action.player.".Length..];

    /// <summary>The one shape of the teleport handler, resolved once at registration against the capability: the
    /// recipient collection, the destination and its facing, the optional area the author constrained it to, and
    /// the one structural carry policy. The native half reads this shape rather than describing a second one.</summary>
    public static readonly HandlerShape TeleportShape = new HandlerShape()
        .Inputs("players", "destination", "rotation", "area").Outputs("result").Parameters("inventory_policy");

    /// <summary>The one shape of the infection handler: the recipient collection, the required source reference,
    /// the requested amount, the optional resistance and the optional ceiling, and the one structural operation.
    /// `resistance` and `cap` stay declared even though this build refuses a request that carries them: the shape
    /// is the catalog's, and a port the plan wires must reach the handler to be refused by name.</summary>
    public static readonly HandlerShape InfectionShape = new HandlerShape()
        .Inputs("targets", "source", "amount", "resistance", "cap").Outputs("result").Parameters("operation");

    /// <summary>Both rows, in the order this provider declares them.</summary>
    public static IReadOnlyList<object> Rows() => Array.AsReadOnly(new[] { TeleportRow(), InfectionRow() });

    /// <summary>Both binding rows, paired with <see cref="Rows"/> by position.</summary>
    public static IReadOnlyList<object> Bindings() => Array.AsReadOnly(new[] { TeleportBinding(), InfectionBinding() });

    /// <summary>Both registration support rows, paired with <see cref="Bindings"/> by position.</summary>
    public static IReadOnlyList<BindingSupport> Support() => Array.AsReadOnly(new[] { TeleportSupport(), InfectionSupport() });

    /// <summary>The handler shapes this provider's native half answers, keyed by handler name. A registration
    /// composes them into its own shape table; the native half never declares a second layout.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [TeleportHandlerName] = TeleportShape,
        [InfectionHandlerName] = InfectionShape
    };

    /// <summary>The `forge.action.player.teleport` row: the catalog's own graph, including the `area` resource
    /// port and the declared result schema, and nothing this provider invented.</summary>
    public static object TeleportRow() => new
    {
        id = TeleportCapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "按授权位置与携带物政策传送",
        version = "1.0.0",
        parameters = new { description = "把玩家传送到授权过的位置。" },
        graph = new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "players", type = "entity", cardinality = "many",
                    entityKinds = new[] { PlayerStateContract.EntityKind } },
                new { id = "destination", type = "vector3", unit = "m" },
                new { id = "rotation", type = "vector3", unit = "deg" },
                new { id = "area", type = "resource", resourceKind = "area_field", schema = "forge.resource.area_field" }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.player.teleport",
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
                new
                {
                    id = "inventory_policy", type = "enum", role = "structural", required = true,
                    values = new[] { "keep", "drop", "store" }
                }
            },
            recipients = new
            {
                input = "players", target = "entity", cardinality = "many",
                requires = new[] { TeleportPermission }, result = "result"
            }
        }
    };

    /// <summary>The `forge.action.player.infection_change` row: the catalog's own graph, including the two
    /// optional numeric ports and the declared result schema.</summary>
    public static object InfectionRow() => new
    {
        id = InfectionCapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "修改感染值",
        version = "1.0.0",
        parameters = new { description = "改变感染值。" },
        graph = PrimitiveGraphSource.Get(InfectionCapabilityId)
    };

    /// <summary>One execute binding row: this provider's own id, the catalog capability it implements, and the
    /// handler the native half supplies. Neither row depends on another binding, so the closure of a plan that
    /// pins one is the row itself.</summary>
    public static object TeleportBinding() => BindingRow(TeleportCapabilityId, TeleportHandlerName);

    /// <inheritdoc cref="TeleportBinding"/>
    public static object InfectionBinding() => BindingRow(InfectionCapabilityId, InfectionHandlerName);

    /// <summary>Both rows' registration support: each carries exactly the permission its own catalog recipient
    /// contract names, and no other row has to be pinned for it.</summary>
    public static BindingSupport TeleportSupport() => new(Binding(TeleportCapabilityId), "implementation-only", new[] { TeleportPermission });

    /// <inheritdoc cref="TeleportSupport"/>
    public static BindingSupport InfectionSupport() => new(Binding(InfectionCapabilityId), "implementation-only", new[] { InfectionPermission });

    private static object BindingRow(string capabilityId, string handler) => new
    {
        id = Binding(capabilityId),
        capabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };
}
