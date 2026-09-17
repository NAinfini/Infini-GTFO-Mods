using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `forge.action.player.stamina_change` row (ruling R1, generic-player batch): one immediate change to
/// a player's own stamina, as an absolute value or as a signed amount.
///
/// Stamina is a value, not a status: the native member is `PlayerStamina.Stamina`, a `0..1` write with a private
/// `MaxStamina` of 1, so this row settles at once and carries no `effect` handle and no restore callback. The
/// ruling's "pause natural regeneration" half of the same idea is a duration and belongs to the continuous batch;
/// the `AllowRegen` member exists and is deliberately not written here, because a row that both changed the value
/// and left a regeneration switch behind would need the restore callback that batch is adding.
///
/// `operation` is a structural parameter and not an input, so the whole command is refused when it names an
/// operation this handler does not implement — half a command applied with an operation nobody recognised is the
/// one outcome an author cannot see. `amount` is an input, in the native `0..1` ratio, so a plan can compute it
/// from a weapon's own numbers instead of hard-coding one.</summary>
public static class PlayerStaminaContract
{
    public const string CapabilityId = "forge.action.player.stamina_change";
    public const string HandlerName = "gtfo.player.stamina_change";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.player.stamina_change";

    /// <summary>The permission this row writes under: stamina belongs to a player, so a plan declares that it may
    /// change that player's own reserve before it can.</summary>
    public const string Permission = "stamina.modify";

    public static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player" };

    /// <summary>The three operations, in the order the row documents them. `set` writes the amount, `add` and
    /// `subtract` move the current value by it.</summary>
    public static readonly string[] Operations = { "set", "add", "subtract" };

    public static readonly string[] Codes =
    {
        "authority-or-phase",
        "no-targets",
        "too-many-targets",
        "amount-out-of-range",
        "stamina-operation-unsupported",
        "stamina-target-kind",
        "stale-or-unsupported-recipient",
        "native-commit-exception"
    };

    /// <summary>The one shape of the handler: the recipient collection, the amount, and the operation parameter
    /// the whole command is validated against.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "amount").Outputs("result").Parameters("operation");

    public static object Row() => new
    {
        id = CapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "改玩家体力",
        version = "1.0.0",
        parameters = new { description = "立即增减一名玩家的体力。" },
        graph = new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "targets", type = "entity", cardinality = "many", entityKinds = new[] { "gtfo.player" } },
                new { id = "amount", type = "number", unit = "ratio_0_1" }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.player.stamina_change", codes = Codes,
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" },
                        new { id = "amount", type = "number" },
                        new { id = "target_count", type = "integer" }
                    }
                }
            },
            parameters = new object[]
            {
                new { id = "operation", type = "enum", role = "structural", required = true, values = Operations }
            },
            recipients = new
            {
                input = "targets", target = "entity", cardinality = "many",
                requires = new[] { Permission }, result = "result"
            }
        }
    };

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

    public static BindingSupport Support() => new(BindingId, "implementation-only", new[] { Permission });

    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [HandlerName] = Shape
    };
}
