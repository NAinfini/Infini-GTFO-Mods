using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `forge.action.combat.impulse` row (ruling R1, generic-player batch): one directional impulse
/// applied to whoever the plan named, player or enemy. It pushes and never damages — the amount of harm a push
/// does is the damage row's business, and keeping the two apart is what lets one action serve a melee shove, an
/// explosion's knockback and a class-dash without any of them owning the other's side effect.
///
/// The recipient set is one set, not two rows: the catalog's own rule is that a capability has one binding owner,
/// and a receiver that can be either kind is dispatched inside the one handler (`GenericPlayerActions.Impulse`).
/// The row therefore declares a plain entity recipient with no `entityKinds` narrowing, which is the only shape in
/// which one step can name both a player and an enemy.
///
/// The direction is an input port and not a parameter: an author computes it (a shooter's aim, a source-to-target
/// vector, a fixed world axis) and the row only applies it. `direction` absent means the handler derives the
/// source-to-target line, which is the shape an explosion's push has; a command with neither a direction nor a
/// solvable source is refused rather than pushed along an axis nobody asked for.
///
/// The native write is the recipient's own push channel: `PlayerLocomotion.AddExternalPushForce` for a player (the
/// same channel the game's push damage writes). The enemy half is refused by name — this provider has no way to turn
/// a `gtfo.enemy` reference into a native `EnemyAgent` — and `falloff_distance`/`falloff_vertical` are refused too:
/// the game's own force falloff lives inside `DamageUtil.DoExplosionDamage`, which is positional and applies damage
/// beside the push, and this row may do neither. There is no `effect` handle: an impulse is one velocity write and
/// has no state of its own to restore, which is why this row registers no restore callback.</summary>
public static class CombatImpulseContract
{
    public const string CapabilityId = "forge.action.combat.impulse";
    public const string HandlerName = "gtfo.combat.impulse";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.combat.impulse";

    /// <summary>The permission this row writes under. A push moves somebody else's body, so a plan declares that
    /// it may apply an impulse before it can.</summary>
    public const string Permission = "combat.impulse";

    public static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player" };

    /// <summary>Every code this handler answers with, in the row's own result contract. `authority-or-phase` is the
    /// host gate, the two target codes are the request's shape, and the rest name the recipient or the native write
    /// that could not be made.</summary>
    public static readonly string[] Codes =
    {
        "authority-or-phase",
        "no-targets",
        "too-many-targets",
        "impulse-strength-required",
        "impulse-strength-out-of-range",
        "impulse-direction-invalid",
        "impulse-source-unresolved",
        "impulse-source-excluded",
        "impulse-falloff-unsupported",
        "impulse-target-kind",
        "impulse-recipient-unresolved",
        "stale-or-unsupported-recipient",
        "native-commit-exception"
    };

    /// <summary>The one shape of the handler: the recipient collection, the two optional entity/vector inputs and
    /// the five structural parameters the falloff and the caster question are read from.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "direction", "source").Outputs("result")
        .Parameters("horizontal", "vertical", "falloff_distance", "falloff_vertical", "include_source");

    /// <summary>The capability row: `host` execution, because a push changes where a body is and every machine has
    /// to see the same one.</summary>
    public static object Row() => new
    {
        id = CapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "施加冲量",
        version = "1.0.0",
        parameters = new { description = "给玩家或敌人一个带方向的推力，只推不伤。" },
        graph = new
        {
            domains = Domains,
            execution = "host",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "targets", type = "entity", cardinality = "many" },
                new { id = "direction", type = "vector3", optional = true },
                new { id = "source", type = "entity", optional = true }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.combat.impulse", codes = Codes,
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" },
                        new { id = "strength", type = "number" },
                        new { id = "target_count", type = "integer" }
                    }
                }
            },
            parameters = new object[]
            {
                new { id = "horizontal", type = "number", role = "structural", required = false, unit = "mps" },
                new { id = "vertical", type = "number", role = "structural", required = false, unit = "mps" },
                new { id = "falloff_distance", type = "boolean", role = "structural", required = false },
                new { id = "falloff_vertical", type = "boolean", role = "structural", required = false },
                new { id = "include_source", type = "boolean", role = "structural", required = false }
            },
            recipients = new
            {
                input = "targets", target = "entity", cardinality = "many",
                requires = new[] { Permission }, result = "result"
            }
        }
    };

    /// <summary>The one execute binding row: this provider's own id, the canonical capability and the handler the
    /// native half supplies.</summary>
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

    /// <summary>The row's registration support: exactly the permission its own recipient contract names.</summary>
    public static BindingSupport Support() => new(BindingId, "implementation-only", new[] { Permission });

    /// <summary>The handler shape, keyed by handler name, for the registration's one shape table.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [HandlerName] = Shape
    };
}
