using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `forge.action.player.movement_profile` row this provider implements for the one `gtfo.player`
/// namespace. The row is the catalog's own graph, port for port, with one exception it states below.
///
/// The native path is the one the two sourced-modifier rows already write through — `AgentModifierManager`'s
/// synced modifier table, whose movement members are `MovementSpeed` (250) and `MovementAcceleration` (251) — and
/// the preset is submitted through the same adapter, not through a second write path onto the same table. The
/// command mints one `effect`/`entity_life` handle for the pair of writes it made per recipient, so a later
/// remove or cancel holds the whole preset.
///
/// The contract exposes only the native movement modifiers this build can actually carry: movement speed and
/// acceleration, plus the requested duration. Unsupported jump/gravity tuning is not advertised as a callable port.</summary>
public static class MovementProfileContract
{
    public const string CapabilityId = "forge.action.player.movement_profile";
    public const string HandlerName = "gtfo.player.movement_profile";

    /// <summary>The permission the catalog's own recipient contract names for this row.</summary>
    public const string Permission = "motion.profile";

    /// <summary>The catalog's domain list for the row: a movement preset is a map- and room-level effect as much
    /// as a player one.</summary>
    public static readonly string[] Domains = { "map", "room", "tool", "consumable", "player" };

    /// <summary>This provider's binding id for the row, spelled like the other player action rows'.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.player.movement_profile";

    /// <summary>The one shape of the handler, resolved once at registration against the capability: the recipient
    /// collection, the required source reference, the two modifiers the native table carries and the requested
    /// lifetime. The native half reads this shape rather than describing a second one.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "source", "speed", "acceleration", "duration")
        .Outputs("result", "profile_handle");

    /// <summary>The capability row, with only native-backed movement ports and the recipients contract naming
    /// the effect handle the row returns.</summary>
    public static object Row() => new
    {
        id = CapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "修改玩家原生属性",
        version = "1.0.0",
        parameters = new { description = "临时改变玩家的移动手感，可恢复。" },
        graph = new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "targets", type = "entity", cardinality = "many" },
                new { id = "source", type = "entity", entityKinds = new[] { "gtfo.player" } },
                new { id = "speed", type = "number" },
                new { id = "acceleration", type = "number" },
                new { id = "duration", type = "integer", unit = "tick" }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.player.movement_profile",
                    codes = new[]
                    {
                        "authority-or-phase",
                        "modifier-target-kind",
                        "stale-or-unsupported-recipient",
                        "amount-out-of-range",
                        "duration-out-of-range",
                        "too-many-targets",
                        "modifier-budget",
                        "handle-budget",
                        "modifier-id-exhausted",
                        "native-commit-exception",
                        "not-attempted-after-unknown-commit"
                    },
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" },
                        new { id = "speed", type = "number" },
                        new { id = "target_count", type = "integer" }
                    }
                },
                new { id = "profile_handle", type = "handle", handleKind = "effect", lifetime = "entity_life" }
            },
            parameters = Array.Empty<object>(),
            recipients = new
            {
                input = "targets", target = "entity", cardinality = "many",
                requires = new[] { Permission }, result = "result", handle = "profile_handle"
            }
        }
    };

    /// <summary>The one execute binding row: this provider's own id, the canonical capability and the handler the
    /// native half supplies. The row depends on no other binding, so the closure of a plan that pins it is the row
    /// itself.</summary>
    public static object BindingRow() => new
    {
        id = BindingId,
        capabilityId = CapabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler = HandlerName,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    /// <summary>The row's registration support: exactly the permission its own catalog recipient contract
    /// names.</summary>
    public static BindingSupport Support() => new(BindingId, "implementation-only", new[] { Permission });

    /// <summary>The handler shape this provider's native half answers, keyed by handler name. A registration
    /// composes it into its own shape table; the native half never declares a second layout.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [HandlerName] = Shape
    };
}
