using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `forge.action.combat.impulse` Outcome: apply one pure movement impulse to player recipients.
///
/// The behavior graph computes the direction and scalar strength before this Outcome runs. Normalization, distance
/// falloff, source exclusion and target selection are composition concerns and stay in Operators/Selectors rather
/// than being duplicated as hidden action parameters. The native write is `PlayerLocomotion.AddExternalPushForce`;
/// no damage entry is submitted.
///
/// Enemy knockback is intentionally not claimed here: current native evidence reaches enemies through a distinct
/// push/damage packet. That path remains a separate pending capability until it can be modeled without pretending
/// that a damage-carrying packet is the same operation as a pure player impulse.</summary>
public static class CombatImpulseContract
{
    public const string CapabilityId = "forge.action.combat.impulse";
    public const string HandlerName = "gtfo.combat.impulse";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.combat.impulse";

    /// <summary>The permission this row writes under. A push moves somebody else's body, so a plan declares that
    /// it may apply an impulse before it can.</summary>
    public const string Permission = "combat.impulse";



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
        "impulse-target-kind",
        "stale-or-unsupported-recipient",
        "native-commit-exception"
    };

    /// <summary>The one shape of the handler: the player recipients plus the direction and strength Data already
    /// computed by the behavior graph. Falloff, normalization and source filtering are separate Operators.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "direction", "strength").Outputs("result");

    /// <summary>The capability row: `host` execution, because a push changes where a body is and every machine has
    /// to see the same one.</summary>
    public static object Row() => new
    {
        id = CapabilityId,
        owner = ModuleDefinition.ProviderId,
        kind = "action",
        label = "施加冲量",
        version = "1.0.0",
        parameters = new { description = "给玩家一个带方向和强度的纯冲量；方向和强度由行为图计算。" },
        graph = PrimitiveGraphSource.Get(CapabilityId)
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
